using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

/// <summary>
/// Spec 53 slice 1: the optional job fields <c>branch</c> and <c>description</c> — typed parse of the three hand-derived goldens
/// (AC-1), the factory, and the parse-time refusals with <c>invalid_field</c> (AC-2).
/// </summary>
public sealed class JobBranchEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly DateTimeOffset T = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The golden job (<c>worktree: true</c>), the base for every refusal case.</summary>
    private static EnvelopeText Job => EnvelopeText.From(Golden.Md("job"));

    // AC-1: both fields, branch only, and a description that needs YAML quoting read back as written.
    [Theory]
    [InlineData("job-branch", "feature/12-cart", "Cart totals and tax lines")]
    [InlineData("job-branch-only", "Feature/Cart-Totals_v2.1", null)]
    [InlineData("job-description-quoted", null, "- Cart: totals #2 for \"v2\" isn't final")]
    public void Parse_GoldenWithBranchFields_YieldsTypedFields(string name, string? branch, string? description)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md(name), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        JobFields job = result.Envelope!.Job!;
        new { job.Branch, job.Description, job.Worktree, job.Project }
            .Should().BeEquivalentTo(new { Branch = branch, Description = description, Worktree = true, Project = "shop" });
    }

    // AC-1: a job without the fields reads both as absent (the existing golden, unchanged).
    [Fact]
    public void Parse_GoldenJobWithoutBranchFields_BothNull()
    {
        // Act
        JobFields job = EnvelopeParser.Parse(Golden.Md("job"), Acme).Envelope!.Job!;

        // Assert
        new { job.Branch, job.Description }.Should().BeEquivalentTo(new { Branch = (string?)null, Description = (string?)null });
    }

    // AC-1: the factory writes both fields, and the signed bytes of a job built with them equal the hand-derived golden.
    [Fact]
    public async Task CreateJob_WithBranchAndDescription_SignsToTheGoldenBytes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var header = new EnvelopeHeader(Acme, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F2"), MachineName.Parse("central"), MachineName.Parse("home-laptop"), T);
        var fields = new JobFields("shop", Worktree: true, Branch: "feature/12-cart", Description: "Cart totals and tax lines");
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));

        // Act
        Model created = Model.CreateJob(header, fields, "Continue the cart feature.\n"u8.ToArray());
        EnvelopeResult signed = await signer.SignAsync(created, TestKeys.Acme1, ct);

        // Assert
        created.Job.Should().BeEquivalentTo(fields);
        EnvelopeWriter.Serialize(signed.Envelope!).Should().Equal(Golden.Md("job-branch"));
    }

    // AC-1: the factory writes the quoting case so that it reads back unchanged.
    [Fact]
    public async Task CreateJob_DescriptionNeedingQuotes_SignsToTheGoldenBytesAndReadsBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(ct));
        const string Text = "- Cart: totals #2 for \"v2\" isn't final";
        var header = new EnvelopeHeader(Acme, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F4"), MachineName.Parse("central"), MachineName.Parse("home-laptop"), T);

        // Act
        Model created = Model.CreateJob(header, new JobFields("shop", Worktree: true, Description: Text), "Continue the cart feature.\n"u8.ToArray());
        byte[] written = EnvelopeWriter.Serialize((await signer.SignAsync(created, TestKeys.Acme1, ct)).Envelope!);

        // Assert
        new { Written = written, EnvelopeParser.Parse(written, Acme).Envelope!.Job!.Description }
            .Should().BeEquivalentTo(new { Written = Golden.Md("job-description-quoted"), Description = Text });
    }

    // AC-2: a branch name of the right shape, mixed case included, is accepted.
    [Theory]
    [InlineData("feature/12-cart")]
    [InlineData("Feature/12-Cart")]
    [InlineData("a")]
    [InlineData("docs/v1.2_notes")]
    [InlineData("zyggy")]
    [InlineData("my-zyggy/x")]
    [InlineData("x.locked/y")]
    public void Parse_BranchOfValidShape_IsAccepted(string branch)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithKey("branch", "'" + branch + "'").ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.Job!.Branch.Should().Be(branch);
    }

    [Fact]
    public void Parse_BranchOfHundredCharacters_IsAccepted()
    {
        // Arrange
        string branch = "feature/" + new string('a', 92);

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithKey("branch", branch).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
    }

    public static TheoryData<string, string> BadBranches => new()
    {
        { "zyggy/ prefix", "zyggy/x" },
        { "Zyggy/ prefix in another case", "Zyggy/x" },
        { "refs/ prefix", "refs/heads/x" },
        { "Refs/ prefix in another case", "Refs/x" },
        { "HEAD", "HEAD" },
        { "double dot", "a..b" },
        { "segment ending in .lock", "x.lock" },
        { "inner segment ending in .lock", "a.lock/b" },
        { "101 characters", "feature/" + new string('a', 93) },
        { "empty segment", "a//b" },
        { "leading slash", "/a" },
        { "trailing slash", "a/" },
        { "trailing dot", "a." },
        { "segment starting with a dot", "a/.b" },
        { "segment starting with a dash", "-a" },
        { "inner segment starting with a dash", "a/-b" },
        { "a space", "'a b'" },
        { "a colon", "'a:b'" },
        { "a tilde", "a~1" },
        { "a non-ASCII letter", "café" },
        { "a sequence", "[a]" },
    };

    // AC-2: one case per rule of the branch shape.
    [Theory]
    [MemberData(nameof(BadBranches))]
    public void Parse_BranchBreakingTheShapeRule_InvalidFieldBranch(string because, string yamlValue)
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithKey("branch", yamlValue).ToBytes());

        // Assert
        new { rejection.Reason, rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = "branch" }, because);
    }

    // AC-2: branch only with worktree: true.
    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public void Parse_BranchWithoutWorktreeTrue_InvalidFieldBranch(string? worktree)
    {
        // Arrange
        EnvelopeText text = Job.WithKey("branch", "feature/12-cart");
        text = worktree is null ? text.WithoutKey("worktree") : text.WithValue("worktree", worktree);

        // Act
        EnvelopeRejection rejection = Reject(text.ToBytes());

        // Assert
        new { rejection.Reason, rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = "branch" });
    }

    // Spec 53 D4: a description without a branch is allowed, also on a job without a worktree.
    [Fact]
    public void Parse_DescriptionWithoutBranchOrWorktree_IsAccepted()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithValue("worktree", "false").WithKey("description", "Cart totals").ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.Job!.Description.Should().Be("Cart totals");
    }

    [Fact]
    public void Parse_DescriptionOfTwoHundredCharacters_IsAccepted()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Job.WithKey("description", new string('d', 200)).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
    }

    public static TheoryData<string, string> BadDescriptions => new()
    {
        { "a tab (control character)", "\"a\\tb\"" },
        { "a line break", "\"line1\\nline2\"" },
        { "a DEL", "\"a\\x7Fb\"" },
        { "201 characters", new string('d', 201) },
        { "only spaces", "'   '" },
        { "a sequence", "[a]" },
    };

    // AC-2: one case per rule of the description shape.
    [Theory]
    [MemberData(nameof(BadDescriptions))]
    public void Parse_DescriptionBreakingTheShapeRule_InvalidFieldDescription(string because, string yamlValue)
    {
        // Act
        EnvelopeRejection rejection = Reject(Job.WithKey("description", yamlValue).ToBytes());

        // Assert
        new { rejection.Reason, rejection.Field }.Should().BeEquivalentTo(new { Reason = EnvelopeRejectionReason.InvalidField, Field = "description" }, because);
    }

    // AC-2: the factory refuses what the parser refuses (one validation path), so no sender can sign such a job.
    [Theory]
    [InlineData("zyggy/x", null, true, "branch")]
    [InlineData("feature/x", null, false, "branch")]
    [InlineData(null, "a\nb", false, "description")]
    public void CreateJob_FieldBreakingTheShapeRule_ThrowsArgumentExceptionNamingIt(string? branch, string? description, bool worktree, string field)
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.Parse("01J8Y3N7Q2X9Z4A5B6C7D8E9F2"), MachineName.Parse("central"), MachineName.Parse("home-laptop"), T);

        // Act
        Action act = () => Model.CreateJob(header, new JobFields("shop", Worktree: worktree, Branch: branch, Description: description), "x\n"u8.ToArray());

        // Assert
        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(field);
    }

    private static EnvelopeRejection Reject(byte[] file)
    {
        EnvelopeResult result = EnvelopeParser.Parse(file, Acme);
        result.IsAccepted.Should().BeFalse();
        return result.Rejection!;
    }
}
