using Zyggy.Core.M365;

namespace Zyggy.Core.Tests.M365;

/// <summary>The m365 paths: the shell's state directory on Central and its closed set of file names.</summary>
public sealed class M365PathsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "zyggy-ut-home");

    private static M365Paths Paths(params (string Key, string Value)[] extra)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HOME"] = Home };
        foreach (var (key, value) in extra)
        {
            env[key] = value;
        }

        return new M365Paths(env);
    }

    [Fact]
    public void Defaults_AreTheShellPathsUnderHome()
    {
        // Act
        var paths = Paths();

        // Assert
        paths.StateDirectory.Should().Be(Path.Join(Home, ".local", "state", "zyggy", "m365"));
        paths.DownloadRoot.Should().Be(Path.Join(Home, ".cache", "zyggy-m365-downloads"));
        paths.KeyFile.Should().Be(Path.Join(Home, ".config", "zyggy", "m365-app.key"));
        paths.CertificateFile.Should().Be(Path.Join(Home, ".config", "zyggy", "m365-app.cer"));
    }

    [Fact]
    public void Overrides_StateDirKeyAndCertificate()
    {
        // Act
        var paths = Paths(("ZYGGY_STATE_DIR", "/s"), ("ZYGGY_M365_KEY_FILE", "/k.key"), ("XDG_CONFIG_HOME", "/cfg"));

        // Assert
        paths.StateDirectory.Should().Be(Path.Join("/s", "m365"));
        paths.KeyFile.Should().Be("/k.key");
        paths.CertificateFile.Should().Be(Path.Join("/cfg", "zyggy", "m365-app.cer"));
    }

    [Theory]
    [InlineData("mail-watermark")]
    [InlineData("backfill-AAMkAGI2.watermark")]
    [InlineData("files-backfill-b!onedrive0001.watermark")]
    [InlineData("drive-b!onedrive0001.token")]
    [InlineData("replied-2026-10-01.ids")]
    [InlineData("actions.jsonl")]
    [InlineData("brief.jsonl")]
    [InlineData("brief-2026-10-01.json")]
    [InlineData("mail-backfill.json")]
    [InlineData("files-backfill.json")]
    public void StateFile_ShellName_InStateDirectory(string name)
    {
        // Assert
        Paths().StateFile(name).Should().Be(Path.Join(Paths().StateDirectory, name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../mail-watermark")]
    [InlineData("drive-a/b.token")]
    [InlineData("proposals.jsonl")]
    [InlineData("replied-2026-9-1.ids")]
    [InlineData("mail-watermark.tmp")]
    public void StateFile_OtherName_Throws(string name)
    {
        // Act
        var act = () => Paths().StateFile(name);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
