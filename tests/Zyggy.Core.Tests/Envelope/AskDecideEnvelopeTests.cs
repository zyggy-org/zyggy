using System.Text;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Envelope;

// AC-1 (question and decision part): both types parse, sign and verify; a missing or misshapen field is refused by name.
public sealed class AskDecideEnvelopeTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 11, 5, 0, TimeSpan.Zero);

    private static readonly EnvelopeId QuestionId = EnvelopeId.Parse("01M7F2Q0DDDDDDDDDDDDDDDDDD");

    private static EnvelopeText Question => EnvelopeText.From(Golden.Md("question"));

    private static EnvelopeText Decision => EnvelopeText.From(Golden.Md("decision"));

    [Fact]
    public void Parse_QuestionGolden_TypedFields()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("question"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        Model question = result.Envelope!;
        question.Type.Should().Be(EnvelopeType.Question);
        question.Header.To.Should().Be(MachineName.Central);
        question.Question!.Project.Should().Be("calizr");
        question.Question.Default.Should().Be("use the machine named central");
        question.Question.Options.Should().Equal("the node with role central", "the machine named central");
        question.Question.Session.Should().Be("5b3fd3ac");
        question.Question.Job.Should().BeNull();
        question.BodyText.Should().Be("Is Central the node with role central or the machine named central?\n");
    }

    [Fact]
    public void Parse_DecisionGolden_TypedFields()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Golden.Md("decision"), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        Model decision = result.Envelope!;
        decision.Type.Should().Be(EnvelopeType.Decision);
        decision.Header.From.Should().Be(MachineName.Central);
        decision.Header.To.Should().Be(HomeLaptop);
        decision.Header.InReplyTo.Should().Be(QuestionId);
        decision.Decision!.Kind.Should().Be(DecisionKind.Answer);
        decision.Decision.Basis.Should().Be(DecisionBasis.Verified);
        decision.Decision.Undo.Should().Be("nothing to undo");
        decision.Decision.Mandate.Should().Be(EnvelopeId.Parse("01M7F1M0FFFFFFFFFFFFFFFFFF"));
        decision.Decision.Project.Should().Be("calizr");
        decision.Decision.Step.Should().Be("slice 2");
        decision.Decision.Evidence.Should().Equal("01M7F2Q0DDDDDDDDDDDDDDDDDD", "9f2c1ab");
    }

    [Theory]
    [InlineData("question")]
    [InlineData("decision")]
    public async Task Goldens_CanonicalBytesAndSignatureEqualTheFiles_AndVerify(string name)
    {
        // Arrange
        var signer = new EnvelopeSigner(await TestKeys.StoreWithBothAsync(TestContext.Current.CancellationToken));
        Model envelope = EnvelopeParser.Parse(Golden.Md(name), Acme).Envelope!;

        // Act
        byte[] canonical = EnvelopeSigner.Canonicalize(envelope);
        EnvelopeResult signed = await signer.SignAsync(envelope, TestKeys.Acme1, TestContext.Current.CancellationToken);
        EnvelopeResult verified = await signer.ParseAndVerifyAsync(Golden.Md(name), Acme, TestContext.Current.CancellationToken);

        // Assert
        canonical.Should().Equal(Golden.Canonical(name));
        signed.Envelope!.Signature.Should().Be("hmac-sha256:" + Golden.Sig(name));
        verified.IsAccepted.Should().BeTrue(verified.Rejection?.ToString());
    }

    [Theory]
    [InlineData("question", "project")]
    [InlineData("question", "default")]
    [InlineData("decision", "in_reply_to")]
    [InlineData("decision", "kind")]
    [InlineData("decision", "basis")]
    [InlineData("decision", "undo")]
    public void Parse_MandatoryFieldRemoved_MissingFieldNamingIt(string golden, string key)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(EnvelopeText.From(Golden.Md(golden)).WithoutKey(key).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [InlineData("question", "to", "home-laptop")]
    [InlineData("question", "project", "Calizr")]
    [InlineData("question", "session", "5b3fd3a")]
    [InlineData("question", "options", "one option")]
    [InlineData("question", "options", "[a, b, c, d, e, f]")]
    [InlineData("decision", "from", "home-laptop")]
    [InlineData("decision", "in_reply_to", "not-a-ulid")]
    [InlineData("decision", "kind", "approve")]
    [InlineData("decision", "kind", "Answer")]
    [InlineData("decision", "basis", "assumed")]
    [InlineData("decision", "mandate", "not-a-ulid")]
    [InlineData("decision", "project", "Calizr")]
    [InlineData("decision", "step", "Slice_2")]
    [InlineData("decision", "evidence", "[see the report]")]
    [InlineData("decision", "evidence", "[9F2C1AB]")]
    [InlineData("decision", "evidence", "9f2c1ab")]
    public void Parse_FieldOfWrongShape_InvalidFieldNamingIt(string golden, string key, string value)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(EnvelopeText.From(Golden.Md(golden)).WithValue(key, value).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be(key);
    }

    [Fact]
    public void Parse_QuestionWithSessionAndJob_InvalidFieldJob()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Question.WithKey("job", "01M7F1M0FFFFFFFFFFFFFFFFFF").ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be("job");
    }

    [Fact]
    public void Parse_QuestionWithNeitherSessionNorJob_MissingFieldSession()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Question.WithoutKey("session").ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.MissingField);
        result.Rejection.Field.Should().Be("session");
    }

    [Fact]
    public void Parse_QuestionAskedFromAJob_Accepted()
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Question.WithoutKey("session").WithKey("job", "01M7F1M0FFFFFFFFFFFFFFFFFF").ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        result.Envelope!.Question!.Job.Should().Be(EnvelopeId.Parse("01M7F1M0FFFFFFFFFFFFFFFFFF"));
    }

    [Theory]
    [InlineData("question", 600, true)]
    [InlineData("question", 601, false)]
    [InlineData("question", 0, false)]
    [InlineData("decision", 1200, true)]
    [InlineData("decision", 1201, false)]
    [InlineData("decision", 0, false)]
    public void Parse_BodyLength_AcceptedUpToTheCapAndNeverEmpty(string golden, int characters, bool accepted)
    {
        // Arrange
        byte[] body = Encoding.UTF8.GetBytes(new string('a', characters) + "\n");

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(EnvelopeText.From(Golden.Md(golden)).WithBody(body).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted);
        (result.Rejection?.Field ?? "body").Should().Be("body");
    }

    [Theory]
    [InlineData("default")]
    [InlineData("undo")]
    public void Parse_OneLineFieldOver200Characters_InvalidField(string key)
    {
        // Arrange
        EnvelopeText text = key == "default" ? Question : Decision;

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(text.WithValue(key, new string('a', 201)).ToBytes(), Acme);

        // Assert
        result.Rejection!.Reason.Should().Be(EnvelopeRejectionReason.InvalidField);
        result.Rejection.Field.Should().Be(key);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, false)]
    public void Parse_QuestionOptionLength_AcceptedUpTo200Characters(int characters, bool accepted)
    {
        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Question.WithValue("options", "[short, " + new string('a', characters) + "]").ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted);
        (result.Rejection?.Field ?? "options").Should().Be("options");
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Parse_DecisionEvidenceCount_AcceptedUpToTenItems(int items, bool accepted)
    {
        // Arrange
        string evidence = "[" + string.Join(", ", Enumerable.Range(0, items).Select(i => "9f2c1a" + (i % 10))) + "]";

        // Act
        EnvelopeResult result = EnvelopeParser.Parse(Decision.WithValue("evidence", evidence).ToBytes(), Acme);

        // Assert
        result.IsAccepted.Should().Be(accepted);
        (result.Rejection?.Field ?? "evidence").Should().Be("evidence");
    }

    [Fact]
    public void CreateQuestion_Fields_ReadBackThroughTheParserRules()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(T), HomeLaptop, MachineName.Central, T);

        // Act
        Model question = Model.CreateQuestion(header, new QuestionFields("calizr", "wait", ["one", "two"], Job: QuestionId), "Which one?\n"u8.ToArray());

        // Assert
        question.Type.Should().Be(EnvelopeType.Question);
        question.Question!.Options.Should().Equal("one", "two");
        question.Question.Job.Should().Be(QuestionId);
        question.FrontMatter.Entries.Keys.Should().NotContain("session");
    }

    [Fact]
    public void CreateDecision_FromALaptop_Throws()
    {
        // Arrange
        var header = new EnvelopeHeader(Acme, EnvelopeId.New(T), HomeLaptop, HomeLaptop, T, InReplyTo: QuestionId);

        // Act
        Action act = () => Model.CreateDecision(header, new DecisionFields(DecisionKind.Answer, DecisionBasis.Reported, "nothing to undo"), "Yes.\n"u8.ToArray());

        // Assert
        act.Should().Throw<ArgumentException>().WithMessage("*'from'*");
    }

    [Theory]
    [InlineData(DecisionKind.Answer, "answer")]
    [InlineData(DecisionKind.Accept, "accept")]
    [InlineData(DecisionKind.Correct, "correct")]
    [InlineData(DecisionKind.Next, "next")]
    [InlineData(DecisionKind.Stop, "stop")]
    [InlineData(DecisionKind.Escalate, "escalate")]
    [InlineData(DecisionKind.Refuse, "refuse")]
    [InlineData(DecisionKind.Resume, "resume")]
    public void DecisionKind_EveryMember_WireAndBack(DecisionKind member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out DecisionKind back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
        Enum.GetValues<DecisionKind>().Should().HaveCount(8);
    }

    [Theory]
    [InlineData(DecisionBasis.Verified, "verified")]
    [InlineData(DecisionBasis.Reported, "reported")]
    public void DecisionBasis_EveryMember_WireAndBack(DecisionBasis member, string wire)
    {
        // Act
        string text = EnvelopeWire.ToWire(member);
        bool ok = EnvelopeWire.TryFromWire(wire, out DecisionBasis back);

        // Assert
        text.Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(member);
        Enum.GetValues<DecisionBasis>().Should().HaveCount(2);
    }

    [Fact]
    public void EnvelopeType_QuestionAndDecision_AppendedAfterStatus()
    {
        // Assert
        EnvelopeWire.ToWire(EnvelopeType.Question).Should().Be("question");
        EnvelopeWire.ToWire(EnvelopeType.Decision).Should().Be("decision");
        ((int)EnvelopeType.Question, (int)EnvelopeType.Decision).Should().Be((5, 6));
    }
}
