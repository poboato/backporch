using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Jellyfin.Plugin.Backporch.Http;

/// <summary>
/// Everything the plugin will say over plain HTTP, which is deliberately almost nothing:
/// an ACME challenge answer, or a redirect to HTTPS.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the port-80 forward the HTTP-01 challenge requires does not also
/// publish Jellyfin. Pointing the forward at Jellyfin's own HTTP port exposes the whole
/// unencrypted interface — login page included — to the internet, and no Jellyfin setting
/// can fix that for the <em>first</em> issuance, because "Require HTTPS" cannot redirect
/// to an HTTPS listener that has no certificate yet.
/// </para>
/// <para>
/// Backporch therefore answers that port itself, from a socket that has no route to any
/// Jellyfin content. The only successful response it can produce is a key authorization
/// for a challenge this server started seconds ago — a value that is public by design.
/// Everything else is a redirect; nothing is ever proxied.
/// </para>
/// </remarks>
public sealed class AcmeHttpHandler
{
    /// <summary>The path prefix the certificate authority fetches proofs from (RFC 8555 §8.3).</summary>
    internal const string ChallengePrefix = "/.well-known/acme-challenge/";

    /// <summary>
    /// Longest token accepted. Let's Encrypt issues 43 characters; the cap only keeps a
    /// hostile request from turning into a long dictionary probe.
    /// </summary>
    private const int MaxTokenLength = 128;

    private readonly HttpChallengeStore _store;
    private readonly Func<PluginConfiguration?> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcmeHttpHandler"/> class.
    /// </summary>
    /// <param name="store">The active challenge answers.</param>
    /// <param name="configuration">
    /// Reads the live configuration per request, so an edited domain or HTTPS port takes
    /// effect without restarting the listener.
    /// </param>
    public AcmeHttpHandler(HttpChallengeStore store, Func<PluginConfiguration?> configuration)
    {
        _store = store;
        _configuration = configuration;
    }

    /// <summary>
    /// Handles one plain-HTTP request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    public async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // Nothing served here may be cached: a challenge answer is valid for seconds, and a
        // redirect cached against the wrong port would outlive the setting that produced it.
        response.Headers.CacheControl = "no-store";

        var isRead = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        var path = request.Path.Value ?? "/";

        if (isRead && path.StartsWith(ChallengePrefix, StringComparison.Ordinal))
        {
            await AnswerChallengeAsync(context, path[ChallengePrefix.Length..]).ConfigureAwait(false);
            return;
        }

        Redirect(context, isRead);
    }

    /// <summary>
    /// ACME tokens are base64url (RFC 8555 §8.3). Anything else cannot be a live token, so
    /// it is refused before it reaches the store — which also disposes of path traversal,
    /// since a token containing a slash never matches.
    /// </summary>
    /// <param name="token">The candidate token from the request path.</param>
    /// <returns><c>true</c> when the token could be one the CA issued.</returns>
    private static bool IsTokenShaped(string token)
    {
        if (token.Length is 0 or > MaxTokenLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            var ok = c is >= 'a' and <= 'z'
                || c is >= 'A' and <= 'Z'
                || c is >= '0' and <= '9'
                || c is '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private async Task AnswerChallengeAsync(HttpContext context, string token)
    {
        var response = context.Response;

        if (!IsTokenShaped(token) || !_store.TryGet(token, out var keyAuthorization))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            response.ContentLength = 0;
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(keyAuthorization);
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/plain";
        response.ContentLength = bytes.Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the caller to HTTPS. The host in the <c>Location</c> header is always the
    /// configured domain, never the request's own <c>Host</c> header — an open redirect on
    /// an unauthenticated internet-facing port is worth nothing to anyone but an attacker.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="isRead">Whether the request was a GET or HEAD.</param>
    private void Redirect(HttpContext context, bool isRead)
    {
        var response = context.Response;
        var config = _configuration();

        if (config is null || string.IsNullOrWhiteSpace(config.Domain))
        {
            // Not configured yet: say nothing rather than guess a destination.
            response.StatusCode = StatusCodes.Status404NotFound;
            response.ContentLength = 0;
            return;
        }

        var domain = DestinationFor(context.Request, config);

        var port = config.PublicHttpsPort;
        var authority = port == 443
            ? domain
            : domain + ":" + port.ToString(CultureInfo.InvariantCulture);

        // The encoded form: re-encoding the decoded path would mangle anything escaped.
        response.Headers.Location = "https://" + authority + context.Request.GetEncodedPathAndQuery();

        // 301 is understood and cached everywhere; 308 keeps the method for the rest, so a
        // client retrying a POST does not silently turn it into a GET.
        response.StatusCode = isRead
            ? StatusCodes.Status301MovedPermanently
            : StatusCodes.Status308PermanentRedirect;
        response.ContentLength = 0;
    }

    /// <summary>
    /// Chooses the name to redirect to: the one that was asked for, when the certificate
    /// covers it, and the primary name otherwise.
    /// </summary>
    /// <remarks>
    /// One certificate can carry many names, and each has to land back on itself \u2014
    /// sending a request for one application to another application's address would be
    /// both wrong and confusing. The requested host is matched against the configured
    /// names rather than trusted, so this stays an allow list: a request carrying a host
    /// header for somewhere else cannot turn the listener into an open redirect.
    /// </remarks>
    private static string DestinationFor(HttpRequest request, PluginConfiguration config)
    {
        var requested = request.Host.Host;

        if (!string.IsNullOrEmpty(requested))
        {
            foreach (var name in config.AllDomains())
            {
                if (string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return config.Domain;
    }
}
