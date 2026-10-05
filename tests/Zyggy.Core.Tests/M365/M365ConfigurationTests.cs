using System.Text.Json.Nodes;

using Zyggy.Core.M365;

namespace Zyggy.Core.Tests.M365;

/// <summary>
/// <c>instance/m365.json</c> validated rule for rule as <c>zy_m365_load_config</c> (spec 33 AC-5): the bats cases "fixture validates",
/// "misconfiguration" and "cert.expires". Messages are the script's, without the caller's <c>&lt;script&gt;: </c> prefix.
/// </summary>
public sealed class M365ConfigurationTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));

    public M365ConfigurationTests() => Directory.CreateDirectory(_directory);

    private string ConfigPath => Path.Combine(_directory, "m365.json");

    public static TheoryData<string, string> Misconfigurations() => new()
    {
        { """{"tenant_id":"not-a-guid"}""", "configuration error: tenant_id is not a GUID" },
        { """{"tenant_id":""}""", "configuration error: tenant_id is not a GUID" },
        { """{"sp_object_id":"3333"}""", "configuration error: sp_object_id is not a GUID" },
        { """{"client_id":""}""", "configuration error: client_id is not a GUID" },
        { """{"mailbox":"alice"}""", "configuration error: mailbox is not a user principal name" },
        { """{"timezone":"Mars/Olympus"}""", "configuration error: timezone 'Mars/Olympus' is not a known time zone" },
        { """{"timezone":null}""", "configuration error: timezone 'null' is not a known time zone" },
        { """{"language":"english"}""", "configuration error: language is not a language tag" },
        { """{"cert":{"subject":"-bad","days":398,"expires":"2027-10-01"}}""", "configuration error: cert.subject is not a plain name" },
        { """{"cert":{"subject":"zyggy-central","days":0,"expires":"2027-10-01"}}""", "configuration error: .cert.days must be an integer 1..3650" },
        { """{"cert":{"subject":"zyggy-central","days":398,"expires":"soon"}}""", "configuration error: cert.expires is not a date (YYYY-MM-DD)" },
        { """{"cert":{"subject":"zyggy-central","days":398,"expires":""}}""", "configuration error: cert.expires is not a date (YYYY-MM-DD)" },
        { """{"cert":{"subject":"zyggy-central","days":398,"expires":"2027-02-30"}}""", "configuration error: cert.expires is not a date (YYYY-MM-DD)" },
        { """{"drives":"x"}""", "configuration error: drives is not an object" },
        { """{"drives":{"onedrive_site":"acme.sharepoint.example/personal/alice"}}""", "configuration error: drives.onedrive_site is not <tenant>-my.sharepoint.<tld>:/personal/<user>" },
        { """{"drives":{"sites":["acme.sharepoint.example/ops"]}}""", "configuration error: drives.sites is not a list of <host>.sharepoint.<tld>:/sites/<name>" },
        { """{"drives":{"sites_granted":["acme.sharepoint.example"]}}""", "configuration error: drives.sites_granted is not a list of <host>,<site collection id>,<site id>" },
        { """{"drives":{"sites_granted":[]}}""", "configuration error: drives.sites_granted is empty while drives.sites names a site" },
        { """{"drives":{"exclude_drives":"x"}}""", "configuration error: drives.exclude_drives is not a list of strings" },
        { """{"drives":{"exclude_paths":[1]}}""", "configuration error: drives.exclude_paths is not a list of strings" },
        { """{"actions":"__delete__"}""", "configuration error: actions missing" },
        { """{"actions":"__delete__","consent":{"ttl_minutes":60,"allowed_actions":["move"]}}""", "configuration error: actions missing (consent is obsolete — D7)" },
        { """{"consent":{"ttl_minutes":60,"allowed_actions":["move"]}}""", "configuration error: consent is obsolete (D7) — remove it" },
        { """{"actions":"x"}""", "configuration error: actions is not an object" },
        { """{"actions":{"enabled":["send","delete"]}}""", "configuration error: actions.enabled may only hold send, upload and move (narrowing only)" },
        { """{"actions":{"enabled":["move","move"]}}""", "configuration error: actions.enabled may only hold send, upload and move (narrowing only)" },
        { """{"actions":{"enabled":"send"}}""", "configuration error: actions.enabled may only hold send, upload and move (narrowing only)" },
        { """{"actions":{"send":{"body_max_chars":0,"max_recipients":10}}}""", "configuration error: .actions.send.body_max_chars must be an integer 1..100000000" },
        { """{"actions":{"send":{"body_max_chars":4000,"max_recipients":"10"}}}""", "configuration error: .actions.send.max_recipients must be an integer 1..100000000" },
        { """{"actions":{"upload":{"max_bytes":1.5,"extensions":["md"]}}}""", "configuration error: .actions.upload.max_bytes must be an integer 1..100000000" },
        { """{"actions":{"upload":{"max_bytes":262144,"extensions":["MD"]}}}""", "configuration error: actions.upload.extensions is not a list of lowercase alphanumeric extensions" },
        { """{"actions":{"upload":{"max_bytes":262144,"extensions":[".md"]}}}""", "configuration error: actions.upload.extensions is not a list of lowercase alphanumeric extensions" },
        { """{"actions":{"files":{"write_drive_id":""}}}""", "configuration error: actions.files.write_drive_id must name the OneDrive drive while upload is enabled" },
        { """{"actions":{"files":"__delete__"}}""", "configuration error: actions.files.write_drive_id must name the OneDrive drive while upload is enabled" },
        { """{"actions":{"files":{"write_drive_id":7}}}""", "configuration error: actions.files.write_drive_id is not a string" },
        { """{"actions":{"files":"x"}}""", "configuration error: actions.files.write_drive_id is not a string" },
        { """{"brief":"x"}""", "configuration error: brief is not an object" },
        { """{"brief":{"budget_usd":"3"}}""", "configuration error: .brief.budget_usd is not a number" },
        { """{"brief":{"budget_usd":-1}}""", "configuration error: .brief.budget_usd must be at least 0" },
        { """{"brief":{"max_turns":1.5}}""", "configuration error: .brief.max_turns must be an integer 0..100000000" },
        { """{"brief":{"file_max_bytes":"__delete__"}}""", "configuration error: .brief.file_max_bytes must be an integer 0..100000000" },
        { """{"brief":{"model":3}}""", "configuration error: brief.model is not a string" },
        { """{"mail_backfill":{"exclude_folders":"junkemail"}}""", "configuration error: mail_backfill.exclude_folders is not a list of strings" },
        { """{"mail_backfill":{"batch_messages":-1}}""", "configuration error: .mail_backfill.batch_messages must be an integer 0..100000000" },
        { """{"mail_backfill":{"budget_usd_total":"x"}}""", "configuration error: .mail_backfill.budget_usd_total is not a number" },
        { """{"mail_backfill":{"model":null}}""", "configuration error: mail_backfill.model is not a string" },
        { """{"files_backfill":[]}""", "configuration error: files_backfill is not an object" },
        { """{"files_backfill":{"file_text_cap_bytes":100000001}}""", "configuration error: .files_backfill.file_text_cap_bytes must be an integer 0..100000000" },
        { """{"files_backfill":{"budget_usd_per_batch":true}}""", "configuration error: .files_backfill.budget_usd_per_batch is not a number" },
        { """{"files_backfill":{"model":["sonnet"]}}""", "configuration error: files_backfill.model is not a string" },
    };

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Load_FixtureM365Json_Valid()
    {
        // Arrange
        File.Copy(M365Run.Golden("fixtures", "m365.json"), ConfigPath);

        // Act
        var load = Load();

        // Assert
        load.Error.Should().BeNull();
        load.Warning.Should().BeNull();
        var config = load.Configuration!;
        config.TenantId.Should().Be("11111111-1111-4111-8111-111111111111");
        config.ClientId.Should().Be("22222222-2222-4222-8222-222222222222");
        config.Mailbox.Should().Be("alice@acme.example");
        config.TimeZone.Should().Be("Europe/Brussels");
        config.ActionsEnabled.Should().Equal("send", "upload", "move");
        config.WriteDriveId.Should().Be("b!onedrive0001");
        config.FileMaxBytes.Should().Be(15728640);
        config.FileTextCapBytes.Should().Be(20000);
    }

    [Fact]
    public void Load_Missing_NotFound()
    {
        // Act
        var load = Load();

        // Assert
        load.Error.Should().Be($"configuration error: {ConfigPath} not found (ZYGGY_M365_CONFIG)");
    }

    [Fact]
    public void Load_Directory_NotAReadableFile()
    {
        // Arrange
        Directory.CreateDirectory(ConfigPath);

        // Act
        var load = Load();

        // Assert
        load.Error.Should().Be($"configuration error: {ConfigPath} is not a readable file");
    }

    [Theory]
    [InlineData("{", "is not valid JSON")]
    [InlineData("[1]", "is not a JSON object")]
    [InlineData("\"x\"", "is not a JSON object")]
    public void Load_NotAnObject_Refused(string content, string message)
    {
        // Arrange
        File.WriteAllText(ConfigPath, content);

        // Act
        var load = Load();

        // Assert
        load.Error.Should().Be($"configuration error: {ConfigPath} {message}");
    }

    [Theory]
    [MemberData(nameof(Misconfigurations))]
    public void Load_Misconfigured_FirstMessageAsShell(string patch, string message)
    {
        // Arrange
        WritePatched(patch);

        // Act
        var load = Load();

        // Assert
        load.Error.Should().Be(message);
        load.Configuration.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"actions":{"enabled":[]}}""")]
    [InlineData("""{"actions":{"enabled":["send","move"],"files":{"write_drive_id":""}}}""")]
    [InlineData("""{"actions":{"files":{"write_drive_id":false},"enabled":["move"]}}""")]
    [InlineData("""{"drives":{"sites":[],"sites_granted":[]}}""")]
    [InlineData("""{"cert":{"subject":"zyggy-central","days":398,"expires":"2027-10-01"},"brief":{"max_turns":1e1}}""")]
    public void Load_NarrowingOrEmptyLists_Valid(string patch)
    {
        // Arrange
        WritePatched(patch);

        // Act
        var load = Load();

        // Assert
        load.Error.Should().BeNull();
    }

    [Fact]
    public void Load_BaseOnly_IgnoresFullKeys()
    {
        // Arrange: cert-init runs before the app registration exists
        WritePatched("""{"client_id":"","sp_object_id":"","cert":{"subject":"zyggy-central","days":398,"expires":""},"drives":{"sites_granted":[]},"actions":{}}""");

        // Act
        var load = M365Configuration.Load(ConfigPath, baseOnly: true, Today, IsKnownZone);

        // Assert
        load.Error.Should().BeNull();
        load.Configuration!.Mailbox.Should().Be("alice@acme.example");
    }

    [Fact]
    public void Load_BaseOnly_StillChecksBaseKeys()
    {
        // Arrange
        WritePatched("""{"mailbox":"alice"}""");

        // Act
        var load = M365Configuration.Load(ConfigPath, baseOnly: true, Today, IsKnownZone);

        // Assert
        load.Error.Should().Be("configuration error: mailbox is not a user principal name");
    }

    [Fact]
    public void Load_Expires20Days_WarningNamesRunbook()
    {
        // Arrange
        WritePatched("""{"cert":{"subject":"zyggy-central","days":398,"expires":"2026-10-20"}}""");

        // Act
        var load = Load();

        // Assert
        load.Error.Should().BeNull();
        load.Warning.Should().Be("m365: certificate expires in 20 days — runbook 13 \"Rotate the certificate\"");
    }

    [Fact]
    public void Load_Expires31Days_NoWarning()
    {
        // Arrange
        WritePatched("""{"cert":{"subject":"zyggy-central","days":398,"expires":"2026-10-31"}}""");

        // Assert
        Load().Warning.Should().BeNull();
    }

    [Fact]
    public void Load_ExpiresToday_ZeroDays()
    {
        // Arrange
        WritePatched("""{"cert":{"subject":"zyggy-central","days":398,"expires":"2026-09-30"}}""");

        // Act
        var load = Load();

        // Assert
        load.Error.Should().BeNull();
        load.Warning.Should().Be("m365: certificate expires in 0 days — runbook 13 \"Rotate the certificate\"");
    }

    [Fact]
    public void Load_ExpiredYesterday_ConfigurationErrorExpired()
    {
        // Arrange
        WritePatched("""{"cert":{"subject":"zyggy-central","days":398,"expires":"2026-09-29"}}""");

        // Act
        var load = Load();

        // Assert
        load.Error.Should().Be("certificate expired 2026-09-29 — runbook 13 \"Rotate the certificate\"");
    }

    private static bool IsKnownZone(string id) => id is "Europe/Brussels" or "UTC";

    private M365ConfigurationLoad Load() => M365Configuration.Load(ConfigPath, baseOnly: false, Today, IsKnownZone);

    // A shallow-to-deep merge of the patch into the fixture; "__delete__" removes a key.
    private void WritePatched(string patch)
    {
        var root = JsonNode.Parse(File.ReadAllText(M365Run.Golden("fixtures", "m365.json")))!.AsObject();
        Merge(root, JsonNode.Parse(patch)!.AsObject());
        File.WriteAllText(ConfigPath, root.ToJsonString());
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var s) && s == "__delete__")
            {
                target.Remove(key);
            }
            else if (value is JsonObject child && target[key] is JsonObject existing)
            {
                Merge(existing, child);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }
}
