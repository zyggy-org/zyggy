using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// A scratch memory tree <c>memory/acme/alice/</c> for Central's pickup (plan 13): a profile, an empty <c>inbox/</c> and today's
/// daily file, the pickup configuration over it (UTC, the golden secret patterns) and the five <c>ZYGGY_*</c> variables of the
/// drop-in. No <c>.git</c>: the pickup never runs git here. Deleted on dispose.
/// </summary>
internal sealed class PickupMemory : IDisposable
{
    public static readonly Principal Alice = new(TenantId.Parse("acme"), UserId.Parse("alice"));

    public PickupMemory()
    {
        Root = Path.Combine(Path.GetTempPath(), "zyggy-it", Guid.NewGuid().ToString("N"), "memory");
        Paths = new MemoryPaths(Root, Alice);
        Directory.CreateDirectory(Paths.InboxDirectory);
        Directory.CreateDirectory(Paths.DailyDirectory);
        File.WriteAllText(Paths.Profile, "---\nname: profile\ndescription: who alice is\nupdated: 2026-10-01\n---\n- Alice works on calizr\n");
        File.WriteAllText(Paths.Daily(DateOnly.FromDateTime(DateTime.UtcNow)), "- a daily line\n");
    }

    public static string PatternsPath => Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt");

    public string Root { get; }

    public MemoryPaths Paths { get; }

    public PickupConfigurationResult Configuration =>
        PickupConfigurationResult.For(Paths, TimeZoneInfo.Utc, SecretPatterns.Load(PatternsPath).Patterns!);

    public IReadOnlyList<string> InboxFiles() =>
        [.. Directory.EnumerateFiles(Paths.InboxDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(n => n!)];

    public string InboxText(string name) => File.ReadAllText(Paths.InboxFile(name));

    public List<(string Path, string Sha256)> FingerprintOutsideInbox() =>
        [.. TreeFingerprint.Of(Root).Where(f => !f.Path.Contains("/inbox/", StringComparison.Ordinal))];

    /// <summary>The drop-in's variables, for the production host as <c>central</c> (Steps 6 and 14).</summary>
    public Dictionary<string, string?> Env() => new()
    {
        ["ZYGGY_MEMORY_ROOT"] = Root,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["ZYGGY_SECRET_PATTERNS"] = PatternsPath,
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is harmless.
        }
    }
}
