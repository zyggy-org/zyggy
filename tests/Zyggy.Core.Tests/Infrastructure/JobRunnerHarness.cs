using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Jobs;
using Zyggy.Core.Models;
using Zyggy.Core.Policy;
using Zyggy.Core.Runs;
using Zyggy.Core.Tenancy;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A <see cref="JobRunner"/> over substitutes (plan 06 Step 1): an NSubstitute <see cref="IModelRunner"/> and <see cref="IPolicySource"/>
/// (returning <see cref="NodePolicy.Default"/>), the real <see cref="GitClient"/> over a <see cref="RecordingProcessRunner"/>, a fake clock
/// and a recording logger.
/// </summary>
internal sealed class JobRunnerHarness
{
    public static readonly DateTimeOffset T = new(2026, 9, 27, 14, 5, 0, TimeSpan.Zero);

    public static readonly MachineName Central = MachineName.Parse("central");

    public static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    public JobRunnerHarness(IModelRunner? model = null, RecordingProcessRunner? processes = null)
    {
        Processes = processes ?? new RecordingProcessRunner();
        Policy.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => NodePolicy);
        if (model is null)
        {
            Model = Substitute.For<IModelRunner>();
            Model.RunAsync(Arg.Any<ModelRunRequest>(), Arg.Any<CancellationToken>()).Returns(Succeeded("done\n"));
        }
        else
        {
            Model = model;
        }
    }

    public IModelRunner Model { get; }

    public IPolicySource Policy { get; } = Substitute.For<IPolicySource>();

    public NodePolicy NodePolicy { get; set; } = NodePolicy.Default;

    public RecordingProcessRunner Processes { get; }

    public FakeTimeProvider Clock { get; } = new(T);

    /// <summary>Gets the options; <see cref="JobRunnerOptions.NodeDir"/> is a fresh temp directory (created only when a test writes into it), never the real per-OS default.</summary>
    public JobRunnerOptions Options { get; } = new() { NodeDir = Path.Combine(Path.GetTempPath(), "zyggy-unit", Guid.NewGuid().ToString("N"), "node") };

    public RecordingLogger<JobRunner> Log { get; } = new();

    /// <summary>Gets or sets a clock that replaces <see cref="Clock"/> for the runner (fault injection).</summary>
    public TimeProvider? ClockOverride { get; set; }

    public JobRunner Build() =>
        new(Model, Policy, new GitClient(Processes, new GitClientOptions(), Clock), Microsoft.Extensions.Options.Options.Create(Options), ClockOverride ?? Clock, Log);

    /// <summary>A signed job from central to the home laptop with <paramref name="fields"/>.</summary>
    public static async Task<Model> JobAsync(JobFields fields, string body = "Investigate 502.\n", EnvelopeId? id = null)
    {
        var store = await TestKeys.StoreWithBothAsync(CancellationToken.None);
        return await TestEnvelopes.SignedJobAsync(Central, HomeLaptop, store, TestKeys.Acme1, body, CancellationToken.None, id, fields);
    }

    /// <summary>The single <see cref="ModelRunRequest"/> the model substitute received.</summary>
    public ModelRunRequest Request() =>
        (ModelRunRequest)Model.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IModelRunner.RunAsync)).GetArguments()[0]!;

    public static ModelRunResult Succeeded(string? resultText) =>
        new(ModelRunOutcome.Succeeded, null, null, resultText, null, 0.42m, 12, TimeSpan.FromSeconds(65), "claude-sonnet-4-5", 1200, 300, 0, 0);

    public static ModelRunResult Failed(RunFailureReason reason, string detail, string? lastText = null) =>
        new(ModelRunOutcome.Failed, reason, detail, null, null, null, null, TimeSpan.FromSeconds(3), null, null, null, 1, 0) { LastAssistantText = lastText };
}
