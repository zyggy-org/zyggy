using System.Text;

using Zyggy.Core.Secrets.Verbs;

namespace Zyggy.Cli.Commands;

/// <summary>
/// The production hidden prompt (spec 09 AC-7, plan 09 A11): one line read key by key from the console without echo. Enter ends
/// it, Backspace removes a character, a cancelled token (Ctrl+C through the host) gives <see langword="null"/>. The characters
/// are handed over once and the buffer cleared.
/// </summary>
internal sealed class ConsoleHiddenInput : IHiddenInput
{
    public string? ReadLine(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.Error.WriteLine();
                        return buffer.ToString();
                    case ConsoleKey.Backspace:
                        if (buffer.Length > 0)
                        {
                            buffer.Length--;
                        }

                        break;
                    default:
                        if (key.KeyChar != '\0')
                        {
                            buffer.Append(key.KeyChar);
                        }

                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // No console to read keys from (a service, a detached process): nothing can be entered.
            return null;
        }
        finally
        {
            buffer.Clear();
        }
    }
}
