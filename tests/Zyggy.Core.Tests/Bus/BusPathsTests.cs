using System.Reflection;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 04 AC-1: every bus path comes from <see cref="BusPaths"/> and parses back.</summary>
public sealed class BusPathsTests
{
    private const string Ulid = "01J8Y3N7Q2X9Z4A5B6C7D8E9F0";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly EnvelopeId Id = EnvelopeId.Parse(Ulid);

    [Fact]
    public void Nodes_ArchiveRoot_Shapes()
    {
        // Assert: plan 08 Step 6 — the folders above the machine and the archive's root, built in the one place
        BusPaths.Tenants.Should().Be("tenants");
        BusPaths.Nodes(Acme).Should().Be("tenants/acme/nodes");
        BusPaths.ArchiveRoot(Acme).Should().Be("tenants/acme/archive");
        BusPaths.RegistryFolder(Acme).Should().Be("tenants/acme/registry");
    }

    [Fact]
    public void TryParse_NodesFolder_False()
    {
        // Act: a folder above the machine is not a §4 location
        bool parsed = BusPaths.TryParse(BusPaths.Nodes(Acme), out _);

        // Assert
        parsed.Should().BeFalse();
    }

    public static TheoryData<string, string> SpecPaths => new()
    {
        { "Tenant", "tenants/acme" },
        { "Registry", "tenants/acme/registry/home-laptop.yaml" },
        { "Jobs", "tenants/acme/nodes/home-laptop/jobs" },
        { "Claimed", "tenants/acme/nodes/home-laptop/jobs/claimed" },
        { "Rejected", "tenants/acme/nodes/home-laptop/jobs/rejected" },
        { "Reports", "tenants/acme/nodes/home-laptop/reports" },
        { "Context", "tenants/acme/nodes/home-laptop/context" },
        { "Archive", "tenants/acme/archive/2026-09" },
        { "EnvelopeFile", "tenants/acme/nodes/home-laptop/jobs/" + Ulid + ".md" },
        { "Protocol", "PROTOCOL.md" },
    };

    public static TheoryData<string, string, BusFolder, bool> RoundTrips => new()
    {
        { "Registry", "home-laptop", BusFolder.Registry, false },
        { "Jobs", "home-laptop", BusFolder.Jobs, false },
        { "Claimed", "home-laptop", BusFolder.Claimed, false },
        { "Rejected", "home-laptop", BusFolder.Rejected, false },
        { "Reports", "home-laptop", BusFolder.Reports, false },
        { "Context", "home-laptop", BusFolder.Context, false },
        { "Archive", "", BusFolder.Archive, false },
        { "JobsFile", "home-laptop", BusFolder.Jobs, true },
        { "ClaimedFile", "home-laptop", BusFolder.Claimed, true },
        { "RejectedFile", "home-laptop", BusFolder.Rejected, true },
        { "ReportsFile", "home-laptop", BusFolder.Reports, true },
        { "ContextFile", "home-laptop", BusFolder.Context, true },
        { "ArchiveFile", "", BusFolder.Archive, true },
    };

    public static TheoryData<string> Refused => new()
    {
        "tenants/acme/policy.yaml",
        "../x",
        "tenants/Acme/nodes/home-laptop/jobs/" + Ulid + ".md",
        "tenants/acme/nodes/home-laptop/jobs/" + Ulid + ".txt",
        "tenants/acme/nodes/home-laptop/jobs/claimed/rejected/" + Ulid + ".md",
        "tenants\\acme\\nodes\\home-laptop\\jobs",
        "",
        "tenants/acme",
        "PROTOCOL.md",
        "tenants/acme/nodes/home-laptop",
        "tenants/acme/nodes/home-laptop/jobs/" + Ulid.ToLowerInvariant() + ".md",
        "tenants/acme/archive/2026-9",
        "/tenants/acme/nodes/home-laptop/jobs",
        "tenants/acme/nodes/home-laptop/jobs/",
    };

    [Theory]
    [MemberData(nameof(SpecPaths))]
    public void Build_EveryMember_EqualsSpecPath(string member, string expected)
    {
        // Act
        string path = Build(member);

        // Assert
        path.Should().Be(expected);
    }

