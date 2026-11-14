// fake-claude: stands in for the `claude` CLI in integration tests (tools/fake-claude/README.md).
// 0. `auth status` (spec 07): answer from ZYGGY_FAKE_CLAUDE_AUTH and exit — no capture, no scenario.
// 1. argument capture (optional) → 2. stdin capture (optional) → 2a. tool calls through PreToolUse hooks (optional) → 2b. file edits (optional) → 3. delay (optional) → 4. resolve scenario
// → 5. stream its bytes unchanged → 5b. linger (optional, the "partial output then hang" case) → 6. exit (ZYGGY_FAKE_CLAUDE_EXIT, default 0).
// Arguments are never parsed or validated, except the two words `auth status` and, for tool calls, the JSON of `--settings`; stdin is read only when ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE is set.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args is ["auth", "status", ..])
{
    var auth = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_AUTH");
    switch (auth)
    {
        case null or "" or "1":
            Console.Out.WriteLine("logged in");
            return 0;
        case "0":
            Console.Out.WriteLine("not logged in");
            return 1;
        default:
            Console.Error.WriteLine($"fake-claude: invalid ZYGGY_FAKE_CLAUDE_AUTH '{auth}'");
            return 5;
    }
}

if (!TryReadInt("ZYGGY_FAKE_CLAUDE_DELAY_MS", min: 0, out var delayMs)
    || !TryReadInt("ZYGGY_FAKE_CLAUDE_LINGER_MS", min: 0, out var lingerMs)
    || !TryReadInt("ZYGGY_FAKE_CLAUDE_EXIT", min: 0, out var exitCode))
{
    return 5;
}

var capturePath = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_CAPTURE");
if (!string.IsNullOrEmpty(capturePath))
{
    try
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var capture = new FileStream(capturePath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteRecord(capture, utf8, Environment.CurrentDirectory);
        foreach (var arg in args)
        {
            WriteRecord(capture, utf8, arg);
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"fake-claude: cannot write capture '{capturePath}': {ex.Message}");
        return 4;
    }
}

var stdinCapturePath = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_STDIN_CAPTURE");
if (!string.IsNullOrEmpty(stdinCapturePath))
{
    try
    {
        using var stdin = Console.OpenStandardInput();
        using var capture = new FileStream(stdinCapturePath, FileMode.Create, FileAccess.Write, FileShare.None);
        stdin.CopyTo(capture);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"fake-claude: cannot write stdin capture '{stdinCapturePath}': {ex.Message}");
        return 4;
    }
}

// 2a. tool calls (spec 15): each scripted call goes through the PreToolUse command hooks of --settings, as Claude Code does.
var toolCallsText = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_TOOL_CALLS");
if (!string.IsNullOrEmpty(toolCallsText))
{
    var toolLog = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_TOOL_LOG");
    var hooks = PreToolUseHooks(args);
    foreach (var call in toolCallsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(call);
        }
        catch (JsonException)
        {
            node = null;
        }

        if (node?["tool_name"]?.GetValue<string>() is not { } toolName || node["tool_input"] is not JsonObject toolInput)
        {
            Console.Error.WriteLine($"fake-claude: invalid ZYGGY_FAKE_CLAUDE_TOOL_CALLS line '{call}'");
            return 5;
        }

        var input = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = toolName,
            ["tool_input"] = toolInput.DeepClone(),
            ["cwd"] = Environment.CurrentDirectory,
        }.ToJsonString();

        // Claude Code: exit 2 blocks the call; any other exit, or a hook that cannot start, is a non-blocking error and the call proceeds.
        var blocked = hooks.Any(h => RunHook(h.Command, h.Args, input) == 2);
        if (!blocked && toolName == "Write"
            && toolInput["file_path"]?.GetValue<string>() is { } filePath)
        {
            var target = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, filePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, toolInput["content"]?.GetValue<string>() ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        if (!string.IsNullOrEmpty(toolLog))
        {
            File.AppendAllText(toolLog, (blocked ? "blocked " : "allowed ") + toolName + "\n");
        }
    }
}

