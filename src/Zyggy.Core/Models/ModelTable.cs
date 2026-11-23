using System.Text.Json;

using Zyggy.Core.Envelope;

namespace Zyggy.Core.Models;

/// <summary>One row of Central's model table: a job type label and the model id its jobs run on (spec 50, <c>jobTypes</c>).</summary>
/// <param name="JobType">The job type label (<c>spec</c>, <c>implementation</c>, a mandate kind, …).</param>
/// <param name="Model">The full Claude Code model id, on the table's allow-list.</param>
internal sealed record ModelTableRow(string JobType, string Model);

/// <summary>
/// The outcome of reading <c>instance/models.json</c>: the table; no table and no error when there is no file; or the offending key
/// and the one line every reading verb prints (exit 2) and the supervisor wake logs.
/// </summary>
/// <param name="Table">The table, when the file is present and valid.</param>
/// <param name="ErrorKey">The offending key (<c>models</c>, <c>models[i]</c>, <c>jobTypes.&lt;type&gt;</c>, <c>supervisor</c>, …).</param>
/// <param name="Error">The line naming the file and the key.</param>
internal sealed record ModelTableResult(ModelTable? Table, string? ErrorKey, string? Error)
{
    /// <summary>The result of a missing file: no table, no error.</summary>
    public static readonly ModelTableResult NoTable = new(null, null, null);

    /// <summary>A refused file.</summary>
    /// <param name="key">The offending key.</param>
    /// <param name="message">What is wrong with it.</param>
    /// <returns>The failed result.</returns>
    public static ModelTableResult Fail(string key, string message) => new(null, key, $"instance/models.json: {key} {message}");
}

/// <summary>
/// Central's model table (spec 50, "Central's table"), read from <c>instance/models.json</c>: <see cref="Models"/> is the allow-list
/// (1–20 full Claude Code ids, each passing the job field's shape rule), <see cref="JobTypes"/> maps a job type to a model on it (file
/// order kept), <see cref="Supervisor"/> is the supervisor run's model or <see langword="null"/> for today's default. Configuration,
/// not a secret: ids and labels only. An invalid file is refused whole with its key named — never a guess, never "ignore the file".
/// </summary>
/// <param name="Models">The allow-list.</param>
/// <param name="JobTypes">The job-type rows, in file order.</param>
/// <param name="Supervisor">The supervisor run's model, or <see langword="null"/>.</param>
internal sealed record ModelTable(IReadOnlyList<string> Models, IReadOnlyList<ModelTableRow> JobTypes, string? Supervisor)
{
    /// <summary>The file name in the instance directory.</summary>
    public const string FileName = "models.json";

    /// <summary>The most ids the allow-list holds.</summary>
    public const int MaxModels = 20;

    private const string ModelsRule = "must be an array of 1–20 model ids";

    /// <summary>Gets the row of <paramref name="jobType"/>, or <see langword="null"/>.</summary>
    /// <param name="jobType">The job type label.</param>
    /// <returns>The model id of the row.</returns>
    public string? ModelFor(string jobType) => JobTypes.FirstOrDefault(r => string.Equals(r.JobType, jobType, StringComparison.Ordinal))?.Model;

    /// <summary>
    /// Where Central's verbs read the table: <c>&lt;ZYGGY_INSTANCE_DIR&gt;/models.json</c>, else <c>&lt;CLAUDE_PROJECT_DIR&gt;/instance/models.json</c>
    /// (as <c>linkedin.json</c> and the secret patterns); <see langword="null"/> when neither is set.
    /// </summary>
    /// <param name="environment">The process environment.</param>
    /// <returns>The file path, or <see langword="null"/>.</returns>
    public static string? PathIn(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? Get(string key) => environment.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
        string? instance = Get("ZYGGY_INSTANCE_DIR") ?? (Get("CLAUDE_PROJECT_DIR") is { } project ? Path.Join(project, "instance") : null);
        return instance is null ? null : Path.Join(instance, FileName);
    }

