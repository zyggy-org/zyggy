using System.Text;

using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary><c>zy_atomic_append</c> of the template's <c>lib.sh</c>: every existing byte kept except the front-matter <c>updated:</c> value.</summary>
public sealed class FactLineWriterTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    private string Target => Path.Combine(_directory, "inbox", "remember-2026-09-30.md");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Append_NewFile_ExactFrontMatterThenLine()
    {
        // Act
        FactLineWriter.Append(Target, "remember 2026-09-30", "facts stated by the owner on 2026-09-30 (remember skill)", Today, ["- [stated] 2026-09-30: x"]);

        // Assert
        Read(Target).Should().Be(
            "---\nname: remember 2026-09-30\ndescription: facts stated by the owner on 2026-09-30 (remember skill)\nupdated: 2026-09-30\n---\n- [stated] 2026-09-30: x\n");
    }

    [Fact]
    public void Append_NewFileWithColonInDescription_NoYamlQuoting()
    {
        // Act
        FactLineWriter.Append(Target, "a: b", "c: d # e", Today, ["- x"]);

        // Assert
        Read(Target).Should().Be("---\nname: a: b\ndescription: c: d # e\nupdated: 2026-09-30\n---\n- x\n");
    }

    [Fact]
    public void Append_Existing_PreservesEveryByteButUpdatedValue()
    {
        // Arrange
        Write(Target, "---\nname: remember\ndescription: \"quoted: value\"\nx-unknown: kept\n\nupdated: 2026-09-01\n---\nbody line\r\n\r\n- [stated] 2026-09-01: old\r\n");

        // Act
        FactLineWriter.Append(Target, "ignored", "ignored", Today, ["- [stated] 2026-09-30: new"]);

        // Assert
        Read(Target).Should().Be(
            "---\nname: remember\ndescription: \"quoted: value\"\nx-unknown: kept\n\nupdated: 2026-09-30\n---\nbody line\r\n\r\n- [stated] 2026-09-01: old\r\n- [stated] 2026-09-30: new\n");
    }

    [Fact]
    public void Append_UpdatedWithoutSpace_Rewritten()
    {
        // Arrange
        Write(Target, "---\nupdated:2026-01-01\n---\n");

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);

        // Assert
        Read(Target).Should().Be("---\nupdated: 2026-09-30\n---\n- x\n");
    }

    [Fact]
    public void Append_UpdatedOutsideFrontMatter_Untouched()
    {
        // Arrange
        Write(Target, "---\nname: a\n---\nupdated: keep me\n");

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);

        // Assert
        Read(Target).Should().Be("---\nname: a\n---\nupdated: keep me\n- x\n");
    }

    [Fact]
    public void Append_NoFrontMatter_LinesAppendedOnly()
    {
        // Arrange
        Write(Target, "hello\nupdated: 2026-01-01\n");

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x", "- y"]);

        // Assert
        Read(Target).Should().Be("hello\nupdated: 2026-01-01\n- x\n- y\n");
    }

    [Fact]
    public void Append_FrontMatterWithCrLfOpening_NotTreatedAsFrontMatter()
    {
        // Arrange
        Write(Target, "---\r\nupdated: 2026-01-01\r\n---\r\n");

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);

        // Assert
        Read(Target).Should().Be("---\r\nupdated: 2026-01-01\r\n---\r\n- x\n");
    }

    [Fact]
    public void Append_ExistingWithoutFinalNewline_AddsOneLikeAwk()
    {
        // Arrange
        Write(Target, "---\nupdated: 2026-01-01\n---\nlast");

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);

        // Assert
        Read(Target).Should().Be("---\nupdated: 2026-09-30\n---\nlast\n- x\n");
    }

    [Fact]
    public void Append_EmptyExistingFile_OnlyTheLine()
    {
        // Arrange
        Write(Target, string.Empty);

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);

        // Assert
        Read(Target).Should().Be("- x\n");
    }

    [Fact]
    public void Append_InvalidUtf8Bytes_PreservedExactly()
    {
        // Arrange
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        byte[] original = [.. "---\nupdated: 1\n---\n"u8, 0xFF, 0xFE, (byte)'\n'];
        File.WriteAllBytes(Target, original);

        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);

        // Assert
        File.ReadAllBytes(Target).Should().Equal([.. "---\nupdated: 2026-09-30\n---\n"u8, 0xFF, 0xFE, (byte)'\n', .. "- x\n"u8]);
    }

    [Fact]
    public void Append_NeverLeavesTempFile()
    {
        // Act
        FactLineWriter.Append(Target, "n", "d", Today, ["- x"]);
        FactLineWriter.Append(Target, "n", "d", Today, ["- y"]);

        // Assert
        Directory.EnumerateFiles(Path.GetDirectoryName(Target)!).Select(Path.GetFileName).Should().Equal("remember-2026-09-30.md");
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
    }

    private static string Read(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
}
