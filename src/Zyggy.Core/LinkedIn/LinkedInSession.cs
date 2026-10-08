using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Zyggy.Core.M365;
using Zyggy.Core.Memory;
using Zyggy.Core.Secrets;
using Zyggy.Core.Tenancy;

namespace Zyggy.Core.LinkedIn;

/// <summary>
/// What a LinkedIn verb that touches a credential needs: the instance file, the paths, the principal (<c>zy_require_config</c>) with its
/// time zone, and the credential store scoped to the principal's tenant. Messages are whole stderr lines' text without the prefix.
/// </summary>
internal sealed record LinkedInSession(
    LinkedInConfiguration Configuration,
    LinkedInPaths Paths,
    MemoryPaths Memory,
    TimeZoneInfo TimeZone,
    ISecretStore Store)
{
    public static readonly SecretName TokenName = SecretName.Parse(CredentialFileSecretStore.LinkedInTokenName);
    public static readonly SecretName ClientSecretName = SecretName.Parse(CredentialFileSecretStore.LinkedInClientSecretName);

    public TenantId Tenant => Memory.Principal.Tenant;

    public static (LinkedInSession? Session, string? Error) Load(LinkedInVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var configuration = LinkedInConfiguration.Load(context.Environment);
        if (configuration.Configuration is not { } config)
        {
            return (null, configuration.Error);
        }

        var memory = MemoryEnvironment.Resolve(context.Environment, context.FindTimeZone);
        if (memory.Error is not null)
        {
            return (null, "configuration error: " + memory.Error);
        }

        var paths = new LinkedInPaths(context.Environment);
        var store = new CredentialFileSecretStore(
            memory.Paths!.Principal.Tenant,
            context.Environment,
            new M365Paths(context.Environment).KeyFile,
            context.CheckOwnership,
            linkedInDirectory: paths.ConfigDirectory);
        return (new LinkedInSession(config, paths, memory.Paths, memory.TimeZone!, store), null);
    }

    /// <summary>Reads the token file through the store: the token, <see langword="null"/> when absent; a refusal or an unreadable file throws.</summary>
    /// <exception cref="CredentialRefusedException">Thrown when the file may not be used or is not a token file.</exception>
    public async Task<LinkedInToken?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var raw = await Store.GetAsync(Tenant, TokenName, cancellationToken).ConfigureAwait(false);
        if (raw is not { } bytes)
        {
            return null;
        }

        try
        {
            return LinkedInToken.TryParse(bytes.Span) ?? throw new CredentialRefusedException($"linkedin: {Paths.TokenFile} is not a token file");
        }
        finally
        {
            Clear(bytes);
        }
    }

    /// <summary>Zeroes a secret's buffer once it is no longer needed.</summary>
    public static void Clear(ReadOnlyMemory<byte> secret)
    {
        if (MemoryMarshal.TryGetArray(secret, out var segment) && segment.Array is { } array)
        {
            CryptographicOperations.ZeroMemory(array.AsSpan(segment.Offset, segment.Count));
        }
    }

    /// <summary>The local calendar date of an instant.</summary>
    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);
}
