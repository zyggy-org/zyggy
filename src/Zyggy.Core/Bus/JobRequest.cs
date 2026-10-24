using System.Text;
using System.Text.RegularExpressions;

using Zyggy.Core.Envelope;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Bus;

/// <summary>A refused <see cref="JobRequest"/> option: the field as the CLI names it and the reason in the words the CLI prints.</summary>
/// <param name="Field">The §4 field (<c>to</c>, <c>project</c>, <c>agent</c>, <c>timeout_minutes</c>, <c>allowed_tools[i]</c>, <c>priority</c>, <c>body</c>).</param>
/// <param name="Detail">The reason.</param>
public sealed record JobRequestError(string Field, string Detail);

/// <summary>
/// A validated request for <c>zyggy submit</c> (spec 08 AC-1): every option checked by shape, in field order, before anything is signed,
/// so no option value can reach the front matter in a form the parser would read differently (the signing path's human entry point).
/// </summary>
/// <param name="To">The target machine.</param>
/// <param name="Project">The project.</param>
/// <param name="Agent">The optional subagent.</param>
/// <param name="TimeoutMinutes">The optional time limit.</param>
/// <param name="AllowedTools">The tool approval rules; <see langword="null"/> when the option is absent.</param>
/// <param name="Priority">The priority.</param>
/// <param name="Body">The normalised body: UTF-8, no BOM, LF endings, one trailing newline.</param>
public sealed partial record JobRequest(
    MachineName To,
    ProjectReference Project,
    string? Agent,
    int? TimeoutMinutes,
    IReadOnlyList<string>? AllowedTools,
    EnvelopePriority Priority,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>The body cap in UTF-8 bytes (a sanity check against a pasted document, not a protocol rule).</summary>
    public const int MaxBodyBytes = 65_536;

    /// <summary>The <c>project</c> length cap.</summary>
    public const int MaxProjectLength = 1024;

    /// <summary>The <c>agent</c> length cap.</summary>
    public const int MaxAgentLength = 64;

    /// <summary>The cap of one <c>allowed_tools</c> item.</summary>
    public const int MaxToolLength = 128;

    /// <summary>The <c>timeout_minutes</c> cap (one day).</summary>
    public const int MaxTimeoutMinutes = 1440;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Validates the raw option values in field order; the first failure wins.</summary>
    /// <param name="to">The <c>--to</c> value.</param>
    /// <param name="project">The <c>--project</c> value.</param>
    /// <param name="agent">The <c>--agent</c> value, or <see langword="null"/>.</param>
    /// <param name="timeoutMinutes">The <c>--timeout</c> value, or <see langword="null"/>.</param>
    /// <param name="allowedToolsRaw">Every <c>--allowed-tools</c> value as given (each may hold a comma-separated list), or <see langword="null"/>.</param>
    /// <param name="priority">The <c>--priority</c> value, or <see langword="null"/> for <c>normal</c>.</param>
    /// <param name="body">The body text as given.</param>
    /// <param name="request">The request when every option is accepted.</param>
    /// <returns>The first refusal, or <see langword="null"/>.</returns>
    public static JobRequestError? TryCreate(
        string? to,
        string? project,
        string? agent,
        int? timeoutMinutes,
        IReadOnlyList<string>? allowedToolsRaw,
        string? priority,
        string? body,
        out JobRequest? request)
    {
        request = null;
        if (!MachineName.TryParse(to, out MachineName? machine))
        {
            return new JobRequestError("to", "to must be a lowercase label ([a-z0-9-], 1–63)");
        }

        if (!ProjectReference.TryParse(project, out ProjectReference reference, out string? projectDetail))
        {
            return new JobRequestError("project", projectDetail!);
        }

        if (agent is not null && !AgentName().IsMatch(agent))
        {
            return new JobRequestError("agent", "agent must be 1–64 characters of A–Z a–z 0–9 . _ -");
        }

        if (timeoutMinutes is < 1 or > MaxTimeoutMinutes)
        {
            return new JobRequestError("timeout_minutes", "timeout_minutes must be between 1 and 1440");
        }

        List<string>? tools = null;
        if (allowedToolsRaw is not null)
        {
            tools = [];
            foreach (string raw in allowedToolsRaw)
            {
                foreach (string item in raw.Split(',').Select(i => i.Trim()))
                {
                    int index = tools.Count;
                    if (item.Length == 0)
                    {
                        return new JobRequestError($"allowed_tools[{index}]", "an empty item (a double comma or a trailing comma)");
                    }

                    if (item.Length > MaxToolLength)
                    {
                        return new JobRequestError($"allowed_tools[{index}]", "an item is longer than 128 characters");
                    }

                    if (item.Any(char.IsControl))
                    {
                        return new JobRequestError($"allowed_tools[{index}]", "an item contains a control character");
                    }

                    tools.Add(item);
                }
            }
        }

        var level = EnvelopePriority.Normal;
        if (priority is not null && !EnvelopeWire.TryFromWire(priority, out level))
        {
            return new JobRequestError("priority", "priority must be low, normal or high");
        }

        string text = (body ?? string.Empty).TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JobRequestError("body", "body is blank");
        }

        text = text.TrimEnd('\n') + "\n";
        byte[] bytes = Utf8.GetBytes(text);
        if (bytes.Length > MaxBodyBytes)
        {
            return new JobRequestError("body", "body is longer than 64 KiB");
        }

        request = new JobRequest(machine, reference, agent, timeoutMinutes, tools, level, bytes);
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex AgentName();
}
