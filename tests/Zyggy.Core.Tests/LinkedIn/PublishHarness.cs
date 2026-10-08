using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Zyggy.Core.LinkedIn;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.LinkedIn;

/// <summary>
/// The publish handler over fakes: an in-memory secret store holding the token, an API (a substitute, or the real adapter over a
/// stubbed handler), a temporary state directory and memory tree <c>acme/alice</c>, the golden secret patterns, Brussels (+02:00) and a
/// fake clock at 2026-10-07 08:00 UTC.
/// </summary>
internal sealed class PublishHarness : IDisposable
{
    public static readonly TenantId Acme = TenantId.Parse("acme");
    public static readonly SecretPatterns Patterns =
        SecretPatterns.Load(Path.Combine(Golden.Directory, "secret-patterns", "secret-patterns.txt")).Patterns!;

    private readonly StringBuilder _diagnostics = new();

    public PublishHarness(ILinkedInApi api, string? instanceJson = null)
    {
        Api = api;
        StateRoot = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        Paths = new LinkedInPaths(new Dictionary<string, string?> { ["HOME"] = StateRoot, ["ZYGGY_STATE_DIR"] = StateRoot });
        Configuration = Load(instanceJson ?? """{"client_id":"clientid0001","redirect_uri":"https://localhost/zyggy/linkedin"}""");
        Log = new LinkedInActionLog(Paths);
        SetToken(LinkedInFixture.Token(LinkedInFixture.Now.AddDays(60)));
    }

    public ILinkedInApi Api { get; }

    public string StateRoot { get; }

    public LinkedInPaths Paths { get; }

    public LinkedInConfiguration Configuration { get; set; }

    public LinkedInActionLog Log { get; }

    public InMemorySecretStore Secrets { get; } = new();

    public MemoryTree Memory { get; } = new();

    public FakeTimeProvider Clock { get; } = new(LinkedInFixture.Now);

    public string Diagnostics => _diagnostics.ToString();

    public PublishPostTool Tool => new(
        Configuration, Secrets, Acme, Api, Log, Patterns, Memory.Paths, LinkedInFixture.FindTimeZone("Europe/Brussels"), Clock,
        new StringWriter(_diagnostics, CultureInfo.InvariantCulture) { NewLine = "\n" });

    public static JsonElement Arguments(string text, string visibility = "PUBLIC") =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = text, ["visibility"] = visibility });

    public static LinkedInConfiguration Load(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "linkedin.json"), json);
            return LinkedInConfiguration.Load(new Dictionary<string, string?> { ["ZYGGY_INSTANCE_DIR"] = directory }).Configuration!;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public void SetToken(LinkedInToken? token)
    {
        var name = SecretName.Parse("linkedin/token");
        if (token is null)
        {
            Secrets.RemoveAsync(Acme, name, CancellationToken.None).GetAwaiter().GetResult();
            return;
        }

        Secrets.SetAsync(Acme, name, token.ToBytes(), CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<ToolCallResult> CallAsync(string text, string visibility = "PUBLIC") => Tool.CallAsync(Arguments(text, visibility), CancellationToken.None);

    public IReadOnlyList<JsonElement> Rows() =>
        File.Exists(Log.FilePath)
            ? [.. File.ReadAllLines(Log.FilePath).Select(l => JsonDocument.Parse(l).RootElement.Clone())]
            : [];

    public IReadOnlyList<string> MemoryFiles() =>
        [.. Directory.EnumerateFiles(Memory.Root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Memory.PrincipalDirectory, f).Replace('\\', '/'))];

    public void Dispose()
    {
        Memory.Dispose();
        try
        {
            Directory.Delete(StateRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // never created
        }
    }
}
