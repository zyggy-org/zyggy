using Zyggy.Core.Dream;

namespace Zyggy.Cli;

/// <summary>The exit code of <c>zyggy dream</c> from its run record (spec 28 CLI table).</summary>
internal static class DreamExitCode
{
    public static int From(DreamRunRecord record) => record.Outcome switch
    {
        "committed" when !record.Pushed => ExitCodes.PushDeferred,
        "committed" or "nothing_to_do" => ExitCodes.Ok,
        "aborted" => ExitCodes.Aborted,
        "partial" => record.Check is not null ? ExitCodes.Aborted : ExitCodes.Failed,
        _ when record.Reason == "locked" => ExitCodes.Locked,
        _ => ExitCodes.Failed,
    };
}
