using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Outlook;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// AC-16 and AC-9 (spec 58 slice 5): once a job has read work mail (its marker exists on the laptop), the <c>zyggy</c> entry point
/// refuses every verb that could carry text to the bus or elsewhere — only <c>outlook</c> (whose <c>share</c> runs the filter), the
/// job hook and <c>--version</c>/<c>--help</c> run until the job ends. Before the first read, outside a job, or on another job, every
/// verb runs as before.
/// </summary>
public sealed class OutlookJobGateTests : IDisposable
{
    private const string Job = "01M81N0000000000000000000A";

    private const string Other = "01M81N0000000000000000000B";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public OutlookJobGateTests()
    {
        Directory.CreateDirectory(_root);
        string json = string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "tenant": "acme",
              "machine": "work-laptop",
              "nodeDir": "{{NodeDir.Replace('\\', '/')}}",
              "bus": { "owner": "acme-org", "repo": "acme-bus", "localPath": "{{_root.Replace('\\', '/')}}/bus", "remote": "https://example.invalid/acme-org/acme-bus.git" },
              "secrets": { "store": "file", "root": "{{_root.Replace('\\', '/')}}/secrets" }
            }
            """);
        File.WriteAllText(Config, json);
    }

    private string NodeDir => Path.Combine(_root, "node");

    private string Config => Path.Combine(_root, "node.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("context")]
    [InlineData("ask")]
    [InlineData("submit")]
    [InlineData("remember")]
    [InlineData("memory")]
    [InlineData("archive")]
    [InlineData("report")]
    [InlineData("decide")]
    public void GateRefusal_JobThatReadWorkMail_OtherVerbRefused(string verb)
    {
        // Arrange
        Read(Job);

        // Act
        string? refusal = OutlookJobRun.GateRefusal([verb, "x"], InJob(Job));

        // Assert
        refusal.Should().Be("zyggy: refused: this job read work mail; until it ends only zyggy outlook runs (spec 58, the sensitive-data filter)");
    }

    [Theory]
    [InlineData("outlook")]
    [InlineData("hook")]
    [InlineData("--version")]
    [InlineData("--help")]
    public void GateRefusal_JobThatReadWorkMail_AllowedVerbRuns(string verb)
    {
        // Arrange
        Read(Job);

        // Act
        string? refusal = OutlookJobRun.GateRefusal([verb], InJob(Job));

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void GateRefusal_NoArguments_JobThatReadWorkMail_Refused()
    {
        // Arrange
        Read(Job);

        // Act
        string? refusal = OutlookJobRun.GateRefusal([], InJob(Job));

        // Assert
        refusal.Should().NotBeNull();
    }

    [Fact]
    public void GateRefusal_JobBeforeItsFirstRead_Runs()
    {
        // Act
        string? refusal = OutlookJobRun.GateRefusal(["context", "x"], InJob(Job));

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void GateRefusal_AnotherJobRead_Runs()
    {
        // Arrange
        Read(Other);

        // Act
        string? refusal = OutlookJobRun.GateRefusal(["context", "x"], InJob(Job));

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void GateRefusal_AttendedSession_Runs()
    {
        // Arrange
        Read(Job);

        // Act
        string? refusal = OutlookJobRun.GateRefusal(["context", "x"], new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ZYGGY_NODE_JSON"] = Config });

        // Assert
        refusal.Should().BeNull();
    }

    private Dictionary<string, string?> InJob(string job) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ZYGGY_JOB"] = job,
            ["ZYGGY_HOOKS"] = "off",
            ["ZYGGY_NODE_JSON"] = Config,
            ["ZYGGY_JOB_MARKER"] = OutlookJobRun.MarkerPath(NodeDir, TenantId.Parse("acme"), EnvelopeId.Parse(job)),
        };

    private void Read(string job) => OutlookJobRun.Record(OutlookJobRun.MarkerPath(NodeDir, TenantId.Parse("acme"), EnvelopeId.Parse(job)), []);
}
