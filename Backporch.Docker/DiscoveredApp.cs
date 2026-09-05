namespace Backporch.Docker;

/// <summary>
/// How dangerous it would be to put an application on the public internet.
/// </summary>
/// <remarks>
/// Discovery finds everything running, but "running" and "safe to publish" are very
/// different questions, and the person answering the second one is usually not thinking
/// about it at the moment they tick a box. The grade travels with the candidate so the
/// interface can refuse, warn, or stay quiet without re-deriving the judgement.
/// </remarks>
public enum ExposureRisk
{
    /// <summary>An ordinary application. Publishing it is the user's call.</summary>
    Ordinary = 0,

    /// <summary>
    /// Publishing this is a bad idea and the interface should say so plainly, but it is
    /// the user's machine and the choice remains theirs.
    /// </summary>
    Sensitive = 1,

    /// <summary>
    /// Publishing this hands over the host itself. Never offered, whatever is ticked.
    /// </summary>
    NeverExpose = 2
}

/// <summary>
/// An application found running on this machine that could be given a name on the
/// certificate and served through the front door.
/// </summary>
public sealed class DiscoveredApp
{
    /// <summary>Gets the container name, as Docker reports it, without the leading slash.</summary>
    public required string Container { get; init; }

    /// <summary>Gets the image the container was started from.</summary>
    public required string Image { get; init; }

    /// <summary>Gets the port published on the host that serves this application.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// Gets the port inside the container behind <see cref="Port"/>.
    /// </summary>
    /// <remarks>
    /// Kept because it is the only reliable way to recognise the application doing the
    /// asking. The published port is chosen by whoever wrote the compose file and says
    /// nothing; the port the software actually listens on is the software's own.
    /// </remarks>
    public required int ContainerPort { get; init; }

    /// <summary>
    /// Gets the other published ports, which the user may prefer. Empty when there was
    /// no ambiguity.
    /// </summary>
    public IReadOnlyList<int> AlternatePorts { get; init; } = Array.Empty<int>();

    /// <summary>Gets the host label suggested for this application, such as <c>sonarr</c>.</summary>
    public required string SuggestedLabel { get; init; }

    /// <summary>Gets how dangerous publishing this application would be.</summary>
    public ExposureRisk Risk { get; init; }

    /// <summary>
    /// Gets the reason behind <see cref="Risk"/>, in words meant for the person deciding.
    /// Empty for an ordinary application.
    /// </summary>
    public string RiskReason { get; init; } = string.Empty;

    /// <summary>Gets the full name this application would answer to under a domain.</summary>
    /// <param name="domain">The domain the front door serves, such as <c>example.com</c>.</param>
    /// <returns>A fully qualified name, such as <c>sonarr.example.com</c>.</returns>
    public string HostnameUnder(string domain) => SuggestedLabel + "." + domain.Trim().TrimStart('.');
}
