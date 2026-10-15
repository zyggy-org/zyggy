namespace Zyggy.Core.Bus;

/// <summary>
/// One git writer per bus checkout, across processes (patch 0.8.1, plan 04 Step 22): an exclusive lock file inside the
/// checkout's <c>.git</c> directory (<see cref="FileShare.None"/>, which .NET maps to <c>flock</c> on Linux), deleted when
/// released. A listener and a sender on the same machine, or two listeners, then take turns instead of colliding in
/// <c>pull --rebase</c>. A directory without <c>.git</c> gets a no-op handle: the start-up check reports it, and the lock
/// never creates a <c>.git</c> directory.
/// </summary>
internal static class BusCheckoutLock
{
    /// <summary>Held around every pull and every §5 write sequence.</summary>
    internal const string WriteLockName = "zyggy-bus.lock";

    /// <summary>Held for a listener's whole lifetime: one listener per checkout.</summary>
    internal const string ListenLockName = "zyggy-listen.lock";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly string[] ConcurrentChangeMessages =
    [
        "Cannot rebase onto multiple branches",
        "cannot lock ref",
        "fetch updated the current branch head",
        "index.lock",
    ];

    /// <summary>Takes the named lock, waiting up to <paramref name="timeout"/> on <paramref name="clock"/>.</summary>
    /// <returns>A handle that releases the lock when disposed, or <see langword="null"/> when another holder kept it past the timeout.</returns>
    internal static async Task<IDisposable?> AcquireAsync(string checkoutRoot, string name, TimeSpan timeout, TimeProvider clock, CancellationToken ct)
    {
        string gitDirectory = Path.Join(checkoutRoot, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            return NoLock.Instance;
        }

        string path = Path.Join(gitDirectory, name);
        DateTimeOffset deadline = clock.GetUtcNow() + timeout;
        while (true)
        {
            FileStream? stream = TryOpen(path);
            if (stream is not null)
            {
                return stream;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(PollInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether git's error output says another process changed the checkout while this command ran (worth one retry).</summary>
    internal static bool IsConcurrentChange(string stderr) =>
        !string.IsNullOrEmpty(stderr) && ConcurrentChangeMessages.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // Windows reports a file pending deletion by the previous holder this way; the next poll succeeds.
            return null;
        }
    }

    private sealed class NoLock : IDisposable
    {
        public static NoLock Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
