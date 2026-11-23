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
    /// <summary>
    /// Gets a value indicating whether the job runs in its own git worktree on branch <c>zyggy/&lt;id&gt;</c> and pushes the result
    /// (<c>zyggy submit --worktree</c>, spec 16); <see langword="false"/> runs in the project's checkout as before.
    /// </summary>
    public bool Worktree { get; init; }

    /// <summary>
    /// Gets the optional branch the job starts from when it exists on <c>origin</c> and that a <c>done</c> run is pushed to (spec 53, job
    /// field <c>branch</c>); only with <see cref="Worktree"/>. <see cref="TryWithBranch"/> checks it for <c>zyggy submit</c>; the shape is
    /// checked again when the envelope is built (§4 parse rules), where a refused value is a build refusal.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>Gets the optional one-line description of what the branch holds after the job (spec 53, job field <c>description</c>).</summary>
    public string? Description { get; init; }
    /// <summary>Gets the mandate the job runs under (deliverable 18); set only by <see cref="MandateSubmission"/>.</summary>
    public EnvelopeId? Mandate { get; init; }

    /// <summary>Gets the step the job belongs to (deliverable 18).</summary>
    public string? Step { get; init; }

    /// <summary>Gets the branch a worktree job starts from and pushes back to (deliverable 18).</summary>
    public string? Base { get; init; }

    /// <summary>Gets how the job reaches its machine (deliverable 18); <see cref="JobDelivery.Run"/> by default.</summary>
    public JobDelivery Deliver { get; init; }

    /// <summary>Gets what the node runs (spec 59, job field <c>kind</c>); <see cref="JobKind.Model"/> by default. Set by <see cref="TryWithFetch"/>.</summary>
    public JobKind Kind { get; init; }

    /// <summary>Gets the repository-relative file a <see cref="JobKind.Fetch"/> job reads (spec 59, job field <c>fetch_path</c>).</summary>
    public string? FetchPath { get; init; }

    /// <summary>Gets the branch a fetch reads as <c>origin/&lt;ref&gt;</c> (spec 59, job field <c>fetch_ref</c>); <see langword="null"/> for the default branch.</summary>
    public string? FetchRef { get; init; }

    /// <summary>Gets the name a <see cref="JobKind.Command"/> job runs (spec 59 slice 2, job field <c>command</c>). Set by <see cref="TryWithCommand"/>.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the run's own turn limit (spec 59 slice 6, job field <c>max_turns</c>); <see langword="null"/> runs with the node's. Set by <see cref="TryWithContinue"/>.</summary>
    public int? MaxTurns { get; init; }

    /// <summary>Gets the earlier job whose branch <c>zyggy/&lt;id&gt;</c> the run starts from (spec 59 slice 6, job field <c>continue</c>); only with <see cref="Worktree"/>.</summary>
    public EnvelopeId? Continue { get; init; }

    /// <summary>
    /// Gets the model the job must run on (spec 50, job field <c>model</c>): a full Claude Code model id, never an alias; <see langword="null"/>
    /// runs on the node's default. Set by <see cref="TryWithModel"/>.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>Gets the informational job type (spec 50, job field <c>job_type</c>): the row of Central's model table the model came from.</summary>
    public string? JobType { get; init; }

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

    /// <summary>
    /// Checks the <c>--worktree</c>, <c>--branch</c> and <c>--description</c> options of <c>zyggy submit</c> (spec 53 AC-15) before
    /// anything is signed, in this order: <paramref name="branch"/> needs <paramref name="worktree"/> (never implied); both values have
    /// the §4 shape; <paramref name="branch"/> is not, in any letter case, the <c>default_branch</c> of this request's project in
    /// <paramref name="targetRegistry"/>. The registry check is skipped when there is no file, the file is another machine's, the project
    /// is given by path or is not in it, or its entry has no <c>default_branch</c>: the node checks again at run time in every case.
    /// </summary>
    /// <param name="worktree">The <c>--worktree</c> flag.</param>
    /// <param name="branch">The <c>--branch</c> value, or <see langword="null"/>.</param>
    /// <param name="description">The <c>--description</c> value, or <see langword="null"/>.</param>
    /// <param name="targetRegistry">The target machine's registry file as read from the bus checkout, or <see langword="null"/>.</param>
    /// <param name="named">This request with the three values set, when every check passes.</param>
    /// <returns>The first refusal (field <c>branch</c> or <c>description</c>), or <see langword="null"/>.</returns>
    public JobRequestError? TryWithBranch(bool worktree, string? branch, string? description, Registry.RegistryDocument? targetRegistry, out JobRequest? named)
    {
        named = null;
        if (branch is not null)
        {
            if (!worktree)
            {
                return new JobRequestError("branch", "--branch requires --worktree");
            }

            if (!JobBranchRules.IsValidBranch(branch))
            {
                return new JobRequestError("branch", "branch " + JobBranchRules.BranchRule);
            }

            if (DefaultBranchOf(targetRegistry) is { } defaultBranch && string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                return new JobRequestError("branch", $"{branch} is the default branch {defaultBranch} of project {Project.Value} on {To.Value}");
            }
        }

        if (description is not null && !JobBranchRules.IsValidDescription(description))
        {
            return new JobRequestError("description", "description " + JobBranchRules.DescriptionRule);
        }

        named = this with { Worktree = worktree, Branch = branch, Description = description };
        return null;
    }

    /// <summary>
    /// Checks the <c>--continue</c> and <c>--max-turns</c> options of <c>zyggy submit</c> (spec 59 slice 6, AC-9) before anything is
    /// signed: <paramref name="continueId"/> needs <see cref="Worktree"/> (set first, by <see cref="TryWithBranch"/>) and is a job id;
    /// <paramref name="maxTurns"/> is at least 1. The node's ceiling is the node's check: a higher value is lowered there and reported.
    /// </summary>
    /// <param name="continueId">The <c>--continue</c> value, or <see langword="null"/>.</param>
    /// <param name="maxTurns">The <c>--max-turns</c> value, or <see langword="null"/>.</param>
    /// <param name="continued">This request with both values set, when every check passes.</param>
    /// <returns>The first refusal (field <c>continue</c> or <c>max_turns</c>), or <see langword="null"/>.</returns>
    public JobRequestError? TryWithContinue(string? continueId, int? maxTurns, out JobRequest? continued)
    {
        continued = null;
        EnvelopeId? earlier = null;
        if (continueId is not null)
        {
            if (!Worktree)
            {
                return new JobRequestError("continue", "--continue requires --worktree");
            }

            if (!EnvelopeId.TryParse(continueId, out earlier))
            {
                return new JobRequestError("continue", "continue must be a job id (ULID)");
            }
        }

        if (maxTurns is < 1)
        {
            return new JobRequestError("max_turns", "max_turns must be at least 1");
        }

        continued = this with { Continue = earlier, MaxTurns = maxTurns };
        return null;
    }

    /// <summary>
    /// Makes this request a <c>fetch</c> job (spec 59, <c>zyggy submit --fetch &lt;path&gt; [--ref &lt;branch&gt;]</c>): <paramref name="path"/> is a
    /// repository-relative path with <c>/</c> separators, no <c>.</c> or <c>..</c> segment, at most 200 characters; <paramref name="reference"/>
    /// a branch name. A fetch carries no worktree, branch or description. The node checks the path again (§6, <c>schema_unsupported</c>).
    /// </summary>
    /// <param name="path">The <c>--fetch</c> value.</param>
    /// <param name="reference">The <c>--ref</c> value, or <see langword="null"/> for the project's default branch.</param>
    /// <param name="fetch">This request as a fetch job, when both values pass.</param>
    /// <returns>The first refusal (field <c>fetch_path</c>, <c>fetch_ref</c> or <c>worktree</c>), or <see langword="null"/>.</returns>
    public JobRequestError? TryWithFetch(string path, string? reference, out JobRequest? fetch)
    {
        fetch = null;
        if (!FetchPaths.IsSafe(path))
        {
            return new JobRequestError("fetch_path", "fetch_path must be a repository-relative path with / separators, without . or .. segments, at most 200 characters");
        }

        if (reference is not null && !FetchPaths.IsBranch(reference))
        {
            return new JobRequestError("fetch_ref", "fetch_ref must be a branch name");
        }

        if (Worktree || Branch is not null || Description is not null)
        {
            return new JobRequestError("worktree", "a fetch job runs no worktree, branch or description");
        }

        if (MaxTurns is not null || Continue is not null)
        {
            return new JobRequestError("max_turns", "a fetch job runs no model: no max_turns or continue");
        }

        fetch = this with { Kind = JobKind.Fetch, FetchPath = path, FetchRef = reference };
        return null;
    }

    /// <summary>
    /// Makes this request a <c>command</c> job (spec 59 slice 2, <c>zyggy submit --command &lt;name&gt;</c>): <paramref name="name"/> is a
    /// name of <c>a-z</c>, <c>0-9</c> and <c>-</c>, looked up by the node in its own <c>node.json</c> <c>commands</c>; no envelope text
    /// reaches the argv. A command job carries no worktree, branch or description.
    /// </summary>
    /// <param name="name">The <c>--command</c> value.</param>
    /// <param name="command">This request as a command job, when the name passes.</param>
    /// <returns>The first refusal (field <c>command</c> or <c>worktree</c>), or <see langword="null"/>.</returns>
    public JobRequestError? TryWithCommand(string name, out JobRequest? command)
    {
        command = null;
        if (!Envelope.JobCommands.IsName(name))
        {
            return new JobRequestError("command", "command must be a name of a-z, 0-9 and -");
        }

        if (Worktree || Branch is not null || Description is not null)
        {
            return new JobRequestError("worktree", "a command job runs no worktree, branch or description");
        }

        if (MaxTurns is not null || Continue is not null)
        {
            return new JobRequestError("max_turns", "a command job runs no model: no max_turns or continue");
        }

        command = this with { Kind = JobKind.Command, Command = name };
        return null;
    }

    /// <summary>
    /// Checks the <c>--model</c> option of <c>zyggy submit</c> (spec 50) before anything is signed: the id has the job field's shape (a
    /// full Claude Code model id, never an alias) and the job runs a model (not <c>fetch</c> or <c>command</c>; call after
    /// <see cref="TryWithFetch"/> and <see cref="TryWithCommand"/>). Whether the target can run it is the submitter's capability check.
    /// </summary>
    /// <param name="model">The <c>--model</c> value, or <see langword="null"/> for the node's default.</param>
    /// <param name="withModel">This request with the model set, when the check passes; this request itself when <paramref name="model"/> is absent.</param>
    /// <returns>The refusal (field <c>model</c>), or <see langword="null"/>.</returns>
    public JobRequestError? TryWithModel(string? model, out JobRequest? withModel)
    {
        withModel = null;
        if (model is null)
        {
            withModel = this;
            return null;
        }

        if (!JobModelRules.IsValidModel(model))
        {
            return new JobRequestError("model", "model " + JobModelRules.ModelRule);
        }

        if (Kind != JobKind.Model)
        {
            return new JobRequestError("model", "a fetch or command job runs no model: no --model");
        }

        withModel = this with { Model = model };
        return null;
    }

    // The registry entry is matched as the node routes: the project's registry name, ordinal, in the target machine's own file. A project
    // given by path never matches (a registry name is a [a-z0-9-] label, a path is absolute), so it is skipped without a rule of its own.
    private string? DefaultBranchOf(Registry.RegistryDocument? registry) =>
        registry is null || registry.Machine != To
            ? null
            : registry.Projects.FirstOrDefault(p => string.Equals(p.Name, Project.Value, StringComparison.Ordinal))?.DefaultBranch;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex AgentName();
}
