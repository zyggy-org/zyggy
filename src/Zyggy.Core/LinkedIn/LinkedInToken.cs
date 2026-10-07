using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// The token file <c>~/.config/zyggy/linkedin/token.json</c> (spec 36 Files): <c>{schema:1, access_token, expires_at, scope, sub, name,
/// obtained_at}</c>, written by <c>auth finish</c> through the secret store. Its text form never shows the access token.
/// </summary>
internal sealed record LinkedInToken(
    int Schema,
    string AccessToken,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset ExpiresAt,
    string Scope,
    string Sub,
    string Name,
    [property: JsonConverter(typeof(UtcSecondsConverter))] DateTimeOffset ObtainedAt)
{
    public const string PostScope = "w_member_social";

    /// <summary>Parses the file's bytes; <see langword="null"/> for anything but a complete schema-1 token.</summary>
    public static LinkedInToken? TryParse(ReadOnlySpan<byte> json)
    {
        try
        {
            var token = JsonSerializer.Deserialize(json, LinkedInJsonContext.Default.LinkedInToken);
            return token is { Schema: 1, AccessToken.Length: > 0, Scope: not null, Sub.Length: > 0, Name: not null } ? token : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file's bytes (compact JSON, no trailing newline).</summary>
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, LinkedInJsonContext.Default.LinkedInToken);

    /// <summary>Whether LinkedIn granted <paramref name="scope"/> (its answer separates scopes by commas; spaces are accepted too).</summary>
    public bool HasScope(string scope) =>
        Scope.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(scope, StringComparer.Ordinal);

    // A record prints every property by default; the access token never appears (spec 36 Assumption 11).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Schema = {Schema}, AccessToken = [withheld], ExpiresAt = {ExpiresAt:O}, Scope = {Scope}, Sub = {Sub}, Name = {Name}, ObtainedAt = {ObtainedAt:O}");
        return true;
    }
}

/// <summary>A timestamp as <c>yyyy-MM-ddTHH:mm:ssZ</c> in UTC.</summary>
internal sealed class UtcSecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value.ToUniversalTime()
            : throw new JsonException("not a timestamp");

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LinkedInToken))]
[JsonSerializable(typeof(OAuthPending))]
internal sealed partial class LinkedInJsonContext : JsonSerializerContext;
