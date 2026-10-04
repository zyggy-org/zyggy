using Zyggy.Core.Memory;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Memory;

/// <summary>The 27 secret-pattern file (byte copy of zyggy-core e0b8290) applied in .NET.</summary>
public sealed class SecretPatternsTests
{
    private static string File(string name) => Path.Combine(Golden.Directory, "secret-patterns", name);

    private static SecretPatterns Patterns() => SecretPatterns.Load(File("secret-patterns.txt")).Patterns!;

    public static TheoryData<string, string> SecretSamples()
    {
        var data = new TheoryData<string, string>();
        foreach (var line in System.IO.File.ReadAllLines(File("secret-samples.txt")).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            data.Add(line[..tab], line[(tab + 1)..]);
        }

        return data;
    }

    public static TheoryData<string> BenignSamples()
    {
        var data = new TheoryData<string>();
        foreach (var line in System.IO.File.ReadAllLines(File("benign-samples.txt")).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            data.Add(line);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SecretSamples))]
    public void TryMatch_EverySecretSample_Matches(string name, string sample)
    {
        // Act
        var matched = Patterns().TryMatch(sample, out var found);

        // Assert
        matched.Should().BeTrue(sample);
        found.Should().Be(name);
    }

    [Theory]
    [MemberData(nameof(BenignSamples))]
    public void TryMatch_EveryBenignSample_DoesNotMatch(string sample)
    {
        // Assert
        Patterns().TryMatch(sample, out _).Should().BeFalse(sample);
    }

    [Fact]
    public void Load_MissingFile_ReturnsLoadFailure()
    {
        // Act
        var load = SecretPatterns.Load(File("no-such-file.txt"));

        // Assert
        load.Patterns.Should().BeNull();
        load.Error.Should().NotBeNullOrEmpty();
    }
}
