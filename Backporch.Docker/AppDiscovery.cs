using System.Globalization;
using System.Text;

namespace Backporch.Docker;

/// <summary>
/// Turns a Docker container listing into the applications a person could sensibly put
/// behind a public name.
/// </summary>
/// <remarks>
/// Pure and synchronous on purpose: everything here is a judgement about a list, so it
/// can be tested against a real listing captured from a real machine rather than only
/// against whatever a mock happened to return.
/// </remarks>
public static class AppDiscovery
{
    /// <summary>
    /// Ports that are well known to speak something other than HTTP. Offering one as a
    /// web application produces a front door that fails in a way nobody can diagnose,
    /// because the proxy connects happily and the protocol then makes no sense.
    /// </summary>
    private static readonly Dictionary<int, string> _notHttp = new()
    {
        [22] = "SSH",
        [25] = "SMTP",
        [53] = "DNS",
        [123] = "NTP",
        [445] = "SMB",
        [1900] = "SSDP",
        [3306] = "MySQL",
        [5432] = "PostgreSQL",
        [6379] = "Redis",
        [6881] = "BitTorrent",
        [27017] = "MongoDB"
    };

    /// <summary>
    /// Images and names that must never be offered, whatever the user ticks, because
    /// publishing them hands over the machine rather than an application on it.
    /// </summary>
    private static readonly (string Match, string Reason)[] _neverExpose =
    {
        // Matched loosely on purpose. Every published socket proxy image names itself
        // "socket-proxy" somewhere (tecnativa, linuxserver, wollomatic, 11notes), and a
        // name this branch fails to recognise is offered a public name like any other
        // application — which is the one outcome this list exists to prevent.
        ("socket-proxy", "it is the Docker API — anyone reaching it can read every container on this machine"),
        ("socketproxy", "it is the Docker API — anyone reaching it can read every container on this machine"),
        ("dockerproxy", "it is the Docker API — anyone reaching it can read every container on this machine"),
        ("docker-api", "it is the Docker API — anyone reaching it controls this machine"),
        ("docker.sock", "it is the Docker API — anyone reaching it controls this machine"),
        ("traefik", "a front door published through another front door loops back on itself")
    };

    /// <summary>
    /// Applications that are ordinary software but hand over far more than their own
    /// data when they are reachable from the internet. Offered, but never quietly.
    /// </summary>
    private static readonly (string Match, string Reason)[] _sensitive =
    {
        ("portainer", "it can start, stop and reconfigure every container, which is close to root on this machine"),
        ("glances", "it reports this machine's processes and resource use to anyone who loads it"),
        ("qbittorrent", "its web interface can add downloads and change where they are written"),
        ("gluetun", "this port belongs to the VPN gateway, and usually fronts a download client"),
        ("transmission", "its web interface can add downloads and change where they are written"),
        ("deluge", "its web interface can add downloads and change where they are written"),
        ("watchtower", "it can replace the image behind any container on this machine"),
        ("adminer", "it is a database console"),
        ("phpmyadmin", "it is a database console"),
        ("cockpit", "it is a host administration console"),
        ("code-server", "it runs arbitrary code on this machine by design")
    };

    /// <summary>
    /// Finds the applications worth offering, most obviously publishable first.
    /// </summary>
    /// <param name="containers">The container listing, as Docker reported it.</param>
    /// <returns>
    /// One entry per publishable application. Containers that are not running, publish no
    /// port, or publish only non-HTTP ports are left out, as is anything on the
    /// never-expose list.
    /// </returns>
    public static IReadOnlyList<DiscoveredApp> Find(IEnumerable<ContainerSummary> containers)
    {
        ArgumentNullException.ThrowIfNull(containers);

        var found = new List<DiscoveredApp>();

        foreach (var container in containers)
        {
            var app = Consider(container);
            if (app is not null)
            {
                found.Add(app);
            }
        }

        return Disambiguate(found);
    }

