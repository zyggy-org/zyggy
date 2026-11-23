using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;
using Zyggy.Core.Git;
using Zyggy.Core.Processes;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

using Model = Zyggy.Core.Envelope.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>
/// Spec 17 slice 1: Central's checkout with claims of other machines, git recorded (never run), the claim time of each path answered
/// by the process runner's <c>git log</c>, a fake clock. Jobs are signed with <c>acme/1</c>; Central signs with <c>acme/2</c>.
/// </summary>
internal sealed class SweepClaimsHarness : IDisposable
{
    public static readonly TenantId Acme = TenantId.Parse("acme");

    public static readonly MachineName Central = MachineName.Central;

    public static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    public static readonly MachineName WorkLaptop = MachineName.Parse("work-laptop");

    public static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly BusRemote Remote = new("acme-org", "acme-bus", "main", new Uri("https://example.invalid/acme-org/acme-bus.git"), null);

    private readonly Dictionary<string, ProcessResult> _claimTimes = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ProcessResult> _addedTimes = new(StringComparer.Ordinal);

    private readonly HashSet<string> _failingMoves = new(StringComparer.Ordinal);

    private ISecretStore? _store;

    public SweepClaimsHarness()
    {
        Provider.Remote.Returns(Remote);
        Provider.PullAsync(Arg.Any<CancellationToken>()).Returns(new BusSyncResult(true, string.Empty));
        Provider.PushAsync(Arg.Any<CancellationToken>()).Returns(new PushSucceeded());
        Processes.Hook = Answer;
    }

    public TempCheckout Checkout { get; } = new(Acme, Central);

    public RecordingProcessRunner Processes { get; } = new();

    public IBusProvider Provider { get; } = Substitute.For<IBusProvider>();

    public RecordingLogger<BusSweep> Log { get; } = new();

    public FakeTimeProvider Clock { get; } = new(Now);

    public void Dispose() => Checkout.Dispose();

    /// <summary>The store both keys live in; <paramref name="centralKey"/> false leaves out the key Central signs with.</summary>
    public async Task<ISecretStore> StoreAsync(CancellationToken ct, bool centralKey = true) =>
        _store ??= centralKey ? await TestKeys.StoreWithBothAsync(ct) : await TestKeys.StoreWithAsync(TestKeys.Acme1, ct);

    /// <summary>A signed job from the work laptop, sitting in <paramref name="machine"/>'s <c>jobs/claimed/</c>, claimed <paramref name="claimedAgo"/> ago (one hour when null).</summary>
    public async Task<(string Path, Model Job, byte[] Bytes)> DropClaimAsync(
        MachineName machine,
        CancellationToken ct,
        int attempt = 1,
        int? timeoutMinutes = 2,
        TimeSpan? claimedAgo = null,
        Func<byte[], byte[]>? mutate = null,
        MachineName? to = null,
        EnvelopeId? mandate = null,
        string? step = null,
        string? model = null,
        string? jobType = null)
    {
        Model job = await TestEnvelopes.SignedJobAsync(
            WorkLaptop, to ?? machine, await StoreAsync(ct), TestKeys.Acme1, "probe body\n", ct,
            fields: new JobFields("probe", TimeoutMinutes: timeoutMinutes, Attempt: attempt, Mandate: mandate, Step: step, Model: model, JobType: jobType));
        byte[] bytes = EnvelopeWriter.Serialize(job);
        bytes = mutate is null ? bytes : mutate(bytes);
        string path = BusPaths.EnvelopeFile(BusPaths.Claimed(Acme, machine), job.Header.Id);
        Checkout.Drop(path, bytes);
        ClaimedAt(path, Now - (claimedAgo ?? TimeSpan.FromHours(1)));
        return (path, job, bytes);
    }

    public void ClaimedAt(string path, DateTimeOffset time) =>
        _claimTimes[path] = RecordingProcessRunner.Ok(time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "\n");

    public void ClaimTimeUnreadable(string path) => _claimTimes[path] = RecordingProcessRunner.Fail(128, "fatal: bad object HEAD\n");

    /// <summary>Spec 17 slice 5: the committer time of the commit that added <paramref name="path"/>, as <c>git log --diff-filter=A</c> answers it.</summary>
    public void AddedAt(string path, DateTimeOffset time) =>
        _addedTimes[path] = RecordingProcessRunner.Ok(time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "\n");

    public void FailMoveOf(string path) => _failingMoves.Add(path);

    public async Task<BusRepository> RepositoryAsync(CancellationToken ct, MachineName? me = null)
    {
        var git = new GitClient(Processes, new GitClientOptions { Identity = TestGitIdentities.Central }, Clock);
        return new BusRepository(
            git, Provider, new BusIdentity(Acme, me ?? Central), Checkout.Root, new EnvelopeSigner(await StoreAsync(ct)), Clock, () => 0.5, new RecordingLogger<BusRepository>());
    }

    public async Task<BusSweep> SweepAsync(CancellationToken ct, int defaultTimeoutMinutes = 30)
    {
        ISecretStore store = await StoreAsync(ct);
        return new BusSweep(await RepositoryAsync(ct), new EnvelopeSigner(store), new SweepOptions(TestKeys.Acme2, defaultTimeoutMinutes), Log);
    }

    /// <summary>The verified envelope at a repository-relative path of the checkout.</summary>
    public async Task<Model> ReadVerifiedAsync(string path, CancellationToken ct)
    {
        EnvelopeResult result = await new EnvelopeSigner(await StoreAsync(ct)).ParseAndVerifyAsync(Checkout.Read(path), Acme, ct);
        result.IsAccepted.Should().BeTrue(result.Rejection?.ToString());
        return result.Envelope!;
    }

    /// <summary>The front matter without the three fields a re-queue may change.</summary>
    public static Dictionary<string, FrontMatterNode> OtherFields(Model envelope) =>
        envelope.FrontMatter.Entries.Where(e => e.Key is not ("attempt" or "key_id" or "sig")).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    private ProcessResult? Answer(ProcessSpec spec)
    {
        switch (RecordingProcessRunner.SubCommand(spec))
        {
            case "log" when spec.Arguments.Contains("--diff-filter=A"):
                return _addedTimes.TryGetValue(spec.Arguments[^1], out ProcessResult? added) ? added : RecordingProcessRunner.Ok();
            case "log":
                return _claimTimes.TryGetValue(spec.Arguments[^1], out ProcessResult? time) ? time : RecordingProcessRunner.Ok();
            case "mv" when _failingMoves.Contains(spec.Arguments[2]):
                return RecordingProcessRunner.Fail(1, "fatal: the move was refused\n");
            default:
                return null;
        }
    }
}
