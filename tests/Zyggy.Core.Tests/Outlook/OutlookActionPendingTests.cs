using Zyggy.Core.Outlook;

namespace Zyggy.Core.Tests.Outlook;

/// <summary>
/// Spec 58 slice 7 (AC-12), verify 01M81R40G1YGA46142HYTV5H7T points 1 and 3: a pass of the outlook guard is taken only with its
/// nonce, and taking it is atomic — load, check, remove and save under one file lock — so two processes running the same call cannot
/// both use one pass.
/// </summary>
public sealed class OutlookActionPendingTests : IDisposable
{
    private const string Key = "key-1";

    private const string Nonce = "nonce-1";

    private static readonly DateTimeOffset T = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly OutlookActionLogRow Row = new(T, OutlookAction.Send, OutlookActionOutcome.Denied, null, null, ["a@example.test"], "s", null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Take_WithTheNonce_True()
    {
        // Arrange
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);

        // Act
        bool taken = pending.Take(Key, Nonce, T);

        // Assert
        taken.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonce-2")]
    public void Take_WithoutTheNonce_False(string? nonce)
    {
        // Arrange
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);

        // Act
        bool taken = pending.Take(Key, nonce, T);

        // Assert
        taken.Should().BeFalse();
    }

    [Fact]
    public void Take_AfterATakeWithoutTheNonce_ThePassStaysForItsOwner()
    {
        // Arrange
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);
        pending.Take(Key, "nonce-2", T);

        // Act
        bool taken = pending.Take(Key, Nonce, T);

        // Assert
        taken.Should().BeTrue();
    }

    [Fact]
    public async Task Take_TwoProcessesAtOnce_OnlyOneTakesThePass()
    {
        // Arrange: many rounds, each one pass and two takers released together (two instances, as two processes have).
        const int Rounds = 60;
        var winners = new List<int>();

        // Act
        for (var round = 0; round < Rounds; round++)
        {
            new OutlookActionPending(_root).Add(Key, Nonce, Row, T);
            using var start = new Barrier(2);
            Task<bool> Taker() => Task.Run(() =>
            {
                var own = new OutlookActionPending(_root);
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return own.Take(Key, Nonce, T);
            });
            bool[] taken = await Task.WhenAll(Taker(), Taker());
            winners.Add(taken.Count(t => t));
        }

        // Assert
        winners.Should().AllSatisfy(w => w.Should().Be(1));
    }

    // ---- authenticated pass lines (verify 01M81W8C4K81GE119A7WH3228D point 2) ----

    [Fact]
    public void Add_WritesAMacPerLine_UnderTheDaysKeyFile()
    {
        // Act
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);

        // Assert
        string line = File.ReadAllLines(pending.FilePath).Single();
        (System.Text.RegularExpressions.Regex.IsMatch(line, "\"mac\":\"[0-9a-f]{64}\""), File.Exists(pending.KeyPath(T)), Path.GetFileName(pending.KeyPath(T)))
            .Should().Be((true, true, "approval-2026-11-22.key"));
    }

    [Fact]
    public void Take_HandWrittenLineWithoutMac_False()
    {
        // Arrange: a same-user process appends a line in the guard's format, with the hash of its own nonce.
        var pending = new OutlookActionPending(_root);
        pending.Add("other-key", "other-nonce", Row, T);
        File.AppendAllText(pending.FilePath, Forged(mac: null) + "\n");

        // Act
        bool taken = pending.Take(Key, Nonce, T);

        // Assert
        taken.Should().BeFalse();
    }

    [Fact]
    public void Take_HandWrittenLineWithAMadeUpMac_False()
    {
        // Arrange
        var pending = new OutlookActionPending(_root);
        pending.Add("other-key", "other-nonce", Row, T);
        File.AppendAllText(pending.FilePath, Forged(mac: new string('a', 64)) + "\n");

        // Act
        bool taken = pending.Take(Key, Nonce, T);

        // Assert
        taken.Should().BeFalse();
    }

    [Fact]
    public void Take_GuardsLineEditedToAnotherRequest_False()
    {
        // Arrange: the guard's own line, its key swapped for another request's.
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);
        File.WriteAllText(pending.FilePath, File.ReadAllText(pending.FilePath).Replace(Key, "key-2", StringComparison.Ordinal));

        // Act
        bool taken = pending.Take("key-2", Nonce, T);

        // Assert
        taken.Should().BeFalse();
    }

    [Fact]
    public void Take_KeyFileReplaced_False()
    {
        // Arrange
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);
        File.WriteAllText(pending.KeyPath(T), new string('b', 64));

        // Act
        bool taken = pending.Take(Key, Nonce, T);

        // Assert
        taken.Should().BeFalse();
    }

    [Fact]
    public void Take_PassFromBeforeMidnight_TakenAfterIt_True()
    {
        // Arrange: the day's key changes at midnight; a pass keeps the key of its own day.
        var before = new DateTimeOffset(2026, 11, 22, 23, 55, 0, TimeSpan.Zero);
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, before);

        // Act
        bool taken = pending.Take(Key, Nonce, before.AddMinutes(10));

        // Assert
        taken.Should().BeTrue();
    }

    [Fact]
    public void Settle_HandWrittenLine_NoDeniedRow()
    {
        // Arrange
        var pending = new OutlookActionPending(_root);
        pending.Add(Key, Nonce, Row, T);
        File.AppendAllText(pending.FilePath, Forged(mac: null) + "\n");

        // Act
        var rows = pending.Settle(T, all: true);

        // Assert
        rows.Count.Should().Be(1);
    }

    private static string Forged(string? mac) =>
        "{\"key\":\"" + Key + "\",\"nonce\":\"" + OutlookActionPending.NonceHash(Nonce) + "\",\"ts\":\"2026-11-22T09:00:00.0000000Z\",\"action\":\"send\",\"to\":[\"a@example.test\"],\"subject\":\"s\""
        + (mac is null ? string.Empty : ",\"mac\":\"" + mac + "\"") + "}";
}
