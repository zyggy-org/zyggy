using System.Text;

namespace Zyggy.Core.Verbs;

/// <summary>
/// The console of a verb that replaces a template script: stdin, stdout and stderr, injected so tests capture exact bytes.
/// Verbs write <c>\n</c> line endings themselves; the console streams are UTF-8 without BOM.
/// </summary>
/// <param name="In">Standard input.</param>
/// <param name="Out">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="InputRedirected">Whether standard input comes from a pipe or file rather than a terminal.</param>
public sealed record VerbIo(TextReader In, TextWriter Out, TextWriter Error, bool InputRedirected)
{
    /// <summary>Returns the process console as a <see cref="VerbIo"/>.</summary>
    /// <returns>The console streams, UTF-8 without BOM, flushed on every write.</returns>
    public static VerbIo FromConsole()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        return new VerbIo(stdin, stdout, stderr, Console.IsInputRedirected);
    }
}
