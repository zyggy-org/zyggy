using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zyggy.Core.M365;

/// <summary>The outcome of <see cref="M365Configuration.Load"/>: the configuration, or the first error; and the expiry warning line.</summary>
/// <param name="Configuration">The configuration when valid.</param>
/// <param name="Error">The first problem, without the caller's <c>&lt;script&gt;: </c> prefix (exit 3).</param>
/// <param name="Warning">The certificate expiry warning, a whole stderr line (<c>m365: certificate expires in …</c>).</param>
internal sealed record M365ConfigurationLoad(M365Configuration? Configuration, string? Error, string? Warning);

/// <summary>
/// <c>instance/m365.json</c>, validated rule for rule as <c>zy_m365_load_config</c> of the template's <c>m365-lib.sh</c> (spec 33 AC-5):
/// the same order, the same first message. With <c>baseOnly</c> only what <c>cert-init</c> needs is checked.
/// </summary>
internal sealed partial class M365Configuration
{
    private const int ExpiryWarnDays = 30;
    private const double IntegerMax = 100000000;

    private M365Configuration(JsonElement root) => Root = root;

    /// <summary>Gets the whole document.</summary>
    public JsonElement Root { get; }

    public string TenantId => String("tenant_id");

    public string Mailbox => String("mailbox");

    /// <summary>Gets <c>brief.mail_max_items</c>: how many new Inbox mails one brief lists.</summary>
    public int MailMaxItems => Int("brief", "mail_max_items");

    /// <summary>Gets <c>brief.reply_cap</c>.</summary>
    public int ReplyCap => Int("brief", "reply_cap");

    /// <summary>Gets <c>brief.suggestion_cap</c>: the most Z items a brief lists.</summary>
    public int SuggestionCap => Int("brief", "suggestion_cap");

    /// <summary>Gets <c>brief.brief_keep_days</c> (default 14).</summary>
    public int BriefKeepDays => IntOr("brief", "brief_keep_days", 14);

    /// <summary>Gets <c>brief.ideas_cap</c> (default 3; 0 disables the ideas run).</summary>
    public int IdeasCap => IntOr("brief", "ideas_cap", 3);

    /// <summary>Gets <c>brief.ideas_repeat_days</c> (default 14).</summary>
    public int IdeasRepeatDays => IntOr("brief", "ideas_repeat_days", 14);

    /// <summary>Gets <c>brief.ideas_suppress_days</c> (default 90).</summary>
    public int IdeasSuppressDays => IntOr("brief", "ideas_suppress_days", 90);

    /// <summary>Gets <c>brief.ideas_max_turns</c> (default 20).</summary>
    public int IdeasMaxTurns => IntOr("brief", "ideas_max_turns", 20);

    /// <summary>Gets <c>brief.ideas_budget_usd</c> (default 1.0).</summary>
    public decimal IdeasBudgetUsd => Find(Root, "brief", "ideas_budget_usd") is { ValueKind: JsonValueKind.Number } n ? n.GetDecimal() : 1.0m;

    /// <summary>Gets <c>brief.ideas_model</c> (default empty: the CLI's default model).</summary>
    public string IdeasModel => String("brief", "ideas_model");

    /// <summary>Gets <c>brief.attachment_parse</c> (default true).</summary>
    public bool AttachmentParse => Find(Root, "brief", "attachment_parse") is { ValueKind: JsonValueKind.False } ? false : true;

    /// <summary>Gets <c>brief.page_max_lines</c> (default 40).</summary>
    public int PageMaxLines => IntOr("brief", "page_max_lines", 40);

    /// <summary>Gets <c>brief.page_max_chars</c> (default 3500).</summary>
    public int PageMaxChars => IntOr("brief", "page_max_chars", 3500);

