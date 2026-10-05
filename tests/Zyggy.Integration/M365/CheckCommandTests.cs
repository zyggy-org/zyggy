using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.M365;

/// <summary>Step 9 (spec 33 AC-17): <c>zyggy m365 check</c> in process over the stubbed Graph, real key files.</summary>
public sealed class CheckCommandTests : IDisposable
{
    private readonly M365InstanceFixture _fixture = new();
    private readonly StubGraphHandler _graph = StubGraphHandler.FromGoldenRoutes();
    private readonly TestCertificates _pair;

    public CheckCommandTests() => _pair = TestCertificates.Create(Path.Combine(_fixture.Home, ".config", "zyggy"));

    public void Dispose()
    {
        _graph.Violations.Should().BeEmpty();
        _pair.Dispose();
        _fixture.Dispose();
    }

    private Task<(int Exit, VerbConsole Console)> Check(params string[] args) => Check(_fixture.Env(), args);

    private Task<(int Exit, VerbConsole Console)> Check(Dictionary<string, string?> env, params string[] args) =>
        M365InProcess.RunAsync(env, _graph, ["check", .. args]);

    [Fact]
    public async Task Check_StdoutByteEqualsExpectedWithStateDirNormalised()
    {
        // Act
        var (exit, console) = await Check();

        // Assert
        exit.Should().Be(0, console.Stderr);
        var expected = File.ReadAllText(M365InstanceFixture.Golden("m365", "expected", "m365-check.txt"));
        console.Stdout.Replace(Path.Join(_fixture.StateDirectory, "m365"), "STATE_DIR", StringComparison.Ordinal).Should().Be(expected);
        console.Stderr.Should().Be("key: file\n");
        _graph.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1);
    }

    [Fact]
    public async Task Check_Counts_FolderLinesAndZyggyDrafts()
    {
        // Act
        var (exit, console) = await Check("--counts");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[4..^1].Should().Equal(
            "folder AQMkInbox0001 Inbox inbox 1240",
            "folder AQMkSentItems0001 Sent Items sentitems 310",
            "folder AQMkDeletedItems0001 Deleted Items deleteditems 12 excluded",
            "folder AQMkDrafts0001 Drafts drafts 4 excluded",
            "folder AQMkJunkEmail0001 Junk Email junkemail 3 excluded",
            "folder AQMkArchive0001 Archive - 90",
            "zyggy-drafts 2");
        _graph.To("mailFolders/drafts/messages\\?\\$select=id,subject&\\$top=50").Should().ContainSingle();
    }

    [Fact]
    public async Task Check_OtherMailboxReadable_ScopeNotEnforcedExitFive()
    {
        // Arrange
        _graph.Once("GET", "users/bob@acme\\.example/mailFolders/inbox", 200, File.ReadAllText(StubGraphHandler.GraphFixture("mail-folders.json")));

        // Act
        var (exit, console) = await Check("--other-mailbox", "bob@acme.example");

        // Assert
        exit.Should().Be(5);
        console.Stdout.Split('\n')[^2].Should().Be("other mailbox bob@acme.example: 200 — SCOPE NOT ENFORCED");
        console.Stderr.Should().EndWith("m365: refused: scope not enforced — bob@acme.example is readable; runbook 13 \"Scope or grant missing\"\n");
    }

    [Fact]
    public async Task Check_OtherMailbox403_ScopeHolds()
    {
        // Act
        var (exit, console) = await Check("--other-mailbox", "bob@acme.example");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[^2].Should().Be("other mailbox bob@acme.example: 403 (expected: scope holds)");
    }

    [Theory]
    [InlineData("b!ungranted0001", "drive b!ungranted0001: 403 (not granted)")]
    [InlineData("b!onedrive0001", "drive b!onedrive0001: 200 (granted)")]
    [InlineData("b!missing0001", "drive b!missing0001: 404 (not found)")]
    public async Task Check_Drive_GrantedNotGrantedNotFound(string drive, string line)
    {
        // Arrange
        _graph.Always("GET", "drives/b!missing0001/root$", 404, "{}");

        // Act
        var (exit, console) = await Check("--drive", drive);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[^2].Should().Be(line);
    }

    [Fact]
    public async Task Check_AllOptions_ThirteenLines()
    {
        // Act
        var (exit, console) = await Check("--counts", "--other-mailbox", "bob@acme.example", "--drive", "b!onedrive0001");

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stdout.Split('\n')[..^1].Should().HaveCount(13);
    }

    [Fact]
    public async Task Check_StderrKeySource_CredentialsDirectory()
    {
        // Arrange
        var credentials = Path.Combine(_fixture.Root, "credentials");
        Directory.CreateDirectory(credentials);
        File.Copy(_pair.KeyFile, Path.Combine(credentials, "m365-app-key"));
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(Path.Combine(credentials, "m365-app-key"), UnixFileMode.UserRead);
        }

        var env = _fixture.Env();
        env["CREDENTIALS_DIRECTORY"] = credentials;

        // Act
        var (exit, console) = await Check(env);

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().Be("key: credentials directory\n");
    }

    [Fact]
    public async Task Check_CertificateExpiresSoon_WarningFirst()
    {
        // Arrange
        var config = Path.Combine(_fixture.InstanceDirectory, "m365.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("2027-10-01", "2026-10-20", StringComparison.Ordinal));

        // Act
        var (exit, console) = await Check();

        // Assert
        exit.Should().Be(0, console.Stderr);
        console.Stderr.Should().Be("m365: certificate expires in 20 days — runbook 13 \"Rotate the certificate\"\nkey: file\n");
    }

    [Fact]
    public async Task Check_TenantUnset_ExitThreeNoRequest()
    {
        // Arrange
        var env = _fixture.Env();
        env.Remove("ZYGGY_TENANT");

        // Act
        var (exit, console) = await Check(env);

        // Assert
        exit.Should().Be(3);
        console.Stderr.Should().Be("m365: configuration error: ZYGGY_TENANT is not set\n");
        _graph.Requests.Should().BeEmpty();
    }
}
