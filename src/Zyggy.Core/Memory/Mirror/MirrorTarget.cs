using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Memory.Mirror;

/// <summary>One entry of <c>mirror.targets</c> in Central's <c>node.json</c> (spec 47 Configuration). Never a secret.</summary>
/// <param name="Machine">The laptop the mirror serves.</param>
/// <param name="Repository">The SSH URL of <c>zyggy-mirror-&lt;machine&gt;</c>.</param>
/// <param name="DeployKey">The path of that mirror's deploy key file (a path, never the key).</param>
/// <param name="Reach">What the machine may read; the default reach when <c>paths</c> is omitted.</param>
internal sealed record MirrorTarget(MachineName Machine, string Repository, string DeployKey, MirrorReach Reach);
