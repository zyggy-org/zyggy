using System.Runtime.Versioning;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Zyggy.Core.Configuration;
using Zyggy.Core.Secrets;
using Zyggy.Core.Secrets.Windows;
using Zyggy.Core.Tenancy;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Secrets;

/// <summary>Spec 09 AC-3: one call selects the store from <c>secrets.store</c> and the OS; a registration added afterwards wins (07's rule); nothing is touched at construction.</summary>
public sealed class SecretStoreRegistrationTests : IDisposable
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly MachineName HomeLaptop = MachineName.Parse("home-laptop");

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "zyggy-ut", Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<object> _log = new();

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsLinux => OperatingSystem.IsLinux();

    private string Root => Path.Combine(_scratch, "secrets");

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    [Fact]
    public void AddSecretStore_Os_ResolvesThePlatformStore()
    {
        // Act
        using ServiceProvider provider = Build(SecretStoreKind.Os);
        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        // Assert
        if (OperatingSystem.IsWindows())
        {
            store.Should().BeOfType<WindowsCredentialManagerSecretStore>();
        }
        else
        {
            store.Should().BeOfType<LinuxSecretStore>();
        }
    }

    [Fact]
    public async Task AddSecretStore_File_ResolvesFileSecretStoreAtRoot()
    {
        // Arrange
        using ServiceProvider provider = Build(SecretStoreKind.File);
        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        // Act
        await store.SetAsync(Acme, SecretName.Parse("hmac/1"), TestKeys.Secret1, TestContext.Current.CancellationToken);

        // Assert
        store.Should().BeOfType<FileSecretStore>();
        File.Exists(Path.Combine(Root, "acme", "hmac", "1")).Should().BeTrue();
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "the warning is for Windows only")]
    public void AddSecretStore_FileOnWindows_WarnsOnceAtFirstResolution()
    {
        // Arrange
        using ServiceProvider provider = Build(SecretStoreKind.File);

        // Act
        provider.GetRequiredService<ISecretStore>();
        provider.GetRequiredService<ISecretStore>();

        // Assert
        _log.AtLevel(LogLevel.Warning).Should().ContainSingle().Which.Message.Should()
            .Be("file secret store selected on Windows: spike and test use only; the product store is Credential Manager");
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "no warning on Linux")]
    public void AddSecretStore_FileOnLinux_NoWarning()
    {
        // Arrange
        using ServiceProvider provider = Build(SecretStoreKind.File);

        // Act
        provider.GetRequiredService<ISecretStore>();

        // Assert
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AddSecretStore_RegistersISecretStoreExactlyOnce()
    {
        // Act
        var services = new ServiceCollection().AddSecretStore(Configuration(SecretStoreKind.Os));

        // Assert
        services.Count(d => d.ServiceType == typeof(ISecretStore)).Should().Be(1);
    }

    [Fact]
    public void AddSecretStore_LaterRegistrationWins()
    {
        // Arrange: 07's rule — the harness's substitute registered afterwards is the one resolved
        var memory = new InMemorySecretStore();
        using ServiceProvider provider = new ServiceCollection()
            .AddSecretStore(Configuration(SecretStoreKind.Os))
            .AddSingleton<ISecretStore>(memory)
            .BuildServiceProvider();

        // Act
        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        // Assert
        store.Should().BeSameAs(memory);
    }

    [Fact]
    public void AddSecretStore_Os_ConstructionDoesNoIo()
    {
        // Act
        using ServiceProvider provider = Build(SecretStoreKind.Os);
        provider.GetRequiredService<ISecretStore>();

        // Assert: no file or directory appears under the root
        Directory.Exists(Root).Should().BeFalse();
    }

    [Fact(SkipUnless = nameof(IsLinux), Skip = "the credentials directory layer: Linux only")]
    [SupportedOSPlatform("linux")]
    public async Task AddSecretStore_LinuxOs_UsesInjectedEnvironment()
    {
        // Arrange: the production store checks the mode, as systemd's 0400 copy satisfies it
        string credentials = Path.Combine(_scratch, "credentials");
        Directory.CreateDirectory(credentials);
        string id = Path.Combine(credentials, "zyggy.acme.hmac.1");
        await File.WriteAllBytesAsync(id, TestKeys.Secret1, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(id, UnixFileMode.UserRead);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CREDENTIALS_DIRECTORY"] = credentials };
        using ServiceProvider provider = new ServiceCollection().AddSecretStore(Configuration(SecretStoreKind.Os), environment).BuildServiceProvider();

        // Act
        ReadOnlyMemory<byte>? value = await provider.GetRequiredService<ISecretStore>().GetAsync(Acme, SecretName.Parse("hmac/1"), TestContext.Current.CancellationToken);

        // Assert
        value!.Value.ToArray().Should().Equal(TestKeys.Secret1);
    }

    [Fact]
    public void AddNodeServices_Os_ResolvesThePlatformStore()
    {
        // Act: through the composition root every host uses
        using ServiceProvider provider = new ServiceCollection().AddNodeServices(Configuration(SecretStoreKind.Os)).BuildServiceProvider();
        ISecretStore store = provider.GetRequiredService<ISecretStore>();

        // Assert
        store.Should().BeOfType(OperatingSystem.IsWindows() ? typeof(WindowsCredentialManagerSecretStore) : typeof(LinuxSecretStore));
    }

    private ServiceProvider Build(SecretStoreKind kind) =>
        new ServiceCollection()
            .AddSingleton<ILoggerFactory>(new SingleLoggerFactory(_log))
            .AddSecretStore(Configuration(kind))
            .BuildServiceProvider();

    private NodeConfiguration Configuration(SecretStoreKind kind) =>
        new(Acme, HomeLaptop, TestKeys.Acme1, new BusSettings("acme-org", "acme-bus", "main", "/bus", "https://example.invalid/acme-org/acme-bus.git"), new PollSettings(), new SecretsSettings(kind, Root));

    /// <summary>Hands every category the same recording logger.</summary>
    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