    /// <summary>Gets <c>brief.expect_by</c> (default 07:00).</summary>
    public TimeOnly ExpectBy => Find(Root, "brief", "expect_by") is { ValueKind: JsonValueKind.String } s && TimeOnly.TryParseExact(s.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(7, 0);

    /// <summary>Gets <c>brief.weekend_days</c> (default Saturday and Sunday).</summary>
    public IReadOnlyList<DayOfWeek> WeekendDays =>
        Find(Root, "brief", "weekend_days") is { ValueKind: JsonValueKind.Array } days
            ? [.. days.EnumerateArray().Select(d => Enum.Parse<DayOfWeek>(d.GetString()!, true))]
            : [DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Gets <c>brief.ideas_areas</c>: area → <c>work</c> | <c>private</c> (the spec's default map).</summary>
    public IReadOnlyDictionary<string, string> IdeasAreas =>
        Find(Root, "brief", "ideas_areas") is { ValueKind: JsonValueKind.Object } areas
            ? areas.EnumerateObject().ToDictionary(a => a.Name, a => a.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["career"] = "work",
                ["business"] = "work",
                ["client"] = "work",
                ["zyggy"] = "work",
                ["family"] = "private",
                ["travel"] = "private",
                ["home"] = "private",
                ["hobbies"] = "private",
            };

    private int IntOr(string section, string key, int fallback) => Find(Root, section, key) is { ValueKind: JsonValueKind.Number } n ? (int)n.GetDouble() : fallback;

    public string TimeZone => String("timezone");

    public string Language => String("language");

    public string CertificateSubject => String("cert", "subject");

    public int CertificateDays => Int("cert", "days");

    public string ClientId => String("client_id");

    public string ServicePrincipalObjectId => String("sp_object_id");

    public string CertificateExpires => String("cert", "expires");

    public IReadOnlyList<string> ActionsEnabled => Strings("actions", "enabled");

    public int SendBodyMaxChars => Int("actions", "send", "body_max_chars");

    public int SendMaxRecipients => Int("actions", "send", "max_recipients");

    public int UploadMaxBytes => Int("actions", "upload", "max_bytes");

    public IReadOnlyList<string> UploadExtensions => Strings("actions", "upload", "extensions");

    public string WriteDriveId => Find(Root, "actions", "files", "write_drive_id") is { ValueKind: JsonValueKind.String } id ? id.GetString()! : string.Empty;

    public IReadOnlyList<string> SitesGranted => Strings("drives", "sites_granted");

    public IReadOnlyList<string> ExcludeDrives => Strings("drives", "exclude_drives");

    public IReadOnlyList<string> ExcludePaths => Strings("drives", "exclude_paths");

    public IReadOnlyList<string> ExcludeFolders => Strings("mail_backfill", "exclude_folders");

    /// <summary>Gets the smaller <c>file_max_bytes</c> of <c>brief</c> and <c>files_backfill</c>.</summary>
    public long FileMaxBytes => Math.Min(Int("brief", "file_max_bytes"), Int("files_backfill", "file_max_bytes"));

    /// <summary>Gets the smaller <c>file_text_cap_bytes</c> of <c>brief</c> and <c>files_backfill</c>.</summary>
    public long FileTextCapBytes => Math.Min(Int("brief", "file_text_cap_bytes"), Int("files_backfill", "file_text_cap_bytes"));

    /// <summary>Loads and validates the configuration file.</summary>
    /// <param name="path">The file (<c>ZYGGY_M365_CONFIG</c> or <c>&lt;instance&gt;/m365.json</c>).</param>
    /// <param name="baseOnly">Validate only the keys <c>cert-init</c> needs.</param>
    /// <param name="todayUtc">Today's date in UTC, for the certificate expiry.</param>
    /// <param name="isKnownTimeZone">Whether a time-zone id other than UTC is known.</param>
    public static M365ConfigurationLoad Load(string path, bool baseOnly, DateOnly todayUtc, Func<string, bool> isKnownTimeZone)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(isKnownTimeZone);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Fail($"{path} not found (ZYGGY_M365_CONFIG)");
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"{path} is not a readable file");
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Fail($"{path} is not valid JSON");
        }

        return root.ValueKind != JsonValueKind.Object
            ? Fail($"{path} is not a JSON object")
            : new Validator(root, isKnownTimeZone).Run(baseOnly, todayUtc);
    }

    private static M365ConfigurationLoad Fail(string message) => new(null, "configuration error: " + message, null);

    // jq: `.a.b.c` — Missing for null on the way (jq yields null), Error for a non-object on the way (jq fails).
    private static JsonElement? Find(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
            {
                return null;
            }
        }

