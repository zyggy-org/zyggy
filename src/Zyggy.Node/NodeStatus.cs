using Zyggy.Core.Bus;

namespace Zyggy.Node;

/// <summary>What the host knows about itself for <c>/health</c> (spec 07): the loop's listener once it exists and the login probe's last verdict.</summary>
internal sealed class NodeStatus
{
    /// <summary>Gets or sets the loop's listener; <see langword="null"/> until <see cref="PollLoop"/> starts.</summary>
    public BusListener? Listener { get; set; }

    /// <summary>Gets or sets the login probe's last verdict (§11 <c>claudeAuthOk</c>); <see langword="null"/> until the first probe answers.</summary>
    public bool? ClaudeAuthOk { get; set; }
}
