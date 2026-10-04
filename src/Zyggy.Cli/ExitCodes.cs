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
}
