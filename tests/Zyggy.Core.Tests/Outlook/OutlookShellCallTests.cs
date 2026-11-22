using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 slice 7 (AC-11, AC-12): how the outlook guard hook reads a <c>Bash</c> command. A command that names an outlook action is
/// either exactly one plain <c>zyggy outlook send|move …</c> call, read into its arguments, or it is refused whole: no variable, no
/// substitution, no second command, no redirect, no glob — so what the guard checked and the prompt showed is what runs.
/// </summary>
public sealed class OutlookShellCallTests
{
    public static TheoryData<string, string[]> Plain() => new()
    {
        { "zyggy outlook send --to a@example.test --subject Hi --body Hello", ["send", "--to", "a@example.test", "--subject", "Hi", "--body", "Hello"] },
        { "zyggy outlook send --to a@example.test --subject 'Two words' --body 'line one\nline two'", ["send", "--to", "a@example.test", "--subject", "Two words", "--body", "line one\nline two"] },
        { "zyggy outlook send --to a@example.test --subject \"It's \\\"ok\\\"\" --body x", ["send", "--to", "a@example.test", "--subject", "It's \"ok\"", "--body", "x"] },
        { "zyggy outlook send --to a@example.test --subject 'it'\\''s' --body x", ["send", "--to", "a@example.test", "--subject", "it's", "--body", "x"] },
        { "zyggy.exe outlook move m1 --to 'Deleted Items'", ["move", "m1", "--to", "Deleted Items"] },
        { "'/c/Program Files/x/zyggy' outlook move m1 --to Archive", ["move", "m1", "--to", "Archive"] },
        { "  zyggy   outlook   send   --draft 2  ", ["send", "--draft", "2"] },
        { "zyggy outlook send --draft 2 --body \"cost: 5 \\$\"", ["send", "--draft", "2", "--body", "cost: 5 $"] },
    };

    [Theory]
    [MemberData(nameof(Plain))]
    public void Read_PlainCall_ArgumentsAfterOutlook(string command, string[] expected)
    {
        // Act
        var call = OutlookShellCall.Read(command);

        // Assert
        (call.Kind, call.Arguments).Should().BeEquivalentTo((OutlookShellCallKind.Action, expected), o => o.WithStrictOrdering());
    }

    [Theory]
    [InlineData("zyggy outlook send --to a@example.test --body $BODY")]
    [InlineData("zyggy outlook send --to a@example.test --body \"$(cat x)\"")]
    [InlineData("zyggy outlook send --to a@example.test --body `cat x`")]
    [InlineData("zyggy outlook send --to a@example.test --body x; rm -rf y")]
    [InlineData("zyggy outlook send --to a@example.test --body x && zyggy outlook send --draft 1")]
    [InlineData("zyggy outlook send --to a@example.test --body x | tee y")]
    [InlineData("zyggy outlook send --to a@example.test --body x > out.txt")]
    [InlineData("zyggy outlook send --to a@example.test --body x < in.txt")]
    [InlineData("zyggy outlook move m1 --to Arch*")]
    [InlineData("zyggy outlook send --draft 1\nzyggy outlook send --draft 2")]
    [InlineData("ZYGGY_JOB= zyggy outlook send --draft 1")]
    [InlineData("env -u ZYGGY_JOB zyggy outlook send --draft 1")]
    [InlineData("cmd /c zyggy outlook send --draft 1")]
    [InlineData("zyggy outlook send --body 'unclosed")]
    [InlineData("zyggy outlook send --draft 1 # comment")]
    [InlineData("zyggy outlook send --draft {1,2}")]
    [InlineData("zyggy outlook send --draft ~1")]
    [InlineData("(zyggy outlook send --draft 1)")]
    public void Read_NamesAnActionButNotOnePlainCall_Refused(string command)
    {
        // Act
        var call = OutlookShellCall.Read(command);

        // Assert
        (call.Kind, call.Arguments.Count).Should().Be((OutlookShellCallKind.Refused, 0));
    }

    [Theory]
    [InlineData("zyggy outlook brief")]
    [InlineData("zyggy outlook drafts 2")]
    [InlineData("git commit -m 'outlook send and move'")]
    [InlineData("dotnet test --filter Outlook")]
    [InlineData("zyggy outlook list --folder Sent")]
    public void Read_NoOutlookAction_None(string command)
    {
        // Act
        var call = OutlookShellCall.Read(command);

        // Assert
        call.Kind.Should().Be(OutlookShellCallKind.None);
    }

    [Theory]
    [InlineData("zyggy outlook send --draft 1", "send")]
    [InlineData("ZYGGY.EXE  Outlook  MOVE m1 --to Archive", "move")]
    [InlineData("x=1; \"zyggy\" 'outlook' \"send\" --draft 1", "send")]
    [InlineData("& 'C:/tools/zyggy.exe' outlook move m1 --to Archive", "move")]
    [InlineData("zyggy outlook brief", null)]
    [InlineData("git commit -m 'the outlook send verb'", null)]
    public void ActionNamed_FindsTheActionHoweverQuoted(string command, string? expected)
    {
        // Act
        var action = OutlookShellCall.ActionNamed(command);

        // Assert
        action.Should().Be(expected);
    }
}
