using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-27 (unit): the legacy layout moves once, byte-identical, into sides and categories, or is refused as a whole.</summary>
public sealed class MigratorTests : IDisposable
{
    private const string Zyggy = "---\nname: Zyggy\ndescription: Zyggy, the platform\nupdated: 2026-09-25\n---\n- [stated] 2026-09-25: Goal: an assistant.\n";
    private const string Redis = "---\nname: riziv-redis\ndescription: Redis at RIZIV\nupdated: 2026-09-25\n---\n- [stated] 2026-09-25: I run the Redis upgrade.\n";
    private const string Carol = "---\nname: carol\ndescription: \"Carol, Alice's sister\"\nupdated: 2026-09-28\n---\n- [stated] 2026-09-28: Carol is my sister.\r\n";
    private const string Tea = "- [stated] 2026-09-20: I like green tea.\n";

    private readonly MemoryTree _tree = new(
        ("profile.md", "- [stated] 2026-09-18: Alice.\n"),
        ("areas/zyggy.md", Zyggy),
        ("areas/riziv-redis.md", Redis),
        ("areas/.gitkeep", ""),
        ("people/carol.md", Carol),
        ("topics/tea.md", Tea),
        ("inbox/remember-2026-10-03.md", "- [stated] 2026-10-03: Carol likes tea.\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();
    private readonly List<ModelRunRequest> _requests = [];

    public void Dispose() => _tree.Dispose();

    private static JsonElement Proposal(IEnumerable<(string From, string To)> moves, params (string Side, string Name, string Description)[] categories) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["moves"] = new JsonArray(moves.Select(m => (JsonNode?)new JsonObject { ["from"] = m.From, ["to"] = m.To }).ToArray()),
            ["new_categories"] = new JsonArray(categories.Select(c => (JsonNode?)new JsonObject { ["side"] = c.Side, ["name"] = c.Name, ["description"] = c.Description }).ToArray()),
        });

    private static (string, string)[] ValidMoves() =>
    [
        ("areas/zyggy.md", "business/areas/zyggy.md"),
        ("areas/riziv-redis.md", "business/areas/riziv-redis.md"),
        ("people/carol.md", "private/people/carol.md"),
        ("topics/tea.md", "private/topics/tea.md"),
    ];

