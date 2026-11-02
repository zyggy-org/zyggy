using Zyggy.Core.Bus;
using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 41 AC-9, AC-10: the usage faults of <c>zyggy archive</c> with the <c>memory archive add</c> words, and the <c>central</c> refusal.</summary>
public sealed class ItemRequestTests
{
    private static readonly string File = Path.Combine(Path.GetTempPath(), "zyggy-ut-item", "restore-notes.md");

    public static TheoryData<string?, string?, string?, string?, string?, string?, string> UsageFaults() => new()
    {
        { "calizr", "!!!", "How the staging database was restored", File, null, null, "cannot derive a slug from --name; pass --slug" },
        { "Calizr", "Staging restore notes", "How the staging database was restored", File, null, null, "--project is not a slug" },
        { "calizr", "   ", "How the staging database was restored", File, null, null, "--name is empty" },
        { "calizr", new string('n', 101), "How the staging database was restored", File, null, null, "--name longer than 100 characters" },
        { "calizr", "Staging restore notes", new string('d', 150), File, null, null, "--description longer than 149 characters" },
        { "calizr", "Staging restore notes", "How the staging database was restored", "restore-notes.md", null, null, "--file must be an absolute path" },
        { "calizr", "Staging restore notes", "How the staging database was restored", File, null, "onedrive", "--via must be bus" },
        { null, "Staging restore notes", "How the staging database was restored", File, null, null, "--project is required" },
        { "calizr", "Staging restore notes", "How the staging database was restored", File, "Bad Slug", null, "--slug is not a slug" },
    };

    [Theory]
    [MemberData(nameof(UsageFaults))]
    public void TryCreate_UsageFaults_OneMessageEach(string? project, string? name, string? description, string? file, string? slug, string? via, string expected)
    {
        // Act
        ItemRequestError? error = ItemRequest.TryCreate(project, name, description, file, slug, via, out ItemRequest? request);

        // Assert
        error.Should().Be(new ItemRequestError(expected));
        request.Should().BeNull();
    }

    [Fact]
    public void TryCreate_Valid_SlugDerivedLikeArchiveAdd()
    {
        // Act
        ItemRequestError? error = ItemRequest.TryCreate("calizr", "Staging restore notes", "How the staging database was restored", File, null, "bus", out ItemRequest? request);

        // Assert
        error.Should().BeNull();
        request!.Project.Should().Be(Slug.Parse("calizr"));
        request.Slug.Should().Be(Slug.Parse(ArchiveArguments.DeriveSlug("Staging restore notes")));
        request.Slug.Value.Should().Be("staging-restore-notes");
        request.Name.Should().Be("Staging restore notes");
        request.Description.Should().Be("How the staging database was restored");
        request.SourcePath.Should().Be(File);
        request.ViaBus.Should().BeTrue();
    }

    [Fact]
    public void CheckSender_Central_UseMemoryArchiveAdd()
    {
        // Act
        ItemRequestError? error = ItemRequest.CheckSender(MachineName.Central);

        // Assert
        error.Should().Be(new ItemRequestError("this machine is central; use zyggy memory archive add"));
    }

    [Fact]
    public void CheckSender_HomeLaptop_Null()
    {
        // Act & Assert
        ItemRequest.CheckSender(MachineName.Parse("home-laptop")).Should().BeNull();
    }
}
