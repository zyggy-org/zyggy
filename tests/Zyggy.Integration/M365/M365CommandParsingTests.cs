using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>
/// Step 9 (spec 33 AC-6): the m365 command surface from the built binary — the removed approval verbs and the Graph read verbs have
/// no surface (exit 4); option errors exit 4 with the verb's usage line. No request is ever made.
/// </summary>
public sealed class M365CommandParsingTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private Task<ZyggyRun> M365(params string[] args) =>
        ZyggyCli.RunAsync(["m365", .. args], _fixture.Env(), null, _fixture.Root, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("send-draft")]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("snapshot")]
    [InlineData("get")]
    [InlineData("sent-since")]
    [InlineData("propose")]
    [InlineData("send-draft", "--approved")]
    [InlineData("move", "--approved")]
    public async Task RemovedD6Verbs_ExitFour(params string[] args)
    {
        // Act
        var run = await M365(args);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be($"m365: unknown verb '{args[0]}' (usage: zyggy m365 <verb> …)\n");
    }

    [Theory]
    [InlineData("mail-folders")]
    [InlineData("drives")]
    [InlineData("drive-files")]
    [InlineData("drafts-since")]
    [InlineData("message-sender")]
    [InlineData("item-exists")]
    [InlineData("item-kind")]
    [InlineData("token")]
    public async Task ReadVerbsHaveNoSurface_ExitFour(string verb)
    {
        // Act
        var run = await M365(verb);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().StartWith($"m365: unknown verb '{verb}'");
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "check: unexpected argument '--bogus'")]
    [InlineData(new[] { "--counts", "--counts" }, "--counts given twice")]
    [InlineData(new[] { "--other-mailbox", "bob" }, "--other-mailbox needs a user principal name")]
    [InlineData(new[] { "--other-mailbox" }, "--other-mailbox needs a user principal name")]
    [InlineData(new[] { "--other-mailbox", "a@b.example", "--other-mailbox", "c@d.example" }, "--other-mailbox given twice")]
    [InlineData(new[] { "--drive", "a/b" }, "--drive needs a drive id")]
    [InlineData(new[] { "--drive", "b!x", "--drive", "b!y" }, "--drive given twice")]
    public async Task Check_BadOption_ExitFourUsage(string[] args, string message)
    {
        // Act
        var run = await M365(["check", .. args]);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be($"m365: {message} (usage: zyggy m365 check [--counts] [--other-mailbox <upn>] [--drive <id>])\n");
    }

    [Theory]
    [InlineData(new[] { "--alg", "HS256" }, "--alg must be PS256 or RS256")]
    [InlineData(new[] { "--alg" }, "--alg must be PS256 or RS256")]
    [InlineData(new[] { "--key", "old" }, "--key takes 'new' (the pair from cert-init --rotate)")]
    [InlineData(new[] { "extra" }, "token-test: unexpected argument 'extra'")]
    public async Task TokenTest_BadOption_ExitFour(string[] args, string message)
    {
        // Act
        var run = await M365(["token-test", .. args]);

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be($"m365: {message} (usage: zyggy m365 token-test [--key new] [--alg PS256|RS256])\n");
    }

    [Fact]
    public async Task CertInit_UnexpectedArgument_ExitFour()
    {
        // Act
        var run = await M365("cert-init", "--force");

        // Assert
        run.ExitCode.Should().Be(4);
        run.Stderr.Should().Be("m365: cert-init: unexpected argument '--force' (usage: zyggy m365 cert-init [--rotate|--commit])\n");
    }
}
