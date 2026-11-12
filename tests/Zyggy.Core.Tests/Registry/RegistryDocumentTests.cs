using System.Text;

using Zyggy.Core.Configuration;
using Zyggy.Core.Health;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

public sealed class RegistryDocumentTests
{
    private static readonly DateTimeOffset LastSeen = new(2026, 9, 27, 14, 0, 0, TimeSpan.Zero);

    internal static RegistryDocument HomeLaptop() => new(
        TenantId.Parse("acme"),
        MachineName.Parse("home-laptop"),
        RegistryOs.Windows,
        LastSeen,
        HealthLevel.Green,
        new WorkHoursSettings("Europe/Brussels", ["Mon", "Tue", "Wed", "Thu", "Fri"], "08:00", "19:00"),
        [
            new RegistryProject(
                "calizr",
                "C:/src/calizr",
                "main",
                [
                    new RegistryEntry("env-debugger", "Diagnoses failing environments: config, secrets, connectivity, startup errors"),
                    new RegistryEntry("db-migrator", "Writes and verifies EF Core migrations against staging"),
                ],
                [new RegistryEntry("deploy-staging", "Builds, tags and deploys the current branch to staging")],
                "Multi-tenant booking SaaS, .NET/Blazor"),
        ]);

    internal static RegistryDocument Central() => new(
        TenantId.Parse("acme"),
        MachineName.Central,
        RegistryOs.Linux,
        LastSeen,
        null,
        null,
        []);

    private static byte[] GoldenRegistry(string name) => File.ReadAllBytes(Path.Combine(Golden.Directory, "registry", name + ".yaml"));

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void Serialize_FullDocument_EqualsGoldenHomeLaptop()
    {
        // Act
        byte[] bytes = HomeLaptop().Serialize();

        // Assert
        Text(bytes).Should().Be(Text(GoldenRegistry("home-laptop")));
        bytes.Should().Equal(GoldenRegistry("home-laptop"));
    }

    [Fact]
    public void Serialize_MinimumCentral_EqualsGoldenCentral()
    {
        // Act
        byte[] bytes = Central().Serialize();

        // Assert
        Text(bytes).Should().Be(Text(GoldenRegistry("central")));
        bytes.Should().Equal(GoldenRegistry("central"));
    }

    [Fact]
    public void Serialize_Twice_ByteIdentical()
    {
        // Arrange
        RegistryDocument document = HomeLaptop();

        // Act
        byte[] first = document.Serialize();

        // Assert
        document.Serialize().Should().Equal(first);
    }

