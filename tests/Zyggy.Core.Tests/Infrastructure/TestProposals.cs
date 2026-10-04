using System.Text.Json;
using System.Text.Json.Nodes;

using Zyggy.Core.Models;
using Zyggy.Core.Runs;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>Builds filing proposals as the model would return them (snake_case JSON per Appendix A).</summary>
internal sealed class TestProposals
{
    private readonly JsonArray _dispositions = [];
    private readonly JsonArray _newCategories = [];
    private readonly JsonArray _creates = [];
    private readonly JsonArray _edits = [];

    public static TestProposals New() => new();

    public TestProposals Disposition(string line, string outcome, string? target = null, string? dropReason = null)
    {
        var d = new JsonObject { ["line"] = line, ["outcome"] = outcome };
        if (target is not null)
        {
            d["target"] = target;
        }

        if (dropReason is not null)
        {
            d["drop_reason"] = dropReason;
        }

        _dispositions.Add(d);
        return this;
    }

    public TestProposals NewCategory(string side, string name, string description)
    {
        _newCategories.Add(new JsonObject { ["side"] = side, ["name"] = name, ["description"] = description });
        return this;
    }

    public TestProposals Create(string path, string name, string description, string[] lines, string[]? aliases = null)
    {
        _creates.Add(new JsonObject
        {
            ["path"] = path,
            ["name"] = name,
            ["description"] = description,
            ["aliases"] = new JsonArray((aliases ?? []).Select(a => (JsonNode?)a).ToArray()),
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode?)l).ToArray()),
        });
        return this;
    }

    public TestProposals Edit(
        string path,
        string[]? append = null,
        (string Old, string New)[]? replace = null,
        (string Old, string Reason)[]? remove = null,
        string? description = null,
        string[]? aliases = null)
    {
        var edit = new JsonObject
        {
            ["path"] = path,
            ["append"] = new JsonArray((append ?? []).Select(a => (JsonNode?)a).ToArray()),
            ["replace"] = new JsonArray((replace ?? []).Select(r => (JsonNode?)new JsonObject { ["old"] = r.Old, ["new"] = r.New }).ToArray()),
            ["remove"] = new JsonArray((remove ?? []).Select(r => (JsonNode?)new JsonObject { ["old"] = r.Old, ["reason"] = r.Reason }).ToArray()),
        };
        if (description is not null)
        {
            edit["description"] = description;
        }

        if (aliases is not null)
        {
            edit["aliases"] = new JsonArray(aliases.Select(a => (JsonNode?)a).ToArray());
        }

        _edits.Add(edit);
        return this;
    }

    public JsonElement Build() =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["dispositions"] = _dispositions.DeepClone(),
            ["new_categories"] = _newCategories.DeepClone(),
            ["creates"] = _creates.DeepClone(),
            ["edits"] = _edits.DeepClone(),
            ["notes"] = "test proposal",
        });
}

/// <summary>Model results a substituted <see cref="IModelRunner"/> returns.</summary>
internal static class TestModelResults
{
    public static ModelRunResult Succeeded(JsonElement structured, decimal cost = 0.25m) =>
        new(ModelRunOutcome.Succeeded, null, null, "done", structured, cost, 3, TimeSpan.FromSeconds(4), "fake-model-1", 100, 20, 0, 0);

    public static ModelRunResult Failed(RunFailureReason reason, string detail) =>
        new(ModelRunOutcome.Failed, reason, detail, null, null, 0.01m, 1, TimeSpan.FromSeconds(1), "fake-model-1", 10, 0, 1, 0);
}
