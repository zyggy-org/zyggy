using System.Text.Json;

using Zyggy.Core.Configuration;
using Zyggy.Core.Secrets;

namespace Zyggy.Core.Tests.Deploy;

/// <summary>Spec 10 AC-3: the installer's embedded <c>node.json</c> template is the spec's JSON and, rendered, is accepted by the real reader.</summary>
public sealed class NodeJsonTemplateTests : IDisposable
{
    private static readonly string[] SecretWords = ["pat", "token", "hmac", "secret", "password"];

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public NodeJsonTemplateTests() => Directory.CreateDirectory(_scratch);

    public static bool IsWindows => OperatingSystem.IsWindows();

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public void Template_IsTheSpecJsonBetweenTheMarkers()
    {
        // Act
        using JsonDocument document = JsonDocument.Parse(DeployAssets.TemplateJson());

        // Assert
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("tenant", "machine", "signWith", "bus", "poll", "workHours", "devRoots", "maxConcurrentJobs", "log", "claude", "secrets");
        document.RootElement.GetProperty("secrets").EnumerateObject().Select(p => p.Name).Should().Equal("store");
        document.RootElement.GetProperty("secrets").GetProperty("store").GetString().Should().Be("os");
    }

    [Fact]
    public void Template_RenderedForAcmeHomeLaptop_AcceptedByTheReader()
    {
        // Arrange: an absolute bus path of the running OS with forward slashes (C:/agents/bus is rooted on Windows only)
        string bus = Path.Combine(_scratch, "bus").Replace('\\', '/');
        string profile = _scratch.Replace('\\', '/');
        string path = Path.Combine(_scratch, "node.json");
        File.WriteAllText(path, DeployAssets.RenderTemplate("acme", "home-laptop", "acme/1", bus, profile));

        // Act
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        NodeConfiguration c = result.Configuration!;
        c.Tenant.Value.Should().Be("acme");
        c.Machine.Value.Should().Be("home-laptop");
        c.SignWith!.ToString().Should().Be("acme/1");
        c.Bus.Remote.Should().Be("https://github.com/zyggy-org/zyggy-bus.git");
        c.Bus.Owner.Should().Be("zyggy-org");
        c.Bus.Repo.Should().Be("zyggy-bus");
        c.Bus.Branch.Should().Be("main");
        c.Bus.LocalPath.Should().Be(bus);
        c.Secrets.Store.Should().Be(SecretStoreKind.Os);
        c.Secrets.Root.Should().BeNull();
        c.WorkHours.Should().NotBeNull();
        c.WorkHours!.Timezone.Should().Be("Europe/Brussels");
        c.WorkHours.Days.Should().HaveCount(5);
        c.Claude.Path.Should().EndWith("/.local/bin/claude.exe");
        Path.IsPathRooted(c.Claude.Path!).Should().BeTrue();
        c.DevRoots.Should().BeEmpty();
        c.MaxConcurrentJobs.Should().Be(2);
        c.Poll.ActiveSeconds.Should().Be(10);
        c.Poll.IdleSeconds.Should().Be(60);
        c.Poll.SafetyFetchMinutes.Should().Be(15);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "C:/agents/bus is rooted on Windows only")]
    public void Template_OnWindows_SpecLiteralBusPathAccepted()
    {
        // Arrange
        string path = Path.Combine(_scratch, "node.json");
        File.WriteAllText(path, DeployAssets.RenderTemplate("acme", "home-laptop", "acme/1", "C:/agents/bus", _scratch.Replace('\\', '/')));

        // Act
        NodeConfigurationResult result = NodeConfigurationReader.Read(path);

        // Assert
        result.IsOk.Should().BeTrue(result.ToString());
        result.Configuration!.Bus.LocalPath.Should().Be("C:/agents/bus");
    }

    [Fact]
    public void Template_HasNoSecretLookingKey()
    {
        // Act
        using JsonDocument document = JsonDocument.Parse(DeployAssets.TemplateJson());
        var names = new List<string>();
        Walk(document.RootElement, string.Empty, names);

        // Assert: the reader's words, outside the documented `secrets` section
        names.Where(n => n != "secrets" && !n.StartsWith("secrets.", StringComparison.Ordinal))
            .Should().NotContain(n => SecretWords.Any(w => n.Split('.').Last().Equals(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static void Walk(JsonElement element, string prefix, List<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            names.Add(name);
            Walk(property.Value, name, names);
        }
    }
}
