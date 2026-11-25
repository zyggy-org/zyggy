using Zyggy.Core.Processes;

namespace Zyggy.Core.Tests.Processes;

/// <summary>Spec 61 slice 8: the Linux parent pid, read from <c>/proc/&lt;pid&gt;/stat</c> text (runs on every OS).</summary>
public sealed class ParentProcessTests
{
    [Theory]
    [InlineData("4242 (zyggy-node) S 4100 4242 4100 0 -1 4194560", 4100)]
    [InlineData("4242 (a (weird) name) R 77 4242 4100 0", 77)]
    [InlineData("4242 (zyggy-node) S 1 4242 1 0", 1)]
    [InlineData("4242 (zyggy-node)", null)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void ParentOfStat_Text_ReadsTheFourthField(string text, int? expected)
    {
        // Act
        int? parent = ParentProcess.ParentOfStat(text);

        // Assert
        parent.Should().Be(expected);
    }

    [Fact]
    public void Of_ThisProcess_NamesALiveParent()
    {
        // Act: the test host was started by something that still runs (dotnet test, the IDE, a shell)
        int? parent = ParentProcess.Of(Environment.ProcessId);

        // Assert
        (parent is > 0, parent is { } p && OsProcessTable.Instance.Find(p) is not null).Should().Be((true, true));
    }
}
