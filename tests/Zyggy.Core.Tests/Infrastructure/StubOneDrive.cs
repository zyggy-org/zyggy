using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// An in-memory OneDrive for the archive offload (plan 38): folders and files by id, parent, name and bytes, answering the offload's reads
/// (<c>/users/&lt;mailbox&gt;/drive</c>, <c>root:/path</c>, <c>items/{id}</c>, <c>items/{parent}:/{name}</c>) and its two writes (folder
/// POST, file PUT, both create-only). A file's <c>quickXorHash</c> comes from <c>quickxor-vectors.tsv</c> (an independent implementation),
/// never from the code under test; bytes without a vector carry no hash.
/// </summary>
internal sealed partial class StubOneDrive
{
    public const string DriveId = "b!onedrive0001";
    public const string RootId = "01ROOTARCH";
    public const string WebBase = "https://acme-my.example.invalid/personal/alice/Documents/";

    private const string DriveRootId = "01DRIVEROOT";
    private static readonly Lazy<Dictionary<string, string>> Vectors = new(LoadVectors);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private int _next;
    private (int Status, string Code)? _refuseNext;
    private bool _corruptNext;

    public StubOneDrive(bool withRoot = true)
    {
        _nodes[DriveRootId] = new Node(DriveRootId, "root", null, true, null);
        if (withRoot)
        {
            _nodes["01ZYGGYROOT"] = new Node("01ZYGGYROOT", "Zyggy", DriveRootId, true, null);
            _nodes[RootId] = new Node(RootId, "archive", "01ZYGGYROOT", true, null);
        }
    }

    /// <summary>Gets the ids the next created items get, in order; generated ids after that.</summary>
    public Queue<string> NextIds { get; } = new();

    public int Puts { get; private set; }

    public int Posts { get; private set; }

    /// <summary>The independent QuickXorHash of <paramref name="bytes"/>, or <see langword="null"/> when no vector has them.</summary>
    public static string? Hash(byte[] bytes) => Vectors.Value.GetValueOrDefault(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>Adds a folder (or, with bytes, a file) under <paramref name="parentId"/>; returns its id.</summary>
    public string Add(string parentId, string name, byte[]? bytes = null, string? id = null)
    {
        id ??= NextIds.Count > 0 ? NextIds.Dequeue() : "01ID" + (++_next).ToString("D4", CultureInfo.InvariantCulture);
        _nodes[id] = new Node(id, name, parentId, bytes is null, bytes);
        return id;
    }

    /// <summary>Makes the root path resolve to a file instead of a folder.</summary>
    public void RootIsFile() => _nodes[RootId] = _nodes[RootId] with { Folder = false, Bytes = [1, 2, 3] };

    public byte[]? File(string parentId, string name) => Child(parentId, name)?.Bytes;

    public string? ChildId(string parentId, string name) => Child(parentId, name)?.Id;

    /// <summary>The next write is answered with <paramref name="status"/> and Graph error <paramref name="code"/>.</summary>
    public void RefuseNextWith(int status, string code = "generalException") => _refuseNext = (status, code);

    /// <summary>The next read of a file by id reports a wrong QuickXorHash.</summary>
    public void CorruptNextHash() => _corruptNext = true;

    public HttpResponseMessage? Handle(HttpMethod method, Uri uri, byte[]? body)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (method == HttpMethod.Get && path == "/v1.0/users/alice@acme.example/drive" && uri.Query == "?$select=id")
        {
            return Json(200, $$"""{"id":"{{DriveId}}"}""");
        }

        if (DrivePath().Match(path) is not { Success: true } match || match.Groups["drive"].Value != DriveId)
        {
            return null;
        }

        var rest = match.Groups["rest"].Value;
        if (method == HttpMethod.Get && rest.StartsWith("root:/", StringComparison.Ordinal))
        {
            var node = _nodes[DriveRootId];
            foreach (var segment in rest["root:/".Length..].Split('/'))
            {
                node = Child(node.Id, segment);
                if (node is null)
                {
                    return NotFound();
                }
            }

            return Item(node);
        }

        if (ChildPath().Match(rest) is { Success: true } child)
        {
            var parent = child.Groups["parent"].Value;
            var name = child.Groups["name"].Value;
            if (method == HttpMethod.Get && !child.Groups["content"].Success)
            {
                return Child(parent, name) is { } found ? Item(found) : NotFound();
            }

            if (method == HttpMethod.Put && child.Groups["content"].Success)
            {
                Puts++;
                return Refused() ?? (Child(parent, name) is not null ? Conflict() : Item(_nodes[Add(parent, name, body ?? [])], 201));
            }
        }

        if (ChildrenPath().Match(rest) is { Success: true } children && method == HttpMethod.Post)
        {
            Posts++;
            using var json = JsonDocument.Parse(body ?? "{}"u8.ToArray());
            var name = json.RootElement.GetProperty("name").GetString()!;
            var parent = children.Groups["parent"].Value;
            return Refused() ?? (Child(parent, name) is not null ? Conflict() : Item(_nodes[Add(parent, name)], 201));
        }

        if (ItemPath().Match(rest) is { Success: true } byId && method == HttpMethod.Get)
        {
            return _nodes.TryGetValue(byId.Groups["id"].Value, out var node) ? Item(node, byId: true) : NotFound();
        }

        return null;
    }

