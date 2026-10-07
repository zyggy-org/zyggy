using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Zyggy.Core.Secrets;

namespace Zyggy.Core.M365.Graph;

/// <summary>
/// The Graph reads of the m365 verbs (spec 33 AC-16, W33-5): an internal adapter, not a seam — <see cref="GraphReader"/> is its one
/// implementation and the only code that reads Graph.
/// </summary>
internal interface IGraphReader
{
    /// <summary>Gets the time of the last token mint (<c>yyyy-MM-ddTHH:mm:ssZ</c>), once a read has minted.</summary>
    string? TokenMinted { get; }

    /// <summary>Gets where the key came from, once a read has minted.</summary>
    CredentialSource KeySource { get; }

    /// <summary>Mints the first token (<c>mint_token</c> before any read); <see cref="KeySource"/> is set even when minting fails.</summary>
    Task<GraphFailure?> SignInAsync(CancellationToken cancellationToken);

    Task<GraphRead<IReadOnlyList<MailFolder>>> MailFoldersAsync(CancellationToken cancellationToken);

    Task<GraphRead<IReadOnlyList<GraphDrive>>> DrivesAsync(CancellationToken cancellationToken);

    Task<GraphRead<IReadOnlyList<DriveFile>>> DriveFilesAsync(string driveId, CancellationToken cancellationToken);

    Task<GraphRead<IReadOnlyList<JsonElement>>> DraftsSinceAsync(string sinceIso, CancellationToken cancellationToken);

    Task<GraphRead<MessageSender>> MessageSenderAsync(string messageId, CancellationToken cancellationToken);

    Task<GraphRead<ItemPresence>> ItemExistsAsync(string driveId, string parentId, string name, CancellationToken cancellationToken);

    Task<GraphRead<ItemKind>> ItemKindAsync(string driveId, string itemId, CancellationToken cancellationToken);

    Task<GraphRead<GraphResponse>> GetAsync(string pathAndQuery, Func<int, bool> tolerate, CancellationToken cancellationToken);

    /// <summary>The Inbox messages received after <paramref name="sinceIso"/>, oldest first, at most <paramref name="top"/> (spec 35 Step 4).</summary>
    Task<GraphRead<IReadOnlyList<InboxMessage>>> InboxSinceAsync(string inboxId, string sinceIso, int top, CancellationToken cancellationToken);

    /// <summary>Every Sent Items message sent at or after <paramref name="sinceIso"/>, as conversation markers; pages followed under the mailbox only.</summary>
    Task<GraphRead<IReadOnlyList<SentMarker>>> SentSinceAsync(string sinceIso, CancellationToken cancellationToken);

    /// <summary>Where a message is now (its folder), or <see cref="MessageLocation.Absent"/> for a 404.</summary>
    Task<GraphRead<MessageLocation>> MessageLocationAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>The mailbox user's display name — the name a file the owner changed carries as "modified by" (spec 35 AC-66).</summary>
    Task<GraphRead<string>> OwnerDisplayNameAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <c>graph.sh</c>'s reads: <c>/users/&lt;mailbox&gt;</c>, <c>/drives</c> and <c>/sites</c> only, GET only, never <c>/me</c>; a token minted
/// before the first read, one re-mint on a 401; 403 and every other failure is the shell's exit-6 message unless tolerated.
/// </summary>
internal sealed partial class GraphReader(GraphTokenClient tokens, GraphHttp http, M365Configuration configuration, TimeProvider clock) : IGraphReader
{
    private const int Page = 100;
    private const int Top = 50;
    private const int MaxDeltaPages = 2000;
    private const int MaxFolderDepth = 64;
    private const int MaxSentPages = 50;
    private const string DraftsSelect = "id,subject,toRecipients,ccRecipients,bccRecipients,conversationId,createdDateTime,changeKey,body";
    private const string DeltaSelect = "id,name,file,folder,root,size,lastModifiedDateTime,parentReference,deleted";

    private string? _token;

    public string? TokenMinted { get; private set; }

    public CredentialSource KeySource { get; private set; }

    private string Mailbox => $"{GraphEndpoints.Graph}/users/{configuration.Mailbox}";

    public Task<GraphFailure?> SignInAsync(CancellationToken cancellationToken) => MintAsync(cancellationToken);

