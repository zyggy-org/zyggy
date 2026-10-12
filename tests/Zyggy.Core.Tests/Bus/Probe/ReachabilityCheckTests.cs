using Zyggy.Core.Bus;
using Zyggy.Core.Bus.Probe;
using Zyggy.Core.Tests.Infrastructure;

namespace Zyggy.Core.Tests.Bus.Probe;

/// <summary>Spec 04 AC-28 (pure half): the classification of git's and the API's answers.</summary>
public sealed class ReachabilityCheckTests
{
    [Theory]
    [InlineData(128, "fatal: Authentication failed for 'https://example.invalid/x.git/'", ReachStatus.AuthFailed)]
    [InlineData(128, "fatal: could not read Username for 'https://example.invalid': terminal prompts disabled", ReachStatus.AuthFailed)]
    [InlineData(128, "git@example.invalid: Permission denied (publickey).", ReachStatus.AuthFailed)]
    [InlineData(128, "remote: Permission to acme-org/acme-bus.git denied to someone.", ReachStatus.AuthFailed)]
    [InlineData(128, "fatal: unable to access 'https://example.invalid/': Could not resolve host: example.invalid", ReachStatus.Unreachable)]
    [InlineData(128, "fatal: unable to access 'https://example.invalid/': Failed to connect to example.invalid port 443", ReachStatus.Unreachable)]
    [InlineData(128, "ssh: connect to host example.invalid port 22: Connection timed out", ReachStatus.Unreachable)]
    [InlineData(128, "ssh: connect to host example.invalid port 22: Connection refused", ReachStatus.Unreachable)]
    [InlineData(128, "ssh: connect to host example.invalid port 22: Network is unreachable", ReachStatus.Unreachable)]
    [InlineData(128, "fatal: unable to access 'https://example.invalid/': Received HTTP code 407 from proxy after CONNECT", ReachStatus.Unreachable)]
    [InlineData(128, "fatal: unable to access 'https://example.invalid/': SSL certificate problem: unable to get local issuer certificate", ReachStatus.TlsFailed)]
    [InlineData(128, "fatal: unable to access 'https://example.invalid/': schannel: next InitializeSecurityContext failed", ReachStatus.TlsFailed)]
    [InlineData(0, "", ReachStatus.Ok)]
    [InlineData(null, "", ReachStatus.Unreachable)]
    public void ClassifyGit_StderrPatterns(int? exitCode, string stderr, ReachStatus expected)
    {
        // Act
        ReachStatus status = ReachabilityCheck.ClassifyGit(exitCode, stderr);

        // Assert
        status.Should().Be(expected);
    }

    public static TheoryData<HeadProbe, ReachStatus, string> ApiCases => new()
    {
        { new Changed("\"abc\"", 4999), ReachStatus.Ok, "answered" },
        { new Unchanged(4999), ReachStatus.Ok, "answered" },
        { new Throttled(null, TimeSpan.FromSeconds(60), 0), ReachStatus.Ok, "answered (rate limited)" },
        { new Unreachable("unauthorized"), ReachStatus.AuthFailed, "unauthorized" },
        { new Unreachable("no github-pat for tenant acme"), ReachStatus.AuthFailed, "no token" },
        { new Unreachable("HttpRequestException"), ReachStatus.Unreachable, "HttpRequestException" },
        { new Unreachable("timeout"), ReachStatus.Unreachable, "timeout" },
        { new Unreachable("AuthenticationException"), ReachStatus.TlsFailed, "AuthenticationException" },
    };

    [Theory]
    [MemberData(nameof(ApiCases))]
    public void ClassifyApi_HeadProbe(HeadProbe probe, ReachStatus expected, string detail)
    {
        // Act
        (ReachStatus status, string text) = ReachabilityCheck.ClassifyApi(probe);

        // Assert
        status.Should().Be(expected);
        text.Should().Be(detail);
    }

    [Fact]
    public void Redact_UrlWithUserinfo_Stripped()
    {
        // Act
        string text = ReachabilityCheck.Redact("fatal: unable to access 'https://user:" + TestKeys.TestPat + "@example.invalid/acme-org/acme-bus.git/': 403\n");

        // Assert
        text.Should().Be("fatal: unable to access 'https://<redacted>@example.invalid/acme-org/acme-bus.git/': 403");
        SecretSweep.AssertAbsent([text]);
    }

    [Fact]
    public void Redact_LongStderr_First300Chars()
    {
        // Act
        string text = ReachabilityCheck.Redact(new string('x', 1000));

        // Assert
        text.Should().HaveLength(300);
    }

    [Fact]
    public void ProxyDisplay_WithUserinfo_HostPortOnly()
    {
        // Act
        string text = ReachabilityCheck.ProxyDisplay(new Uri("http://user:pass@proxy.example.invalid:8080"));

        // Assert
        text.Should().Be("proxy.example.invalid:8080");
    }

    [Fact]
    public void ProxyDisplay_Null_None()
    {
        // Act & Assert
        ReachabilityCheck.ProxyDisplay(null).Should().Be("none");
    }

    [Theory]
    [InlineData(ReachStatus.Ok, "ok")]
    [InlineData(ReachStatus.AuthFailed, "auth failed")]
    [InlineData(ReachStatus.Unreachable, "unreachable")]
    [InlineData(ReachStatus.TlsFailed, "tls failed")]
    [InlineData(ReachStatus.Skipped, "skipped")]
    public void ReachStatusWire_EveryMember(ReachStatus status, string wire)
    {
        // Act & Assert
        ReachStatusWire.ToWire(status).Should().Be(wire);
    }

    [Theory]
    [InlineData(ProbeOutcome.Done, "done")]
    [InlineData(ProbeOutcome.Rejected, "rejected")]
    [InlineData(ProbeOutcome.Timeout, "timeout")]
    [InlineData(ProbeOutcome.PushFailed, "push_failed")]
    public void ProbeOutcomeWire_RoundTrips(ProbeOutcome outcome, string wire)
    {
        // Act
        bool ok = ProbeOutcomeWire.TryFromWire(ProbeOutcomeWire.ToWire(outcome), out ProbeOutcome back);

        // Assert
        ProbeOutcomeWire.ToWire(outcome).Should().Be(wire);
        ok.Should().BeTrue();
        back.Should().Be(outcome);
    }
}