    private static Dictionary<string, string> LoadVectors()
    {
        var vectors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in System.IO.File.ReadAllLines(Path.Combine(Golden.Directory, "archive", "offload", "quickxor-vectors.tsv")).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split('\t');
            var length = int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (length > 2_000_000)
            {
                continue;
            }

            var input = parts[2] == "gen"
                ? [.. Enumerable.Range(0, length).Select(i => (byte)(((long)i * 31 + 7) % 256))]
                : Convert.FromBase64String(parts[2]["b64:".Length..]);
            vectors[Convert.ToHexStringLower(SHA256.HashData(input))] = parts[3];
        }

        return vectors;
    }

    private Node? Child(string parentId, string name) => _nodes.Values.FirstOrDefault(n => n.ParentId == parentId && n.Name == name);

    private string PathOf(Node node) => node.ParentId is null || node.ParentId == DriveRootId ? node.Name : PathOf(_nodes[node.ParentId]) + "/" + node.Name;

    private HttpResponseMessage? Refused()
    {
        if (_refuseNext is not { } refuse)
        {
            return null;
        }

        _refuseNext = null;
        return Json(refuse.Status, $$$"""{"error":{"code":"{{{refuse.Code}}}","message":"refused by the stub"}}""");
    }

    private HttpResponseMessage Item(Node node, int status = 200, bool byId = false)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $$"""{"id":"{{node.Id}}","name":"{{node.Name}}","size":{{node.Bytes?.Length ?? 0}}""");
        if (node.Folder)
        {
            builder.Append(""","folder":{"childCount":0}""");
        }
        else
        {
            var hash = Hash(node.Bytes!);
            if (_corruptNext && byId)
            {
                _corruptNext = false;
                hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAA=";
            }

            builder.Append(hash is null ? ""","file":{"mimeType":"application/octet-stream"}""" : $$$""","file":{"mimeType":"application/octet-stream","hashes":{"quickXorHash":"{{{hash}}}"}}""");
        }

        if (node.ParentId is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $$""","parentReference":{"driveId":"{{DriveId}}","id":"{{node.ParentId}}"}""");
        }

        builder.Append(CultureInfo.InvariantCulture, $$""","webUrl":"{{WebBase}}{{PathOf(node)}}"}""");
        return Json(status, builder.ToString());
    }

    private static HttpResponseMessage NotFound() => Json(404, """{"error":{"code":"itemNotFound","message":"not found"}}""");

    private static HttpResponseMessage Conflict() => Json(409, """{"error":{"code":"nameAlreadyExists","message":"The specified item name already exists."}}""");

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"\A/v1\.0/drives/(?<drive>[^/]+)/(?<rest>.+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"\Aitems/(?<parent>[^/:]+):/(?<name>[^/:]+)(?<content>:/content)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChildPath();

    [GeneratedRegex(@"\Aitems/(?<parent>[^/:]+)/children\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChildrenPath();

    [GeneratedRegex(@"\Aitems/(?<id>[^/:]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPath();

    private sealed record Node(string Id, string Name, string? ParentId, bool Folder, byte[]? Bytes);
}
