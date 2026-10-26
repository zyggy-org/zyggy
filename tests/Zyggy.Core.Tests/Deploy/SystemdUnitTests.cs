using System.Globalization;
using System.Text.RegularExpressions;

using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Deploy;

/// <summary>
/// Spec 10 AC-1: the shipped Central unit and the credentials drop-in equal the spec's exact texts and keep every guard — the
/// restart policy of A10-1, no credential or secret-naming line in the unit, the drop-in's two ids computed from the Linux store.
/// </summary>
public sealed partial class SystemdUnitTests
{
    private const string Unit = "systemd/zyggy-node.service";
    private const string DropIn = "systemd/zyggy-node.service.d/credentials.conf.example";

    // NodeHostOptions.ShutdownTimeout of deliverable 07 is 20 s; Zyggy.Core.Tests does not reference Zyggy.Node, so the literal is pinned here (plan 10 A8).
    private const int HostDrainSeconds = 20;

    private static readonly TenantId Acme = TenantId.Parse("acme");

    [Fact]
    public void Unit_EqualsTheExpectedTextByteForByte()
    {
        DeployAssets.Bytes(Unit).Should().Equal(DeployAssets.ExpectedBytes("zyggy-node.service"));
    }

    [Theory]
    [InlineData("Type=notify")]
    [InlineData("User=zyggy")]
    [InlineData("Group=zyggy")]
    [InlineData("WorkingDirectory=~")]
    [InlineData("ExecStart=/usr/local/bin/zyggy-node")]
    [InlineData("Restart=on-failure")]
    [InlineData("RestartSec=30")]
    [InlineData("RestartPreventExitStatus=3 6")]
    [InlineData("KillMode=mixed")]
    [InlineData("NoNewPrivileges=yes")]
    [InlineData("PrivateTmp=yes")]
    [InlineData("Environment=\"GIT_SSH_COMMAND=ssh -o BatchMode=yes\"")]
    [InlineData("WantedBy=multi-user.target")]
    [InlineData("After=network-online.target")]
    [InlineData("StartLimitIntervalSec=600")]
    [InlineData("StartLimitBurst=5")]
    public void Unit_Invariants_NotifyUnprivilegedSymlinkOnFailurePreventExit3And6(string line)
    {
        DeployAssets.Lines(DeployAssets.Text(Unit)).Count(l => l == line).Should().Be(1, line);
    }

    [Fact]
    public void Unit_TimeoutStopSec_AtLeastTheHostsTwentySecondDrain()
    {
        // Act
        string? value = DeployAssets.Lines(DeployAssets.Text(Unit)).SingleOrDefault(l => l.StartsWith("TimeoutStopSec=", StringComparison.Ordinal))?["TimeoutStopSec=".Length..];

        // Assert
        int.Parse(value!, CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(HostDrainSeconds);
    }

    [Fact]
    public void Unit_NoCredentialLineNoSecretNamingEnvironmentNoMachinePath()
    {
        // Arrange
        IReadOnlyList<string> lines = DeployAssets.Lines(DeployAssets.Text(Unit));

        // Assert
        lines.Should().NotContain(l => l.StartsWith("LoadCredential", StringComparison.Ordinal) || l.StartsWith("SetCredential", StringComparison.Ordinal));
        lines.Where(l => l.StartsWith("Environment=", StringComparison.Ordinal)).Should().NotContain(l => SecretWord().IsMatch(l));
        lines.Should().NotContain(l => l.StartsWith("Environment=PATH=", StringComparison.Ordinal) || l.StartsWith("Environment=\"PATH=", StringComparison.Ordinal));
        DeployAssets.Text(Unit).Should().NotContain("ZYGGY_NODE_JSON").And.NotContain("--config").And.NotContain("WatchdogSec");
    }

    [Fact]
    public void DropIn_ExactlyTwoLoadCredentialLinesWithTheStoreIds()
    {
        // Act
        var settings = DeployAssets.Lines(DeployAssets.Text(DropIn))
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('['))
            .ToList();

        // Assert: the ids are computed from the store's mapping, never hard-coded
        settings.Should().Equal(
            "LoadCredential=" + LinuxSecretStore.CredentialId(Acme, SecretName.Parse("hmac/2")) + ":<zyggy-home>/.local/share/zyggy/secrets/acme/hmac/2",
            "LoadCredential=" + LinuxSecretStore.CredentialId(Acme, SecretName.Parse("github-pat")) + ":<zyggy-home>/.local/share/zyggy/secrets/acme/github-pat");
    }

    [Fact]
    public void DropIn_EqualsTheExpectedTextByteForByte()
    {
        DeployAssets.Bytes(DropIn).Should().Equal(DeployAssets.ExpectedBytes("credentials.conf.example"));
    }

    [Fact]
    public void DropIn_CommentsNameTheCopyTargetAndTheRestart()
    {
        string text = DeployAssets.Text(DropIn);
        text.Should().Contain("/etc/systemd/system/zyggy-node.service.d/credentials.conf")
            .And.Contain("daemon-reload")
            .And.Contain("systemctl restart zyggy-node")
            .And.Contain("Inside the unit the credentials directory wins");
    }

    [Theory]
    [InlineData(Unit)]
    [InlineData(DropIn)]
    public void SystemdFiles_LfOnlyPlaceholdersOnlyNoSecret(string relativePath)
    {
        // Act
        string text = DeployAssets.Text(relativePath);

        // Assert
        text.Should().NotContain("\r");
        text.Should().EndWith("\n");
        text.Should().NotEndWith("\n\n");
        DeployAssets.Lines(text).Where(l => l.Length > 0 && char.IsWhiteSpace(l[l.Length - 1])).Should().BeEmpty("no trailing whitespace");
        text.Should().NotContain("/home/").And.NotContain("/srv/").And.NotContain("C:\\");
        text.Should().NotContainEquivalentOf(DeployAssets.OwnerNeedle);
        SecretSweep.AssertAbsent([text]);
    }

    [GeneratedRegex(@"\b(pat|token|hmac|secret|password)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretWord();
}
