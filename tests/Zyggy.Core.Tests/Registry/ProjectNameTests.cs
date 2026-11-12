using Zyggy.Core.Registry;

namespace Zyggy.Core.Tests.Registry;

public sealed class ProjectNameTests
{
    [Theory]
    [InlineData("calizr", "calizr")]
    [InlineData("My_App", "my-app")]
    [InlineData("Client Portal.v2", "client-portal-v2")]
    [InlineData("--", "")]
    [InlineData("ÄÖ", "")]
    [InlineData("_lead-and-trail_", "lead-and-trail")]
    public void Derive_FolderName_Label(string folder, string expected)
    {
        // Act + Assert
        ProjectName.Derive(folder).Should().Be(expected);
    }

    [Fact]
    public void Derive_SeventyCharacters_CutTo63WithoutTrailingHyphen()
    {
        // Arrange: the 63rd character is a separator, so a plain cut would end on "-"
        string folder = new string('a', 62) + "_" + new string('b', 7);

        // Act
        string name = ProjectName.Derive(folder);

        // Assert
        name.Should().Be(new string('a', 62));
        ProjectName.IsLabel(name).Should().BeTrue();
    }
}
