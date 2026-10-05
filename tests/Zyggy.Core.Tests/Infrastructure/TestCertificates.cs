using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zyggy.Core.Tests.Infrastructure;

/// <summary>
/// A throw-away RSA 2048 key and self-signed certificate per test, written as PEM files (the shapes <c>openssl req -x509</c>
/// produces). No key material is committed.
/// </summary>
internal sealed class TestCertificates : IDisposable
{
    private TestCertificates(RSA key, X509Certificate2 certificate, string keyFile, string certificateFile)
    {
        Key = key;
        Certificate = certificate;
        KeyFile = keyFile;
        CertificateFile = certificateFile;
    }

    public RSA Key { get; }

    public X509Certificate2 Certificate { get; }

    public string KeyFile { get; }

    public string CertificateFile { get; }

    /// <summary>Writes <c>&lt;directory&gt;/m365-app.key</c> (PKCS#8, or PKCS#1 with <paramref name="pkcs1"/>) and <c>m365-app.cer</c>.</summary>
    public static TestCertificates Create(string directory, string baseName = "m365-app", bool pkcs1 = false)
    {
        Directory.CreateDirectory(directory);
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=zyggy-central", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(398));
        var keyFile = Path.Combine(directory, baseName + ".key");
        var certificateFile = Path.Combine(directory, baseName + ".cer");
        File.WriteAllText(keyFile, pkcs1 ? key.ExportRSAPrivateKeyPem() : key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(certificateFile, certificate.ExportCertificatePem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new TestCertificates(key, certificate, keyFile, certificateFile);
    }

    /// <summary>An EC key in PEM form (the refusal case).</summary>
    public static string WriteEcKey(string path)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, key.ExportECPrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }

    public void Dispose()
    {
        Key.Dispose();
        Certificate.Dispose();
    }
}
