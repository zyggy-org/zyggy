namespace Zyggy.Core.Bus;

/// <summary>What the start-up check found about the key a machine signs with (spec 09 AC-6).</summary>
public enum SigningKeyState
{
    /// <summary>Present and at least 32 bytes.</summary>
    Usable,

    /// <summary>Absent from the store: <c>zyggy secret set hmac/&lt;n&gt;</c> first.</summary>
    Missing,

    /// <summary>Present but shorter than 32 bytes: generate a new one.</summary>
    TooShort,

    /// <summary>The store itself cannot be opened; absence was not established.</summary>
    StoreUnavailable,
}

/// <summary>The start-up check's result with the owner-facing sentence (no <c>zyggy:</c> prefix; empty when <see cref="SigningKeyState.Usable"/>).</summary>
/// <param name="State">The state.</param>
/// <param name="Detail">The sentence naming the store and the exact command, or the store's refusal; never a byte of a secret.</param>
public sealed record SigningKeyCheck(SigningKeyState State, string Detail);
