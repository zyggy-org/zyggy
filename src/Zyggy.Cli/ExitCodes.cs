namespace Zyggy.Cli;

/// <summary>The process exit codes of every <c>zyggy</c> verb (spec 28, CLI table).</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int NoRun = 1;
    public const int Usage = 2;
    public const int Configuration = 3;
    public const int Locked = 4;
    public const int UnknownSection = 4;
    public const int Aborted = 5;
    public const int Failed = 6;
    public const int PushDeferred = 7;

    /// <summary>Spec 08 exit-code table: <c>zyggy status</c> amber ("look later"); the same number as <see cref="NoRun"/>.</summary>
    public const int Amber = 1;

    /// <summary>Spec 08 exit-code table: <c>zyggy status --id</c> found no such job; the same number as <see cref="NoRun"/>.</summary>
    public const int NotFound = 1;
}
