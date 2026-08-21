using Jellyfin.Plugin.Backporch.Acme;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The domain is handed to the resolver, the certificate authority, and the challenge
/// record builder, so it is validated once at the door rather than trusted downstream.
/// </summary>
public class HostnameValidationTests
{
    [Theory]
    [InlineData("example.com")]
    [InlineData("media.example.com")]
    [InlineData("a.b.c.d.example.com")]
    [InlineData("my-server.example.co.uk")]
    [InlineData("xn--bcher-kva.example.com")]
    [InlineData("123.example.com")]
    public void AcceptsRealHostnames(string domain)
    {
        Assert.True(AcmeService.IsValidHostname(domain));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]                       // single label
    [InlineData("https://example.com")]             // scheme
    [InlineData("example.com/path")]                // path
    [InlineData("example.com:8096")]                // port
    [InlineData("exa mple.com")]                    // space
    [InlineData("example..com")]                    // empty label
    [InlineData(".example.com")]                    // leading dot
    [InlineData("example.com.")]                    // trailing dot
    [InlineData("-example.com")]                    // label starts with hyphen
    [InlineData("example-.com")]                    // label ends with hyphen
    [InlineData("*.example.com")]                   // wildcard
    [InlineData("example.com\nHost: evil")]         // control characters
    [InlineData("exam_ple.com")]                    // underscore
    public void RejectsEverythingElse(string domain)
    {
        Assert.False(AcmeService.IsValidHostname(domain));
    }

    [Fact]
    public void RejectsOverlongNames()
    {
        var label = new string('a', 63);
        var tooLong = string.Join('.', label, label, label, label) + ".example.com";

        Assert.True(tooLong.Length > 253);
        Assert.False(AcmeService.IsValidHostname(tooLong));
    }

    [Fact]
    public void RejectsOverlongLabel()
    {
        Assert.False(AcmeService.IsValidHostname(new string('a', 64) + ".example.com"));
    }
}
