using System.Globalization;

using Zyggy.Core.Envelope;
using Zyggy.Core.Registry;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <content>Spec 61 slice 6: the registry key <c>running</c> — written by the node in its own commits, read by Central's sweep.</content>
public sealed partial class BusRepository
{
    /// <summary>
    /// The registry file of <paramref name="machine"/> in this tenant, read from the checkout without the write lock (as
    /// <see cref="ReadOwnRegistryAsync"/>); <see langword="null"/> when the machine has none.
    /// </summary>
    /// <param name="machine">The machine.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file bytes, or <see langword="null"/>.</returns>
    internal async Task<byte[]?> ReadRegistryOfAsync(MachineName machine, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machine);
        string full = BusPaths.Resolve(_checkoutRoot, BusPaths.Registry(_me.Tenant, machine));
        return File.Exists(full) ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Spec 61 §3: at start-up and at a noted wake-up, rewrites this machine's registry file with <paramref name="running"/> and a fresh
    /// <c>last_seen</c> in one commit <c>&lt;tenant&gt; registry &lt;machine&gt;</c>. Nothing is written when the machine has no readable
    /// registry file yet (the discovery publish writes the first one).
    /// </summary>
    /// <param name="running">The ids with a live run on this machine.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The write outcome, or <see langword="null"/> when there was nothing to rewrite.</returns>
    internal async Task<WriteOutcome?> RefreshRunningAsync(IReadOnlyCollection<EnvelopeId> running, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(running);
        if (await ReadOwnRegistryAsync(ct).ConfigureAwait(false) is not { } bytes || !RegistryDocument.Parse(bytes).IsOk)
        {
            return null;
        }

        string path = BusPaths.Registry(_me.Tenant, _me.Machine);
        var paths = new List<string>();
        return await WriteAsync(
            string.Create(CultureInfo.InvariantCulture, $"{_me.Tenant.Value} registry {_me.Machine.Value}"),
            paths,
            token => StampRunningAsync(running, paths, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Spec 61 §3: inside a write (after its pull), rewrites this machine's registry file with <paramref name="running"/> and a fresh
    /// <c>last_seen</c>, and stages it with the write's own commit. A machine without a readable registry file is left alone (nothing
    /// staged); a 0.30.0 reader ignores the key.
    /// </summary>
    /// <param name="running">The ids with a live run on this machine once the write's commit is made, or <see langword="null"/> to leave the file alone.</param>
    /// <param name="paths">The write's paths; the registry file is added when it is staged.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A failure, or <see langword="null"/>.</returns>
    private async Task<WriteFailed?> StampRunningAsync(IReadOnlyCollection<EnvelopeId>? running, List<string> paths, CancellationToken ct)
    {
        if (running is null || await ReadOwnRegistryAsync(ct).ConfigureAwait(false) is not { } bytes || RegistryDocument.Parse(bytes).Document is not { } document)
        {
            return null;
        }

        DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();
        var stamp = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        RegistryDocument stamped = document with { LastSeen = stamp, Running = [.. running.Distinct().OrderBy(r => r.Value, StringComparer.Ordinal)] };
        string path = BusPaths.Registry(_me.Tenant, _me.Machine);
        AssertMayWrite(path, BusFolder.Registry);
        await WriteFileAsync(path, stamped.Serialize(), ct).ConfigureAwait(false);
        WriteFailed? failed = Failure("add", await _git.AddAsync(_checkoutRoot, [path], ct).ConfigureAwait(false));
        if (failed is null)
        {
            paths.Add(path);
        }

        return failed;
    }
}
