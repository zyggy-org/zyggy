using System.Text;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Registry;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Registry;

public sealed class DiscoveryScannerContentTests : IDisposable
{
    private const string Summary = "Multi-tenant booking SaaS, .NET/Blazor";

    private readonly ScanTree _tree = new();
    private readonly RecordingProcessRunner _processes = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<DiscoveryScanner> _log = new();
    private readonly DiscoveryOptions _options = new();
    private readonly string _root;

    public DiscoveryScannerContentTests() => _root = _tree.Root("src");

    public void Dispose() => _tree.Dispose();

    private static string SecretSample(string pattern) =>
        File.ReadLines(Path.Combine(Golden.Directory, "secret-patterns", "secret-samples.txt"))
            .Select(l => l.Split('\t', 2))
            .First(p => p.Length == 2 && p[0] == pattern)[1];

    private static string BenignSample() =>
        File.ReadLines(Path.Combine(Golden.Directory, "secret-patterns", "benign-samples.txt"))
            .First(l => l.Length is > 10 and < 120 && char.IsLetter(l[0]));

    private async Task<RegistryProject> ScanOneAsync(string project, SecretPatterns? patterns = null)
    {
        ScanResult result = await ScanAsync(patterns ?? DiscoveryScannerTests.Patterns);
        return result.Projects.Should().ContainSingle(p => p.Path == ScanTree.Published(project)).Subject;
    }

    private Task<ScanResult> ScanAsync(SecretPatterns? patterns) =>
        new DiscoveryScanner(new GitClient(_processes, new GitClientOptions(), _clock), patterns, _options, _clock, _log)
            .ScanAsync([_root], DiscoveryDetail.Full, TestContext.Current.CancellationToken);

    private static string Agent(string description) => $"---\nname: x\ndescription: {description}\n---\nBody.\n";

