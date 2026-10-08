namespace Zyggy.Core.LinkedIn;

/// <summary>The closed set of outcomes a LinkedIn call can fail with (spec 36 AC-12); each has one wire token.</summary>
internal enum LinkedInFailure
{
    /// <summary>No token file: the owner never connected, or removed it.</summary>
    NotConnected,

    /// <summary>The token's expiry has passed, or LinkedIn answered 401.</summary>
    TokenExpired,

    /// <summary>LinkedIn answered 403, or the token lacks <c>w_member_social</c>.</summary>
    Forbidden,

    /// <summary>LinkedIn answered 426: the configured <c>api_version</c> is no longer active.</summary>
    VersionRetired,

    /// <summary>LinkedIn answered 429; never retried.</summary>
    RateLimited,

    /// <summary>LinkedIn answered 400 or 422 with its message.</summary>
    Rejected,

    /// <summary>A 5xx, a timeout or a reset: the post may exist.</summary>
    OutcomeUnknown,

    /// <summary>A local check refused the call before any request.</summary>
    Refused,

    /// <summary>The instance, the environment or a credential file is not usable.</summary>
    ConfigurationError,
}

/// <summary>The wire tokens of <see cref="LinkedInFailure"/>, as the tool result and the action-log row carry them.</summary>
internal static class LinkedInFailureWire
{
    public static string Token(LinkedInFailure failure) => failure switch
    {
        LinkedInFailure.NotConnected => "not_connected",
        LinkedInFailure.TokenExpired => "token_expired",
        LinkedInFailure.Forbidden => "forbidden",
        LinkedInFailure.VersionRetired => "version_retired",
        LinkedInFailure.RateLimited => "rate_limited",
        LinkedInFailure.Rejected => "rejected",
        LinkedInFailure.OutcomeUnknown => "outcome_unknown",
        LinkedInFailure.Refused => "refused",
        LinkedInFailure.ConfigurationError => "configuration_error",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };
}
