using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Registry;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;
using Zyggy.Core.Tests.Registry;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 50 slice 2: <c>zyggy submit --model</c> on the sender side. The id passes the job field's shape rule and belongs to a model job
/// (AC-9 edge cases); a job with a model goes only to a machine whose registry file lists the capability <c>model</c> (AC-7), and the
/// signed envelope reads back with it.
/// </summary>
public sealed class JobSubmitterModelTests : IAsyncLifetime
{
    private const string Opus = "claude-opus-5-5";

    private static readonly TenantId Acme = TenantId.Parse("acme");

    private static readonly MachineName Central = MachineName.Parse("central");

    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private static readonly DateTimeOffset T = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly TempCheckout _checkout = new(Acme, Central);

    private readonly RecordingProcessRunner _processes = new();

    private readonly IBusProvider _provider = Substitute.For<IBusProvider>();

    private readonly FakeTimeProvider _clock = new(T);

    private InMemorySecretStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        _provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        _provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
    }

    public ValueTask DisposeAsync()
    {
        _checkout.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void TryWithModel_FullId_RequestCarriesIt()
    {
        // Act
        JobRequestError? error = ModelJob().TryWithModel(Opus, out JobRequest? request);

        // Assert
        new { Error = error, request?.Model, request?.JobType }.Should().BeEquivalentTo(new { Error = (JobRequestError?)null, Model = Opus, JobType = (string?)null });
    }

    [Fact]
    public void TryWithModel_Absent_RequestUnchanged()
    {
        // Arrange
        JobRequest job = ModelJob();

        // Act
        job.TryWithModel(null, out JobRequest? request).Should().BeNull();

        // Assert
        request.Should().Be(job);
    }

    // AC-1 / AC-9: an alias or a value of the wrong shape never reaches the envelope.
    [Theory]
    [InlineData("opus")]
    [InlineData("sonnet[1m]")]
    [InlineData("Claude-Opus-5-5")]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("claude opus")]
    [InlineData("")]
    public void TryWithModel_AliasOrBadShape_RefusedModel(string model)
    {
        // Act
        JobRequestError? error = ModelJob().TryWithModel(model, out _);

        // Assert
        error!.Field.Should().Be("model");
    }

    // AC-9 edge case: a fetch or command job runs no model.
    [Fact]
    public void TryWithModel_FetchJob_RefusedModel()
    {
        // Arrange
        ModelJob().TryWithFetch("docs/plan.md", null, out JobRequest? fetch).Should().BeNull();

        // Act
        JobRequestError? error = fetch!.TryWithModel(Opus, out _);

        // Assert
        error!.Field.Should().Be("model");
    }

    [Fact]
    public void TryWithModel_CommandJob_RefusedModel()
    {
        // Arrange
        ModelJob().TryWithCommand("run-tests", out JobRequest? command).Should().BeNull();

        // Act
        JobRequestError? error = command!.TryWithModel(Opus, out _);

        // Assert
        error!.Field.Should().Be("model");
    }

    // AC-7: no registry file, or one without the capability (an older node would run its default model): refused, nothing written.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitAsync_ModelToMachineWithoutModelCapability_RefusedCapabilityNothingWritten(bool registryPresent)
    {
        // Arrange
        if (registryPresent)
        {
            WriteRegistry([RegistryDocument.CommandCapability, RegistryDocument.FetchCapability, RegistryDocument.SupervisionCapability]);
        }

        // Act
        SubmitResult result = await Submitter().SubmitAsync(WithModel(Opus), TestContext.Current.CancellationToken);

        // Assert
        SubmitRefused refused = result.Should().BeOfType<SubmitRefused>().Subject;
        new { refused.Field, refused.Detail, Jobs = _checkout.FilesUnder(BusPaths.Jobs(Acme, HomeLaptop)).Count, Calls = _processes.Calls.Count }
            .Should().BeEquivalentTo(new
            {
                Field = "capability",
                Detail = "home-laptop does not run jobs with a model yet (no capability model); upgrade it or submit without --model",
                Jobs = 0,
                Calls = 0,
            });
    }

    // AC-7: a machine publishing model gets the job; the signed file reads back with the model and no job type.
    [Fact]
    public async Task SubmitAsync_ModelToMachinePublishingModel_SubmittedSignedJobCarriesModel()
    {
        // Arrange
        WriteRegistry([RegistryDocument.ModelCapability]);

        // Act
        SubmitResult result = await Submitter().SubmitAsync(WithModel(Opus), TestContext.Current.CancellationToken);

        // Assert
        Submitted submitted = result.Should().BeOfType<Submitted>().Subject;
        EnvelopeResult parsed = await new EnvelopeSigner(_store).ParseAndVerifyAsync(
            File.ReadAllBytes(BusPaths.Resolve(_checkout.Root, submitted.RelativePath)), Acme, TestContext.Current.CancellationToken);
        new { parsed.IsAccepted, parsed.Envelope?.Job?.Model, parsed.Envelope?.Job?.JobType }
            .Should().BeEquivalentTo(new { IsAccepted = true, Model = Opus, JobType = (string?)null });
    }

    // A job without a model goes to any machine, as before (no registry file at all here).
    [Fact]
    public async Task SubmitAsync_NoModelToMachineWithoutRegistry_SubmittedWithoutModel()
    {
        // Act
        SubmitResult result = await Submitter().SubmitAsync(ModelJob(), TestContext.Current.CancellationToken);

        // Assert
        result.Should().BeOfType<Submitted>();
    }

    // AC-6: the report's model matches the job's when equal, or equal followed by one [...] suffix (a context-size variant).
    [Theory]
    [InlineData("claude-opus-5-5", "claude-opus-5-5", true)]
    [InlineData("claude-opus-5-5", "claude-opus-5-5[1m]", true)]
    [InlineData("claude-opus-5-5[1m]", "claude-opus-5-5[1m]", true)]
    [InlineData("claude-opus-5-5", "claude-fable-5-1", false)]
    [InlineData("claude-opus-5-5", "claude-opus-5-55", false)]
    [InlineData("claude-opus-5-5", "claude-opus-5-5[", false)]
    [InlineData("claude-opus-5-5", null, false)]
    public void IsSameModel_AskedAndRan_MatchRule(string asked, string? ran, bool expected) =>
        JobModelRules.IsSameModel(asked, ran).Should().Be(expected);

    private void WriteRegistry(IReadOnlyList<string> capabilities)
    {
        var document = RegistryDocumentTests.Central() with { Tenant = Acme, Machine = HomeLaptop, Capabilities = capabilities };
        string path = BusPaths.Resolve(_checkout.Root, BusPaths.Registry(Acme, HomeLaptop));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, document.Serialize());
    }

    private JobSubmitter Submitter()
    {
        var git = new GitClient(_processes, new GitClientOptions { Identity = new GitIdentity("zyggy (central)", "central@zyggy.org") }, _clock);
        var signer = new EnvelopeSigner(_store);
        var bus = new BusRepository(git, _provider, new BusIdentity(Acme, Central), _checkout.Root, signer, _clock, () => 0, new RecordingLogger<BusRepository>());
        return new JobSubmitter(bus, signer, TestKeys.Acme1, _clock, new RecordingLogger<JobSubmitter>());
    }

    private static JobRequest ModelJob()
    {
        JobRequest.TryCreate("home-laptop", "shop", null, null, null, null, "Write the spec.\n", out JobRequest? request).Should().BeNull();
        return request!;
    }

    private static JobRequest WithModel(string model)
    {
        ModelJob().TryWithModel(model, out JobRequest? request).Should().BeNull();
        return request!;
    }
}