    [Fact]
    public void Build_NeverContainsBackslash()
    {
        // Act
        string[] members = ["Tenant", "Registry", "Jobs", "Claimed", "Rejected", "Reports", "Context", "Archive", "EnvelopeFile", "Protocol"];
        var paths = members.Select(Build).ToList();

        // Assert
        paths.Should().OnlyContain(p => !p.Contains('\\'));
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void TryParse_ProducedPath_RoundTrips(string member, string machine, BusFolder folder, bool withId)
    {
        // Arrange
        string path = Build(member);
        MachineName? expectedMachine = machine.Length == 0 ? null : MachineName.Parse(machine);

        // Act
        bool ok = BusPaths.TryParse(path, out BusLocation? location);

        // Assert
        ok.Should().BeTrue(path);
        location.Should().Be(new BusLocation(Acme, expectedMachine, folder, withId ? Id : null));
    }

    [Fact]
    public void TryParse_OtherTenantPath_YieldsThatTenant()
    {
        // Act
        bool ok = BusPaths.TryParse("tenants/globex/nodes/x/jobs/" + Ulid + ".md", out BusLocation? location);

        // Assert
        ok.Should().BeTrue();
        location!.Tenant.Should().Be(TenantId.Parse("globex"));
        location.Machine.Should().Be(MachineName.Parse("x"));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void TryParse_Refused_ReturnsFalse(string relative)
    {
        // Act
        bool ok = BusPaths.TryParse(relative, out BusLocation? location);

        // Assert
        ok.Should().BeFalse(relative);
        location.Should().BeNull();
    }

    [Fact]
    public void Resolve_JoinsWithOsSeparator()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "bus");

        // Act
        string full = BusPaths.Resolve(root, BusPaths.Jobs(Acme, HomeLaptop));

        // Assert
        full.Should().Be(Path.Combine(root, "tenants", "acme", "nodes", "home-laptop", "jobs"));
    }

    [Fact]
    public void Api_HasNoParameterlessOrStringTenantOverload()
    {
        // Arrange: only the file-system join and the folder of EnvelopeFile take raw strings; tenants and machines are typed.
        string[] allowedStringParameters = ["checkoutRoot", "relative", "folder"];

        // Act
        var methods = typeof(BusPaths).GetMethods(BindingFlags.Public | BindingFlags.Static);
        var stringParameters = methods.SelectMany(m => m.GetParameters()).Where(p => p.ParameterType == typeof(string)).Select(p => p.Name).ToList();
        var parameterless = methods.Where(m => m.GetParameters().Length == 0 && !m.IsSpecialName).Select(m => m.Name).ToList();
        var tenantParameters = methods.SelectMany(m => m.GetParameters()).Where(p => p.Name == "tenant").Select(p => p.ParameterType).Distinct().ToList();

        // Assert
        stringParameters.Should().OnlyContain(name => allowedStringParameters.Contains(name));
        parameterless.Should().BeEmpty();
        tenantParameters.Should().Equal(typeof(TenantId));
    }

    private static string Build(string member) => member switch
    {
        "Tenant" => BusPaths.Tenant(Acme),
        "Registry" => BusPaths.Registry(Acme, HomeLaptop),
        "Jobs" => BusPaths.Jobs(Acme, HomeLaptop),
        "Claimed" => BusPaths.Claimed(Acme, HomeLaptop),
        "Rejected" => BusPaths.Rejected(Acme, HomeLaptop),
        "Reports" => BusPaths.Reports(Acme, HomeLaptop),
        "Context" => BusPaths.Context(Acme, HomeLaptop),
        "Archive" => BusPaths.Archive(Acme, 2026, 9),
        "EnvelopeFile" or "JobsFile" => BusPaths.EnvelopeFile(BusPaths.Jobs(Acme, HomeLaptop), Id),
        "ClaimedFile" => BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, HomeLaptop), Id),
        "RejectedFile" => BusPaths.EnvelopeFile(BusPaths.Rejected(Acme, HomeLaptop), Id),
        "ReportsFile" => BusPaths.EnvelopeFile(BusPaths.Reports(Acme, HomeLaptop), Id),
        "ContextFile" => BusPaths.EnvelopeFile(BusPaths.Context(Acme, HomeLaptop), Id),
        "ArchiveFile" => BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 9), Id),
        "Protocol" => BusPaths.Protocol,
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };
}
