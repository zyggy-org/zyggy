using Zyggy.Core.Bus;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 53 slice 7, AC-15 (unit side): the <c>--worktree</c>, <c>--branch</c> and <c>--description</c> options of <c>zyggy submit</c> are
/// checked before signing — <c>--branch</c> needs <c>--worktree</c>, both values have the §4 shape, and the branch is not the target
/// project's <c>default_branch</c> in the target machine's registry file (any letter case); without an entry the node decides.
/// </summary>
public sealed class JobRequestNamedBranchTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    public static TheoryData<string> MalformedBranches => new()
    {
        string.Empty, "zyggy/x", "Zyggy/x", "refs/heads/x", "HEAD", "a..b", "x.lock", "a//b", "-x", "a/", new string('b', 101), "a b",
    };

    public static TheoryData<string> MalformedDescriptions => new()
    {
        string.Empty, "   ", "line one\nline two", "tab\there", new string('d', 201),
    };

    // AC-15: --branch without --worktree is refused (Q7: never implied).
    [Fact]
    public void TryWithBranch_BranchWithoutWorktree_RefusedOnBranch()
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: false, "feature/12-cart", null, null, out JobRequest? named);

        // Assert
        new { error?.Field, Named = named }.Should().BeEquivalentTo(new { Field = "branch", Named = (JobRequest?)null });
    }

    // AC-15: a malformed name is refused before signing.
    [Theory]
    [MemberData(nameof(MalformedBranches))]
    public void TryWithBranch_MalformedBranch_RefusedOnBranch(string branch)
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: true, branch, null, null, out JobRequest? named);

        // Assert
        new { error?.Field, Named = named }.Should().BeEquivalentTo(new { Field = "branch", Named = (JobRequest?)null });
    }

    // AC-15: the target project's registry default branch is refused, in any letter case.
    [Theory]
    [InlineData("main")]
    [InlineData("Main")]
    [InlineData("MAIN")]
    public void TryWithBranch_RegistryDefaultBranchAnyCase_RefusedOnBranchNamingDefault(string branch)
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: true, branch, null, Registry(HomeLaptop, ("shop", "main")), out JobRequest? named);

        // Assert
        new { error?.Field, DetailNamesDefault = error?.Detail.Contains("default branch main", StringComparison.Ordinal), Named = named }
            .Should().BeEquivalentTo(new { Field = "branch", DetailNamesDefault = (bool?)true, Named = (JobRequest?)null });
    }

    // AC-15: the skip rule — no registry file, the project not in it, an entry without default_branch, a path-named project, or a file of
    // another machine: the job is accepted and the node decides at run time.
    [Theory]
    [InlineData("shop", "absent")]
    [InlineData("shop", "other-project")]
    [InlineData("shop", "no-default")]
    [InlineData("/home/alice/src/shop", "path")]
    [InlineData("shop", "other-machine")]
    public void TryWithBranch_NoUsableRegistryEntry_AcceptedNodeDecides(string project, string registry)
    {
        // Arrange
        JobRequest request = Request(project);
        RegistryDocument? document = registry switch
        {
            "absent" => null,
            "other-project" => Registry(HomeLaptop, ("calizr", "main")),
            "no-default" => Registry(HomeLaptop, ("shop", null)),
            "path" => Registry(HomeLaptop, ("shop", "main")),
            _ => Registry(MachineName.Parse("central"), ("shop", "main")),
        };

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: true, "main", null, document, out JobRequest? named);

        // Assert
        new { Error = error, named?.Branch }.Should().BeEquivalentTo(new { Error = (JobRequestError?)null, Branch = "main" });
    }

    // AC-15: a malformed description is refused before signing.
    [Theory]
    [MemberData(nameof(MalformedDescriptions))]
    public void TryWithBranch_MalformedDescription_RefusedOnDescription(string description)
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: true, "feature/12-cart", description, null, out JobRequest? named);

        // Assert
        new { error?.Field, Named = named }.Should().BeEquivalentTo(new { Field = "description", Named = (JobRequest?)null });
    }

    // AC-15: accepted values are carried into the request, the branch and description unchanged.
    [Fact]
    public void TryWithBranch_Accepted_SetsWorktreeBranchAndDescription()
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: true, "feature/12-cart", "Cart, slice 2: totals", Registry(HomeLaptop, ("shop", "main")), out JobRequest? named);

        // Assert
        new { Error = error, named?.Worktree, named?.Branch, named?.Description }.Should().BeEquivalentTo(new
        {
            Error = (JobRequestError?)null,
            Worktree = (bool?)true,
            Branch = "feature/12-cart",
            Description = "Cart, slice 2: totals",
        });
    }

    // D4: a description without a branch (and without a worktree) is allowed.
    [Fact]
    public void TryWithBranch_DescriptionWithoutBranchOrWorktree_Accepted()
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: false, null, "Notes on the cart", null, out JobRequest? named);

        // Assert
        new { Error = error, named?.Worktree, named?.Branch, named?.Description }.Should().BeEquivalentTo(new
        {
            Error = (JobRequestError?)null,
            Worktree = (bool?)false,
            Branch = (string?)null,
            Description = "Notes on the cart",
        });
    }

    // AC-1 / AC-12: without both options the request is the one TryCreate built, with only Worktree set.
    [Fact]
    public void TryWithBranch_NeitherOption_SameRequestWithWorktree()
    {
        // Arrange
        JobRequest request = Request("shop");

        // Act
        JobRequestError? error = request.TryWithBranch(worktree: true, null, null, null, out JobRequest? named);

        // Assert
        new { Error = error, Named = named }.Should().BeEquivalentTo(new { Error = (JobRequestError?)null, Named = request with { Worktree = true } });
    }

    private static JobRequest Request(string project)
    {
        JobRequest.TryCreate("home-laptop", project, null, null, null, null, "Change the shop.\n", out JobRequest? request).Should().BeNull();
        return request!;
    }

    private static RegistryDocument Registry(MachineName machine, params (string Name, string? DefaultBranch)[] projects) =>
        new(Acme, machine, RegistryOs.Linux, null, null, null, [.. projects.Select(p => new RegistryProject(p.Name, "/home/alice/src/" + p.Name, p.DefaultBranch, [], [], null))]);
}
