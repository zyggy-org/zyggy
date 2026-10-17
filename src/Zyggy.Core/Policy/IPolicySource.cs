namespace Zyggy.Core.Policy;

/// <summary>
/// Where a node's run policy comes from (founding spec §9 seam). v1 reads the machine's <c>node.json</c>; a signed
/// <c>tenants/&lt;org&gt;/policy.yaml</c> is a §14 item behind the same interface.
/// </summary>
public interface IPolicySource
{
    /// <summary>Returns the policy that applies to the next run.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The node's policy.</returns>
    Task<NodePolicy> GetAsync(CancellationToken ct);
}
