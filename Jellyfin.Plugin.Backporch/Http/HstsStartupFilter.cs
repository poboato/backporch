using System.Globalization;
using Jellyfin.Plugin.Backporch.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Backporch.Http;

/// <summary>
/// Adds <c>Strict-Transport-Security</c> to Jellyfin's own HTTPS responses.
/// </summary>
/// <remarks>
/// <para>
/// With the plugin holding port 80, a browser that gets sent to HTTPS still made one
/// plain-HTTP request to be told so — and that first request is the one an attacker on the
/// path can answer instead of the redirect. HSTS closes the gap for every visit after the
/// first: the browser refuses to speak HTTP to this host at all, and refuses to let anyone
/// click through a certificate warning.
/// </para>
/// <para>
/// The header only means anything over HTTPS, and is only sent there. The default lifetime
/// is six months rather than the customary year because it is a promise that cannot be
/// withdrawn from a browser that has already heard it — a shorter window is a shorter
/// mistake. <c>includeSubDomains</c> and <c>preload</c> are deliberately not set: both make
/// promises about names this plugin does not own and cannot verify.
/// </para>
/// <para>
/// A startup filter is the only seam a Jellyfin plugin has into the server's request
/// pipeline, and this one runs on every request — so it does the least work that can
/// possibly be correct, and swallows anything that goes wrong.
/// </para>
/// </remarks>
internal sealed class HstsStartupFilter : IStartupFilter
{
    private readonly Func<PluginConfiguration?> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="HstsStartupFilter"/> class, reading
    /// settings from the loaded plugin. Used by the dependency injection container.
    /// </summary>
    public HstsStartupFilter()
        : this(static () => Plugin.Instance?.Configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HstsStartupFilter"/> class with an
    /// explicit configuration source.
    /// </summary>
    /// <param name="configuration">Reads the current settings.</param>
    internal HstsStartupFilter(Func<PluginConfiguration?> configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use((HttpContext context, RequestDelegate continuation) =>
            {
                AddHeader(context);
                return continuation(context);
            });

            next(app);
        };
    }

    /// <summary>
    /// Builds the header value, or <c>null</c> when nothing should be sent.
    /// </summary>
    /// <param name="enabled">Whether the user has HSTS turned on.</param>
    /// <param name="maxAgeDays">Configured lifetime in days.</param>
    /// <returns>The header value, or <c>null</c>.</returns>
    internal static string? BuildValue(bool enabled, int maxAgeDays)
    {
        if (!enabled || maxAgeDays <= 0)
        {
            return null;
        }

        var seconds = (long)maxAgeDays * 86400L;
        return "max-age=" + seconds.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Arranges for the header to be present on the response, if it should be.
    /// </summary>
    /// <param name="context">The request being handled.</param>
    internal void AddHeader(HttpContext context)
    {
        try
        {
            if (!context.Request.IsHttps)
            {
                return;
            }

            var config = _configuration();
            if (config is null)
            {
                return;
            }

            var value = BuildValue(config.EnableHsts, config.HstsMaxAgeDays);
            if (value is null)
            {
                return;
            }

            // Set at the last moment rather than now: middleware further in may reset the
            // response — an error page, most obviously — and a header set here would go
            // with it.
            context.Response.OnStarting(
                static state =>
                {
                    var (response, headerValue) = ((HttpResponse, string))state;
                    response.Headers.StrictTransportSecurity = headerValue;
                    return Task.CompletedTask;
                },
                (context.Response, value));
        }
        catch (Exception)
        {
            // A hardening header is never worth failing a request over.
        }
    }
}
