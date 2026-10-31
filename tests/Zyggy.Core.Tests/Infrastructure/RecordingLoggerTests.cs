using Microsoft.Extensions.Logging;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>The recording logger is used by concurrent tests (<c>JobDispatcherTests.AdmitAsync_Concurrent_*</c>); scopes and entries must survive parallel callers.</summary>
public sealed class RecordingLoggerTests
{
    [Fact]
    public void BeginScopeAndLog_FromParallelCallers_NeverThrowsAndRecordsEveryEntry()
    {
        // Arrange
        var logger = new RecordingLogger<RecordingLoggerTests>();
        const int Callers = 20_000;

        // Act
        Parallel.For(0, Callers, i =>
        {
            using (logger.BeginScope(i))
            {
                logger.Log(LogLevel.Information, default, i, null, (s, _) => "entry");
            }
        });

        // Assert
        logger.Entries.Should().HaveCount(Callers);
    }
}
