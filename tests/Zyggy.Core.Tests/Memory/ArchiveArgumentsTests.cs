using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

public sealed class ArchiveArgumentsTests
{
    private static readonly string AbsoluteFile = Path.Combine(Path.GetTempPath(), "zyggy-ut-archive", "quote.pdf");

    public static TheoryData<string[], string> UsageErrors() => new()
    {
        { [], "--project is required" },
        { [.. Add(), "--project", "zyggy"], "--project given twice" },
        { Add("--project", "Has Space"), "--project is not a slug" },
        { Add("--name", ""), "--name is empty" },
        { Add("--name", new string('a', 101)), "--name longer than 100 characters" },
        { Add("--description", ""), "--description is empty" },
        { Add("--description", new string('d', 150)), "--description longer than 149 characters" },
        { Add("--file", "relative/x.pdf"), "--file must be an absolute path" },
        { [.. Add(), "--bogus"], "unexpected argument '--bogus'" },
        { [.. Add(), "--slug", "Bad Slug"], "--slug is not a slug" },
        { ["--project", "zyggy", "--name", "Quote", "--description", "Roof", "--file"], "--file needs a value" },
    };

    public static TheoryData<string, string?> SlugRows() => new()
    {
        { "Quote 2026 — Roof", "quote-2026-roof" },
        { "  Hello, World!  ", "hello-world" },
        { "ÉTÉ", "t" },
        { string.Concat(Enumerable.Repeat("abcdefghi ", 7)), "abcdefghi-abcdefghi-abcdefghi-abcdefghi-abcdefghi-abcdefghi" },
        { "###", null },
    };

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public void ParseAdd_UsageErrors(string[] args, string error)
    {
        // Act
        var (request, actual) = ArchiveArguments.ParseAdd(args);

        // Assert
        request.Should().BeNull();
        actual.Should().Be(error);
    }

    [Fact]
    public void ParseAdd_Valid_ReturnsRequest()
    {
        // Act
        var (request, error) = ArchiveArguments.ParseAdd(Add());

        // Assert
        error.Should().BeNull();
        request.Should().Be(new ArchiveAddRequest(Slug.Parse("zyggy"), "Quote 2026", "Roof repair quote", AbsoluteFile, Slug.Parse("quote-2026")));
    }

    [Fact]
    public void ParseAdd_ExplicitSlug_Kept()
    {
        // Act
        var (request, _) = ArchiveArguments.ParseAdd([.. Add(), "--slug", "roof-quote"]);

        // Assert
        request!.Slug.Value.Should().Be("roof-quote");
    }

    [Theory]
    [MemberData(nameof(SlugRows))]
    public void DeriveSlug_Rows(string name, string? expected)
    {
        // Act
        var (request, error) = ArchiveArguments.ParseAdd(Add("--name", name));

        // Assert
        if (expected is null)
        {
            request.Should().BeNull();
            error.Should().Be("cannot derive a slug from --name; pass --slug");
        }
        else
        {
            error.Should().BeNull();
            request!.Slug.Value.Should().Be(expected);
            request.Slug.Value.Length.Should().BeLessThanOrEqualTo(60);
        }
    }

    [Fact]
    public void ParseRemove_ProjectSlashSlug_Ok()
    {
        // Act
        var (reference, error) = ArchiveArguments.ParseRemove(["zyggy/quote-2026"]);

        // Assert
        error.Should().BeNull();
        reference.Should().Be(new ArchiveRef(Slug.Parse("zyggy"), Slug.Parse("quote-2026")));
    }

    [Theory]
    [InlineData("zyggy")]
    [InlineData("zyggy/")]
    [InlineData("a/b/c")]
    [InlineData("Zyggy/x")]
    public void ParseRemove_Invalid(string argument)
    {
        // Act
        var (reference, error) = ArchiveArguments.ParseRemove([argument]);

        // Assert
        reference.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void ParseList_Flags_Ok()
    {
        // Act
        var (request, error) = ArchiveArguments.ParseList(["--project", "zyggy", "--unindexed", "--json"]);

        // Assert
        error.Should().BeNull();
        request.Should().Be(new ArchiveListRequest(Slug.Parse("zyggy"), true, true));
        ArchiveArguments.ParseList([]).Request.Should().Be(new ArchiveListRequest(null, false, false));
    }

    [Fact]
    public void ParseList_UnknownFlag_Error()
    {
        // Act
        var (request, error) = ArchiveArguments.ParseList(["--all"]);

        // Assert
        request.Should().BeNull();
        error.Should().Be("unexpected argument '--all'");
    }

    // A valid add command line with one option's value replaced by the override pair.
    private static string[] Add(params string[] overrides)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--project"] = "zyggy",
            ["--name"] = "Quote 2026",
            ["--description"] = "Roof repair quote",
            ["--file"] = AbsoluteFile,
        };
        for (var i = 0; i < overrides.Length; i += 2)
        {
            values[overrides[i]] = overrides[i + 1];
        }

        return [.. values.SelectMany(p => new[] { p.Key, p.Value })];
    }
}
