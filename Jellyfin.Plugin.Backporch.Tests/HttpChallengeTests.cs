using Jellyfin.Plugin.Backporch.Acme;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The HTTP-01 answer store and the anonymous well-known route that serves from it.
/// </summary>
public class HttpChallengeTests
{
    [Fact]
    public void StoreRoundTripsAndForgets()
    {
        var store = new HttpChallengeStore();

        Assert.False(store.TryGet("tok", out _));

        store.Put("tok", "tok.thumbprint");
        Assert.True(store.TryGet("tok", out var answer));
        Assert.Equal("tok.thumbprint", answer);

        store.Remove("tok");
        Assert.False(store.TryGet("tok", out _));
    }

    [Fact]
    public void RouteServesActiveChallengesAsPlainText()
    {
        var store = new HttpChallengeStore();
        store.Put("abc123", "abc123.keyauth");
        var controller = new AcmeChallengeController(store);

        var hit = Assert.IsType<ContentResult>(controller.Get("abc123"));
        Assert.Equal("abc123.keyauth", hit.Content);
        Assert.Equal("text/plain", hit.ContentType);

        Assert.IsType<NotFoundResult>(controller.Get("unknown"));
    }

    [Fact]
    public void RouteIsAnonymousAndTheAdminControllerIsNot()
    {
        // The well-known route must carry AllowAnonymous (the CA has no credentials);
        // the admin controller must NOT — a regression here is a security hole either way.
        var challenge = typeof(AcmeChallengeController).GetMethod(nameof(AcmeChallengeController.Get))!;
        Assert.NotEmpty(challenge.GetCustomAttributes(
            typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));

        Assert.NotEmpty(typeof(BackporchController).GetCustomAttributes(
            typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true));
        Assert.Empty(typeof(BackporchController).GetCustomAttributes(
            typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), inherit: true));
    }
}