        return current;
    }

    private string String(params string[] path) => Find(Root, path)?.GetString() ?? string.Empty;

    private int Int(params string[] path) => (int)Find(Root, path)!.Value.GetDouble();

    private string[] Strings(params string[] path) =>
        Find(Root, path) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray().Select(e => e.GetString() ?? string.Empty)] : [];

    private sealed partial class Validator(JsonElement root, Func<string, bool> isKnownTimeZone)
    {
        private string? _error;

        public M365ConfigurationLoad Run(bool baseOnly, DateOnly todayUtc)
        {
            string? warning = null;
            var ok =
                Text("tenant_id", M365Grammar.Guid(), "tenant_id is not a GUID")
                && Text("mailbox", M365Grammar.Upn(), "mailbox is not a user principal name")
                && TimeZone()
                && Text("language", LanguageTag(), "language is not a language tag")
                && Text("cert.subject", PlainName(), "cert.subject is not a plain name")
                && Integer(".cert.days", 1, 3650);
            if (ok && !baseOnly)
            {
                ok = Text("client_id", M365Grammar.Guid(), "client_id is not a GUID")
                    && Text("sp_object_id", M365Grammar.Guid(), "sp_object_id is not a GUID")
                    && Expiry(todayUtc, out warning)
                    && Full();
            }

            return _error is not null
                ? new M365ConfigurationLoad(null, _error, null)
                : new M365ConfigurationLoad(new M365Configuration(root), null, warning);
        }

        private bool Full() =>
            Check(Is("drives", JsonValueKind.Object), "drives is not an object")
            && Check(Value("drives.onedrive_site") is { ValueKind: JsonValueKind.String } site && OneDriveSite().IsMatch(site.GetString()!),
                "drives.onedrive_site is not <tenant>-my.sharepoint.<tld>:/personal/<user>")
            && Check(AllStrings("drives.sites", Site()), "drives.sites is not a list of <host>.sharepoint.<tld>:/sites/<name>")
            && Check(AllStrings("drives.sites_granted", SiteGranted()), "drives.sites_granted is not a list of <host>,<site collection id>,<site id>")
            && Check(Length("drives.sites") == 0 || Length("drives.sites_granted") > 0, "drives.sites_granted is empty while drives.sites names a site")
            && Check(AllStrings("drives.exclude_drives"), "drives.exclude_drives is not a list of strings")
            && Check(AllStrings("drives.exclude_paths"), "drives.exclude_paths is not a list of strings")
            && Actions()
            && Section("brief", ["mail_max_items", "reply_cap", "files_max_items", "file_max_bytes", "file_text_cap_bytes", "max_turns", "max_facts", "suggestion_cap"], ["budget_usd"])
            && BriefExtras()
            && Section("mail_backfill", ["batch_messages", "max_turns", "max_facts", "max_messages"], ["budget_usd_per_batch", "budget_usd_total"], "exclude_folders")
            && Section("files_backfill", ["batch_files", "max_turns", "file_max_bytes", "file_text_cap_bytes", "max_facts"], ["budget_usd_per_batch", "budget_usd_total"]);

        // Spec 35: the brief's optional keys (defaults apply when absent), each validated when present; `delivery` is removed (OQ-1).
        private bool BriefExtras()
        {
            if (Value("brief.delivery") is not null)
            {
                return Fail("brief.delivery is removed (spec 35: the brief is shown in the session; rollback restores the Draft brief)");
            }

            foreach (var key in new[] { "ideas_cap", "ideas_repeat_days", "ideas_suppress_days", "ideas_max_turns" })
            {
                if (Value($"brief.{key}") is not null && !Integer($".brief.{key}", 0, IntegerMax))
                {
                    return false;
                }
            }

            foreach (var key in new[] { "brief_keep_days", "page_max_lines", "page_max_chars" })
            {
                if (Value($"brief.{key}") is not null && !Integer($".brief.{key}", 1, IntegerMax))
                {
                    return false;
                }
            }

            if (Value("brief.ideas_budget_usd") is not null && !Number(".brief.ideas_budget_usd", 0))
            {
                return false;
            }

            if (Value("brief.ideas_model") is { } model && !Check(model.ValueKind == JsonValueKind.String, "brief.ideas_model is not a string"))
            {
                return false;
            }

            if (Value("brief.attachment_parse") is { } parse && !Check(parse.ValueKind is JsonValueKind.True or JsonValueKind.False, "brief.attachment_parse is not true or false"))
            {
                return false;
            }

            if (Value("brief.expect_by") is { } expect
                && !Check(expect.ValueKind == JsonValueKind.String && TimeOnly.TryParseExact(expect.GetString(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "brief.expect_by is not a time HH:MM"))
            {
                return false;
            }

            if (Value("brief.weekend_days") is { } weekend
                && !Check(weekend.ValueKind == JsonValueKind.Array && weekend.EnumerateArray().All(d => d.ValueKind == JsonValueKind.String && Enum.TryParse<DayOfWeek>(d.GetString(), true, out _)),
                    "brief.weekend_days is not an array of day names"))
            {
                return false;
            }

            if (Value("brief.ideas_areas") is { } areas
                && !Check(areas.ValueKind == JsonValueKind.Object && areas.EnumerateObject().All(a => a.Value.ValueKind == JsonValueKind.String && a.Value.GetString() is "work" or "private"),
                    "brief.ideas_areas is not an object of area: work|private"))
            {
                return false;
            }

            return true;
        }

        private bool Actions()
        {
            if (!root.TryGetProperty("actions", out _))
            {
                return Fail(root.TryGetProperty("consent", out _) ? "actions missing (consent is obsolete — D7)" : "actions missing");
            }

            if (root.TryGetProperty("consent", out _))
            {
                return Fail("consent is obsolete (D7) — remove it");
            }

            if (!Check(Is("actions", JsonValueKind.Object), "actions is not an object"))
            {
                return false;
            }

            var enabled = Value("actions.enabled");
            var enabledValid = enabled is { ValueKind: JsonValueKind.Array } list
                && list.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String && e.GetString() is "send" or "upload" or "move")
                && list.EnumerateArray().Select(e => e.GetString()).Distinct(StringComparer.Ordinal).Count() == list.GetArrayLength();
            if (!Check(enabledValid, "actions.enabled may only hold send, upload and move (narrowing only)")
                || !Integer(".actions.send.body_max_chars", 1, IntegerMax)
                || !Integer(".actions.send.max_recipients", 1, IntegerMax)
                || !Integer(".actions.upload.max_bytes", 1, IntegerMax)
                || !Check(AllStrings("actions.upload.extensions", Extension()), "actions.upload.extensions is not a list of lowercase alphanumeric extensions"))
            {
                return false;
            }

            // (.actions.files.write_drive_id // "") | type == "string": null or false count as ""; a non-object on the way fails.
            var files = root.GetProperty("actions").TryGetProperty("files", out var f) ? f : (JsonElement?)null;
            string driveId;
            if (files is null || files.Value.ValueKind == JsonValueKind.Null)
            {
                driveId = string.Empty;
            }
            else if (files.Value.ValueKind != JsonValueKind.Object)
            {
                return Fail("actions.files.write_drive_id is not a string");
            }
            else
            {
                var id = files.Value.TryGetProperty("write_drive_id", out var w) ? w : (JsonElement?)null;
                if (id is null || id.Value.ValueKind is JsonValueKind.Null or JsonValueKind.False)
                {
                    driveId = string.Empty;
                }
                else if (id.Value.ValueKind == JsonValueKind.String)
                {
                    driveId = id.Value.GetString()!;
                }
                else
                {
                    return Fail("actions.files.write_drive_id is not a string");
                }
            }

            var uploadEnabled = enabled!.Value.EnumerateArray().Any(e => e.GetString() == "upload");
            return !uploadEnabled
                || Check(M365Grammar.DriveId().IsMatch(driveId), "actions.files.write_drive_id must name the OneDrive drive while upload is enabled");
        }

        private bool Section(string name, string[] integers, string[] numbers, string? folders = null)
        {
            if (!Check(Is(name, JsonValueKind.Object), $"{name} is not an object"))
            {
                return false;
            }

            if (folders is not null && !Check(AllStrings($"{name}.{folders}"), $"{name}.{folders} is not a list of strings"))
            {
                return false;
            }

            return integers.All(key => Integer($".{name}.{key}", 0, IntegerMax))
                && numbers.All(key => Number($".{name}.{key}", 0))
                && Check(Value($"{name}.model") is { ValueKind: JsonValueKind.String }, $"{name}.model is not a string");
        }

        private bool Expiry(DateOnly todayUtc, out string? warning)
        {
            warning = null;
            var expires = Render("cert.expires");
            if (!M365Grammar.Date().IsMatch(expires)
                || !DateOnly.TryParseExact(expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return Fail("cert.expires is not a date (YYYY-MM-DD)");
            }

            var days = date.DayNumber - todayUtc.DayNumber;
            if (days < 0)
            {
                _error = $"certificate expired {expires} — runbook 13 \"Rotate the certificate\"";
                return false;
            }

            if (days <= ExpiryWarnDays)
            {
                warning = $"m365: certificate expires in {days} days — runbook 13 \"Rotate the certificate\"";
            }

            return true;
        }

        private bool TimeZone()
        {
            var zone = Render("timezone");
            return Check(zone == "UTC" || isKnownTimeZone(zone), $"timezone '{zone}' is not a known time zone");
        }

        // `zy_m365_cfg .x` (jq -r '.x // null') matched against a grammar.
        private bool Text(string path, Regex grammar, string message) => Check(grammar.IsMatch(Render(path)), message);

        // zy_m365_require_integer: a JSON number, whole, within the bounds.
        private bool Integer(string jqPath, double min, double max) =>
            Check(
                Value(jqPath[1..]) is { ValueKind: JsonValueKind.Number } n && n.GetDouble() is var d && Math.Floor(d) == d && d >= min && d <= max,
                $"{jqPath} must be an integer {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}");

        // zy_m365_require_number: a JSON number, at least the minimum.
        private bool Number(string jqPath, double min)
        {
            if (Value(jqPath[1..]) is not { ValueKind: JsonValueKind.Number } n)
            {
                return Fail($"{jqPath} is not a number");
            }

            return Check(n.GetDouble() >= min, $"{jqPath} must be at least {min.ToString(CultureInfo.InvariantCulture)}");
        }

        private bool AllStrings(string path, Regex? grammar = null) =>
            Value(path) is { ValueKind: JsonValueKind.Array } array
            && array.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String && (grammar is null || grammar.IsMatch(e.GetString()!)));

        private int Length(string path) => Value(path) is { ValueKind: JsonValueKind.Array } array ? array.GetArrayLength() : 0;

        private bool Is(string path, JsonValueKind kind) => Value(path)?.ValueKind == kind;

        private JsonElement? Value(string dottedPath) => Find(root, dottedPath.Split('.'));

        // jq -r '<path> // null': a string raw; null, false or missing "null"; anything else its JSON text.
        private string Render(string dottedPath) => Value(dottedPath) switch
        {
            null => "null",
            { ValueKind: JsonValueKind.String } s => s.GetString()!,
            { ValueKind: JsonValueKind.Null or JsonValueKind.False } => "null",
            var other => other.Value.GetRawText(),
        };

        private bool Check(bool condition, string message) => condition || Fail(message);

        private bool Fail(string message)
        {
            _error = "configuration error: " + message;
            return false;
        }

        [GeneratedRegex(@"\A[a-z]{2}(-[A-Z]{2})?\z", RegexOptions.CultureInvariant)]
        private static partial Regex LanguageTag();

        [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
        private static partial Regex PlainName();

        [GeneratedRegex(@"\A[a-z0-9-]+-my\.sharepoint\.[a-z.]+:/personal/[A-Za-z0-9_]+\z", RegexOptions.CultureInvariant)]
        private static partial Regex OneDriveSite();

        [GeneratedRegex(@"\A[a-z0-9-]+\.sharepoint\.[a-z.]+:/sites/[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
        private static partial Regex Site();

        [GeneratedRegex(@"\A[a-z0-9.-]+,[0-9a-fA-F-]{36},[0-9a-fA-F-]{36}\z", RegexOptions.CultureInvariant)]
        private static partial Regex SiteGranted();

        [GeneratedRegex(@"\A[a-z0-9]{1,10}\z", RegexOptions.CultureInvariant)]
        private static partial Regex Extension();
    }
}