    private static DiscoveredApp? Consider(ContainerSummary container)
    {
        if (!string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = container.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var (risk, reason) = Classify(name, container.Image);
        if (risk == ExposureRisk.NeverExpose)
        {
            return null;
        }

        // A container may publish the same port on several addresses; the set collapses
        // those, and dropping UDP leaves only what a web proxy could serve.
        var published = container.Ports
            .Where(p => p.PublicPort is > 0 && string.Equals(p.Type, "tcp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.PublicPort!.Value)
            .ToDictionary(g => g.Key, g => g.First().PrivatePort);

        // Both ends of the mapping have to be checked. What the software speaks is
        // decided by the container-side port, and "51413:6881" or "2222:22" publishes it
        // on a host port that is in no well-known table at all.
        var serveable = published
            .Where(p => !_notHttp.ContainsKey(p.Key) && !_notHttp.ContainsKey(p.Value))
            .Select(p => p.Key)
            .OrderBy(p => p)
            .ToList();

        if (serveable.Count == 0)
        {
            return null;
        }

        var label = SuggestLabel(name);
        if (label.Length == 0)
        {
            return null;
        }

        return new DiscoveredApp
        {
            Container = name,
            Image = container.Image,
            Port = serveable[0],
            ContainerPort = published[serveable[0]],
            AlternatePorts = serveable.Skip(1).ToList(),
            SuggestedLabel = label,
            Risk = risk,
            RiskReason = reason
        };
    }

    private static (ExposureRisk Risk, string Reason) Classify(string name, string image)
    {
        var haystack = (name + " " + image).ToLowerInvariant();

        foreach (var (match, reason) in _neverExpose)
        {
            if (haystack.Contains(match, StringComparison.Ordinal))
            {
                return (ExposureRisk.NeverExpose, reason);
            }
        }

        foreach (var (match, reason) in _sensitive)
        {
            if (haystack.Contains(match, StringComparison.Ordinal))
            {
                return (ExposureRisk.Sensitive, reason);
            }
        }

        return (ExposureRisk.Ordinary, string.Empty);
    }

    /// <summary>
    /// Turns a container name into the host label to suggest for it.
    /// </summary>
    /// <remarks>
    /// A DNS label allows only letters, digits and hyphens, so anything else becomes a
    /// hyphen and runs of them collapse. The <c>-ui</c> and <c>-app</c> style suffixes
    /// people give containers are dropped, because <c>fogline.example.com</c> reads
    /// better than <c>fogline-ui.example.com</c> and the container name is an internal
    /// detail the visitor should not inherit.
    /// </remarks>
    internal static string SuggestLabel(string containerName)
    {
        var lowered = containerName.Trim().ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);

        foreach (var c in lowered)
        {
            if (c is >= 'a' and <= 'z' || c is >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var label = builder.ToString().Trim('-');

        // Only the suffixes that are plainly an artefact of naming a container. "-web"
        // and "-server" are parts of real product names — stripping them turned
        // "media-server" into "media" — so they stay.
        foreach (var suffix in new[] { "-ui", "-app" })
        {
            if (label.Length > suffix.Length && label.EndsWith(suffix, StringComparison.Ordinal))
            {
                label = label[..^suffix.Length];
                break;
            }
        }

        return Cap(label);
    }

    /// <summary>
    /// Trims a label to what DNS allows: 63 characters, never ending on a hyphen.
    /// </summary>
    /// <remarks>
    /// Every path that produces a label has to go through this. One illegal identifier
    /// fails the entire certificate order at the authority, not merely its own name, so
    /// a single over-long container name would cost the user every other application.
    /// </remarks>
    private static string Cap(string label)
        => label.Length <= 63 ? label : label[..63].TrimEnd('-');

    /// <summary>
    /// Keeps two applications from suggesting the same name.
    /// </summary>
    /// <remarks>
    /// Stripping suffixes can collide — <c>fogline</c> and <c>fogline-ui</c> reduce to the
    /// same label — and two identical names on one certificate would be silently
    /// de-duplicated into a front door where one application is unreachable. The loser
    /// keeps its full container name, which is unique by Docker's own rules.
    /// </remarks>
    private static IReadOnlyList<DiscoveredApp> Disambiguate(List<DiscoveredApp> apps)
    {
        var proposed = new List<(DiscoveredApp App, string Label)>(apps.Count);

        foreach (var group in apps.GroupBy(a => a.SuggestedLabel, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                var only = group.First();
                proposed.Add((only, only.SuggestedLabel));
                continue;
            }

            foreach (var app in group)
            {
                proposed.Add((app, SanitiseExact(app.Container)));
            }
        }

        // Falling back to the container name can collide in its own right: "foo-ui" and
        // "foo_ui" are both legal Docker names and both sanitise to "foo-ui". So a
        // counting suffix has the last word, and nothing leaves here sharing a label.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var settled = new List<DiscoveredApp>(apps.Count);

        foreach (var (app, label) in proposed)
        {
            var unique = label;
            var next = 2;

            while (!used.Add(unique))
            {
                unique = WithSuffix(label, "-" + next.ToString(CultureInfo.InvariantCulture));
                next++;
            }

            settled.Add(new DiscoveredApp
            {
                Container = app.Container,
                Image = app.Image,
                Port = app.Port,
                ContainerPort = app.ContainerPort,
                AlternatePorts = app.AlternatePorts,
                SuggestedLabel = unique,
                Risk = app.Risk,
                RiskReason = app.RiskReason
            });
        }

        // Ordinary applications first, then the ones that need a second thought, and
        // alphabetically within each so the list does not reshuffle between visits.
        return settled
            .OrderBy(a => (int)a.Risk)
            .ThenBy(a => a.SuggestedLabel, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Appends a distinguishing suffix, making room for it inside the 63-character limit
    /// rather than overrunning it.
    /// </summary>
    private static string WithSuffix(string label, string suffix)
    {
        var room = 63 - suffix.Length;
        var head = label.Length <= room ? label : label[..room].TrimEnd('-');
        return head + suffix;
    }

    private static string SanitiseExact(string containerName)
    {
        var builder = new StringBuilder(containerName.Length);

        foreach (var c in containerName.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' || c is >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return Cap(builder.ToString().Trim('-'));
    }
}
