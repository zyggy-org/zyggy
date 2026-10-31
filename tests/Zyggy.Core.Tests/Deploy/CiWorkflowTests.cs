using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Deploy;

/// <summary>Spec 10 AC-5 (and the CI half of AC-2): the release artefact carries <c>deploy/</c> under the checksums and the installer is parsed on both runners.</summary>
public sealed class CiWorkflowTests
{
    private static string Workflow => File.ReadAllText(Path.Combine(RepoRoot.Find(), ".github", "workflows", "ci.yml"));

    [Fact]
    public void Ci_CopiesDeployIntoTheArtefact()
    {
        // Act
        string text = Workflow;
        int publishNode = text.IndexOf("Publish zyggy-node", StringComparison.Ordinal);
        int copy = text.IndexOf("Copy-Item -Recurse deploy artifacts/${{ matrix.rid }}/deploy", StringComparison.Ordinal);
        int upload = text.IndexOf("name: zyggy-${{ matrix.rid }}", StringComparison.Ordinal);

        // Assert: after the node publish, before the artefact upload
        copy.Should().BeGreaterThan(publishNode);
        upload.Should().BeGreaterThan(copy);
    }

    [Fact]
    public void Ci_Sha256SumsEnumeratesDeployFiles()
    {
        // Act
        string step = Section(Workflow, "Write SHA256SUMS", "Smoke-run the published binaries");

        // Assert: still sha256sum format and LF, now over the binaries and every deploy file
        step.Should().Contain("Get-ChildItem -Recurse -File \"$dir/deploy\"");
        step.Should().Contain("\"$hash  $name`n\"");
        step.Should().Contain("[IO.File]::WriteAllText");
    }

    [Fact]
    public void Ci_ParsesInstallScriptOnBothRunners()
    {
        // Act
        string step = Section(Workflow, "Parse install.ps1", "name: zyggy-${{ matrix.rid }}");

        // Assert
        step.Should().Contain("[System.Management.Automation.Language.Parser]::ParseFile('deploy/windows/install.ps1'");
        step.Should().Contain("exit 1");
        step.Should().NotContain("if: runner.os ==");
    }

    [Fact]
    public void Ci_StillPublishesBothBinariesAndSmokeRunsThem()
    {
        Workflow.Should().Contain("dotnet publish src/Zyggy.Cli -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -o artifacts/${{ matrix.rid }}");
        Workflow.Should().Contain("dotnet publish src/Zyggy.Node -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -o artifacts/${{ matrix.rid }}");
        Workflow.Should().Contain("$out = & \"$dir/$name\" --version");
    }

    [Theory]
    [InlineData("Install-Module")]
    [InlineData("pester")]
    [InlineData("choco")]
    [InlineData("apt-get")]
    [InlineData("npm")]
    [InlineData("pip")]
    public void Ci_NoNewToolChain(string forbidden)
    {
        Workflow.Should().NotContainEquivalentOf(forbidden);
    }

    [Theory]
    [InlineData("zyggy-node.service")]
    [InlineData("credentials.conf.example")]
    [InlineData("install.ps1")]
    [InlineData("README.md")]
    [InlineData("install-node.md")]
    public void DeployReadme_NamesTheFourAssetsAndTheRunbookHome(string token)
    {
        DeployAssets.Text("README.md").Should().Contain(token);
    }

    [Fact]
    public void DeployTree_ExactlyTheFiveFiles()
    {
        // Assert: a sixth shipped file is a plan change (the fifth, pickup.conf.example, is spec 13 AC-33)
        DeployAssets.AllFiles().Should().Equal(
            "README.md",
            "systemd/zyggy-node.service",
            "systemd/zyggy-node.service.d/credentials.conf.example",
            "systemd/zyggy-node.service.d/pickup.conf.example",
            "windows/install.ps1");
    }

    // The workflow text from the step named `start` up to the step or key named `end`.
    private static string Section(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, end);
        return text[from..to];
    }
}