    [Fact]
    public async Task ScanAsync_Ac2AgentsAndSkills_DescriptionsCleanedAndOrdered()
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, ".claude/agents/env-debugger.md", Agent("\"Diagnoses failing environments\""));
        _tree.File(project, ".claude/agents/plain.md", "No front matter here.\n");
        _tree.File(project, ".claude/agents/bad.md", "---\ndescription: [unclosed\n---\n");
        _tree.File(project, ".claude/agents/with space.md", Agent("ignored"));
        _tree.File(project, ".claude/agents/notes.txt", "not an agent");
        // Words, not one run: a 120+ character run without spaces is the long-opaque-token secret pattern.
        string first = string.Concat(Enumerable.Repeat("d ", 40));
        string second = string.Concat(Enumerable.Repeat("e ", 44));
        string longDescription = "\"" + first + "\\t\\a" + second + "\"";
        _tree.File(project, ".claude/skills/deploy-staging/SKILL.md", Agent(longDescription));
        _tree.Folder(Path.Combine(project, ".claude", "skills"), "empty");

        // Act
        RegistryProject result = await ScanOneAsync(project);

        // Assert
        result.Agents.Should().Equal(
            new RegistryEntry("bad", ""),
            new RegistryEntry("env-debugger", "Diagnoses failing environments"),
            new RegistryEntry("plain", ""));
        RegistryEntry skill = result.Skills.Should().ContainSingle().Subject;
        skill.Name.Should().Be("deploy-staging");
        skill.Description.Should().HaveLength(150).And.NotContainAny("\t", "\a");
        skill.Description.Should().StartWith(first + "e");
    }

    public static TheoryData<string, string?> Summaries() => new()
    {
        { "# Calizr\n\n> a quote\n- a list item\n* another\n+ third\n1. numbered\n2) numbered\n```\ncode line\n```\n~~~\nmore code\n~~~\n" + Summary + "\nSecond line.\n", Summary },
        { "# Calizr\n## Build\n### Test\n", null },
        { "﻿# Calizr\r\n\r\n" + Summary + "\r\nNext\r\n", Summary },
    };

    [Theory]
    [MemberData(nameof(Summaries))]
    public async Task ScanAsync_Ac3Summary(string claudeMd, string? expected)
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, "CLAUDE.md", claudeMd);

        // Act + Assert
        (await ScanOneAsync(project)).Summary.Should().Be(expected);
    }

    [Fact]
    public async Task ScanAsync_NoClaudeMd_SummaryNull()
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");

        // Act + Assert
        (await ScanOneAsync(project)).Summary.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_QualifyingLineBeyond64KiB_SummaryNull()
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");
        var text = new StringBuilder();
        while (text.Length < 70 * 1024)
        {
            text.Append("# heading line\n");
        }

        text.Append(Summary).Append('\n').Append(new string('x', 1024 * 1024));
        _tree.File(project, "CLAUDE.md", text.ToString());

        // Act + Assert
        (await ScanOneAsync(project)).Summary.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_QualifyingLineWithin64KiBOfLargeFile_ThatLine()
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, "CLAUDE.md", "# Calizr\n" + Summary + "\n" + new string('z', 1024 * 1024));

        // Act + Assert
        (await ScanOneAsync(project)).Summary.Should().Be(Summary);
    }

    [Fact]
    public async Task ScanAsync_BlockScalarDescription_LineBreaksRemoved()
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, ".claude/agents/multi.md", "---\ndescription: |\n  first line\n  second line\n---\n");

        // Act
        RegistryProject result = await ScanOneAsync(project);

        // Assert
        string description = result.Agents.Should().ContainSingle().Subject.Description;
        description.Should().NotContain("\n").And.Contain("first line").And.Contain("second line");
    }

    [Fact]
    public async Task ScanAsync_MoreAgentsThanCap_CappedWithNotice()
    {
        // Arrange
        _options.MaxEntriesPerProject = 3;
        string project = _tree.Project(_root, "calizr");
        foreach (string name in new[] { "d", "a", "c", "b" })
        {
            _tree.File(project, $".claude/agents/{name}.md", Agent(name));
        }

        // Act
        ScanResult result = await ScanAsync(DiscoveryScannerTests.Patterns);

        // Assert
        result.Projects.Single().Agents.Select(a => a.Name).Should().Equal("a", "b", "c");
        ScanNotice notice = result.Notices.Should().ContainSingle(n => n.Kind == ScanNoticeKind.EntryCapReached).Subject;
        notice.Subject.Should().Be("calizr");
    }

    [Fact]
    public async Task ScanAsync_SecretInDescriptionAndSummary_RedactedWithPatternNotice()
    {
        // Arrange
        string sample = SecretSample("aws-access-key");
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, ".claude/agents/leaky.md", Agent("\"uses key " + sample + "\""));
        _tree.File(project, "CLAUDE.md", "The key is " + sample + "\n");

        // Act
        ScanResult result = await ScanAsync(DiscoveryScannerTests.Patterns);

        // Assert
        RegistryProject only = result.Projects.Single();
        only.Agents.Should().Equal(new RegistryEntry("leaky", ""));
        only.Summary.Should().BeNull();
        result.Notices.Should().HaveCount(2).And.OnlyContain(n =>
            n.Kind == ScanNoticeKind.SecretRedacted && n.Subject == "calizr" && n.Detail == "aws-access-key");
        result.Notices.Should().NotContain(n => n.Subject.Contains(sample, StringComparison.Ordinal)
            || (n.Detail ?? "").Contains(sample, StringComparison.Ordinal));
        _log.Messages.Should().NotContain(m => m.Contains(sample, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_BenignSample_PassesUnchanged()
    {
        // Arrange
        string benign = BenignSample();
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, "CLAUDE.md", benign + "\n");

        // Act + Assert
        (await ScanOneAsync(project)).Summary.Should().Be(RegistryText.Clean(benign.Trim()));
    }

    [Fact]
    public async Task ScanAsync_NoPatternsFullDetail_FallsBackToNamesWithNotice()
    {
        // Arrange
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, ".claude/agents/env-debugger.md", Agent("Debugs"));
        _tree.File(project, "CLAUDE.md", Summary + "\n");

        // Act
        ScanResult result = await new DiscoveryScanner(new GitClient(_processes, new GitClientOptions(), _clock), null, _options, _clock, _log)
            .ScanAsync([_root], DiscoveryDetail.Full, TestContext.Current.CancellationToken);

        // Assert
        result.EffectiveDetail.Should().Be(DiscoveryDetail.Names);
        result.Notices.Should().ContainSingle().Which.Kind.Should().Be(ScanNoticeKind.PatternsUnavailable);
        RegistryProject only = result.Projects.Single();
        only.Agents.Should().BeEmpty();
        only.Summary.Should().BeNull();
        only.Path.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_LogOutput_NeverContainsContent()
    {
        // Arrange
        string sample = SecretSample("aws-access-key");
        string project = _tree.Project(_root, "calizr");
        _tree.File(project, ".claude/agents/env-debugger.md", Agent("\"Diagnoses failing environments\""));
        _tree.File(project, ".claude/agents/leaky.md", Agent("\"key " + sample + "\""));
        _tree.File(project, "CLAUDE.md", Summary + "\n");

        // Act
        await ScanAsync(DiscoveryScannerTests.Patterns);

        // Assert
        _log.Messages.Should().NotContain(m => m.Contains("Diagnoses", StringComparison.Ordinal)
            || m.Contains(Summary, StringComparison.Ordinal)
            || m.Contains(sample, StringComparison.Ordinal));
    }
}
