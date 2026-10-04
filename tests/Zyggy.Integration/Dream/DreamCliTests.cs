using System.Text.Json;

using Zyggy.Core.Dream;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Dream;

/// <summary><c>zyggy dream request</c>, the request file and <c>zyggy dream status</c>.</summary>
public sealed class DreamCliTests : IAsyncLifetime
{
    private readonly MemoryRepoFixture _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RequestFile => Path.Combine(_repo.StateDir, "dream.request");

    public async ValueTask InitializeAsync()
    {
        await _repo.InitializeAsync();
        await _repo.SeedAsync("dream", Ct);
    }

    public ValueTask DisposeAsync() => _repo.DisposeAsync();

    private Task<ZyggyRun> Zyggy(params string[] args) =>
        ZyggyCli.RunAsync(args, _repo.DreamEnv("dream-file-ok"), null, _repo.RootDir, Ct);

    [Fact]
    public async Task Request_WritesRequestFileAndPrints()
    {
        // Act
        var run = await Zyggy("dream", "request");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        run.Stdout.Trim().Should().Be("dream requested");
        File.Exists(RequestFile).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(RequestFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task Dream_WithRequestFilePresent_TriggerOnDemandAndFileDeleted()
    {
        // Arrange
        await Zyggy("dream", "request");

        // Act
        var run = await Zyggy("dream");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        File.Exists(RequestFile).Should().BeFalse();
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Trigger.Should().Be("on-demand");
        (await _repo.LastCommitBodyAsync(Ct)).Should().Contain("Zyggy-Trigger: on-demand");
    }

    [Fact]
    public async Task Dream_TriggerOption_IsRecorded()
    {
        // Act
        var run = await Zyggy("dream", "--trigger", "nightly");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        DreamRunRecordStore.ReadLast(_repo.StateDir)!.Trigger.Should().Be("nightly");
    }

    [Fact]
    public async Task Status_NoRunYet_ExitsOne()
    {
        // Act
        var run = await Zyggy("dream", "status");

        // Assert
        run.ExitCode.Should().Be(1);
        run.Stdout.Trim().Should().Be("no dream run yet");
    }

    [Fact]
    public async Task Status_AfterRun_PrintsSummaryLine()
    {
        // Arrange
        await Zyggy("dream");

        // Act
        var run = await Zyggy("dream", "status");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        var line = run.Stdout.Trim();
        line.Should().Contain("committed").And.Contain("batches 1").And.Contain("lines 5").And.Contain("pushed yes");
        line.Split('\n').Should().ContainSingle();
    }

    [Fact]
    public async Task Status_Json_PrintsRecord()
    {
        // Arrange
        await Zyggy("dream");

        // Act
        var run = await Zyggy("dream", "status", "--json");

        // Assert
        run.ExitCode.Should().Be(0, run.Stderr);
        using var json = JsonDocument.Parse(run.Stdout);
        json.RootElement.GetProperty("outcome").GetString().Should().Be("committed");
        json.RootElement.GetProperty("trigger").GetString().Should().Be("manual");
    }
}