    public async Task<GraphRead<IReadOnlyList<MailFolder>>> MailFoldersAsync(CancellationToken cancellationToken)
    {
        var top = await CallAsync($"{Mailbox}/mailFolders?$top={Page}", None, cancellationToken).ConfigureAwait(false);
        if (top.Failure is not null)
        {
            return GraphRead<IReadOnlyList<MailFolder>>.Fail(top.Failure);
        }

        var items = Values(top.Value!.Body);
        var all = new List<JsonElement>(items);
        foreach (var folder in items.Where(f => Long(f, "childFolderCount") > 0))
        {
            var id = Text(folder, "id") ?? string.Empty;
            if (!M365Grammar.Id().IsMatch(id))
            {
                return GraphRead<IReadOnlyList<MailFolder>>.Fail(new GraphFailure(6, "Graph returned an unexpected folder id"));
            }

            var children = await CallAsync($"{Mailbox}/mailFolders/{id}/childFolders?$top={Page}", None, cancellationToken).ConfigureAwait(false);
            if (children.Failure is not null)
            {
                return GraphRead<IReadOnlyList<MailFolder>>.Fail(children.Failure);
            }

            all.AddRange(Values(children.Value!.Body));
        }

        var excluded = configuration.ExcludeFolders;
        return GraphRead<IReadOnlyList<MailFolder>>.Ok(
        [
            .. all.Select(f =>
            {
                var wellKnown = Text(f, "wellKnownName");
                return new MailFolder(
                    Text(f, "id") ?? string.Empty, Text(f, "displayName"), wellKnown, Long(f, "totalItemCount"), excluded.Contains(wellKnown ?? string.Empty));
            }),
        ]);
    }

    public async Task<GraphRead<IReadOnlyList<GraphDrive>>> DrivesAsync(CancellationToken cancellationToken)
    {
        var drives = new List<GraphDrive>();
        var oneDrive = await CallAsync($"{Mailbox}/drive?$select=id,name,webUrl", None, cancellationToken).ConfigureAwait(false);
        if (oneDrive.Failure is not null)
        {
            return GraphRead<IReadOnlyList<GraphDrive>>.Fail(oneDrive.Failure);
        }

        using (var document = JsonDocument.Parse(oneDrive.Value!.Body))
        {
            drives.Add(new GraphDrive(Text(document.RootElement, "id") ?? string.Empty, Text(document.RootElement, "name"), "onedrive"));
        }

        foreach (var site in configuration.SitesGranted)
        {
            var listing = await CallAsync($"{GraphEndpoints.Graph}/sites/{site}/drives?$select=id,name,webUrl", None, cancellationToken).ConfigureAwait(false);
            if (listing.Failure is not null)
            {
                return GraphRead<IReadOnlyList<GraphDrive>>.Fail(listing.Failure);
            }

            drives.AddRange(Values(listing.Value!.Body).Select(d => new GraphDrive(Text(d, "id") ?? string.Empty, Text(d, "name"), site)));
        }

        var exclude = configuration.ExcludeDrives;
        return GraphRead<IReadOnlyList<GraphDrive>>.Ok(
        [
            .. drives.DistinctBy(d => d.Id, StringComparer.Ordinal)
                .Where(d => !exclude.Contains(d.Id) && !exclude.Contains(d.Name ?? "\0")),
        ]);
    }

    public async Task<GraphRead<IReadOnlyList<DriveFile>>> DriveFilesAsync(string driveId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driveId);
        var url = $"{GraphEndpoints.Graph}/drives/{driveId}/root/delta?$select={DeltaSelect}";
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var pages = 0;
        while (url is not null)
        {
            if (++pages > MaxDeltaPages)
            {
                return GraphRead<IReadOnlyList<DriveFile>>.Fail(new GraphFailure(6, $"drive {driveId}: more than {MaxDeltaPages} delta pages"));
            }

            var page = await CallAsync(url, status => status is 403 or 404, cancellationToken).ConfigureAwait(false);
            if (page.Failure is not null)
            {
                return GraphRead<IReadOnlyList<DriveFile>>.Fail(page.Failure);
            }

            switch (page.Value!.Status)
            {
                case 403:
                    return GraphRead<IReadOnlyList<DriveFile>>.Fail(new GraphFailure(5, $"drive {driveId}: 403 (not granted)"));
                case 404:
                    return GraphRead<IReadOnlyList<DriveFile>>.Fail(new GraphFailure(5, $"drive {driveId}: 404 (not found)"));
            }

            using var document = JsonDocument.Parse(page.Value.Body);
            foreach (var item in Values(document.RootElement))
            {
                if (Text(item, "id") is { } id)
                {
                    byId[id] = item;
                }
            }

            url = Text(document.RootElement, "@odata.nextLink");
            if (url is { Length: 0 })
            {
                url = null;
            }

            if (url is not null && !url.StartsWith(GraphEndpoints.Graph + "/", StringComparison.Ordinal))
            {
                return GraphRead<IReadOnlyList<DriveFile>>.Fail(new GraphFailure(6, $"Graph returned a nextLink outside {GraphEndpoints.Graph}"));
            }
        }

