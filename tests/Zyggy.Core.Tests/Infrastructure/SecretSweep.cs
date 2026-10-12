using System.Text;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// Spec 04 AC-29: no test secret (the synthetic PAT, the two golden keys — raw or as lowercase hex) may appear in any text the
/// code under test produced: process arguments, environment values, standard input, log lines, exception messages, console output.
/// </summary>
public static class SecretSweep
{
    public static IReadOnlyList<string> Needles { get; } =
    [
        TestKeys.TestPat,
        Encoding.UTF8.GetString(TestKeys.Secret1),
        Encoding.UTF8.GetString(TestKeys.Secret2),
        Convert.ToHexStringLower(TestKeys.Secret1),
        Convert.ToHexStringLower(TestKeys.Secret2),
    ];

    /// <summary>Fails when any text contains a needle.</summary>
    public static void AssertAbsent(IEnumerable<string?> texts)
    {
        var offenders = texts
            .Where(t => t is not null)
            .Where(t => Needles.Any(n => t!.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        offenders.Should().BeEmpty("no secret may appear in process arguments, environment, stdin, logs, exceptions or console output");
    }

    /// <summary>Every string of a process spec: file name, arguments, environment values and standard input.</summary>
    public static IEnumerable<string?> TextsOf(Zyggy.Core.Processes.ProcessSpec spec)
    {
        yield return spec.FileName;
        foreach (var argument in spec.Arguments)
        {
            yield return argument;
        }

        foreach (var value in spec.Environment.Values)
        {
            yield return value;
        }

        yield return spec.StandardInput;
    }
}
