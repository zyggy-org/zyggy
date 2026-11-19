using System.IO.Enumeration;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Bus;
using Zyggy.Core.Configuration;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.Tests.Configuration;

/// <summary>
/// Spec 55 slice 3: a Central-shaped temp tree for the settings verb — a <c>node.json</c>, a bus checkout with registry files, a parent folder
/// holding <c>instance/</c> and <c>home/</c> (both with <c>.claude/</c>), and the owner's memory folder. The project scan is a fake over the
/// file system (folders holding <c>.claude/</c> directly under a root, the owner's exclude globs applied).
/// </summary>
internal sealed class SettingsTree : IDisposable
{
    public SettingsTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "zyggy-settings-" + Guid.NewGuid().ToString("N"));
        Bus = Directory.CreateDirectory(Path.Combine(Root, "bus")).FullName;
        Parent = Directory.CreateDirectory(Path.Combine(Root, "srv")).FullName;
        Instance = Directory.CreateDirectory(Path.Combine(Parent, "instance", ".claude")).Parent!.FullName;
        Home = Directory.CreateDirectory(Path.Combine(Parent, "home", ".claude")).Parent!.FullName;
        Other = Directory.CreateDirectory(Path.Combine(Root, "repos")).FullName;
        Directory.CreateDirectory(Path.Combine(Other, "calizr", ".claude"));
        MemoryRoot = Directory.CreateDirectory(Path.Combine(Root, "memory")).FullName;
        Principal = Directory.CreateDirectory(Path.Combine(MemoryRoot, "acme", "alice")).FullName;
        Business = Directory.CreateDirectory(Path.Combine(Principal, "business")).FullName;
        Directory.CreateDirectory(Path.Combine(Business, "topics"));
        foreach (string part in new[] { "private", "inbox", "daily", "archive", ".git" })
        {
            Directory.CreateDirectory(Path.Combine(Principal, part));
        }

        Registry("central");
        Registry("home-laptop");
        Registry("work-laptop");
        Config = Path.Combine(Root, "node.json");
    }

    public string Root { get; }

    public string Bus { get; }

    public string Parent { get; }

    public string Instance { get; }

    public string Home { get; }

    public string Other { get; }

    public string MemoryRoot { get; }

    public string Principal { get; }

    public string Business { get; }

    public string Config { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

    public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

    public string BackupPath => Config + ".bak-20261119T090000Z";

    public static string J(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    public void WithMemory()
    {
        Environment["ZYGGY_MEMORY_ROOT"] = MemoryRoot;
        Environment["ZYGGY_TENANT"] = "acme";
        Environment["ZYGGY_USER"] = "alice";
    }

    /// <summary>Writes <c>node.json</c>: the minimum keys for <paramref name="machine"/>, then <paramref name="extra"/> (JSON members, comma-separated).</summary>
    public string Write(string machine = "central", string? extra = null)
    {
        string text = $$"""
            {
              "tenant": "acme",
              "machine": "{{machine}}",
              "signWith": "acme/1",
              "bus": { "owner": "acme", "repo": "zyggy-bus", "localPath": "{{J(Bus)}}", "remote": "https://example.invalid/bus.git" }{{(extra is null ? string.Empty : ",\n  " + extra)}}
            }
            """;
        File.WriteAllText(Config, text);
        return Config;
    }

    /// <summary>A file with the parent as root, <c>home</c> excluded and <c>instance</c> as the supervision project.</summary>
    public string WriteCentralShaped(string? supervisionExtra = null) =>
        Write(extra: $$"""
            "devRoots": ["{{J(Parent)}}"],
              "discovery": { "exclude": ["home"] },
              "supervision": { "project": "instance"{{(supervisionExtra is null ? string.Empty : ", " + supervisionExtra)}} }
            """);

    public SettingsVerb Verb() => new(Environment, Clock, ScanAsync);

    public async Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter { NewLine = "\n" };
        using var error = new StringWriter { NewLine = "\n" };
        int exit = await Verb().RunAsync([.. args, "--config", Config], new VerbIo(TextReader.Null, output, error, false), CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static Task<IReadOnlyList<SettingsProject>> ScanAsync(NodeConfiguration configuration, CancellationToken cancellationToken)
    {
        var found = new List<SettingsProject>();
        foreach (string root in configuration.DevRoots.Where(Directory.Exists))
        {
            foreach (DirectoryInfo child in new DirectoryInfo(root).EnumerateDirectories())
            {
                bool excluded = configuration.Discovery.Exclude.Any(glob => FileSystemName.MatchesSimpleExpression(glob, child.Name, ignoreCase: true));
                if (!excluded && Directory.Exists(Path.Combine(child.FullName, ".claude")))
                {
                    found.Add(new SettingsProject(child.Name, child.FullName));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<SettingsProject>>(found);
    }

    private void Registry(string machine)
    {
        string path = Path.Combine(Bus, BusPaths.Registry(TenantId.Parse("acme"), MachineName.Parse(machine)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "machine: " + machine + "\n");
    }
}
