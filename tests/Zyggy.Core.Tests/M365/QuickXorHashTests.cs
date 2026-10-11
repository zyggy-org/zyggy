using Zyggy.Core.M365.Graph;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>Spec 38 AC-12: QuickXorHash equals values from an implementation independent of the code under test (golden README).</summary>
public sealed class QuickXorHashTests
{
    private const string TextItem = "Quote for the Zyggy roof repair.\nTotal 1 234,00 EUR, valid 30 days.\n";

    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (var line in File.ReadAllLines(VectorFile()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            data.Add(line.Split('\t')[0]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Base64_Vectors_MatchIndependentReference(string name)
    {
        // Arrange
        var (input, expected) = Load(name);

        // Act
        var hash = QuickXorHash.Base64(input);

        // Assert
        hash.Should().Be(expected);
    }

    [Fact]
    public void Base64_FixedTextItem_EqualsGolden()
    {
        // Act
        var hash = QuickXorHash.Base64(System.Text.Encoding.UTF8.GetBytes(TextItem));

        // Assert: the 68-byte item of tests/golden/archive/sidecar-text.md, value in quickxor-vectors.tsv (text-item)
        hash.Should().Be("jAt95bF64I8MSmyO3joPMtROLIU=");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(160)]
    [InlineData(4096)]
    public void HashAlgorithm_IncrementalChunks_EqualsOneShot(int chunk)
    {
        // Arrange
        var (input, expected) = Load("gen-1MiB");
        using var hash = new QuickXorHash();

        // Act
        for (var offset = 0; offset < input.Length; offset += chunk)
        {
            hash.TransformBlock(input, offset, Math.Min(chunk, input.Length - offset), null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);

        // Assert
        Convert.ToBase64String(hash.Hash!).Should().Be(expected);
    }

    [Fact]
    public void HashSize_Is160()
    {
        // Act
        using var hash = new QuickXorHash();

        // Assert
        hash.HashSize.Should().Be(160);
    }

    private static string VectorFile() => Path.Combine(Golden.Directory, "archive", "offload", "quickxor-vectors.tsv");

    private static (byte[] Input, string Expected) Load(string name)
    {
        var parts = File.ReadAllLines(VectorFile()).Select(l => l.Split('\t')).Single(p => p[0] == name);
        var length = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var input = parts[2] == "gen"
            ? [.. Enumerable.Range(0, length).Select(i => (byte)(((long)i * 31 + 7) % 256))]
            : Convert.FromBase64String(parts[2]["b64:".Length..]);
        input.Length.Should().Be(length);
        return (input, parts[3]);
    }
}
