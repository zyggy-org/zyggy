using Zyggy.Core.Memory;

namespace Zyggy.Core.Tests.Memory;

/// <summary>Spec 38 CLI: <c>archive offload [--dry-run | --probe | --retry &lt;project&gt;/&lt;slug&gt;]</c>, at most one mode.</summary>
public sealed class ArchiveArgumentsOffloadTests
{
    [Theory]
    [InlineData(new string[0], "Run")]
    [InlineData(new[] { "--dry-run" }, "DryRun")]
    [InlineData(new[] { "--probe" }, "Probe")]
    [InlineData(new[] { "--retry", "zyggy/quote-2026" }, "Retry")]
    public void ParseOffload_Valid_Mode(string[] args, string mode)
    {
        // Act
        var (request, error) = ArchiveArguments.ParseOffload(args);

        // Assert
        error.Should().BeNull();
        request!.Mode.Should().Be(Enum.Parse<OffloadMode>(mode));
    }

    [Fact]
    public void ParseOffload_Retry_Reference()
    {
        // Act
        var (request, _) = ArchiveArguments.ParseOffload(["--retry", "zyggy/quote-2026"]);

        // Assert
        request!.Retry.Should().Be(new ArchiveRef(Slug.Parse("zyggy"), Slug.Parse("quote-2026")));
    }

    [Theory]
    [InlineData(new[] { "--dry-run", "--probe" }, "give at most one of --dry-run, --probe, --retry")]
    [InlineData(new[] { "--retry" }, "--retry needs <project>/<slug>")]
    [InlineData(new[] { "--retry", "zyggy" }, "--retry: 'zyggy' is not <project>/<slug>")]
    [InlineData(new[] { "--retry", "Zyggy/x" }, "--retry: 'Zyggy/x' is not <project>/<slug>")]
    [InlineData(new[] { "--bogus" }, "unexpected argument '--bogus'")]
    public void ParseOffload_Invalid_Error(string[] args, string expected)
    {
        // Act
        var (request, error) = ArchiveArguments.ParseOffload(args);

        // Assert
        request.Should().BeNull();
        error.Should().Be(expected);
    }
}
