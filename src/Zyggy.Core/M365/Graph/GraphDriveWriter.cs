using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.Memory;

namespace Zyggy.Core.M365.Graph;

/// <summary>How a OneDrive write ended (spec 38 <c>OffloadReason</c> maps from it).</summary>
internal enum GraphWriteFailure
{
    None,
    Refused,
    Conflict,
    Forbidden,
    TypeBlocked,
    ClientError,
    ServerError,
    Throttled,
    Transport,
    Auth,
}

/// <summary>
/// A OneDrive write's created item, or how it failed. <see cref="Detail"/> is the scope rule or the Graph error code — never the body, a
/// token or a URL query string.
/// </summary>
internal sealed record GraphWrite<T>(T? Value, int Status, GraphWriteFailure Failure, string? Detail)
{
    public bool Sent => Failure != GraphWriteFailure.Refused;

    public static GraphWrite<T> Ok(T value, int status) => new(value, status, GraphWriteFailure.None, null);

    public static GraphWrite<T> Refused(string rule) => new(default, 0, GraphWriteFailure.Refused, rule);

    public static GraphWrite<T> Fail(GraphWriteFailure failure, int status, string? detail) => new(default, status, failure, detail);
}

/// <summary>
/// The archive offload's OneDrive writes (spec 38): an internal adapter beside <see cref="GraphReader"/>, which stays GET-only. Exactly
/// two operations — create a project folder, create a new file — and nothing else can be expressed.
/// </summary>
internal interface IGraphDriveWriter
{
    /// <summary>Creates folder <paramref name="name"/> under <paramref name="parentId"/>, failing if the name exists.</summary>
    Task<GraphWrite<DriveItemInfo>> CreateFolderAsync(string driveId, string parentId, Slug name, CancellationToken cancellationToken);

    /// <summary>Creates file <paramref name="name"/> with <paramref name="bytes"/> in <paramref name="parentId"/>, failing if the name exists.</summary>
    Task<GraphWrite<DriveItemInfo>> CreateFileAsync(string driveId, string parentId, string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

/// <summary>
/// The one <see cref="IGraphDriveWriter"/>: every request checked by <see cref="DriveWriteScope"/> before it is built (a refusal sends
/// nothing, not even a token request); <c>conflictBehavior=fail</c> on both writes; its own token, one re-mint on a 401.
/// </summary>
internal sealed partial class GraphDriveWriter(ITokenSource tokens, GraphHttp http, DriveWriteScope scope) : IGraphDriveWriter
{
    private const string Fail = "fail";

    private string? _token;

    public async Task<GraphWrite<DriveItemInfo>> CreateFolderAsync(string driveId, string parentId, Slug name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var request = new DriveWriteRequest(DriveWriteKind.CreateFolder, driveId, parentId, name.Value, Fail, 0);
        if (scope.Check(request) is { } rule)
        {
            return GraphWrite<DriveItemInfo>.Refused(rule);
        }

        var json = FolderBody(name);
        var created = await SendAsync(
            HttpMethod.Post,
            $"{GraphEndpoints.Graph}/drives/{driveId}/items/{parentId}/children",
            () => new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);
        if (created.Value is { } folder)
        {
            scope.KnowProjectFolder(folder.Id);
        }

        return created;
    }

    public Task<GraphWrite<DriveItemInfo>> CreateFileAsync(string driveId, string parentId, string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        var request = new DriveWriteRequest(DriveWriteKind.CreateFile, driveId, parentId, name, Fail, bytes.Length);
        if (scope.Check(request) is { } rule)
        {
            return Task.FromResult(GraphWrite<DriveItemInfo>.Refused(rule));
        }

        return SendAsync(
            HttpMethod.Put,
            $"{GraphEndpoints.Graph}/drives/{driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}:/content?@microsoft.graph.conflictBehavior={Fail}",
            () =>
            {
                var content = new ReadOnlyMemoryContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return content;
            },
            cancellationToken);
    }

    private async Task<GraphWrite<DriveItemInfo>> SendAsync(HttpMethod method, string url, Func<HttpContent> content, CancellationToken cancellationToken)
    {
        if (_token is null && await MintAsync(cancellationToken).ConfigureAwait(false) is { } mintFailure)
        {
            return GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Auth, 0, mintFailure);
        }

        var reminted = false;
        while (true)
        {
            var token = _token!;
            var result = await http.SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(method, url) { Content = content() };
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    return request;
                },
                _ => true,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                return GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Transport, 0, QueryString().Replace(result.Failure, string.Empty));
            }

            var response = result.Response!;
            if (response.Status == 401)
            {
                if (reminted)
                {
                    return GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Auth, 401, "unauthorized after a fresh token");
                }

                if (await MintAsync(cancellationToken).ConfigureAwait(false) is { } failure)
                {
                    return GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Auth, 0, failure);
                }

                reminted = true;
                continue;
            }

            return Classify(response);
        }
    }

    // Assumption A2: a folder carries its conflict behaviour in the JSON body (Graph's documented form for creating a folder).
    private static string FolderBody(Slug name)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name.Value);
            writer.WriteStartObject("folder");
            writer.WriteEndObject();
            writer.WriteString("@microsoft.graph.conflictBehavior", Fail);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static GraphWrite<DriveItemInfo> Classify(GraphResponse response)
    {
        var status = response.Status;
        if (status is 200 or 201)
        {
            using var document = Parse(response.Body);
            return document is not null && DriveItemInfo.Parse(document.RootElement) is { } item
                ? GraphWrite<DriveItemInfo>.Ok(item, status)
                : GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.ServerError, status, "unexpected item");
        }

        var (code, inner, message) = Error(response.Body);
        var detail = code ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return status switch
        {
            409 => GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Conflict, status, detail),
            403 => GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Forbidden, status, detail),
            429 or 503 => GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.Throttled, status, detail),
            >= 500 => GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.ServerError, status, detail),
            >= 400 when IsBlockedType(code, inner, message) => GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.TypeBlocked, status, inner ?? detail),
            _ => GraphWrite<DriveItemInfo>.Fail(GraphWriteFailure.ClientError, status, detail),
        };
    }

    // Assumption A9: Graph's code for a type the tenant blocks is unverified; these codes, or a message naming the file type or extension.
    private static bool IsBlockedType(string? code, string? inner, string? message) =>
        code is "typeNotAllowed" or "fileTypeBlocked" || inner is "typeNotAllowed" or "fileTypeBlocked"
        || (message is not null && (message.Contains("file type", StringComparison.OrdinalIgnoreCase) || message.Contains("extension", StringComparison.OrdinalIgnoreCase)));

    private static (string? Code, string? Inner, string? Message) Error(string body)
    {
        using var document = Parse(body);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        var inner = error.TryGetProperty("innerError", out var i) && i.ValueKind == JsonValueKind.Object ? Text(i, "code") : null;
        return (Text(error, "code"), inner, Text(error, "message"));
    }

    private static JsonDocument? Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private async Task<string?> MintAsync(CancellationToken cancellationToken)
    {
        var minted = await tokens.MintAsync(new TokenRequest(UseNewKey: false, AssertionAlgorithm.Ps256), cancellationToken).ConfigureAwait(false);
        if (minted.AccessToken is null)
        {
            return minted.Error;
        }

        _token = minted.AccessToken;
        return null;
    }

    [GeneratedRegex(@"\?\S*", RegexOptions.CultureInvariant)]
    private static partial Regex QueryString();
}
