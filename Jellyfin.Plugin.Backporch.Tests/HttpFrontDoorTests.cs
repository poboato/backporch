using System.Net;
using System.Text;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The plugin's own plain-HTTP listener: the thing that makes a port-80 forward safe.
/// Every test here is really the same assertion from a different angle — that this port
/// can disclose a challenge answer and nothing else, and can never reach Jellyfin.
/// </summary>
public class HttpFrontDoorTests
{
    private static PluginConfiguration Config(string domain = "media.example.com", int httpsPort = 443)
        => new()
        {
            Challenge = ChallengeKind.Http,
            Domain = domain,
            PublicHttpsPort = httpsPort,
            ServeHttpRedirect = true,
            ChallengeListenPort = 80
        };

    private static DefaultHttpContext Request(
        string path,
        string method = "GET",
        string query = "",
        string host = "media.example.com")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Request.Path = new PathString(path);
        context.Request.QueryString = new QueryString(query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string BodyOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    private static AcmeHttpHandler Handler(HttpChallengeStore store, PluginConfiguration? config)
        => new(store, () => config);

    [Fact]
    public async Task ServesAnActiveChallengeAnswer()
    {
        var store = new HttpChallengeStore();
        store.Put("Tok3n-_x", "Tok3n-_x.thumbprint");

        var context = Request(AcmeHttpHandler.ChallengePrefix + "Tok3n-_x");
        await Handler(store, Config()).HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal("Tok3n-_x.thumbprint", BodyOf(context));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task HeadOfAChallengeSendsTheLengthWithoutTheBody()
    {
        var store = new HttpChallengeStore();
        store.Put("tok", "tok.answer");

        var context = Request(AcmeHttpHandler.ChallengePrefix + "tok", method: "HEAD");
        await Handler(store, Config()).HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("tok.answer".Length, context.Response.ContentLength);
        Assert.Equal(string.Empty, BodyOf(context));
    }

    [Fact]
    public async Task AnExpiredOrUnknownTokenIsNotFound()
    {
        var store = new HttpChallengeStore();
        store.Put("live", "live.answer");

        var context = Request(AcmeHttpHandler.ChallengePrefix + "notlive");
        await Handler(store, Config()).HandleAsync(context);

        // Deliberately 404 rather than a redirect: sending the certificate authority to
        // HTTPS for a token that does not exist would turn a clear failure into a
        // confusing one, and would hand it a certificate error instead of an answer.
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(string.Empty, BodyOf(context));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("tok.")]
    [InlineData("tok token")]
    [InlineData("")]
    public async Task AnythingNotShapedLikeATokenNeverReachesTheStore(string token)
    {
        var store = new HttpChallengeStore();
        store.Put(token, "should.never.be.served");

        var context = Request(AcmeHttpHandler.ChallengePrefix + token);
        await Handler(store, Config()).HandleAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(string.Empty, BodyOf(context));
    }

    [Fact]
    public async Task AnOverlongTokenIsRefusedBeforeTheLookup()
    {
        var token = new string('a', 200);
        var store = new HttpChallengeStore();
        store.Put(token, "should.never.be.served");

        var context = Request(AcmeHttpHandler.ChallengePrefix + token);
        await Handler(store, Config()).HandleAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/web/index.html")]
    [InlineData("/System/Info")]
    [InlineData("/Users/AuthenticateByName")]
    [InlineData("/.well-known/something-else")]
    public async Task EverythingElseIsRedirected(string path)
    {
        var context = Request(path);
        await Handler(new HttpChallengeStore(), Config()).HandleAsync(context);

        Assert.Equal(301, context.Response.StatusCode);
        Assert.Equal("https://media.example.com" + path, context.Response.Headers.Location);
        Assert.Equal(string.Empty, BodyOf(context));
    }

    [Fact]
    public async Task TheRedirectKeepsThePathAndQuery()
    {
        var context = Request("/web/index.html", query: "?start=1&x=a%20b");
        await Handler(new HttpChallengeStore(), Config()).HandleAsync(context);

        Assert.Equal(
            "https://media.example.com/web/index.html?start=1&x=a%20b",
            context.Response.Headers.Location);
    }

    [Fact]
    public async Task TheRedirectHostIsTheConfiguredDomainNotTheHostHeader()
    {
        // An unauthenticated, internet-facing port that reflects its Host header into a
        // Location is an open redirect. The configured domain is the only source here.
        var context = Request("/", host: "evil.example.net");
        await Handler(new HttpChallengeStore(), Config()).HandleAsync(context);

        Assert.Equal("https://media.example.com/", context.Response.Headers.Location);
    }

    [Fact]
    public async Task ANonDefaultHttpsPortAppearsInTheRedirect()
    {
        var context = Request("/");
        await Handler(new HttpChallengeStore(), Config(httpsPort: 8920)).HandleAsync(context);

        Assert.Equal("https://media.example.com:8920/", context.Response.Headers.Location);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task MethodsThatCarryDataRedirectWithoutBecomingAGet(string method)
    {
        var context = Request("/Users/AuthenticateByName", method: method);
        await Handler(new HttpChallengeStore(), Config()).HandleAsync(context);

        // 308, not 301: a client retrying credentials must not have them silently
        // downgraded into a GET with the body dropped.
        Assert.Equal(308, context.Response.StatusCode);
        Assert.Equal(
            "https://media.example.com/Users/AuthenticateByName",
            context.Response.Headers.Location);
    }

    [Fact]
    public async Task AChallengeIsAnsweredEvenBeforeADomainIsConfigured()
    {
        // Ordering matters at first setup: the answer must not depend on settings that are
        // only filled in later.
        var store = new HttpChallengeStore();
        store.Put("tok", "tok.answer");

        var context = Request(AcmeHttpHandler.ChallengePrefix + "tok");
        await Handler(store, config: null).HandleAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("tok.answer", BodyOf(context));
    }

    [Fact]
    public async Task WithNoDomainThereIsNowhereToRedirectSoNothingIsSaid()
    {
        var context = Request("/");
        await Handler(new HttpChallengeStore(), config: null).HandleAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.True(StringValuesIsEmpty(context.Response.Headers.Location));
    }

    private static bool StringValuesIsEmpty(Microsoft.Extensions.Primitives.StringValues value)
        => value.Count == 0 || string.IsNullOrEmpty(value.ToString());

    // ---- when the listener should exist at all -------------------------------------

    [Fact]
    public void TheListenerRunsForHttpProofOnceADomainIsKnown()
    {
        Assert.Equal(80, AcmeHttpServer.WantedPort(Config()));
    }

    [Fact]
    public void TheListenerStaysOffForDnsProof()
    {
        var config = Config();
        config.Challenge = ChallengeKind.Dns;
        Assert.Equal(0, AcmeHttpServer.WantedPort(config));
    }

    [Fact]
    public void TheListenerStaysOffWhenAProxyOwnsThePort()
    {
        var config = Config();
        config.ServeHttpRedirect = false;
        Assert.Equal(0, AcmeHttpServer.WantedPort(config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a hostname")]
    [InlineData("*.example.com")]
    public void TheListenerStaysOffUntilTheDomainIsReal(string domain)
    {
        Assert.Equal(0, AcmeHttpServer.WantedPort(Config(domain)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void AnImpossiblePortMeansOff(int port)
    {
        var config = Config();
        config.ChallengeListenPort = port;
        Assert.Equal(0, AcmeHttpServer.WantedPort(config));
    }

    [Fact]
    public void AnUnprivilegedPortIsAllowedForContainersThatCannotBindEighty()
    {
        var config = Config();
        config.ChallengeListenPort = 8080;
        Assert.Equal(8080, AcmeHttpServer.WantedPort(config));
    }

    // ---- over a real socket ---------------------------------------------------------

    [Fact]
    public async Task OverARealSocketItAnswersTheChallengeAndRedirectsEverythingElse()
    {
        var store = new HttpChallengeStore();
        store.Put("realtoken", "realtoken.answer");
        var config = Config();

        var host = await AcmeHttpServer.StartForTestAsync(store, () => config, port: 0);
        try
        {
            var port = BoundPortOf(host);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + port)
            };

            var answer = await client.GetAsync(AcmeHttpHandler.ChallengePrefix + "realtoken");
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal("realtoken.answer", await answer.Content.ReadAsStringAsync());

            var missing = await client.GetAsync(AcmeHttpHandler.ChallengePrefix + "ghost");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // The whole point: a real request for a real Jellyfin path gets a redirect,
            // not content. Nothing on this socket can reach the server.
            var jellyfin = await client.GetAsync("/web/index.html");
            Assert.Equal(HttpStatusCode.MovedPermanently, jellyfin.StatusCode);
            Assert.Equal(
                new Uri("https://media.example.com/web/index.html"),
                jellyfin.Headers.Location);
            Assert.Equal(string.Empty, await jellyfin.Content.ReadAsStringAsync());

            // And it does not announce what it is.
            Assert.False(jellyfin.Headers.Contains("Server"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task OverARealSocketAnEncodedTraversalStillCannotEscape()
    {
        var store = new HttpChallengeStore();
        store.Put("realtoken", "realtoken.answer");
        var config = Config();

        var host = await AcmeHttpServer.StartForTestAsync(store, () => config, port: 0);
        try
        {
            var port = BoundPortOf(host);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + port)
            };

            // Percent-encoded so the server, not the client, does the decoding.
            var probe = await client.GetAsync("/.well-known/acme-challenge/%2e%2e%2f%2e%2e%2frealtoken");
            Assert.NotEqual(HttpStatusCode.OK, probe.StatusCode);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static int BoundPortOf(IWebHost host)
    {
        var address = host.ServerFeatures.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)).Port;
    }

    // ---- HSTS -----------------------------------------------------------------------

    [Fact]
    public void HstsIsSixMonthsAndPromisesNothingAboutOtherNames()
    {
        var value = HstsStartupFilter.BuildValue(enabled: true, maxAgeDays: 180);

        Assert.Equal("max-age=15552000", value);
        Assert.DoesNotContain("includeSubDomains", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preload", value, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 180)]
    [InlineData(true, 0)]
    [InlineData(true, -5)]
    public void HstsSendsNothingWhenOffOrNonsensical(bool enabled, int days)
    {
        Assert.Null(HstsStartupFilter.BuildValue(enabled, days));
    }

    [Fact]
    public void HstsIsNeverSentOverPlainHttp()
    {
        // A Strict-Transport-Security header on a plain-HTTP response is ignored by
        // browsers by design; sending one would only look like protection.
        var config = Config();
        var filter = new HstsStartupFilter(() => config);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";

        filter.AddHeader(context);

        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public void HstsSurvivesAMissingPlugin()
    {
        var filter = new HstsStartupFilter(() => null);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";

        filter.AddHeader(context);

        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task HstsActuallyReachesTheResponseThroughTheStartupFilter()
    {
        // The header is set from an OnStarting callback, which no in-memory HttpContext
        // ever runs — so the only honest proof is a real response off a real socket.
        var config = Config();
        var filter = new HstsStartupFilter(() => config);

        var host = new WebHostBuilder()
            .UseKestrel(options => options.ListenAnyIP(0))
            .UseContentRoot(AppContext.BaseDirectory)
            .Configure(app =>
            {
                // Stand in for TLS termination: the filter only acts on HTTPS requests.
                app.Use((HttpContext context, RequestDelegate next) =>
                {
                    context.Request.Scheme = "https";
                    return next(context);
                });

                filter.Configure(inner => inner.Run(context =>
                {
                    context.Response.StatusCode = 204;
                    return Task.CompletedTask;
                }))(app);
            })
            .Build();

        await host.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + BoundPortOf(host)) };
            var response = await client.GetAsync("/anything");

            Assert.Equal("max-age=15552000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));

            config.EnableHsts = false;
            var afterOff = await client.GetAsync("/anything");
            Assert.False(afterOff.Headers.Contains("Strict-Transport-Security"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