        var folders = byId.Values
            .Where(i => IsPresent(i, "folder") || IsPresent(i, "root"))
            .ToDictionary(i => Text(i, "id")!, i => (Name: Text(i, "name"), Parent: ParentId(i), Root: IsPresent(i, "root")), StringComparer.Ordinal);

        string FolderPath(string? id, int depth) =>
            id is null || depth > MaxFolderDepth || !folders.TryGetValue(id, out var folder) || folder.Root
                ? string.Empty
                : FolderPath(folder.Parent, depth + 1) + "/" + folder.Name;

        return GraphRead<IReadOnlyList<DriveFile>>.Ok(
        [
            .. byId.Values
                .Where(i => IsPresent(i, "file") && !IsPresent(i, "deleted"))
                .Select(i =>
                {
                    var modified = Text(i, "lastModifiedDateTime") ?? "1970-01-01T00:00:00Z";
                    return new DriveFile(
                        Text(i, "id")!, FolderPath(ParentId(i), 0) + "/" + Text(i, "name"), Long(i, "size"), modified[..Math.Min(19, modified.Length)] + "Z");
                })
                .OrderBy(f => f.Modified, StringComparer.Ordinal)
                .ThenBy(f => f.Id, StringComparer.Ordinal),
        ]);
    }

    public async Task<GraphRead<IReadOnlyList<JsonElement>>> DraftsSinceAsync(string sinceIso, CancellationToken cancellationToken)
    {
        var drafts = await CallAsync(
            $"{Mailbox}/mailFolders/drafts/messages?$filter=createdDateTime%20ge%20{sinceIso}&$select={DraftsSelect}&$top={Top}",
            None,
            cancellationToken,
            preferText: true).ConfigureAwait(false);
        return drafts.Failure is not null
            ? GraphRead<IReadOnlyList<JsonElement>>.Fail(drafts.Failure)
            : GraphRead<IReadOnlyList<JsonElement>>.Ok(Values(drafts.Value!.Body));
    }

