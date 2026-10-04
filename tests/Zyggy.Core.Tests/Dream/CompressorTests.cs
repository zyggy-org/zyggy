using System.Text.Json;
using System.Text.Json.Nodes;

using NSubstitute;

using Zyggy.Core.Dream;
using Zyggy.Core.Memory;
using Zyggy.Core.Models;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Dream;

/// <summary>AC-16: a file over 300 body lines is compressed by one call, checked, or rejected with <c>compress_rejected</c>.</summary>
public sealed class CompressorTests : IDisposable
{
    private const string Path1 = "business/areas/zyggy.md";
    private static readonly string[] ReadTools = ["Read", "Grep", "Glob"];
    private static readonly DateOnly RunDate = new(2026, 10, 4);

    private static readonly string[] Observed = Enumerable.Range(1, 300)
        .Select(i => $"- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy fact {i}.").ToArray();

    private static readonly string[] Stated = Enumerable.Range(1, 10).Select(i => $"- [stated] 2026-09-18: stated fact {i}.").ToArray();

    private readonly MemoryTree _tree = new(
        (Path1, "---\nname: zyggy\ndescription: Zyggy\nupdated: 2026-09-20\n---\n" + string.Concat(Stated.Concat(Observed).Select(l => l + "\n"))),
        ("private/areas/small.md", "- [stated] 2026-09-18: small.\n"));

    private readonly IModelRunner _model = Substitute.For<IModelRunner>();

    public void Dispose() => _tree.Dispose();

    private DreamRunContext Context(DreamOptions? options = null) =>
        new(_tree.Paths, Path.Combine(_tree.Root, "state"), RunDate, options ?? new DreamOptions())
        {
            Secrets = SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!,
        };

    private static JsonElement Proposal(string path, IEnumerable<string> lines, IEnumerable<(string Old, string Reason, string? Into)> removed,
        string? description = null)
    {
        var node = new JsonObject
        {
            ["path"] = path,
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)l).ToArray()),
            ["removed"] = new JsonArray(removed.Select(r =>
            {
                var o = new JsonObject { ["old"] = r.Old, ["reason"] = r.Reason };
                if (r.Into is not null)
                {
                    o["into"] = r.Into;
                }

                return (JsonNode?)o;
            }).ToArray()),
        };
        if (description is not null)
        {
            node["description"] = description;
        }

        return JsonSerializer.SerializeToElement(node);
    }

    // The valid compression: every stated line kept, the first 20 observed lines merged into one line that keeps the provenance.
    private static JsonElement Valid(string? description = null)
    {
        var merged = "- [observed] 2026-09-20 [github-inventory 2026-09-20]: zyggy facts 1 to 20.";
        var lines = Stated.Append(merged).Concat(Observed.Skip(20)).ToList();
        return Proposal(Path1, lines, Observed.Take(20).Select(o => (o, "merged", (string?)merged)), description);
    }

    private async Task<(IReadOnlyList<CompressionOutcome> Outcomes, WorkingSet Set)> CompressAsync(JsonElement proposal, DreamOptions? options = null)
    {
        _model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(TestModelResults.Succeeded(proposal));
        var set = new WorkingSet(MemorySnapshot.Load(_tree.Paths));
        var compressor = new Compressor(_model, new DreamPrompts());
        var outcomes = await compressor.CompressAsync(set, Context(options), TestContext.Current.CancellationToken);
        return (outcomes, set);
    }

    [Fact]
    public async Task Compress_ValidProposal_AtMost300LinesProvenanceKept()
    {
        // Act
        var (outcomes, set) = await CompressAsync(Valid());

        // Assert
        outcomes.Should().ContainSingle().Which.Accepted.Should().BeTrue();
        var file = MemoryFileReader.Parse(set.Text(Path1)!);
        file.BodyLines.Should().HaveCount(291).And.OnlyContain(l => MemoryLine.Parse(l).Tag != MemoryTag.Other);
        file.BodyLines.Should().Contain(Stated);
        file.Updated.Should().Be(RunDate);
        await _model.Received(1).RunAsync(Arg.Is<ModelRunRequest>(r => r.Prompt.Contains("<<<") && r.Tools!.SequenceEqual(ReadTools)),
            Arg.Any<CancellationToken>());
    }

    public static TheoryData<string> InvalidCases() => ["over 300", "ratio", "no provenance", "stated unmapped", "other path"];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task Compress_Invalid_CompressRejected(string kind)
    {
        // Arrange
        var lines = Stated.Concat(Observed.Skip(20)).ToList();
        var removed = Observed.Take(20).Select(o => (o, "expired", (string?)null)).ToList();
        var proposal = kind switch
        {
            "over 300" => Proposal(Path1, Stated.Concat(Observed).Take(301), []),
            "ratio" => Proposal(Path1, Stated.Concat(Observed.Take(140)), Observed.Skip(140).Select(o => (o, "expired", (string?)null))),
            "no provenance" => Proposal(Path1, lines.Append("- [observed] 2026-09-20: no bracket."), removed),
            "stated unmapped" => Proposal(Path1, Stated.Skip(1).Concat(Observed.Skip(20)), removed),
            _ => Proposal("business/areas/other.md", lines, removed),
        };

        // Act
        var (outcomes, set) = await CompressAsync(proposal);

        // Assert
        outcomes.Should().ContainSingle().Which.Rejected.Should().Be(DreamCheck.CompressRejected);
        set.ChangedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task Compress_UpdatesDescriptionWhenGiven()
    {
        // Act
        var (_, set) = await CompressAsync(Valid("Zyggy, the agent platform (compressed)"));

        // Assert
        MemoryFileReader.Parse(set.Text(Path1)!).Description.Should().Be("Zyggy, the agent platform (compressed)");
    }

    [Fact]
    public async Task Compress_MaxCompressionsPerRun_DefersRest()
    {
        // Arrange
        _tree.Write("private/areas/big.md", string.Concat(Enumerable.Range(1, 301).Select(i => $"- [stated] 2026-09-18: big {i}.\n")));

        // Act
        var (outcomes, _) = await CompressAsync(Valid(), new DreamOptions { MaxCompressionsPerRun = 1 });

        // Assert
        outcomes.Should().ContainSingle();
        await _model.Received(1).RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Compress_FileOverLimitAtRunStart_IsCompressedEvenWithoutBatch()
    {
        // Act: the working set is untouched by any batch.
        var (outcomes, set) = await CompressAsync(Valid());

        // Assert
        outcomes.Should().ContainSingle().Which.Path.Should().Be(Path1);
        set.ChangedPaths.Should().Equal(Path1);
    }
}
