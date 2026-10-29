using System.Globalization;
using System.Text;
using System.Text.Json;

using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Hub;

/// <summary>
/// A scratch memory root for <c>acme/alice</c> (spec 11 AC-20), written inline: two durable files that match <c>zyggy</c>, one that does
/// not, daily files dated from the real clock (today, yesterday, and one 40 days old), a monthly roll-up, and the excluded places
/// (inbox, auto, profile) all holding the topic word. The environment names the principal, UTC and the golden secret patterns, and
/// removes <c>CLAUDE_PROJECT_DIR</c> so the runner's own project never fills a variable.
/// </summary>
internal sealed class HubFixture : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public HubFixture()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Write("business/areas/zyggy.md",
            "---\nname: zyggy\naliases: [zyggy, zyggy platform]\ndescription: The platform Alice builds\nupdated: 2026-10-20\n---\n"
            + "- [stated] 2026-10-20: Goal: an assistant on central.\n");
        Write("private/people/carol.md",
            "---\nname: carol\ndescription: Carol, who fixes the zyggy roof\nupdated: 2026-10-25\n---\n- [stated] 2026-10-25: Carol is my sister.\n");
        Write("private/areas/roof.md", "---\nname: roof\nupdated: 2026-10-26\n---\n- [stated] 2026-10-26: New tiles in spring.\n");
        Write($"daily/{Date(today)}.md", "- [observed] 09:15 session 1a2b3c4d: zyggy hub planned\n- lunch\n");
        Write($"daily/{Date(today.AddDays(-1))}.md", "- [observed] 18:00 session 5e6f7a8b: roof call with carol\n");
        Write($"daily/{Date(today.AddDays(-40))}.md", "- zyggy too old\n");
        Write($"daily/{today.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.md", "- zyggy roll-up\n");
        Write("inbox/remember-2026-10-01.md", "- [stated] 2026-10-01: zyggy in the inbox\n");
        Write("auto/MEMORY.md", "- zyggy auto\n");
        Write("profile.md", "zyggy profile\n");
    }

    public static string PatternsPath => Path.Combine(AppContext.BaseDirectory, "golden", "secret-patterns", "secret-patterns.txt");

    public string Root => _scratch.Path;

    public string MemoryRoot => Path.Combine(Root, "memory");

    public string PrincipalDirectory => Path.Combine(MemoryRoot, "acme", "alice");

    public string InboxDirectory => Path.Combine(PrincipalDirectory, "inbox");

    public Dictionary<string, string?> Env() => new(StringComparer.Ordinal)
    {
        ["ZYGGY_MEMORY_ROOT"] = MemoryRoot,
        ["ZYGGY_TENANT"] = "acme",
        ["ZYGGY_USER"] = "alice",
        ["ZYGGY_TIMEZONE"] = "UTC",
        ["ZYGGY_SECRET_PATTERNS"] = PatternsPath,
        ["CLAUDE_PROJECT_DIR"] = null,
    };

    public List<(string Path, string Sha256)> Fingerprint() => TreeFingerprint.Of(MemoryRoot);

    /// <summary>A scratch checkout whose <c>.claude/settings.local.json</c> names the principal and whose hooks hold the patterns.</summary>
    public string CheckoutWithSettings()
    {
        var checkout = Path.Combine(Root, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, ".claude", "hooks"));
        File.Copy(PatternsPath, Path.Combine(checkout, ".claude", "hooks", "secret-patterns.txt"), overwrite: true);
        var settings = new Dictionary<string, Dictionary<string, string>>
        {
            ["env"] = new()
            {
                ["ZYGGY_MEMORY_ROOT"] = MemoryRoot,
                ["ZYGGY_TENANT"] = "acme",
                ["ZYGGY_USER"] = "alice",
                ["ZYGGY_TIMEZONE"] = "UTC",
            },
        };
        File.WriteAllText(Path.Combine(checkout, ".claude", "settings.local.json"), JsonSerializer.Serialize(settings));
        return checkout;
    }

    public void Dispose() => _scratch.Dispose();

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Write(string relative, string content)
    {
        var full = Path.Combine(PrincipalDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(content));
    }
}
