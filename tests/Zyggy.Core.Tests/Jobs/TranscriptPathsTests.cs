using Zyggy.Core.Envelope;
using Zyggy.Core.Jobs;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Jobs;

/// <summary>Plan 06 Step 7: the one builder of <c>&lt;nodeDir&gt;/runs/&lt;tenant&gt;/&lt;job id&gt;.jsonl</c>.</summary>
public sealed class TranscriptPathsTests
{
    private static readonly EnvelopeId Id = EnvelopeId.New(new DateTimeOffset(2026, 9, 27, 14, 5, 0, TimeSpan.Zero));

    [Fact]
    public void For_AcmeAndId_IsNodeDirRunsTenantIdJsonl()
    {
        // Act
        var path = TranscriptPaths.For("/var/zyggy", TenantId.Parse("acme"), Id);

        // Assert
        path.Should().Be(Path.Join("/var/zyggy", "runs", "acme", Id.Value + ".jsonl"));
    }

    [Fact]
    public void TenantDirectory_NeverContainsAnotherTenant()
    {
        // Act
        var acme = TranscriptPaths.TenantDirectory("/var/zyggy", TenantId.Parse("acme"));
        var globex = TranscriptPaths.TenantDirectory("/var/zyggy", TenantId.Parse("globex"));

        // Assert
        acme.Should().Be(Path.Join("/var/zyggy", "runs", "acme"));
        globex.Should().NotStartWith(acme);
        acme.Should().NotStartWith(globex);
    }

    [Fact]
    public void DefaultNodeDir_EndsWithZyggyUnderLocalApplicationData()
    {
        // Act
        var dir = TranscriptPaths.DefaultNodeDir();

        // Assert
        dir.Should().Be(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zyggy"));
    }
}