    /// <summary>Reads the table at <paramref name="path"/>: no path or no file → <see cref="ModelTableResult.NoTable"/>; else <see cref="Parse"/>.</summary>
    /// <param name="path">The file, or <see langword="null"/>.</param>
    /// <param name="readFile">Reads a file; <see langword="null"/> when it does not exist.</param>
    /// <returns>The outcome.</returns>
    public static ModelTableResult Load(string? path, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(readFile);
        return path is not null && readFile(path) is { } json ? Parse(json) : ModelTableResult.NoTable;
    }

    /// <summary>Reads a file from disk for <see cref="Load"/>: <see langword="null"/> when the file or its folder does not exist.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Parses and validates the text of <c>instance/models.json</c>.</summary>
    /// <param name="json">The file text.</param>
    /// <returns>The table, or the first offending key.</returns>
    public static ModelTableResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ModelTableResult.Fail(FileName, "is not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ModelTableResult.Fail(FileName, "is not a JSON object");
            }

            JsonElement? models = null;
            JsonElement? jobTypes = null;
            JsonElement? supervisor = null;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "models":
                        models = property.Value;
                        break;
                    case "jobTypes":
                        jobTypes = property.Value;
                        break;
                    case "supervisor":
                        supervisor = property.Value;
                        break;
                    default:
                        return ModelTableResult.Fail(property.Name, "is not a models.json key");
                }
            }

            // The allow-list first, so the rows and the supervisor are checked against it whatever the key order in the file.
            if (models is not { } list)
            {
                return ModelTableResult.Fail("models", "is missing");
            }

            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaxModels)
            {
                return ModelTableResult.Fail("models", ModelsRule);
            }

            List<string> allowed = [];
            int index = 0;
            foreach (JsonElement entry in list.EnumerateArray())
            {
                string key = $"models[{index++}]";
                if (Id(entry) is not { } id)
                {
                    return ModelTableResult.Fail(key, JobModelRules.ModelRule);
                }

                if (allowed.Contains(id, StringComparer.Ordinal))
                {
                    return ModelTableResult.Fail(key, "appears twice");
                }

                allowed.Add(id);
            }

            List<ModelTableRow> rows = [];
            if (jobTypes is { } types)
            {
                if (types.ValueKind != JsonValueKind.Object)
                {
                    return ModelTableResult.Fail("jobTypes", "must be an object");
                }

                foreach (JsonProperty row in types.EnumerateObject())
                {
                    string key = "jobTypes." + row.Name;
                    if (!JobModelRules.IsValidJobType(row.Name))
                    {
                        return ModelTableResult.Fail(key, "is not a job type label (a–z 0–9 -, 1–63)");
                    }

                    if (rows.Any(r => string.Equals(r.JobType, row.Name, StringComparison.Ordinal)))
                    {
                        return ModelTableResult.Fail(key, "appears twice");
                    }

                    if (Id(row.Value) is not { } id)
                    {
                        return ModelTableResult.Fail(key, JobModelRules.ModelRule);
                    }

                    if (!allowed.Contains(id, StringComparer.Ordinal))
                    {
                        return ModelTableResult.Fail(key, $"model {id} is not on models");
                    }

                    rows.Add(new ModelTableRow(row.Name, id));
                }
            }

            string? supervisorModel = null;
            if (supervisor is { ValueKind: not JsonValueKind.Null } chosen)
            {
                if (Id(chosen) is not { } id)
                {
                    return ModelTableResult.Fail("supervisor", JobModelRules.ModelRule);
                }

                if (!allowed.Contains(id, StringComparer.Ordinal))
                {
                    return ModelTableResult.Fail("supervisor", $"model {id} not allowed: not on models");
                }

                supervisorModel = id;
            }

            return new ModelTableResult(new ModelTable(allowed, rows, supervisorModel), null, null);
        }
    }

    // A string that passes the job field's shape rule (a full id, never an alias), or null.
    private static string? Id(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { } id && JobModelRules.IsValidModel(id) ? id : null;
}
