using Microsoft.Extensions.Logging.Abstractions;

using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Processes;

/// <summary>
/// Spec 61 slice 8, AC-24/AC-26 (the record half, on the real OS): a process the runner starts is recorded with the pid and the start
/// time this machine's process table reports for it, on Windows and Linux alike — the record the next start compares against.
/// </summary>
public sealed class ModelProcessRecordOnStartTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zyggy-model-onstart-");

    public void Dispose() => _dir.Delete(recursive: true);

    // AC-26: the model process just started → one record naming its pid and the start the process table reports (1 s tolerance)
    [Fact]
    public async Task RunAsync_ProcessStarted_RecordedWithThePidAndStartTheTableReports()
    {
        // Arrange
        var records = new ModelProcessRecords(_dir.FullName, Sleeper.FileName, OsProcessTable.Instance, NullLogger<ModelProcessRecords>.Instance);
        ProcessFacts? table = null;
        string? record = null;
        var spec = new ProcessSpec(Sleeper.FileName, Sleeper.Arguments, _dir.FullName)
        {
            Timeout = TimeSpan.FromSeconds(30),
            OnStarted = facts =>
            {
                table = OsProcessTable.Instance.Find(facts.Pid);
                record = records.Add(facts);
            },
        };

        // Act
        await new ProcessRunner(TimeProvider.System).RunAsync(spec, TestContext.Current.CancellationToken);

        // Assert
        IReadOnlyList<ModelProcessRecord> written = records.Read();
        (record is not null, written.Count, written is [var r] && table is { StartedUtc: { } s } && r.Pid == table.Pid && (r.StartedUtc - s).Duration() <= TimeSpan.FromSeconds(1))
            .Should().Be((true, 1, true), $"table: {table}");
    }

    /// <summary>A program that runs for about a second on this OS.</summary>
    private static class Sleeper
    {
        public static string FileName => OperatingSystem.IsWindows() ? Path.Join(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";

        public static IReadOnlyList<string> Arguments => OperatingSystem.IsWindows() ? ["/c", "ping -n 2 127.0.0.1 >nul"] : ["-c", "sleep 1"];
    }
}
