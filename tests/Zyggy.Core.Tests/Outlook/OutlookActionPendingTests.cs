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
}
