using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Zyggy.Core.Memory;
using Zyggy.Core.Verbs;

namespace Zyggy.Core.M365;

/// <summary>
/// <c>zyggy m365 cert-init [--rotate|--commit]</c>: <c>graph.sh cert-init</c> (spec 33 AC-19). Generates the application's RSA 2048 key
/// (0600, in a 0700 directory) and its self-signed certificate (0644, <c>CN=&lt;cert.subject&gt;</c>, <c>cert.days</c>) on this machine;
/// <c>--rotate</c> writes the <c>.new</c> pair, <c>--commit</c> swaps it in. Prints thumbprints, expiry and path — never the key. The
/// only code that writes a secret outside <c>ISecretStore</c> (plan 33 Assumption 4). Attended only; Linux only.
/// Exit 0 · 3 configuration · 4 usage · 5 refused.
/// </summary>
internal sealed class CertInitVerb(M365VerbContext context) : IM365Verb
{
    private const string Prefix = "m365: ";
    private const string Usage = " (usage: zyggy m365 cert-init [--rotate|--commit])";

    public async Task<int> RunAsync(IReadOnlyList<string> args, VerbIo io, CancellationToken cancellationToken)
    {
        string? mode = null;
        foreach (var arg in args)
        {
            if (arg is not ("--rotate" or "--commit"))
            {
                return await FailAsync(io, 4, $"cert-init: unexpected argument '{arg}'" + Usage).ConfigureAwait(false);
            }

            if (mode is not null)
            {
                return await FailAsync(io, 4, "cert-init takes --rotate or --commit, not both" + Usage).ConfigureAwait(false);
            }

            mode = arg[2..];
        }

        if (context.Environment.TryGetValue("ZYGGY_HOOKS", out var hooks) && hooks == "off")
        {
            return await FailAsync(io, 5, "refused: unattended run (ZYGGY_HOOKS=off)").ConfigureAwait(false);
        }

        if (!OperatingSystem.IsLinux())
        {
            return await FailAsync(io, 3, "not supported on this platform").ConfigureAwait(false);
        }

        var memory = MemoryEnvironment.Resolve(context.Environment, context.FindTimeZone);
        if (memory.Error is not null)
        {
            return await FailAsync(io, 3, "configuration error: " + memory.Error).ConfigureAwait(false);
        }

        var instance = M365Environment.Load(context.Environment);
        if (instance.Environment is not { } m365)
        {
            return await FailAsync(io, 3, instance.Error!).ConfigureAwait(false);
        }

        var load = M365Configuration.Load(
            m365.ConfigPath, baseOnly: true, DateOnly.FromDateTime(context.Clock.GetUtcNow().UtcDateTime), id => KnownZone(context, id));
        if (load.Configuration is not { } config)
        {
            return await FailAsync(io, 3, load.Error!).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var key = m365.Paths.KeyFile;
        var cer = m365.Paths.CertificateFile;
        switch (mode)
        {
            case null:
                if (Exists(key))
                {
                    return await FailAsync(io, 5, $"refused: key exists — use --rotate ({key})").ConfigureAwait(false);
                }

                await GenerateAsync(io, key, cer, config).ConfigureAwait(false);
                return 0;
            case "rotate":
                if (Exists(key + ".new") || Exists(cer + ".new"))
                {
                    return await FailAsync(io, 5, $"refused: a rotation is pending — use --commit or remove {key}.new").ConfigureAwait(false);
                }

                await GenerateAsync(io, key + ".new", cer + ".new", config).ConfigureAwait(false);
                return 0;
            default:
                if (!File.Exists(key + ".new") || !File.Exists(cer + ".new"))
                {
                    return await FailAsync(io, 4, $"--commit: no {key}.new to commit" + Usage).ConfigureAwait(false);
                }

                File.Move(key + ".new", key, overwrite: true);
                File.Move(cer + ".new", cer, overwrite: true);
                File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(cer, PublicMode);
                await PrintAsync(io, cer).ConfigureAwait(false);
                return 0;
        }
    }

    private const UnixFileMode PublicMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    // generate_pair: (umask 077 && mkdir -p), chmod 700; openssl req -x509 -newkey rsa:2048 -nodes -days <n> -subj /CN=<subject>.
    [SupportedOSPlatform("linux")]
    private async Task GenerateAsync(VerbIo io, string keyPath, string cerPath, M365Configuration config)
    {
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var directory = Path.GetDirectoryName(keyPath)!;
        CreateOwnerOnly(directory, Owner);
        File.SetUnixFileMode(directory, Owner);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={config.CertificateSubject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = context.Clock.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now, now.AddDays(config.CertificateDays));

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using (var stream = new FileStream(keyPath, options))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(rsa.ExportPkcs8PrivateKeyPem()).ConfigureAwait(false);
            await writer.WriteAsync('\n').ConfigureAwait(false);
        }

        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllTextAsync(cerPath, certificate.ExportCertificatePem() + "\n").ConfigureAwait(false);
        File.SetUnixFileMode(cerPath, PublicMode);
        await PrintAsync(io, cerPath).ConfigureAwait(false);
    }

    // print_cert: public material only.
    private static async Task PrintAsync(VerbIo io, string cerPath)
    {
        using var certificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(cerPath).ConfigureAwait(false));
        await io.Out.WriteAsync($"thumbprint sha1: {certificate.Thumbprint}\n").ConfigureAwait(false);
        await io.Out.WriteAsync($"thumbprint sha256: {Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256))}\n").ConfigureAwait(false);
        await io.Out.WriteAsync($"expires: {certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n").ConfigureAwait(false);
        await io.Out.WriteAsync($"certificate: {cerPath}\n").ConfigureAwait(false);
    }

    [SupportedOSPlatform("linux")]
    private static void CreateOwnerOnly(string directory, UnixFileMode mode)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        if (Path.GetDirectoryName(directory) is { Length: > 0 } parent)
        {
            CreateOwnerOnly(parent, mode);
        }

        Directory.CreateDirectory(directory, mode);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static bool KnownZone(M365VerbContext context, string id)
    {
        try
        {
            _ = context.FindTimeZone(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static async Task<int> FailAsync(VerbIo io, int exit, string message)
    {
        await io.Error.WriteAsync(Prefix + message + "\n").ConfigureAwait(false);
        return exit;
    }
}
