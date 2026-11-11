using System.Globalization;

using Zyggy.Core.Configuration;
using Zyggy.Core.Envelope;
using Zyggy.Core.Health;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Registry;

/// <summary>
/// A machine's registry file, <c>tenants/&lt;org&gt;/registry/&lt;machine&gt;.yaml</c> (founding spec §4 "Registry file" as amended by
/// A14-1): a plain, unsigned YAML document of derived state. Serialised through the one front-matter emitter (keys ordinal at every
/// level, <c>\n</c>, UTF-8 without BOM, no <c>---</c>, optional keys omitted) and read with the same strictness as an envelope's front
/// matter. Equality is structural.
/// </summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Machine">The machine that publishes the file.</param>
/// <param name="Os">The machine's operating system.</param>
/// <param name="LastSeen">The liveness stamp (UTC, whole seconds); <see langword="null"/> omits the key.</param>
/// <param name="Health">The §11 health verdict (green, amber or red); <see langword="null"/> omits the key.</param>
/// <param name="WorkHours">The machine's work hours; <see langword="null"/> omits the key (Central).</param>
/// <param name="Projects">The projects, ordered by name (ordinal).</param>
public sealed record RegistryDocument(
    TenantId Tenant,
    MachineName Machine,
    RegistryOs Os,
    DateTimeOffset? LastSeen,
    HealthLevel? Health,
    WorkHoursSettings? WorkHours,
    IReadOnlyList<RegistryProject> Projects)
{
    /// <summary>The registry file's schema version; <see cref="Parse"/> refuses any other.</summary>
    public const int Schema = 1;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Serialises the document in its canonical form.</summary>
    /// <returns>The UTF-8 bytes of the file.</returns>
    /// <exception cref="ArgumentException">Thrown when <see cref="Health"/> is <see cref="HealthLevel.Down"/> (a caller bug: a node that publishes is up).</exception>
    public byte[] Serialize()
    {
        var root = new Dictionary<string, FrontMatterNode>(StringComparer.Ordinal)
        {
            ["machine"] = Scalar(Machine.Value),
            ["os"] = Scalar(Os.ToWire()),
            ["projects"] = new FrontMatterSequence(Projects.Select(ProjectNode)),
            ["schema"] = Scalar(Schema.ToString(CultureInfo.InvariantCulture)),
            ["tenant"] = Scalar(Tenant.Value),
        };

        if (Health is { } health)
        {
            root["health"] = Scalar(HealthWire(health));
        }

        if (LastSeen is { } lastSeen)
        {
            root["last_seen"] = Scalar(lastSeen.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
        }

        if (WorkHours is { } workHours)
        {
            root["work_hours"] = new FrontMatterMapping(new Dictionary<string, FrontMatterNode>(StringComparer.Ordinal)
            {
                ["days"] = new FrontMatterSequence(workHours.Days.Select(Scalar)),
                ["from"] = Scalar(workHours.From),
                ["timezone"] = Scalar(workHours.Timezone),
                ["to"] = Scalar(workHours.To),
            });
        }

        return FrontMatterEmitter.Emit(new FrontMatterMapping(root), includeSig: true);
    }

    /// <summary>Builds this machine's document from a scan: tenant, machine and work hours from <c>node.json</c>, the current OS.</summary>
    /// <param name="configuration">The node configuration.</param>
    /// <param name="scan">The scan.</param>
    /// <param name="lastSeen">The liveness stamp; truncated to whole seconds.</param>
    /// <param name="health">The health verdict, or <see langword="null"/> to omit it.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    public static RegistryDocument FromScan(NodeConfiguration configuration, ScanResult scan, DateTimeOffset lastSeen, HealthLevel? health)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scan);
        DateTimeOffset utc = lastSeen.ToUniversalTime();
        var seconds = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
        return new RegistryDocument(
            configuration.Tenant,
            configuration.Machine,
            RegistryOsExtensions.Current(),
            seconds,
            health,
            configuration.WorkHours,
            scan.Projects);
    }

    /// <summary>Parses a registry file. Never throws on input: every defect is a refusal naming the field.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The document, or a refusal.</returns>
    public static RegistryParse Parse(ReadOnlyMemory<byte> bytes)
    {
        EnvelopeRejection? rejection = FrontMatterReader.ReadDocument(bytes, out FrontMatterMapping root);
        return rejection is not null
            ? RegistryParse.Refused("root", rejection.Detail ?? "not a YAML mapping")
            : Reader.Read(root);
    }

    /// <summary>Compares everything but the liveness group (<see cref="LastSeen"/> and <see cref="Health"/>).</summary>
    /// <param name="other">The other document.</param>
    /// <returns><see langword="true"/> when the content is equal; lists are compared as given.</returns>
    public bool ContentEquals(RegistryDocument other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Equals(other with { LastSeen = LastSeen, Health = Health });
    }

    /// <summary>Determines whether both documents are structurally equal.</summary>
    /// <param name="other">The other document.</param>
    /// <returns><see langword="true"/> when every field and every list item is equal.</returns>
    public bool Equals(RegistryDocument? other) =>
        other is not null
        && Tenant == other.Tenant
        && Machine == other.Machine
        && Os == other.Os
        && LastSeen == other.LastSeen
        && Health == other.Health
        && WorkHoursEqual(WorkHours, other.WorkHours)
        && Projects.SequenceEqual(other.Projects);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Tenant);
        hash.Add(Machine);
        hash.Add(Os);
        hash.Add(LastSeen);
        hash.Add(Health);
        foreach (RegistryProject project in Projects)
        {
            hash.Add(project);
        }

        return hash.ToHashCode();
    }

    private static bool WorkHoursEqual(WorkHoursSettings? a, WorkHoursSettings? b) =>
        a is null || b is null
            ? a is null && b is null
            : string.Equals(a.Timezone, b.Timezone, StringComparison.Ordinal)
              && string.Equals(a.From, b.From, StringComparison.Ordinal)
              && string.Equals(a.To, b.To, StringComparison.Ordinal)
              && a.Days.SequenceEqual(b.Days, StringComparer.Ordinal);

    private static FrontMatterScalar Scalar(string value) => new(value);

    private static string HealthWire(HealthLevel level) => level switch
    {
        HealthLevel.Green => "green",
        HealthLevel.Amber => "amber",
        HealthLevel.Red => "red",
        _ => throw new ArgumentException("a registry file carries green, amber or red, never " + level, nameof(level)),
    };

    private static FrontMatterMapping ProjectNode(RegistryProject project)
    {
        var entries = new Dictionary<string, FrontMatterNode>(StringComparer.Ordinal)
        {
            ["agents"] = new FrontMatterSequence(project.Agents.Select(EntryNode)),
            ["name"] = Scalar(project.Name),
            ["skills"] = new FrontMatterSequence(project.Skills.Select(EntryNode)),
        };

        if (project.DefaultBranch is { } branch)
        {
            entries["default_branch"] = Scalar(branch);
        }

        if (project.Path is { } path)
        {
            entries["path"] = Scalar(path);
        }

        if (project.Summary is { } summary)
        {
            entries["summary"] = Scalar(summary);
        }

        return new FrontMatterMapping(entries);
    }

    private static FrontMatterMapping EntryNode(RegistryEntry entry) => new(new Dictionary<string, FrontMatterNode>(StringComparer.Ordinal)
    {
        ["description"] = Scalar(entry.Description),
        ["name"] = Scalar(entry.Name),
    });

    /// <summary>Reads the parsed tree into the records; unknown keys are ignored (additive fields within schema 1).</summary>
    private static class Reader
    {
        public static RegistryParse Read(FrontMatterMapping root)
        {
            if (!root.Entries.TryGetValue("schema", out FrontMatterNode? schemaNode))
            {
                return RegistryParse.Refused("schema", "missing");
            }

            if (schemaNode is not FrontMatterScalar { Value: var schemaText }
                || !int.TryParse(schemaText, NumberStyles.None, CultureInfo.InvariantCulture, out int schema))
            {
                return RegistryParse.Refused("schema", "not a number");
            }

            if (schema != Schema)
            {
                return RegistryParse.Refused("schema", $"unsupported schema {schema}; this reader knows {Schema}");
            }

            if (!TryScalar(root, "tenant", out string? tenantText) || !TenantId.TryParse(tenantText, out TenantId? tenant))
            {
                return RegistryParse.Refused("tenant", "missing or not a label");
            }

            if (!TryScalar(root, "machine", out string? machineText) || !MachineName.TryParse(machineText, out MachineName? machine))
            {
                return RegistryParse.Refused("machine", "missing or not a label");
            }

            if (!TryScalar(root, "os", out string? osText) || !RegistryOsExtensions.TryParseWire(osText, out RegistryOs os))
            {
                return RegistryParse.Refused("os", "missing or not windows, linux, macos or other");
            }

            DateTimeOffset? lastSeen = null;
            if (root.Entries.ContainsKey("last_seen"))
            {
                if (!TryScalar(root, "last_seen", out string? text)
                    || !DateTimeOffset.TryParseExact(text, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                {
                    return RegistryParse.Refused("last_seen", "not YYYY-MM-DDTHH:MM:SSZ");
                }

                lastSeen = parsed;
            }

            HealthLevel? health = null;
            if (root.Entries.ContainsKey("health"))
            {
                (bool ok, HealthLevel level) = TryScalar(root, "health", out string? text) ? text switch
                {
                    "green" => (true, HealthLevel.Green),
                    "amber" => (true, HealthLevel.Amber),
                    "red" => (true, HealthLevel.Red),
                    _ => (false, HealthLevel.Down),
                }
                : (false, HealthLevel.Down);
                if (!ok)
                {
                    return RegistryParse.Refused("health", "not green, amber or red");
                }

                health = level;
            }

            WorkHoursSettings? workHours = null;
            if (root.Entries.TryGetValue("work_hours", out FrontMatterNode? workNode))
            {
                if (workNode is not FrontMatterMapping work
                    || !work.Entries.TryGetValue("days", out FrontMatterNode? daysNode)
                    || daysNode is not FrontMatterSequence days
                    || days.Items.Any(d => d is not FrontMatterScalar)
                    || !TryScalar(work, "from", out string? from)
                    || !TryScalar(work, "timezone", out string? timezone)
                    || !TryScalar(work, "to", out string? to))
                {
                    return RegistryParse.Refused("work_hours", "needs days, from, timezone and to");
                }

                workHours = new WorkHoursSettings(timezone, [.. days.Items.Cast<FrontMatterScalar>().Select(d => d.Value)], from, to);
            }

            if (!root.Entries.TryGetValue("projects", out FrontMatterNode? projectsNode) || projectsNode is not FrontMatterSequence projectsSequence)
            {
                return RegistryParse.Refused("projects", "missing or not a sequence");
            }

            var projects = new List<RegistryProject>(projectsSequence.Items.Count);
            for (int i = 0; i < projectsSequence.Items.Count; i++)
            {
                RegistryParse? refusal = ReadProject(projectsSequence.Items[i], $"projects[{i}]", out RegistryProject? project);
                if (refusal is not null)
                {
                    return refusal;
                }

                projects.Add(project!);
            }

            return RegistryParse.Ok(new RegistryDocument(tenant, machine, os, lastSeen, health, workHours, projects));
        }

        private static RegistryParse? ReadProject(FrontMatterNode node, string field, out RegistryProject? project)
        {
            project = null;
            if (node is not FrontMatterMapping mapping)
            {
                return RegistryParse.Refused(field, "not a mapping");
            }

            if (!TryScalar(mapping, "name", out string? name) || !Label.IsValid(name))
            {
                return RegistryParse.Refused(field + ".name", "missing or not a label");
            }

            string? path = null, branch = null, summary = null;
            if (mapping.Entries.ContainsKey("path") && !TryScalar(mapping, "path", out path))
            {
                return RegistryParse.Refused(field + ".path", "not a scalar");
            }

            if (mapping.Entries.ContainsKey("default_branch") && !TryScalar(mapping, "default_branch", out branch))
            {
                return RegistryParse.Refused(field + ".default_branch", "not a scalar");
            }

            if (mapping.Entries.ContainsKey("summary") && !TryScalar(mapping, "summary", out summary))
            {
                return RegistryParse.Refused(field + ".summary", "not a scalar");
            }

            RegistryParse? refusal = ReadEntries(mapping, "agents", field, out List<RegistryEntry> agents);
            if (refusal is not null)
            {
                return refusal;
            }

            refusal = ReadEntries(mapping, "skills", field, out List<RegistryEntry> skills);
            if (refusal is not null)
            {
                return refusal;
            }

            project = new RegistryProject(name, path, branch, agents, skills, summary);
            return null;
        }

        private static RegistryParse? ReadEntries(FrontMatterMapping project, string key, string field, out List<RegistryEntry> entries)
        {
            entries = [];
            if (!project.Entries.TryGetValue(key, out FrontMatterNode? node))
            {
                return null;
            }

            if (node is not FrontMatterSequence sequence)
            {
                return RegistryParse.Refused($"{field}.{key}", "not a sequence");
            }

            for (int j = 0; j < sequence.Items.Count; j++)
            {
                string entryField = $"{field}.{key}[{j}]";
                if (sequence.Items[j] is not FrontMatterMapping entry)
                {
                    return RegistryParse.Refused(entryField, "not a mapping");
                }

                if (!TryScalar(entry, "name", out string? name) || name.Length == 0)
                {
                    return RegistryParse.Refused(entryField + ".name", "missing or empty");
                }

                string description = string.Empty;
                if (entry.Entries.ContainsKey("description") && !TryScalar(entry, "description", out description!))
                {
                    return RegistryParse.Refused(entryField + ".description", "not a scalar");
                }

                if (description.Length > RegistryEntry.MaxDescriptionLength)
                {
                    return RegistryParse.Refused(entryField + ".description", $"longer than {RegistryEntry.MaxDescriptionLength} characters");
                }

                entries.Add(new RegistryEntry(name, description));
            }

            return null;
        }

        private static bool TryScalar(FrontMatterMapping mapping, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = mapping.Entries.TryGetValue(key, out FrontMatterNode? node) && node is FrontMatterScalar scalar ? scalar.Value : null;
            return value is not null;
        }
    }
}
