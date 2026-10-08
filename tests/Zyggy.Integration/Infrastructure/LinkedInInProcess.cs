using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.LinkedIn;

namespace Zyggy.Integration.Infrastructure;

/// <summary>
/// The <c>publish_post</c> handler in process over the real edge (plan 36 Step 6): <see cref="LinkedInVerbHost"/>'s factory with the real
/// <c>SocketsHttpHandler</c> to the loopback stand-in, a 1 s HTTP timeout, the real credential store with its Linux checks, real files and
/// the real clock. The token file comes from the built binary's <c>auth start</c> + <c>auth finish</c> against the same stand-in.
/// </summary>
internal sealed partial class LinkedInInProcess : IDisposable
{
    private readonly StringBuilder _diagnostics = new();
    private readonly IPublishTool _tool;

    public LinkedInInProcess(LinkedInFixture fixture, StubLinkedInServer stub)
    {
        var host = new LinkedInVerbHost(
            fixture.Env(stub.Port), TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById, linkedInHandler: null, checkOwnership: true, httpTimeout: TimeSpan.FromSeconds(1));
        _tool = host.CreatePublishTool(new StringWriter(_diagnostics, CultureInfo.InvariantCulture) { NewLine = "\n" });
    }

    public string Diagnostics => _diagnostics.ToString();

    /// <summary>Connects through the built binary: <c>auth start</c>, then the landed address into <c>auth finish</c>.</summary>
    public static async Task ConnectAsync(LinkedInFixture fixture, StubLinkedInServer stub)
    {
        fixture.WriteClientSecret();
        var start = await ZyggyCli.RunAsync(["linkedin", "auth", "start"], fixture.Env(stub.Port), null, fixture.Root, TestContext.Current.CancellationToken);
        var state = StateParameter().Match(start.Stdout).Groups[1].Value;
        var finish = await ZyggyCli.RunAsync(
            ["linkedin", "auth", "finish"], fixture.Env(stub.Port), $"{LinkedInFixture.Redirect}?code={LinkedInFixture.Code}&state={state}\n", fixture.Root, TestContext.Current.CancellationToken);
        finish.ExitCode.Should().Be(0, finish.Stderr);
    }

    public Task<ToolCallResult> PublishAsync(string text, string visibility = "PUBLIC") =>
        _tool.CallAsync(
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["text"] = text, ["visibility"] = visibility }),
            TestContext.Current.CancellationToken);

    /// <summary>Publishes with one image by path and SHA-256 (plan 36b D1).</summary>
    public Task<ToolCallResult> PublishWithImageAsync(string text, string imagePath, string imageSha256, string? alt, string visibility = "PUBLIC")
    {
        var arguments = new Dictionary<string, string> { ["text"] = text, ["visibility"] = visibility, ["image_path"] = imagePath, ["image_sha256"] = imageSha256 };
        if (alt is not null)
        {
            arguments["image_alt"] = alt;
        }

        return _tool.CallAsync(JsonSerializer.SerializeToElement(arguments), TestContext.Current.CancellationToken);
    }

    public void Dispose() => _tool.Dispose();

    [GeneratedRegex("[?&]state=([^&\n]*)")]
    private static partial Regex StateParameter();
}
