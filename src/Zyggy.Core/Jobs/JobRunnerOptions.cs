using System.Collections.ObjectModel;

namespace Zyggy.Core.Jobs;

/// <summary>Settings of <see cref="JobRunner"/> (founding spec §6 Streaming and limits, §10 <c>nodeDir</c>).</summary>
public sealed class JobRunnerOptions
{
    /// <summary>Gets or sets the node directory; transcripts go to <c>&lt;NodeDir&gt;/runs/&lt;tenant&gt;/&lt;job id&gt;.jsonl</c>.</summary>
    public string? NodeDir { get; set; }

    /// <summary>Gets or sets how long a transcript is kept (§6: 30 days).</summary>
    public TimeSpan TranscriptRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Gets or sets environment additions for the model process. Tests only (the fake's variables); production leaves it empty.</summary>
    public IReadOnlyDictionary<string, string> ModelEnvironment { get; set; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Gets or sets a time limit that replaces <c>timeout_minutes</c>. Tests only (an integration test cannot wait a minute); null in production.</summary>
    public TimeSpan? TimeoutOverride { get; set; }
}
