using Zyggy.Core.Configuration;
using Zyggy.Core.Memory;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// Central's memory for the item path of the pickup (plan 41 Step 10): a <see cref="MemoryRepoFixture"/> (clone + bare remote, principal
/// <c>acme/alice</c>) seeded with a profile and pushed, an instance directory holding <c>archive.json</c>, and the pickup configuration
/// loaded through the production <see cref="PickupConfiguration.Load"/> from the drop-in's variables plus <c>ZYGGY_INSTANCE_DIR</c>.
/// <see cref="PickupMemory"/> stays as it is for the spec 13 tests (no <c>.git</c>, no archive).
/// </summary>
internal sealed class PickupArchiveMemory : IAsyncLifetime
{
    private readonly string _archiveJson;

    public PickupArchiveMemory(string archiveJson = "{}")
    {
        _archiveJson = archiveJson;
    }

    public MemoryRepoFixture Repo { get; } = new();

    public string InstanceDir => Path.Combine(Repo.RootDir, "instance");

    public string HomeDir => Path.Combine(Repo.RootDir, "home");

    public MemoryPaths Paths => new(Repo.CloneDir, PickupMemory.Alice);

    public async ValueTask InitializeAsync()
    {
        await Repo.InitializeAsync();
        Directory.CreateDirectory(InstanceDir);
        Directory.CreateDirectory(HomeDir);
        await File.WriteAllTextAsync(Path.Combine(InstanceDir, "archive.json"), _archiveJson);
        Directory.CreateDirectory(Paths.InboxDirectory);
        await File.WriteAllTextAsync(Paths.Profile, "---\nname: profile\ndescription: who alice is\nupdated: 2026-10-01\n---\n- Alice works on calizr\n");
        await Repo.GitAsync(Repo.CloneDir, ["add", "--", "acme/alice/profile.md"], CancellationToken.None);
        await Repo.GitAsync(Repo.CloneDir, ["commit", "-q", "-m", "seed"], CancellationToken.None);
        await Repo.GitAsync(Repo.CloneDir, ["push", "-q", "origin", "HEAD:main"], CancellationToken.None);
    }

    public ValueTask DisposeAsync() => Repo.DisposeAsync();

    /// <summary>The drop-in's five variables, <c>ZYGGY_INSTANCE_DIR</c> and a <c>HOME</c> under the fixture (deterministic deny list).</summary>
    public Dictionary<string, string?> Env(bool withInstanceDir = true)
    {
        var env = new Dictionary<string, string?>
        {
            ["ZYGGY_MEMORY_ROOT"] = Repo.CloneDir,
            ["ZYGGY_TENANT"] = "acme",
            ["ZYGGY_USER"] = "alice",
            ["ZYGGY_TIMEZONE"] = "UTC",
            ["ZYGGY_SECRET_PATTERNS"] = PickupMemory.PatternsPath,
            ["HOME"] = HomeDir,
            ["USERPROFILE"] = HomeDir,
        };
        if (withInstanceDir)
        {
            env["ZYGGY_INSTANCE_DIR"] = InstanceDir;
        }

        return env;
    }

    /// <summary>The pickup configuration as the node loads it.</summary>
    public PickupConfigurationResult Configuration(NodeConfiguration node, bool withInstanceDir = true)
    {
        PickupConfigurationResult result = PickupConfiguration.Load(Env(withInstanceDir), node);
        return result.Error is null ? result : throw new InvalidOperationException("pickup configuration: " + result.Error);
    }

    /// <summary>The principal-relative file's text in the clone.</summary>
    public string Text(string relative) => File.ReadAllText(Path.Combine(Repo.PrincipalDir, relative));

    public bool Exists(string relative) => File.Exists(Path.Combine(Repo.PrincipalDir, relative));
}
