using Zyggy.Core.Memory;
using Zyggy.Core.Tenancy;
using Zyggy.Integration.Infrastructure;

namespace Zyggy.Integration.Memory;

public sealed class MemoryPathsSymlinkTests : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public void Dispose() => _scratch.Dispose();

    [Fact(SkipUnless = nameof(IsLinux), Skip = "symbolic links without privileges: Linux only")]
    public void TryResolve_SymlinkLeavingPrincipal_OnLinux_RefusedAsSymlinkEscape()
    {
        // Arrange
        var root = Path.Combine(_scratch.Path, "memory");
        var principal = Path.Combine(root, "acme", "alice");
        var outside = Path.Combine(_scratch.Path, "outside");
        Directory.CreateDirectory(Path.Combine(principal, "private"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "- [stated] 2026-09-30: outside\n");
        Directory.CreateSymbolicLink(Path.Combine(principal, "private", "areas"), outside);
        File.CreateSymbolicLink(Path.Combine(principal, "profile.md"), Path.Combine(outside, "secret.md"));
        var paths = new MemoryPaths(root, new Principal(TenantId.Parse("acme"), UserId.Parse("alice")));

        // Act
        var throughDirectory = paths.TryResolve("private/areas/secret.md");
        var throughFile = paths.TryResolve("profile.md");

        // Assert
        throughDirectory.Refusal.Should().Be(MemoryPathRefusal.SymlinkEscape);
        throughFile.Refusal.Should().Be(MemoryPathRefusal.SymlinkEscape);
    }
}