    public async Task<GraphRead<MessageSender>> MessageSenderAsync(string messageId, CancellationToken cancellationToken)
    {
        var message = await CallAsync($"{Mailbox}/messages/{messageId}?$select=from,replyTo,conversationId", status => status == 404, cancellationToken)
            .ConfigureAwait(false);
        if (message.Failure is not null)
        {
            return GraphRead<MessageSender>.Fail(message.Failure);
        }

        if (message.Value!.Status == 404)
        {
            return GraphRead<MessageSender>.Fail(new GraphFailure(6, $"not found ({messageId})"));
        }

        using var document = JsonDocument.Parse(message.Value.Body);
        var root = document.RootElement;
        var from = Address(root.TryGetProperty("from", out var f) ? f : default) ?? string.Empty;
        var replyTo = root.TryGetProperty("replyTo", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(Address).OfType<string>().Select(AsciiLower).ToList()
            : [];
        return GraphRead<MessageSender>.Ok(new MessageSender(AsciiLower(from), replyTo, Text(root, "conversationId") ?? string.Empty));
    }

    public async Task<GraphRead<ItemPresence>> ItemExistsAsync(string driveId, string parentId, string name, CancellationToken cancellationToken)
    {
        var item = await CallAsync(
            $"{GraphEndpoints.Graph}/drives/{driveId}/items/{parentId}:/{Uri.EscapeDataString(name)}?$select=id", status => status == 404, cancellationToken)
            .ConfigureAwait(false);
        return item.Failure is not null
            ? GraphRead<ItemPresence>.Fail(item.Failure)
            : GraphRead<ItemPresence>.Ok(item.Value!.Status == 404 ? ItemPresence.Absent : ItemPresence.Exists);
    }

    public async Task<GraphRead<ItemKind>> ItemKindAsync(string driveId, string itemId, CancellationToken cancellationToken)
    {
        var item = await CallAsync($"{GraphEndpoints.Graph}/drives/{driveId}/items/{itemId}?$select=id,folder,root", status => status == 404, cancellationToken)
            .ConfigureAwait(false);
        if (item.Failure is not null)
        {
            return GraphRead<ItemKind>.Fail(item.Failure);
        }

        if (item.Value!.Status == 404)
        {
            return GraphRead<ItemKind>.Ok(ItemKind.Absent);
        }

        using var document = JsonDocument.Parse(item.Value.Body);
        return GraphRead<ItemKind>.Ok(IsPresent(document.RootElement, "folder") || IsPresent(document.RootElement, "root") ? ItemKind.Folder : ItemKind.File);
    }

    public Task<GraphRead<GraphResponse>> GetAsync(string pathAndQuery, Func<int, bool> tolerate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        return CallAsync(GraphEndpoints.Graph + pathAndQuery, tolerate, cancellationToken);
    }

    public async Task<GraphRead<IReadOnlyList<InboxMessage>>> InboxSinceAsync(string inboxId, string sinceIso, int top, CancellationToken cancellationToken)
    {
        var url = $"{Mailbox}/mailFolders/{inboxId}/messages?$filter=receivedDateTime%20gt%20{sinceIso}&$orderby=receivedDateTime%20asc&$top={top}"
            + "&$select=id,subject,from,receivedDateTime,conversationId,hasAttachments";
        var page = await CallAsync(url, None, cancellationToken).ConfigureAwait(false);
        if (page.Failure is not null)
        {
            return GraphRead<IReadOnlyList<InboxMessage>>.Fail(page.Failure);
        }

        var messages = new List<InboxMessage>();
        foreach (var m in Values(page.Value!.Body))
        {
            var from = m.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("emailAddress", out var e) ? e : default;
            messages.Add(new InboxMessage(
                Str(m, "id"),
                Str(m, "subject"),
                from.ValueKind == JsonValueKind.Object ? Str(from, "name") : string.Empty,
                from.ValueKind == JsonValueKind.Object ? Str(from, "address") : string.Empty,
                Str(m, "receivedDateTime"),
                Str(m, "conversationId"),
                m.TryGetProperty("hasAttachments", out var h) && h.ValueKind == JsonValueKind.True));
        }

        return GraphRead<IReadOnlyList<InboxMessage>>.Ok(messages);
    }

    public async Task<GraphRead<IReadOnlyList<SentMarker>>> SentSinceAsync(string sinceIso, CancellationToken cancellationToken)
    {
        var url = $"{Mailbox}/mailFolders/sentitems/messages?$filter=sentDateTime%20ge%20{sinceIso}&$select=conversationId,sentDateTime&$top={Page}";
        var markers = new List<SentMarker>();
        for (var pages = 0; pages < MaxSentPages; pages++)
        {
            var page = await CallAsync(url, None, cancellationToken).ConfigureAwait(false);
            if (page.Failure is not null)
            {
                return GraphRead<IReadOnlyList<SentMarker>>.Fail(page.Failure);
            }

            using var document = JsonDocument.Parse(page.Value!.Body);
            if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in value.EnumerateArray())
                {
                    markers.Add(new SentMarker(Str(m, "conversationId"), Str(m, "sentDateTime")));
                }
            }

            if (!document.RootElement.TryGetProperty("@odata.nextLink", out var next) || next.ValueKind != JsonValueKind.String)
            {
                return GraphRead<IReadOnlyList<SentMarker>>.Ok(markers);
            }

            url = next.GetString()!;
            if (!url.StartsWith(Mailbox + "/", StringComparison.Ordinal))
            {
                return GraphRead<IReadOnlyList<SentMarker>>.Fail(new GraphFailure(6, "Graph returned a next page outside the mailbox"));
            }
        }

