using System.Globalization;
using System.Text.Json;

namespace Zyggy.Core.Outlook;

/// <summary>Why an outlook tool refused (spec 58 failure modes). Local to the laptop: not a bus reason code.</summary>
internal enum OutlookRefusal
{
    /// <summary>No valid <c>mail.policyConfirmed</c> record (or its <c>node.json</c> mirror), or its scope lacks the action.</summary>
    PolicyNotConfirmed,

    /// <summary><c>outlook.enabled</c> is not <see langword="true"/> in <c>node.json</c>.</summary>
    NotEnabled,

    /// <summary>Outlook is not running, its profile is not loaded, or it refused the call.</summary>
    OutlookNotAvailable,

    /// <summary>The folder does not exist in the default store.</summary>
    FolderNotFound,

    /// <summary>The item reference names no item of the default store.</summary>
    ItemNotFound,
}

/// <summary>Where the policy record that was used came from (spec 58 D-Q7).</summary>
internal enum OutlookPolicySource
{
    /// <summary>No record was found.</summary>
    None,

    /// <summary>The copy of the instance's <c>work-boundary.json</c> on the laptop.</summary>
    WorkBoundaryFile,

    /// <summary>The <c>outlook.policyConfirmed</c> mirror in the laptop's <c>node.json</c>.</summary>
    NodeJsonMirror,
}

/// <summary>The owner's confirmation that the client's IT policy covers work mail through the agent (spec 58 AC-1).</summary>
/// <param name="Date">The date of the confirmation.</param>
/// <param name="Scope">What it covers: a subset of <c>read</c>, <c>send</c>, <c>move</c>.</param>
internal sealed record OutlookPolicyRecord(DateOnly Date, IReadOnlySet<string> Scope)
{
    /// <summary>The scope values a record may hold.</summary>
    public static readonly IReadOnlySet<string> KnownScopes = new HashSet<string>(["read", "send", "move"], StringComparer.Ordinal);

    /// <summary>Parses a <c>policyConfirmed</c> object: <c>{"date":"YYYY-MM-DD","scope":["read",…]}</c>; other keys are ignored.</summary>
    /// <param name="element">The object, or <see langword="null"/> when absent.</param>
    /// <returns>The record, or <see langword="null"/> when absent or malformed (fail closed).</returns>
    public static OutlookPolicyRecord? Parse(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } record
            || !record.TryGetProperty("date", out var date)
            || date.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || !record.TryGetProperty("scope", out var scope)
            || scope.ValueKind != JsonValueKind.Array
            || scope.GetArrayLength() == 0
            || !scope.EnumerateArray().All(s => s.ValueKind == JsonValueKind.String && KnownScopes.Contains(s.GetString()!)))
        {
            return null;
        }

        return new OutlookPolicyRecord(day, scope.EnumerateArray().Select(s => s.GetString()!).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Reads <c>mail.policyConfirmed</c> from the text of a <c>work-boundary.json</c>.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The record, or <see langword="null"/> when absent or the file is not valid JSON (fail closed).</returns>
    public static OutlookPolicyRecord? FromWorkBoundary(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("mail", out var mail)
                && mail.ValueKind == JsonValueKind.Object
                && mail.TryGetProperty("policyConfirmed", out var confirmed)
                    ? Parse(confirmed)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The outcome of <see cref="OutlookPolicy.Evaluate"/>.</summary>
/// <param name="Record">The record used, if one was found.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Enabled">Whether <c>outlook.enabled</c> is true.</param>
internal sealed record OutlookPolicyDecision(OutlookPolicyRecord? Record, OutlookPolicySource Source, bool Enabled)
{
    /// <summary>Returns why <paramref name="scope"/> is refused, or <see langword="null"/> when it is allowed.</summary>
    /// <param name="scope"><c>read</c>, <c>send</c> or <c>move</c>.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public OutlookRefusal? RefusalFor(string scope) =>
        !Enabled ? OutlookRefusal.NotEnabled
        : Record is null || !Record.Scope.Contains(scope) ? OutlookRefusal.PolicyNotConfirmed
        : null;
}

/// <summary>
/// Finds the policy record the outlook tools run under (spec 58 "The policy confirmation", D-Q7): the copy of
/// <c>work-boundary.json</c> when it is on the laptop, else the <c>outlook.policyConfirmed</c> mirror in <c>node.json</c>.
/// </summary>
internal static class OutlookPolicy
{
    /// <summary>The file name of the instance record and of its copy on the laptop.</summary>
    public const string WorkBoundaryFileName = "work-boundary.json";

    /// <summary>
    /// Returns where the laptop keeps its copy of <c>work-boundary.json</c> (spec 58 D-Q7): in the node directory, beside the runs and
    /// logs, written by <c>zyggy setup --instance-path</c> and read by every outlook tool.
    /// </summary>
    /// <param name="nodeDir">The node directory (<c>nodeDir</c>, or its per-OS default).</param>
    /// <returns>The path of the copy.</returns>
    public static string CopyPath(string nodeDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeDir);
        return Path.GetFullPath(Path.Combine(nodeDir, WorkBoundaryFileName));
    }

    /// <summary>Evaluates the policy.</summary>
    /// <param name="workBoundaryJson">The text of the laptop's copy of <c>work-boundary.json</c>, or <see langword="null"/> when there is no copy.</param>
    /// <param name="settings">The <c>outlook</c> section of <c>node.json</c>.</param>
    /// <returns>The decision.</returns>
    public static OutlookPolicyDecision Evaluate(string? workBoundaryJson, OutlookSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The copy, when present, is the record: a copy without a valid record refuses, it never falls back to the mirror.
        if (workBoundaryJson is not null)
        {
            var record = OutlookPolicyRecord.FromWorkBoundary(workBoundaryJson);
            return new OutlookPolicyDecision(record, record is null ? OutlookPolicySource.None : OutlookPolicySource.WorkBoundaryFile, settings.Enabled);
        }

        return settings.PolicyMirror is { } mirror
            ? new OutlookPolicyDecision(mirror, OutlookPolicySource.NodeJsonMirror, settings.Enabled)
            : new OutlookPolicyDecision(null, OutlookPolicySource.None, settings.Enabled);
    }
}
