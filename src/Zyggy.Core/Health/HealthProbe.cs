namespace Zyggy.Core.Health;

/// <summary>What <see cref="HealthClient.ReadAsync"/> found on the loopback health URL (spec 08 Contracts).</summary>
public abstract record HealthProbe
{
    private protected HealthProbe()
    {
    }
}

/// <summary>The node answered with a §11 body.</summary>
/// <param name="Health">The body.</param>
public sealed record Reachable(NodeHealth Health) : HealthProbe;

/// <summary>Nothing usable answered: <c>connection refused</c>, <c>timeout</c>, <c>HTTP &lt;status&gt;</c> or <c>unparsable body</c> — never a copied message.</summary>
/// <param name="Detail">One of the four constant texts (with the status code).</param>
public sealed record Unreachable(string Detail) : HealthProbe;
