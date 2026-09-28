using System.Reflection;

using Zyggy.Core.Tenancy;

namespace Zyggy.Core.Tests.Tenancy;

public sealed class LabelTypesTests
{
    private static readonly string Max63 = "a" + new string('b', 61) + "c";
    private static readonly string Over64 = new('a', 64);

    public static TheoryData<string> ValidLabels => new() { "acme", "a", "home-laptop", "x1-2y", Max63 };

    public static TheoryData<string> InvalidLabels =>
        new() { "", "Acme", "-acme", "acme-", "ac me", "acme/1", "a..b", Over64, "..", "geo/" };

    [Theory]
    [MemberData(nameof(ValidLabels))]
    public void TryParse_ValidLabel_ReturnsTrueWithValue(string value)
    {
        // Act
        bool tenantOk = TenantId.TryParse(value, out TenantId? tenant);
        bool userOk = UserId.TryParse(value, out UserId? user);
        bool machineOk = MachineName.TryParse(value, out MachineName? machine);

        // Assert
        tenantOk.Should().BeTrue();
        userOk.Should().BeTrue();
        machineOk.Should().BeTrue();
        tenant!.Value.Should().Be(value);
        user!.Value.Should().Be(value);
        machine!.Value.Should().Be(value);
        tenant.ToString().Should().Be(value);
        user.ToString().Should().Be(value);
        machine.ToString().Should().Be(value);
    }

    [Theory]
    [MemberData(nameof(InvalidLabels))]
    public void TryParse_InvalidLabel_ReturnsFalse(string value)
    {
        // Act
        bool tenantOk = TenantId.TryParse(value, out TenantId? tenant);
        bool userOk = UserId.TryParse(value, out UserId? user);
        bool machineOk = MachineName.TryParse(value, out MachineName? machine);

        // Assert
        tenantOk.Should().BeFalse();
        userOk.Should().BeFalse();
        machineOk.Should().BeFalse();
        tenant.Should().BeNull();
        user.Should().BeNull();
        machine.Should().BeNull();
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        // Act
        bool tenantOk = TenantId.TryParse(null, out TenantId? tenant);
        bool userOk = UserId.TryParse(null, out UserId? user);
        bool machineOk = MachineName.TryParse(null, out MachineName? machine);

        // Assert
        (tenantOk || userOk || machineOk).Should().BeFalse();
        tenant.Should().BeNull();
        user.Should().BeNull();
        machine.Should().BeNull();
    }

    [Theory]
    [InlineData("Acme")]
    [InlineData("")]
    [InlineData("acme/1")]
    public void Parse_InvalidLabel_ThrowsFormatException(string value)
    {
        // Arrange
        Action tenant = () => TenantId.Parse(value);
        Action user = () => UserId.Parse(value);
        Action machine = () => MachineName.Parse(value);

        // Act & Assert
        tenant.Should().Throw<FormatException>();
        user.Should().Throw<FormatException>();
        machine.Should().Throw<FormatException>();
    }

    [Fact]
    public void Equals_SameValue_IsEqual()
    {
        // Act
        TenantId first = TenantId.Parse("acme");
        TenantId second = TenantId.Parse("acme");

        // Assert
        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    [Theory]
    [InlineData(typeof(TenantId))]
    [InlineData(typeof(UserId))]
    [InlineData(typeof(MachineName))]
    public void PublicSurface_HasNoDefaultNoParameterlessConstructorNoImplicitConversion(Type type)
    {
        // Act
        ConstructorInfo? parameterless = type.GetConstructor(Type.EmptyTypes);
        ConstructorInfo[] publicConstructors = type.GetConstructors();
        MemberInfo[] defaults = type.GetMember("Default", BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
        MethodInfo[] implicits = [.. type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "op_Implicit")];

        // Assert
        parameterless.Should().BeNull();
        publicConstructors.Should().BeEmpty();
        defaults.Should().BeEmpty();
        implicits.Should().BeEmpty();
    }

    [Fact]
    public void Principal_TwoIds_ExposesBoth()
    {
        // Act
        var principal = new Principal(TenantId.Parse("acme"), UserId.Parse("alice"));

        // Assert
        principal.Tenant.Value.Should().Be("acme");
        principal.User.Value.Should().Be("alice");
    }
}
