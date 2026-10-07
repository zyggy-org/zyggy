using System.Text;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>The <c>little</c> escaping (spec 36 AC-14, AC-3): golden cases per rule, and un-escaping a commentary gives the text back.</summary>
public sealed class LittleTextTests
{
    public static TheoryData<string, string> GoldenCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(Golden.Directory, "linkedin", "little", "cases.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split('\t');
            data.Add(parts[0], parts[1]);
        }

        return data;
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 500)];

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Escape_GoldenCases(string input, string escaped)
    {
        // Act
        var result = LittleText.Escape(input);

        // Assert
        result.Should().Be(escaped);
    }

    [Fact]
    public void Escape_HashtagKept_HashBeforeSpaceEscaped()
    {
        // Act
        var result = LittleText.Escape("#AI is # one, #2026 too, end #");

        // Assert
        result.Should().Be("#AI is \\# one, #2026 too, end \\#");
    }

    [Fact]
    public void Escape_NewlineKept()
    {
        // Act
        var result = LittleText.Escape("one\n\ntwo (x)\n");

        // Assert
        result.Should().Be("one\n\ntwo \\(x\\)\n");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RoundTrip_500SeededRandomTexts_UnescapeEscapeIsIdentity(int seed)
    {
        // Arrange
        var text = RandomText(seed);

        // Act
        var back = LittleText.Unescape(LittleText.Escape(text));

        // Assert
        back.Should().Be(text);
    }

    /// <summary>A text drawn from every reserved character, <c>#</c>, letters, digits, emoji, <c>\n</c> and spaces.</summary>
    internal static string RandomText(int seed)
    {
        string[] alphabet = ["|", "{", "}", "@", "[", "]", "(", ")", "<", ">", "\\", "*", "_", "~", "#", "#", "a", "Z", "é", "7", "0", "🚀", "✨", "\n", " ", " ", ".", "\""];
        var random = new Random(seed);
        var builder = new StringBuilder();
        var length = random.Next(1, 200);
        for (var i = 0; i < length; i++)
        {
            builder.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return builder.ToString();
    }
}
