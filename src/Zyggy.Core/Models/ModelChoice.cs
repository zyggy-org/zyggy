using System.Text;

using Zyggy.Core.Envelope;

namespace Zyggy.Core.Models;

/// <summary>A refused <c>--type</c> or <c>--model</c> (exit 2): the option and the line the CLI prints, with what is allowed.</summary>
/// <param name="Field"><c>type</c> or <c>model</c>.</param>
/// <param name="Detail">The reason, listing the types or the allow-list.</param>
internal sealed record ModelPickError(string Field, string Detail);

/// <summary>The model a job gets and why (spec 50, CLI): the job fields <c>model</c> and <c>job_type</c>, and the confirmation line.</summary>
/// <param name="Model">The job's <c>model</c>; <see langword="null"/> runs on the node's default.</param>
/// <param name="JobType">The job's <c>job_type</c>; <see langword="null"/> for none.</param>
/// <param name="Line">The line printed after a submit: <c>model: &lt;id&gt; (job type &lt;t&gt;)</c>, <c>model: &lt;id&gt; (override)</c> or <c>model: node default</c>.</param>
/// <param name="Error">The refusal; when set, nothing else is.</param>
internal sealed record ModelPick(string? Model, string? JobType, string? Line, ModelPickError? Error = null);

/// <summary>
/// Picks a job's model from Central's table (spec 50, "CLI"): <c>--type</c> takes the row's model and sets <c>job_type</c>; <c>--model</c>
/// overrides it for one job and must be on the allow-list — the table's, or on a machine without the instance file its
/// <c>node.json</c> <c>claude.models</c>; under a mandate the kind is the type. The model choice never comes from any other text.
/// </summary>
internal static class ModelChoice
{
    private const string NodeDefault = "model: node default";

    private const string TableSource = "instance/models.json";

    private const string NodeSource = "node.json claude.models";

    /// <summary>The text of <c>zyggy models</c>: the allow-list, one line per job type, the supervisor, and where they were read.</summary>
    /// <param name="table">Central's table, or <see langword="null"/> when there is no file.</param>
    /// <param name="nodeModels">This machine's <c>node.json</c> <c>claude.models</c>, or <see langword="null"/>.</param>
    /// <returns>Lines ending in <c>\n</c>.</returns>
    public static string Describe(ModelTable? table, IReadOnlyList<string>? nodeModels)
    {
        IReadOnlyList<string>? allowed = table?.Models ?? nodeModels;
        if (allowed is null)
        {
            return $"models: none (no {TableSource}, no {NodeSource}): jobs run on the node default\n";
        }

        var text = new StringBuilder();
        text.Append("models: ").AppendJoin(", ", allowed).Append('\n');
        if (table is null || table.JobTypes.Count == 0)
        {
            text.Append("no job types\n");
        }
        else
        {
            foreach (ModelTableRow row in table.JobTypes)
            {
                text.Append(row.JobType).Append("  ").Append(row.Model).Append('\n');
            }
        }

        text.Append("supervisor  ").Append(table?.Supervisor ?? "default").Append('\n');
        text.Append("from: ").Append(table is null ? NodeSource : TableSource).Append('\n');
        return text.ToString();
    }

    /// <summary>The model of <c>zyggy submit [--type t] [--model id]</c> without a mandate.</summary>
    /// <param name="table">Central's table, or <see langword="null"/> when there is no file.</param>
    /// <param name="nodeModels">This machine's <c>node.json</c> <c>claude.models</c>: the allow-list when there is no table.</param>
    /// <param name="type">The <c>--type</c> value, or <see langword="null"/>.</param>
    /// <param name="model">The <c>--model</c> value, or <see langword="null"/>.</param>
    /// <returns>The pick, or its refusal.</returns>
    public static ModelPick ForSubmit(ModelTable? table, IReadOnlyList<string>? nodeModels, string? type, string? model)
    {
        string? rowModel = null;
        if (type is not null)
        {
            if (!JobModelRules.IsValidJobType(type))
            {
                return Refused("type", "type is not a job type label (a–z 0–9 -, 1–63)");
            }

            if (table is null)
            {
                return Refused("type", $"--type needs a model table: no {TableSource} on this machine");
            }

            rowModel = table.ModelFor(type);
            if (rowModel is null)
            {
                string types = table.JobTypes.Count == 0 ? "none" : string.Join(", ", table.JobTypes.Select(r => r.JobType));
                return Refused("type", $"type {type} is not in the model table; types: {types}");
            }
        }

        if (model is null)
        {
            return rowModel is null ? new ModelPick(null, null, NodeDefault) : new ModelPick(rowModel, type, $"model: {rowModel} (job type {type})");
        }

        if (!JobModelRules.IsValidModel(model))
        {
            return Refused("model", "model " + JobModelRules.ModelRule);
        }

        (IReadOnlyList<string>? allowed, string source) = table is not null ? (table.Models, TableSource + " models") : (nodeModels, NodeSource);
        if (allowed is null)
        {
            return Refused("model", $"--model needs an allow-list: {TableSource} models or {NodeSource}");
        }

        return allowed.Contains(model, StringComparer.Ordinal)
            ? new ModelPick(model, type, $"model: {model} (override)")
            : Refused("model", $"model {model} is not on the allow-list ({source}): {string.Join(", ", allowed)}");
    }

    /// <summary>
    /// The model of <c>zyggy submit --mandate … --kind k</c> (spec 50 Q6): <c>job_type</c> is the kind; the model is the table's row for
    /// it, else none (the node's default). No per-job choice under a mandate. A session task (<c>deliver: session</c>) never carries a
    /// model: a live session's model is the owner's, not the job's (spec 50, edge cases).
    /// </summary>
    /// <param name="table">Central's table, or <see langword="null"/>.</param>
    /// <param name="kind">The mandate job's kind.</param>
    /// <param name="session"><see langword="true"/> for a session task.</param>
    /// <returns>The pick.</returns>
    public static ModelPick ForMandate(ModelTable? table, MandateJobKind kind, bool session = false)
    {
        string type = EnvelopeWire.ToWire(kind);
        if (session)
        {
            return new ModelPick(null, type, "model: the session's own (deliver: session)");
        }

        return table?.ModelFor(type) is { } model
            ? new ModelPick(model, type, $"model: {model} (job type {type})")
            : new ModelPick(null, type, NodeDefault);
    }

    private static ModelPick Refused(string field, string detail) => new(null, null, null, new ModelPickError(field, detail));
}
