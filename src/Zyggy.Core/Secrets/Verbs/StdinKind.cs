namespace Zyggy.Core.Secrets.Verbs;

/// <summary>What standard input is when <c>zyggy secret set</c> runs: a pipe or file (read to the end) or a terminal (the hidden prompt).</summary>
public enum StdinKind
{
    /// <summary>Redirected from a pipe or a file: the value is read to the end, one trailing newline stripped.</summary>
    Redirected,

    /// <summary>A terminal: the value is asked for without echo.</summary>
    Terminal,
}