    [Fact]
    public void Serialize_TopLevelKeysOrdinal()
    {
        // Act
        string[] keys = [.. Text(HomeLaptop().Serialize())
            .Split('\n')
            .Where(l => l.Length > 0 && l[0] != ' ')
            .Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)])];

        // Assert
        keys.Should().Equal("health", "last_seen", "machine", "os", "projects", "schema", "tenant", "work_hours");
    }

    [Fact]
    public void Serialize_NoBomNoCrNoDelimiterNoNull()
    {
        // Act
        byte[] bytes = HomeLaptop().Serialize();
        string text = Text(bytes);

        // Assert
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        text.Should().NotContain("\r");
        text.Should().NotStartWith("---");
        text.Should().NotContain(": null").And.NotContain(": ~");
        text.Should().Contain("schema: 1\n");
    }

    [Fact]
    public void Serialize_AbsentOptionalKeys_Omitted()
    {
        // Arrange
        var document = Central() with
        {
            Projects = [new RegistryProject("calizr", null, null, [], [new RegistryEntry("deploy", "")], null)],
        };

        // Act
        string text = Text(document.Serialize());

        // Assert
        text.Should().NotContain("path:").And.NotContain("default_branch:").And.NotContain("summary:");
        text.Should().NotContain("health:").And.NotContain("work_hours:");
        text.Should().Contain("agents: []");
        text.Should().Contain("description: ''");
    }

    [Fact]
    public void Serialize_HealthDown_Throws()
    {
        // Arrange
        var document = HomeLaptop() with { Health = HealthLevel.Down };

        // Act
        Action act = () => document.Serialize();

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("home-laptop")]
    [InlineData("central")]
    public void Parse_SerializeRoundTrip_EqualsOriginal(string golden)
    {
        // Arrange
        RegistryDocument original = golden == "central" ? Central() : HomeLaptop();

        // Act
        RegistryParse parse = RegistryDocument.Parse(original.Serialize());

        // Assert
        parse.IsOk.Should().BeTrue(parse.Detail);
        parse.Document.Should().Be(original);
        parse.Document!.Serialize().Should().Equal(GoldenRegistry(golden));
    }

    [Fact]
    public void Parse_MissingOptionalKey_NotARefusal()
    {
        // Arrange
        byte[] bytes = Encoding.UTF8.GetBytes("machine: home-laptop\nos: windows\nprojects:\n  - agents: []\n    name: calizr\n    skills: []\nschema: 1\ntenant: acme\n");

        // Act
        RegistryParse parse = RegistryDocument.Parse(bytes);

        // Assert
        parse.IsOk.Should().BeTrue(parse.Detail);
        parse.Document!.LastSeen.Should().BeNull();
        parse.Document.Health.Should().BeNull();
        parse.Document.Projects[0].Path.Should().BeNull();
    }

    public static TheoryData<string, string> Refusals()
    {
        const string Valid = "machine: home-laptop\nos: windows\nprojects: []\nschema: 1\ntenant: acme\n";
        var unrelated = new StringBuilder();
        for (int i = 0; unrelated.Length < 50 * 1024; i++)
        {
            unrelated.Append(System.Globalization.CultureInfo.InvariantCulture, $"key{i:D6}: some unrelated value number {i}\n");
        }

        return new TheoryData<string, string>
        {
            { Valid.Replace("schema: 1", "schema: 2", StringComparison.Ordinal), "schema" },
            { "- a\n- b\n", "root" },
            { Valid.Replace("tenant: acme", "tenant: Acme", StringComparison.Ordinal), "tenant" },
            { Valid.Replace("projects: []", "projects:\n  - agents: []\n    skills: []", StringComparison.Ordinal), "projects[0].name" },
            {
                Valid.Replace("projects: []", "projects:\n  - agents:\n      - description: " + new string('x', 151) + "\n        name: a\n    name: calizr\n    skills: []", StringComparison.Ordinal),
                "projects[0].agents[0].description"
            },
            { "a: &x 1\nb: *x\n", "root" },
            { Valid + "---\nschema: 1\n", "root" },
            { unrelated.ToString(), "schema" },
        };
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Parse_Refusals_FieldNamed(string yaml, string field)
    {
        // Act
        RegistryParse parse = RegistryDocument.Parse(Encoding.UTF8.GetBytes(yaml));

        // Assert
        parse.Document.Should().BeNull();
        parse.Field.Should().Be(field);
        parse.Detail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_NotUtf8_RootRefusal()
    {
        // Act
        RegistryParse parse = RegistryDocument.Parse(new byte[] { 0x73, 0x3A, 0x20, 0xC3, 0x28, 0x0A });

        // Assert
        parse.Document.Should().BeNull();
        parse.Field.Should().Be("root");
    }

    [Fact]
    public void ContentEquals_DifferOnlyInLastSeenAndHealth_True()
    {
        // Arrange
        RegistryDocument a = HomeLaptop();
        RegistryDocument b = a with { LastSeen = LastSeen.AddHours(7), Health = HealthLevel.Red };

        // Act + Assert
        a.ContentEquals(b).Should().BeTrue();
    }

    [Fact]
    public void ContentEquals_OneDescriptionDiffers_False()
    {
        // Arrange
        RegistryDocument a = HomeLaptop();
        RegistryProject project = a.Projects[0];
        RegistryDocument b = a with
        {
            Projects = [project with { Skills = [new RegistryEntry("deploy-staging", "Deploys to staging")] }],
        };

        // Act + Assert
        a.ContentEquals(b).Should().BeFalse();
    }

    [Fact]
    public void ContentEquals_ProjectOrderDiffers_False()
    {
        // Arrange
        RegistryProject other = new("billing", "C:/src/billing", "main", [], [], null);
        RegistryDocument a = HomeLaptop() with { Projects = [HomeLaptop().Projects[0], other] };
        RegistryDocument b = HomeLaptop() with { Projects = [other, HomeLaptop().Projects[0]] };

        // Act + Assert
        a.ContentEquals(b).Should().BeFalse("documents are compared as given; the scanner sorts");
        a.ContentEquals(HomeLaptop() with { Projects = [HomeLaptop().Projects[0], other] }).Should().BeTrue();
    }
}
