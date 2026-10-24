using Zyggy.Core.Bus;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Plan 08 Step 1: a job's <c>project</c> is a registry name or an absolute path, judged by shape on every OS, never by existence.</summary>
public sealed class ProjectReferenceTests
{
    [Theory]
    [InlineData("calizr", false)]
    [InlineData("/home/u/src/calizr", true)]
    [InlineData("C:/src/calizr", true)]
    [InlineData(@"C:\src\calizr", true)]
    public void TryParse_Accepted_ShapeAndIsPath(string text, bool isPath)
    {
        // Act
        bool ok = ProjectReference.TryParse(text, out ProjectReference result, out string? detail);

        // Assert
        ok.Should().BeTrue(detail);
        result.Value.Should().Be(text);
        result.IsPath.Should().Be(isPath);
        detail.Should().BeNull();
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "", "project is empty" },
        { "x\nsig: hmac-sha256:00", "project contains a control character" },
        { new string('a', 1025), "project is longer than 1024 characters" },
        { "../x", "project must not contain '..'" },
        { @"C:\src\..\x", "project must not contain '..'" },
        { "Calizr", "project must be a registry name ([a-z0-9-]) or an absolute path" },
        { "src/calizr", "project must be a registry name ([a-z0-9-]) or an absolute path" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void TryParse_Refused_Detail(string text, string expectedDetail)
    {
        // Act
        bool ok = ProjectReference.TryParse(text, out _, out string? detail);

        // Assert
        ok.Should().BeFalse();
        detail.Should().Be(expectedDetail);
    }

    [Fact]
    public void TryParse_Null_Empty()
    {
        // Act
        bool ok = ProjectReference.TryParse(null, out _, out string? detail);

        // Assert
        ok.Should().BeFalse();
        detail.Should().Be("project is empty");
    }
}
