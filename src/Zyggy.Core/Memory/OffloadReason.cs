namespace Zyggy.Core.Memory;

/// <summary>
/// Why an archive offload did not move an item, or stopped (spec 38 <c>OffloadReason</c>, closed). Transient reasons are counted and
/// retried at the next run; permanent ones park the item at once; run-level ones stop the run before or between items.
/// </summary>
internal enum OffloadReason
{
    TransportError,
    ServerError,
    Throttled,
    AuthFailed,
    GitError,
    PushDeferred,
    ShaMismatch,
    WriteRefused,
    TargetConflict,
    VerifyFailed,
    TypeBlocked,
    TooLarge,
    Forbidden,
    RootMissing,
    RootNotFolder,
    ConfigError,
    LockHeld,
    LogUnwritable,
}

/// <summary>The snake_case wire names of <see cref="OffloadReason"/> and its classes.</summary>
internal static class OffloadReasonWire
{
    public static string ToWire(OffloadReason reason) => reason switch
    {
        OffloadReason.TransportError => "transport_error",
        OffloadReason.ServerError => "server_error",
        OffloadReason.Throttled => "throttled",
        OffloadReason.AuthFailed => "auth_failed",
        OffloadReason.GitError => "git_error",
        OffloadReason.PushDeferred => "push_deferred",
        OffloadReason.ShaMismatch => "sha_mismatch",
        OffloadReason.WriteRefused => "write_refused",
        OffloadReason.TargetConflict => "target_conflict",
        OffloadReason.VerifyFailed => "verify_failed",
        OffloadReason.TypeBlocked => "type_blocked",
        OffloadReason.TooLarge => "too_large",
        OffloadReason.Forbidden => "forbidden",
        OffloadReason.RootMissing => "root_missing",
        OffloadReason.RootNotFolder => "root_not_folder",
        OffloadReason.ConfigError => "config_error",
        OffloadReason.LockHeld => "lock_held",
        OffloadReason.LogUnwritable => "log_unwritable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    public static bool TryFromWire(string? wire, out OffloadReason reason)
    {
        foreach (var member in Enum.GetValues<OffloadReason>())
        {
            if (string.Equals(ToWire(member), wire, StringComparison.Ordinal))
            {
                reason = member;
                return true;
            }
        }

        reason = default;
        return false;
    }

    /// <summary>Parks the item at once: never retried automatically.</summary>
    public static bool IsPermanent(OffloadReason reason) => reason is OffloadReason.ShaMismatch or OffloadReason.WriteRefused or OffloadReason.TargetConflict
        or OffloadReason.VerifyFailed or OffloadReason.TypeBlocked or OffloadReason.TooLarge or OffloadReason.Forbidden;

    /// <summary>Stops the whole run before or between items; no item is half-done.</summary>
    public static bool IsRunLevel(OffloadReason reason) => reason is OffloadReason.RootMissing or OffloadReason.RootNotFolder
        or OffloadReason.ConfigError or OffloadReason.LockHeld or OffloadReason.LogUnwritable;
}
