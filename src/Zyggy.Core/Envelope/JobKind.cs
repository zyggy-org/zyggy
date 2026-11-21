namespace Zyggy.Core.Envelope;

/// <summary>What a node runs for a job (spec 59, the job field <c>kind</c>); wire strings via <see cref="EnvelopeWire"/>.</summary>
public enum JobKind
{
    /// <summary><c>model</c> (the default, never written): the node runs the model on the job's body, as before spec 59.</summary>
    Model,

    /// <summary><c>fetch</c>: the node reads one file of the project at <c>origin/&lt;fetch_ref&gt;</c> and attaches it to the report; no model.</summary>
    Fetch,

    /// <summary><c>command</c>: the node runs the argv its own <c>node.json</c> <c>commands</c> lists under the job's <c>command</c> name; no model, no shell.</summary>
    Command,
}