// 2b. edits (spec 16): the model changing files in its working directory, before the delay ("writes a file then hangs").
var editsText = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_EDITS");
if (!string.IsNullOrEmpty(editsText))
{
    if (!TryParseEdits(editsText, Environment.CurrentDirectory, out var edits))
    {
        Console.Error.WriteLine($"fake-claude: invalid ZYGGY_FAKE_CLAUDE_EDITS '{editsText.ReplaceLineEndings(" | ")}'");
        return 5;
    }

    foreach (var (path, content) in edits)
    {
        if (content is null)
        {
            File.Delete(path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}

if (delayMs > 0)
{
    Thread.Sleep(delayMs);
}

var scenario = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_SCENARIO");
if (string.IsNullOrEmpty(scenario))
{
    scenario = "done";
}

// A test that materialises a scenario (today's date in it) points here instead of the executable's own scenarios/ folder.
var scenarioDirectory = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_SCENARIO_DIR");
var scenarioPath = Path.Combine(
    string.IsNullOrEmpty(scenarioDirectory) ? Path.Combine(AppContext.BaseDirectory, "scenarios") : scenarioDirectory,
    $"{scenario}.jsonl");
if (!File.Exists(scenarioPath))
{
    Console.Error.WriteLine($"fake-claude: unknown scenario '{scenario}'");
    return 3;
}

using (var input = File.OpenRead(scenarioPath))
using (var stdout = Console.OpenStandardOutput())
{
    input.CopyTo(stdout);
    stdout.Flush();
}

if (lingerMs > 0)
{
    Thread.Sleep(lingerMs);
}

return exitCode;

static void WriteRecord(Stream stream, Encoding encoding, string value)
{
    stream.Write(encoding.GetBytes(value));
    stream.WriteByte(0);
}

// One entry per line: "+<relative path>\t<content>" writes, "-<relative path>" deletes. Every path must stay inside the
// working directory; one bad entry refuses all of them, so nothing is half applied.
static bool TryParseEdits(string text, string root, out List<(string Path, string? Content)> edits)
{
    edits = [];
    var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var tab = line.IndexOf('\t', StringComparison.Ordinal);
        var (kind, relative, content) = line[0] switch
        {
            '+' when tab > 1 => ('+', line[1..tab], line[(tab + 1)..]),
            '-' when line.Length > 1 => ('-', line[1..], (string?)null),
            _ => ('?', string.Empty, null),
        };
        if (kind == '?' || Path.IsPathRooted(relative))
        {
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        edits.Add((full, content));
    }

    return true;
}

// Every "type":"command" hook under hooks.PreToolUse of each --settings argument (a JSON text; a file path is not read).
static List<(string Command, List<string> Args)> PreToolUseHooks(string[] args)
{
    var hooks = new List<(string, List<string>)>();
    for (var i = 0; i + 1 < args.Length; i++)
    {
        if (args[i] != "--settings" || !args[i + 1].TrimStart().StartsWith('{'))
        {
            continue;
        }

        if (JsonNode.Parse(args[i + 1])?["hooks"]?["PreToolUse"] is not JsonArray groups)
        {
            continue;
        }

        foreach (var hook in groups.SelectMany(g => g?["hooks"]?.AsArray() ?? []))
        {
            if (hook?["type"]?.GetValue<string>() == "command" && hook["command"]?.GetValue<string>() is { } command)
            {
                var hookArgs = hook["args"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? [];
                hooks.Add((command, hookArgs));
            }
        }
    }

    return hooks;
}

static int RunHook(string command, List<string> hookArgs, string input)
{
    var info = new ProcessStartInfo(command)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var arg in hookArgs)
    {
        info.ArgumentList.Add(arg);
    }

    try
    {
        using var process = Process.Start(info)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Console.Error.Write(stderr.Result);
        return process.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return -1;
    }
}

static bool TryReadInt(string name, int min, out int value)
{
    var text = Environment.GetEnvironmentVariable(name);
    value = 0;
    if (string.IsNullOrEmpty(text))
    {
        return true;
    }

    if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min)
    {
        return true;
    }

    Console.Error.WriteLine($"fake-claude: invalid {name} '{text}'");
    return false;
}
