namespace Zyggy.Core.Models;

/// <summary>
/// A command hook a model session runs before each tool call (spec 15, founding spec §3 Hooks): an executable and its arguments, passed
/// without a shell, so nothing in them needs quoting. The job runner uses it for <c>zyggy hook pre-tool-use</c>.
/// </summary>
/// <param name="Command">The absolute path of the executable.</param>
/// <param name="Args">Its arguments, in order.</param>
public sealed record ModelHookCommand(string Command, IReadOnlyList<string> Args);
