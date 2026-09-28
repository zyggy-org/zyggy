// fake-claude: stands in for the `claude` CLI in integration tests (tools/fake-claude/README.md).
// 1. capture (optional) → 2. resolve scenario → 3. stream its bytes unchanged → 4. exit.
// Arguments are never parsed or validated; stdin is never read.
using System.Text;

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

var scenario = Environment.GetEnvironmentVariable("ZYGGY_FAKE_CLAUDE_SCENARIO");
if (string.IsNullOrEmpty(scenario))
{
    scenario = "done";
}

var scenarioPath = Path.Combine(AppContext.BaseDirectory, "scenarios", $"{scenario}.jsonl");
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

return 0;

static void WriteRecord(Stream stream, Encoding encoding, string value)
{
    stream.Write(encoding.GetBytes(value));
    stream.WriteByte(0);
}