    private void Returns(JsonElement proposal) =>
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _requests.Add(call.Arg<ModelRunRequest>());
            return TestModelResults.Succeeded(proposal);
        });

    private async Task<(MigrationOutcome Outcome, WorkingSet Set)> Migrate(JsonElement proposal)
    {
        Returns(proposal);
        var snapshot = MemorySnapshot.Load(_tree.Paths);
        var set = new WorkingSet(snapshot);
        var context = new DreamRunContext(_tree.Paths, Path.Combine(_tree.Root, "state"), new DateOnly(2026, 10, 4), new DreamOptions());
        var outcome = await new Migrator(_model, new DreamPrompts()).MigrateAsync(snapshot, set, context, TestContext.Current.CancellationToken);
        return (outcome, set);
    }

    [Fact]
    public async Task Migrate_EveryFileMovedOnce_ContentByteIdenticalIndexesCreated()
    {
        // Act
        var (outcome, set) = await Migrate(Proposal(ValidMoves()));

        // Assert
        outcome.Accepted.Should().BeTrue();
        outcome.Moves.Should().Be(4);
        set.Text("business/areas/zyggy.md").Should().Be(Zyggy);
        set.Text("business/areas/riziv-redis.md").Should().Be(Redis);
        set.Text("private/people/carol.md").Should().Be(Carol);
        set.Text("private/topics/tea.md").Should().Be(Tea);
        foreach (var legacy in new[] { "areas/zyggy.md", "areas/riziv-redis.md", "areas/.gitkeep", "people/carol.md", "topics/tea.md" })
        {
            set.Exists(legacy).Should().BeFalse(legacy);
        }

        foreach (var index in new[] { "private/areas", "private/people", "private/topics", "business/areas", "business/people", "business/topics" })
        {
            MemoryFileReader.Parse(set.Text(index + "/_index.md")!).Name.Should().Be(index.Split('/')[1]);
        }

        set.Exists("inbox/remember-2026-10-03.md").Should().BeTrue();
        _requests.Single().JsonSchema.Should().Be(new DreamPrompts().MigrationSchema);
        _requests.Single().Prompt.Should().Contain("areas/riziv-redis.md — riziv-redis — Redis at RIZIV");
    }

    [Fact]
    public async Task Migrate_InitialCategoriesAreasPeopleTopicsOnBothSides_Allowed()
    {
        // Act
        var (outcome, _) = await Migrate(Proposal(
        [
            ("areas/zyggy.md", "private/areas/zyggy.md"),
            ("areas/riziv-redis.md", "business/topics/riziv-redis.md"),
            ("people/carol.md", "business/people/carol.md"),
            ("topics/tea.md", "private/topics/tea.md"),
        ]));

        // Assert
        outcome.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_NewCategory_CreatedWithIndex()
    {
        // Arrange
        var moves = ValidMoves().Select(m => m.Item1 == "areas/riziv-redis.md" ? (m.Item1, "business/employer/riziv-redis.md") : m);

        // Act
        var (outcome, set) = await Migrate(Proposal(moves, ("business", "employer", "The owner's employer RIZIV/NIHDI")));

        // Assert
        outcome.Accepted.Should().BeTrue();
        outcome.CategoriesCreated.Should().Be(1);
        var index = MemoryFileReader.Parse(set.Text("business/employer/_index.md")!);
        index.Description.Should().Be("The owner's employer RIZIV/NIHDI");
        set.Text("business/employer/riziv-redis.md").Should().Be(Redis);
    }

    public static TheoryData<string> InvalidCases() =>
        ["missing", "twice", "outside side", "bad category", "bad slug", "renamed", "same target", "collision", "not legacy"];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task Migrate_Invalid_MigrationRejected(string kind)
    {
        // Arrange
        var moves = ValidMoves().ToList();
        switch (kind)
        {
            case "missing":
                moves.RemoveAt(3);
                break;
            case "twice":
                moves.Add(("topics/tea.md", "business/topics/tea.md"));
                break;
            case "outside side":
                moves[3] = ("topics/tea.md", "inbox/tea.md");
                break;
            case "bad category":
                moves[3] = ("topics/tea.md", "private/Topics/tea.md");
                break;
            case "bad slug":
                moves[3] = ("topics/tea.md", "private/topics/Tea_Time.md");
                break;
            case "renamed":
                moves[3] = ("topics/tea.md", "private/topics/green-tea.md");
                break;
            case "same target":
                moves[3] = ("topics/tea.md", "private/people/carol.md");
                break;
            case "collision":
                _tree.Write("private/topics/tea.md", "- [stated] 2026-09-01: already here.\n");
                break;
            default:
                moves.Add(("inbox/remember-2026-10-03.md", "private/topics/remember-2026-10-03.md"));
                break;
        }

        // Act
        var (outcome, set) = await Migrate(Proposal(moves));

        // Assert
        outcome.Rejected.Should().Be(DreamCheck.MigrationRejected);
        set.ChangedPaths.Should().BeEmpty();
    }

    private DreamRunner Runner(RecordingProcessRunner git)
    {
        var environment = new DreamEnvironment(_tree.Root, MemoryTree.Alice, TimeZoneInfo.Utc, Path.Combine(_tree.Root, "state"),
            Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt"), null, "0.0.0-test");
        var prompts = new DreamPrompts();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
        return new DreamRunner(environment, new DreamOptions(), new DreamFiler(_model, prompts, clock), new Compressor(_model, prompts),
            new GitClient(git, new GitClientOptions(), clock), clock, NullLogger<DreamRunner>.Instance, new Migrator(_model, prompts));
    }

    [Fact]
    public async Task Run_LegacyPresent_MigrationOnlyNoBatchCall()
    {
        // Arrange
        Returns(Proposal(ValidMoves()));
        var git = new RecordingProcessRunner();

        // Act
        var record = await Runner(git).RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        record.Outcome.Should().Be("committed");
        record.Migrated.Should().Be(4);
        _requests.Should().ContainSingle().Which.JsonSchema.Should().Be(new DreamPrompts().MigrationSchema);
        File.ReadAllText(_tree.Full("private/people/carol.md")).Should().Be(Carol);
        File.Exists(_tree.Full("people/carol.md")).Should().BeFalse();
        Directory.Exists(_tree.Full("areas")).Should().BeFalse("an empty legacy directory is removed");
        File.Exists(_tree.Paths.Ledger).Should().BeFalse("no inbox line was consumed");
    }

    [Fact]
    public async Task Run_LegacyPresent_CommitBodyLayoutMigrated()
    {
        // Arrange
        Returns(Proposal(ValidMoves()));
        var git = new RecordingProcessRunner();

        // Act
        await Runner(git).RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        var commit = git.CallsOf("commit").Single();
        commit.StandardInput.Should().StartWith("dream 2026-10-04\n\nlayout migrated\n");
        commit.Arguments.Should().Contain("acme/alice/people/carol.md").And.Contain("acme/alice/private/people/carol.md").And.Contain("acme/alice/areas/.gitkeep");
    }

    [Fact]
    public async Task Run_NoLegacy_NoMigrationCall()
    {
        // Arrange
        foreach (var legacy in new[] { "areas", "people", "topics" })
        {
            Directory.Delete(_tree.Full(legacy), recursive: true);
        }

        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _requests.Add(call.Arg<ModelRunRequest>());
            return TestModelResults.Failed(global::Zyggy.Core.Runs.RunFailureReason.ClaudeError, "is_error");
        });

        // Act
        await Runner(new RecordingProcessRunner()).RunAsync(DreamTrigger.Manual, TestContext.Current.CancellationToken);

        // Assert
        _requests.Should().ContainSingle().Which.JsonSchema.Should().Be(new DreamPrompts().FilingSchema);
    }
}