        return GraphRead<IReadOnlyList<SentMarker>>.Fail(new GraphFailure(6, $"Sent Items listing did not end within {MaxSentPages} pages"));
    }

    public async Task<GraphRead<MessageLocation>> MessageLocationAsync(string messageId, CancellationToken cancellationToken)
    {
        var message = await CallAsync(
            $"{Mailbox}/messages/{messageId}?$select=id,parentFolderId,conversationId,subject,from,receivedDateTime",
            status => status == 404,
            cancellationToken).ConfigureAwait(false);
        if (message.Failure is not null)
        {
            return GraphRead<MessageLocation>.Fail(message.Failure);
        }

        if (message.Value!.Status == 404)
        {
            return GraphRead<MessageLocation>.Ok(MessageLocation.Absent);
        }

        using var document = JsonDocument.Parse(message.Value.Body);
        var m = document.RootElement;
        var from = m.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("emailAddress", out var e) ? e : default;
        return GraphRead<MessageLocation>.Ok(new MessageLocation(
            true,
            Str(m, "id"),
            Str(m, "parentFolderId"),
            Str(m, "conversationId"),
            Str(m, "subject"),
            from.ValueKind == JsonValueKind.Object ? Str(from, "name") : string.Empty,
            Str(m, "receivedDateTime")));
    }

    public async Task<GraphRead<string>> OwnerDisplayNameAsync(CancellationToken cancellationToken)
    {
        var user = await CallAsync($"{Mailbox}?$select=displayName", None, cancellationToken).ConfigureAwait(false);
        if (user.Failure is not null)
        {
            return GraphRead<string>.Fail(user.Failure);
        }

        using var document = JsonDocument.Parse(user.Value!.Body);
        return GraphRead<string>.Ok(Str(document.RootElement, "displayName"));
    }

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

    // graph_call: the Bearer header, one re-mint on a 401 not tolerated; anything not accepted is graph_error.
    private async Task<GraphRead<GraphResponse>> CallAsync(string url, Func<int, bool> tolerate, CancellationToken cancellationToken, bool preferText = false)
    {
        if (_token is null && await MintAsync(cancellationToken).ConfigureAwait(false) is { } mintFailure)
        {
            return GraphRead<GraphResponse>.Fail(mintFailure);
        }

        var reminted = false;
        while (true)
        {
            var token = _token!;
            var result = await http.SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (preferText)
                    {
                        request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
                    }

                    return request;
                },
                status => status == 401 || tolerate(status),
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                return GraphRead<GraphResponse>.Fail(new GraphFailure(6, result.Failure));
            }

            var response = result.Response!;
            if (!response.Accepted)
            {
                return GraphRead<GraphResponse>.Fail(new GraphFailure(6, GraphHttp.Error(response.Status, response.Body)));
            }

            if (response.Status == 401 && !tolerate(401))
            {
                if (reminted)
                {
                    return GraphRead<GraphResponse>.Fail(new GraphFailure(6, "unauthorized (401) after a fresh token — runbook 13 \"Certificate rejected\""));
                }

                if (await MintAsync(cancellationToken).ConfigureAwait(false) is { } failure)
                {
                    return GraphRead<GraphResponse>.Fail(failure);
                }

                reminted = true;
                continue;
            }

            return GraphRead<GraphResponse>.Ok(response);
        }
    }

    private async Task<GraphFailure?> MintAsync(CancellationToken cancellationToken)
    {
        var minted = await tokens.MintAsync(new TokenRequest(UseNewKey: false, AssertionAlgorithm.Ps256), cancellationToken).ConfigureAwait(false);
        KeySource = minted.Source;
        if (minted.AccessToken is null)
        {
            return new GraphFailure(minted.ExitCode, minted.Error!);
        }

        _token = minted.AccessToken;
        TokenMinted = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return null;
    }

    private static bool None(int status) => false;

    private static List<JsonElement> Values(string body)
    {
        using var document = JsonDocument.Parse(body);
        return Values(document.RootElement);
    }

    // jq '.value[]?'
    private static List<JsonElement> Values(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => e.Clone())]
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool IsPresent(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static string? ParentId(JsonElement item) =>
        item.TryGetProperty("parentReference", out var parent) ? Text(parent, "id") : null;

    private static string? Address(JsonElement recipient) =>
        recipient.ValueKind == JsonValueKind.Object && recipient.TryGetProperty("emailAddress", out var email) ? Text(email, "address") : null;

    // jq ascii_downcase: A–Z only.
    private static string AsciiLower(string text) => AsciiUpper().Replace(text, m => m.Value.ToLowerInvariant());

    [GeneratedRegex("[A-Z]+", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiUpper();
}
