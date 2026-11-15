using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Memory;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

// AC-2 (questions, decisions, the archive move) and AC-6 (unit half): who may write a question and a decision, and what they may carry.
public sealed class AskDecideBusTests : IAsyncLifetime
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Central;

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 15, 11, 5, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, HomeLaptop);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private readonly RecordingLogger<QuestionAsker> _askLog = new();

    private InMemorySecretStore _store = null!;

    public static TheoryData<string, string> SecretSamples() => SecretPatternsTests.SecretSamples();

    public async ValueTask InitializeAsync()
    {
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void BusPaths_QuestionsAndDecisions_BuildAndParse()
    {
        // Arrange
        var id = EnvelopeId.New(T);

        // Act
        string question = BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), id);
        string decision = BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), id);
        bool questionParsed = BusPaths.TryParse(question, out BusLocation? questionAt);
        bool decisionParsed = BusPaths.TryParse(decision, out BusLocation? decisionAt);

        // Assert
        question.Should().Be($"tenants/acme/nodes/home-laptop/questions/{id.Value}.md");
        decision.Should().Be($"tenants/acme/nodes/central/decisions/{id.Value}.md");
        (questionParsed, decisionParsed).Should().Be((true, true));
        questionAt.Should().Be(new BusLocation(Acme, HomeLaptop, BusFolder.Questions, id));
        decisionAt.Should().Be(new BusLocation(Acme, Central, BusFolder.Decisions, id));
    }

    // AC-2: questions
    [Fact]
    public async Task PublishQuestionAsync_SignedQuestionFromMe_FileUnderMyQuestionsFolder_OneCommitWithSubject()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model question = await QuestionAsync(HomeLaptop, T, ct);
        string path = BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question.Header.Id);

        // Act
        WriteOutcome outcome = await Repository(HomeLaptop).PublishQuestionAsync(question, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.Read(path).Should().Equal(EnvelopeWriter.Serialize(question));
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme question {question.Header.Id.Value} home-laptop→central");
    }

    [Fact]
    public async Task PublishQuestionAsync_FromAnotherMachine_ThrowsWriteRule_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model question = await QuestionAsync(WorkLaptop, T, ct);

        // Act
        Func<Task> act = () => Repository(HomeLaptop).PublishQuestionAsync(question, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*another machine*");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Questions(Acme, WorkLaptop)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("unsigned", "*unsigned*")]
    [InlineData("tenant", "*another tenant*")]
    [InlineData("type", "*not a question*")]
    public async Task PublishQuestionAsync_UnsignedForeignTenantOrWrongType_ThrowsWriteRule_NoGitCall(string broken, string message)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model envelope = broken switch
        {
            "unsigned" => UnsignedQuestion(HomeLaptop, T, Acme),
            "tenant" => UnsignedQuestion(HomeLaptop, T, TenantId.Parse("globex")),
            _ => await TestEnvelopes.SignedJobAsync(HomeLaptop, Central, _store, TestKeys.Acme1, "probe\n", ct),
        };

        // Act
        Func<Task> act = () => Repository(HomeLaptop).PublishQuestionAsync(envelope, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:" + message);
        _processes.Calls.Should().BeEmpty();
    }

    // AC-2: decisions and the archive move
    [Fact]
    public async Task PublishDecisionAsync_OnCentral_DecisionUnderCentralsFolder_QuestionMovedToItsArchiveMonth_OneCommit()
    {
        // Arrange: a question created in October, answered in November
        var ct = TestContext.Current.CancellationToken;
        Model question = await QuestionAsync(HomeLaptop, new DateTimeOffset(2026, 10, 31, 23, 50, 0, TimeSpan.Zero), ct);
        string questionPath = BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question.Header.Id);
        _checkout.Drop(questionPath, EnvelopeWriter.Serialize(question));
        Model decision = await DecisionAsync(Central, HomeLaptop, question.Header.Id, ct);
        string decisionPath = BusPaths.EnvelopeFile(BusPaths.Decisions(Acme, Central), decision.Header.Id);
        string archived = BusPaths.EnvelopeFile(BusPaths.Archive(Acme, 2026, 10), question.Header.Id);

        // Act
        WriteOutcome outcome = await Repository(Central).PublishDecisionAsync(decision, question, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _checkout.Read(decisionPath).Should().Equal(EnvelopeWriter.Serialize(decision));
        _processes.CallsOf("mv").Should().ContainSingle().Which.Arguments.Should().Contain(questionPath).And.Contain(archived);
        _processes.CallsOf("commit").Should().ContainSingle().Which.StandardInput.Should().StartWith($"acme decision {decision.Header.Id.Value} central→home-laptop");
    }

    [Fact]
    public async Task PublishDecisionAsync_NoQuestionFile_DecisionOnly_NoMove()
    {
        // Arrange: a decision on a report, or on a question that is already archived
        var ct = TestContext.Current.CancellationToken;
        Model decision = await DecisionAsync(Central, HomeLaptop, EnvelopeId.New(T.AddMinutes(-1)), ct);

        // Act
        WriteOutcome outcome = await Repository(Central).PublishDecisionAsync(decision, null, ct);

        // Assert
        outcome.Should().Be(new Pushed(1));
        _processes.CallsOf("mv").Should().BeEmpty();
        _processes.CallsOf("add").Should().ContainSingle();
    }

    [Fact]
    public async Task PublishDecisionAsync_OnALaptop_ThrowsWriteRule_NoGitCall()
    {
        // Arrange: a decision that verifies (signed as central) but published by home-laptop's repository
        var ct = TestContext.Current.CancellationToken;
        Model decision = await DecisionAsync(Central, HomeLaptop, EnvelopeId.New(T), ct);

        // Act
        Func<Task> act = () => Repository(HomeLaptop).PublishDecisionAsync(decision, null, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*only central*");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Decisions(Acme, Central)).Should().BeEmpty();
    }

    [Fact]
    public async Task PublishDecisionAsync_QuestionIsNotTheOneAnswered_ThrowsWriteRule_NoGitCall()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model question = await QuestionAsync(HomeLaptop, T, ct);
        Model decision = await DecisionAsync(Central, HomeLaptop, EnvelopeId.New(T.AddMinutes(-5)), ct);

        // Act
        Func<Task> act = () => Repository(Central).PublishDecisionAsync(decision, question, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*does not answer*");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishDecisionAsync_QuestionOfAnotherTenant_ThrowsWriteRule_NoGitCall()
    {
        // Arrange: the ids match, the tenant does not — nothing of another tenant's subtree is ever moved
        var ct = TestContext.Current.CancellationToken;
        Model foreign = UnsignedQuestion(HomeLaptop, T, TenantId.Parse("globex"));
        Model decision = await DecisionAsync(Central, HomeLaptop, foreign.Header.Id, ct);

        // Act
        Func<Task> act = () => Repository(Central).PublishDecisionAsync(decision, foreign, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:*does not answer*");
        _processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("unsigned", "*unsigned*")]
    [InlineData("type", "*not a decision*")]
    public async Task PublishDecisionAsync_UnsignedOrWrongType_ThrowsWriteRule_NoGitCall(string broken, string message)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model envelope = broken == "unsigned"
            ? UnsignedDecision(Central, HomeLaptop, EnvelopeId.New(T))
            : await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, _store, TestKeys.Acme1, "probe\n", ct);

        // Act
        Func<Task> act = () => Repository(Central).PublishDecisionAsync(envelope, null, ct);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("write rule:" + message);
        _processes.Calls.Should().BeEmpty();
    }

    // AC-6 (unit half): what a question may carry
    [Fact]
    public async Task AskAsync_Question_SignedToCentralOnTheBus()
    {
        // Act
        AskDecideResult result = await Asker().AskAsync(Request(Words(600)), TestContext.Current.CancellationToken);

        // Assert
        AskDecidePublished published = result.Should().BeOfType<AskDecidePublished>().Subject;
        EnvelopeResult onBus = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(published.Path), Acme, TestContext.Current.CancellationToken);
        onBus.IsAccepted.Should().BeTrue(onBus.Rejection?.ToString());
        onBus.Envelope!.Header.To.Should().Be(Central);
        onBus.Envelope.Question!.Session.Should().Be("5b3fd3ac");
        onBus.Envelope.Question.Options.Should().Equal("keep it", "change it");
    }

    [Fact]
    public async Task AskAsync_Over600Characters_RefusedBody_NothingWritten()
    {
        // Act
        AskDecideResult result = await Asker().AskAsync(Request(Words(601)), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<AskDecideRefused>().Which.Field.Should().Be("body");
        _processes.Calls.Should().BeEmpty();
        _checkout.FilesUnder(BusPaths.Questions(Acme, HomeLaptop)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public async Task AskAsync_SecretShapedQuestion_Refused_NothingSignedOrWritten_TextNowhere(string name, string sample)
    {
        // Act
        AskDecideResult result = await Asker().AskAsync(Request("Is this the key: " + sample), TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AskDecideRefused("body", "matches secret pattern " + name));
        _processes.Calls.Should().BeEmpty();
        _provider.ReceivedCalls().Should().BeEmpty();
        _askLog.Messages.Should().NotContain(m => m.Contains(sample, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("options")]
    [InlineData("default")]
    public async Task AskAsync_SecretShapedOptionOrDefault_RefusedNamingTheField(string field)
    {
        // Arrange
        (string name, string sample) = SecretPatternsTests.SecretSamples().First().Data;
        QuestionRequest request = field == "options" ? Request("Which?") with { Options = ["keep it", sample] } : Request("Which?") with { Default = sample };

        // Act
        AskDecideResult result = await Asker().AskAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AskDecideRefused(field, "matches secret pattern " + name));
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AskAsync_SupervisionDisabled_SkippedNothingWritten()
    {
        // Act
        AskDecideResult result = await Asker(enabled: false).AskAsync(Request("Which?"), TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AskDecideSkipped("supervision disabled"));
        _processes.Calls.Should().BeEmpty();
    }

    // What a decision may carry, and who may write one
    [Fact]
    public async Task DecideAsync_OnCentral_SignedDecisionOnTheBus_QuestionArchived()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        Model question = await QuestionAsync(HomeLaptop, T, ct);
        _checkout.Drop(BusPaths.EnvelopeFile(BusPaths.Questions(Acme, HomeLaptop), question.Header.Id), EnvelopeWriter.Serialize(question));

        // Act
        AskDecideResult result = await Decider(Central).DecideAsync(Decision(question.Header.Id, Words(1200)), question, ct);

        // Assert
        AskDecidePublished published = result.Should().BeOfType<AskDecidePublished>().Subject;
        EnvelopeResult onBus = await new EnvelopeSigner(_store).ParseAndVerifyAsync(_checkout.Read(published.Path), Acme, ct);
        onBus.IsAccepted.Should().BeTrue(onBus.Rejection?.ToString());
        onBus.Envelope!.Header.From.Should().Be(Central);
        onBus.Envelope.Header.InReplyTo.Should().Be(question.Header.Id);
        onBus.Envelope.Decision!.Kind.Should().Be(DecisionKind.Answer);
        _processes.CallsOf("mv").Should().ContainSingle();
    }

    [Fact]
    public async Task DecideAsync_OnALaptop_Refused_NothingSignedOrWritten()
    {
        // Act
        AskDecideResult result = await Decider(HomeLaptop).DecideAsync(Decision(EnvelopeId.New(T), "Yes."), null, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AskDecideRefused("machine", "only central decides"));
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DecideAsync_Over1200Characters_RefusedBody()
    {
        // Act
        AskDecideResult result = await Decider(Central).DecideAsync(Decision(EnvelopeId.New(T), Words(1201)), null, TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<AskDecideRefused>().Which.Field.Should().Be("body");
        _processes.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("body")]
    [InlineData("undo")]
    public async Task DecideAsync_SecretShapedTextOrUndo_RefusedNamingTheField(string field)
    {
        // Arrange
        (string name, string sample) = SecretPatternsTests.SecretSamples().First().Data;
        DecisionRequest request = field == "body" ? Decision(EnvelopeId.New(T), "The key is " + sample) : Decision(EnvelopeId.New(T), "Yes.") with { Undo = sample };

        // Act
        AskDecideResult result = await Decider(Central).DecideAsync(request, null, TestContext.Current.CancellationToken);

        // Assert
        result.Should().Be(new AskDecideRefused(field, "matches secret pattern " + name));
        _processes.Calls.Should().BeEmpty();
    }

    /// <summary>Ordinary prose of exactly <paramref name="characters"/> characters (a long run of one letter looks like a token to the secret patterns).</summary>
    private static string Words(int characters) => string.Concat(Enumerable.Repeat("word ", characters / 5 + 1))[..(characters - 1)] + "x";

    private static QuestionRequest Request(string text) => new("calizr", text, "keep it", ["keep it", "change it"], Session: "5b3fd3ac");

    private static DecisionRequest Decision(EnvelopeId inReplyTo, string text) =>
        new(inReplyTo, HomeLaptop, DecisionKind.Answer, DecisionBasis.Verified, text, "nothing to undo");

    private static Model UnsignedQuestion(MachineName from, DateTimeOffset at, TenantId tenant) =>
        Model.CreateQuestion(new EnvelopeHeader(tenant, EnvelopeId.New(at), from, Central, at), new QuestionFields("calizr", "keep it", [], Session: "5b3fd3ac"), "Which one?\n"u8.ToArray());

    private static Model UnsignedDecision(MachineName from, MachineName to, EnvelopeId inReplyTo) =>
        Model.CreateDecision(new EnvelopeHeader(Acme, EnvelopeId.New(T), from, to, T, InReplyTo: inReplyTo), new DecisionFields(DecisionKind.Answer, DecisionBasis.Verified, "nothing to undo"), "Yes.\n"u8.ToArray());

    private async Task<Model> QuestionAsync(MachineName from, DateTimeOffset at, CancellationToken ct) =>
        (await new EnvelopeSigner(_store).SignAsync(UnsignedQuestion(from, at, Acme), TestKeys.Acme1, ct)).Envelope!;

    private async Task<Model> DecisionAsync(MachineName from, MachineName to, EnvelopeId inReplyTo, CancellationToken ct) =>
        (await new EnvelopeSigner(_store).SignAsync(UnsignedDecision(from, to, inReplyTo), TestKeys.Acme1, ct)).Envelope!;

    private BusRepository Repository(MachineName me)
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (" + me.Value + ")", me.Value + "@zyggy.org") }, _clock);
        return new BusRepository(git, _provider, new BusIdentity(Acme, me), _checkout.Root, new EnvelopeSigner(_store), _clock, () => 0.5, new RecordingLogger<BusRepository>());
    }

    private static SecretPatterns Patterns() => SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private QuestionAsker Asker(bool enabled = true) =>
        new(Repository(HomeLaptop), new EnvelopeSigner(_store), TestKeys.Acme1, Patterns(), enabled, _clock, _askLog);

    private DecisionWriter Decider(MachineName me) =>
        new(Repository(me), new EnvelopeSigner(_store), TestKeys.Acme1, Patterns(), _clock, new RecordingLogger<DecisionWriter>());
}
