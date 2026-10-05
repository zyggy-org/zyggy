using System.Globalization;
using System.Text;

using Zyggy.Core.Verbs;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>A <see cref="VerbIo"/> over string readers and writers, so a test sees a verb's exact stdout and stderr.</summary>
internal sealed class VerbConsole
{
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _error = new();

    public VerbConsole(string? stdin = null)
    {
        Io = new VerbIo(
            new StringReader(stdin ?? string.Empty),
            new StringWriter(_out, CultureInfo.InvariantCulture) { NewLine = "\n" },
            new StringWriter(_error, CultureInfo.InvariantCulture) { NewLine = "\n" },
            stdin is not null);
    }

    public VerbIo Io { get; }

    public string Stdout => _out.ToString();

    public string Stderr => _error.ToString();
}
