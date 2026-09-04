using System.Text.Json.Serialization;

namespace Backporch.Docker;

/// <summary>
/// The few fields of Docker's container listing that discovery actually reads.
/// </summary>
/// <remarks>
/// Deliberately a small hand-written shape rather than a Docker client library. The
/// listing endpoint is stable, this needs five fields from it, and a dependency that can
/// mutate containers is a liability in a component whose whole job is to read.
/// </remarks>
public sealed class ContainerSummary
{
    /// <summary>Gets or sets the container names, each with Docker's leading slash.</summary>
    [JsonPropertyName("Names")]
    public List<string> Names { get; set; } = new();

    /// <summary>Gets or sets the image reference.</summary>
    [JsonPropertyName("Image")]
    public string Image { get; set; } = string.Empty;

    /// <summary>Gets or sets the container state, such as <c>running</c>.</summary>
    [JsonPropertyName("State")]
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets the port mappings.</summary>
    [JsonPropertyName("Ports")]
    public List<ContainerPort> Ports { get; set; } = new();

    /// <summary>Gets or sets the container labels.</summary>
    [JsonPropertyName("Labels")]
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>Gets the container's name without Docker's leading slash.</summary>
    public string Name =>
        Names.Count > 0 ? Names[0].TrimStart('/') : string.Empty;
}

/// <summary>
/// One port mapping from Docker's container listing.
/// </summary>
public sealed class ContainerPort
{
    /// <summary>Gets or sets the port inside the container.</summary>
    [JsonPropertyName("PrivatePort")]
    public int PrivatePort { get; set; }

    /// <summary>Gets or sets the port published on the host, absent when unpublished.</summary>
    [JsonPropertyName("PublicPort")]
    public int? PublicPort { get; set; }

    /// <summary>Gets or sets the protocol, <c>tcp</c> or <c>udp</c>.</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = "tcp";
}
