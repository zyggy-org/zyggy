using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.M365;
using Zyggy.Core.M365.Graph;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.M365;

/// <summary>A <see cref="GraphReader"/> over the stubbed login host and Graph, a throw-away key pair and the fixture m365.json.</summary>
internal sealed class GraphFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly TestCertificates _pair;
    private readonly GraphHttp _http;

    public GraphFixture(string? configPatch = null)
    {
        _pair = TestCertificates.Create(Path.Combine(_root, "config", "zyggy"));
        var config = Path.Combine(_root, "m365.json");
        var json = File.ReadAllText(M365Run.Golden("fixtures", "m365.json"));
        if (configPatch is not null)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse(configPatch)!.AsObject().ToList())
            {
                if (value is System.Text.Json.Nodes.JsonObject child && node[key] is System.Text.Json.Nodes.JsonObject existing)
                {
                    foreach (var (k, v) in child.ToList())
                    {
                        existing[k] = v?.DeepClone();
                    }
                }
                else
                {
                    node[key] = value?.DeepClone();
                }
            }

            json = node.ToJsonString();
        }

        File.WriteAllText(config, json);
        Configuration = M365Configuration.Load(config, baseOnly: false, new DateOnly(2026, 9, 30), _ => true).Configuration!;
        _http = new GraphHttp(Stub, Clock, SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!, delay: (wait, _) =>
        {
            Waits.Add(wait);
            return Task.CompletedTask;
        });
        var tokens = new GraphTokenClient(
            new CredentialFileSecretStore(TenantId.Parse("acme"), new Dictionary<string, string?>(), _pair.KeyFile, checkOwnership: false),
            TenantId.Parse("acme"), Configuration, _pair.CertificateFile, _http, Clock);
        Reader = new GraphReader(tokens, _http, Configuration, Clock);
    }

    public StubGraphHandler Stub { get; } = StubGraphHandler.FromGoldenRoutes();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));

    public List<TimeSpan> Waits { get; } = [];

    public M365Configuration Configuration { get; }

    public GraphReader Reader { get; }

    public int TokenPosts => Stub.Requests.Count(r => r.Method == HttpMethod.Post);

    public IEnumerable<string> Urls => Stub.Requests.Select(r => $"{r.Method} {Uri.UnescapeDataString(r.Uri.ToString())}");

    public void Dispose()
    {
        _http.Dispose();
        _pair.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
