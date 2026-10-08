using Zyggy.Core.Verbs;

namespace Zyggy.Core.Brief;

/// <summary>
/// <c>zyggy brief request</c>: asks for a brief run now. Writes the empty <c>brief.request</c> file (0600) that the instance's
/// <c>zyggy-morning-brief.path</c> unit watches; the unit starts <c>zyggy m365 brief</c> in its own sandbox, which deletes the file first
/// and writes today's brief (or reports "already created"). The session itself never runs the brief: this verb reads no mailbox and no
/// model. Exit 0 requested (also when a request is already pending) · 3 the state root cannot be written · 4 usage.
/// </summary>
internal sealed class BriefRequestVerb(BriefVerbContext context)
{
    private const string Prefix = "brief: ";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(io);
        if (args.Count > 0)
        {
            await io.Error.WriteAsync(Prefix + "request takes no argument (usage: zyggy brief request)\n").ConfigureAwait(false);
            return 4;
        }

        var path = new BriefPaths(context.Environment).Request;
        try
        {
            var root = Path.GetDirectoryName(path)!;
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(root);
            }
            else
            {
                Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (new FileStream(path, options))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await io.Error.WriteAsync($"{Prefix}could not write {path} — runbook 13 \"Brief run failed\"\n").ConfigureAwait(false);
            return 3;
        }

        await io.Out.WriteAsync("brief requested\n").ConfigureAwait(false);
        return 0;
    }
}
