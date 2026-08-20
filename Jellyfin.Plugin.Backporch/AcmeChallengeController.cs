using System.Net.Mime;
using Jellyfin.Plugin.Backporch.Acme;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Backporch;

/// <summary>
/// Serves HTTP-01 challenge answers at the well-known path the certificate authority
/// fetches. Anonymous by protocol necessity: the CA has no credentials. It only ever
/// discloses answers for challenges this server itself initiated seconds earlier, and
/// key authorizations are public by design — proof lies in serving them, not knowing them.
/// </summary>
[ApiController]
[Route(".well-known/acme-challenge")]
public class AcmeChallengeController : ControllerBase
{
    private readonly HttpChallengeStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcmeChallengeController"/> class.
    /// </summary>
    /// <param name="store">The active challenge answers.</param>
    public AcmeChallengeController(HttpChallengeStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Answers an HTTP-01 challenge.
    /// </summary>
    /// <param name="token">The challenge token being validated.</param>
    /// <returns>The key authorization, or 404 when no such challenge is active.</returns>
    [HttpGet("{token}")]
    [AllowAnonymous]
    public ActionResult Get(string token)
    {
        if (!_store.TryGet(token, out var keyAuthorization))
        {
            return NotFound();
        }

        return Content(keyAuthorization, MediaTypeNames.Text.Plain);
    }
}
