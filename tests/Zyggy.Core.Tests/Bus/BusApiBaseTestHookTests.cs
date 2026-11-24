using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Zyggy.Core.Bus;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 61 slice 7 correction: the bus API test hook accepts a loopback URL only.</summary>
public sealed class BusApiBaseTestHookTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5123/", true)]
    [InlineData("http://localhost:5123/", true)]
    [InlineData("http://[::1]:5123/", true)]
    [InlineData("https://api.example.invalid/", false)]
    [InlineData("http://10.0.0.5:5123/", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void AddBusApiBaseTestHook_Value_AppliedOnlyWhenLoopback(string value, bool applied)
    {
        // Arrange
        var services = new ServiceCollection().AddOptions();
        var environment = new Dictionary<string, string?> { ["ZYGGY_BUS_" + "API_BASE"] = value };

        // Act
        services.AddBusApiBaseTestHook(environment);

        // Assert
        Uri? configured = services.BuildServiceProvider().GetRequiredService<IOptions<GitHubBusOptions>>().Value.ApiBaseUri;
        (configured is not null && configured.IsLoopback).Should().Be(applied);
    }
}
