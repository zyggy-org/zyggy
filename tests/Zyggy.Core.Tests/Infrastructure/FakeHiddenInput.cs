using Zyggy.Core.Secrets.Verbs;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>A scripted hidden prompt: answers <paramref name="line"/> once (<see langword="null"/> = the owner aborted) and records that it was asked.</summary>
internal sealed class FakeHiddenInput(string? line) : IHiddenInput
{
    public bool Called { get; private set; }

    public string? ReadLine(CancellationToken cancellationToken)
    {
        Called = true;
        return line;
    }
}
