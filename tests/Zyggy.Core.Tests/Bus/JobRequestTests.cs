using System.Text;

using Zyggy.Core.Bus;
using Zyggy.Core.Envelope;

namespace Zyggy.Core.Tests.Bus;

/// <summary>Spec 08 AC-1: every bad option is refused with its field named before anything is signed; good ones are normalised.</summary>
public sealed class JobRequestTests
{
    private static readonly string[] DoubleComma = ["Read,,Grep"];

    private static readonly string[] ControlInTool = ["Read\n---"];

    public static TheoryData<string, string, string?, int?, string[]?, string?, string, string> Refused => new()
    {
        { "Home Laptop", "calizr", null, null, null, null, "do it\n", "to" },
        { "home-laptop", "", null, null, null, null, "do it\n", "project" },
        { "home-laptop", "x\nsig: hmac-sha256:00", null, null, null, null, "do it\n", "project" },
        { "home-laptop", new string('a', 1025), null, null, null, null, "do it\n", "project" },
        { "home-laptop", "../x", null, null, null, null, "do it\n", "project" },
        { "home-laptop", "calizr", "a: b", null, null, null, "do it\n", "agent" },
        { "home-laptop", "calizr", "a\n", null, null, null, "do it\n", "agent" },
        { "home-laptop", "calizr", new string('a', 65), null, null, null, "do it\n", "agent" },
        { "home-laptop", "calizr", null, 0, null, null, "do it\n", "timeout_minutes" },
        { "home-laptop", "calizr", null, 1441, null, null, "do it\n", "timeout_minutes" },
        { "home-laptop", "calizr", null, null, DoubleComma, null, "do it\n", "allowed_tools[1]" },
        { "home-laptop", "calizr", null, null, ControlInTool, null, "do it\n", "allowed_tools[0]" },
        { "home-laptop", "calizr", null, null, new[] { new string('R', 129) }, null, "do it\n", "allowed_tools[0]" },
        { "home-laptop", "calizr", null, null, null, "urgent", "do it\n", "priority" },
        { "home-laptop", "calizr", null, null, null, "High", "do it\n", "priority" },
        { "home-laptop", "calizr", null, null, null, null, "", "body" },
        { "home-laptop", "calizr", null, null, null, null, "   \n", "body" },
        { "home-laptop", "calizr", null, null, null, null, new string('b', JobRequest.MaxBodyBytes + 1), "body" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void TryCreate_Refused_NamesField(string to, string project, string? agent, int? timeout, string[]? tools, string? priority, string body, string field)
    {
        // Act
        JobRequestError? error = JobRequest.TryCreate(to, project, agent, timeout, tools, priority, body, out JobRequest? request);

        // Assert
        error.Should().NotBeNull();
        error!.Field.Should().Be(field);
        error.Detail.Should().NotBeNullOrWhiteSpace();
        request.Should().BeNull();
    }

    [Fact]
    public void TryCreate_Accepted_NormalisesBodyAndSplitsTools()
    {
        // Act
        JobRequestError? error = JobRequest.TryCreate("home-laptop", "/home/u/src/calizr", "reviewer", 15, ["Read, Grep, Bash(dotnet *)", "Glob"], "high", "﻿a\r\nb\n\n\n", out JobRequest? request);

        // Assert
        error.Should().BeNull();
        request!.To.Value.Should().Be("home-laptop");
        request.Project.Should().Be(new ProjectReference("/home/u/src/calizr", true));
        request.Agent.Should().Be("reviewer");
        request.TimeoutMinutes.Should().Be(15);
        request.AllowedTools.Should().Equal("Read", "Grep", "Bash(dotnet *)", "Glob");
        request.Priority.Should().Be(EnvelopePriority.High);
        Encoding.UTF8.GetString(request.Body.Span).Should().Be("a\nb\n");
    }

    [Fact]
    public void TryCreate_DuplicateToolsKeptInOrder()
    {
        // Act
        JobRequest.TryCreate("home-laptop", "calizr", null, null, ["Read", "Grep,Read"], null, "x", out JobRequest? request).Should().BeNull();

        // Assert
        request!.AllowedTools.Should().Equal("Read", "Grep", "Read");
    }

    [Fact]
    public void TryCreate_BodyStartingWithFrontMatterDelimiter_KeptVerbatim()
    {
        // Act
        JobRequest.TryCreate("home-laptop", "calizr", null, null, null, null, "---\nnot front matter\n", out JobRequest? request).Should().BeNull();

        // Assert
        Encoding.UTF8.GetString(request!.Body.Span).Should().Be("---\nnot front matter\n");
    }

    [Fact]
    public void TryCreate_BodyExactlyThreeDashes_Accepted()
    {
        // Act
        JobRequest.TryCreate("home-laptop", "calizr", null, null, null, null, "---", out JobRequest? request).Should().BeNull();

        // Assert
        Encoding.UTF8.GetString(request!.Body.Span).Should().Be("---\n");
    }

    [Fact]
    public void TryCreate_AllowedToolsAbsent_Null()
    {
        // Act
        JobRequest.TryCreate("home-laptop", "calizr", null, null, null, null, "x", out JobRequest? request).Should().BeNull();

        // Assert
        request!.AllowedTools.Should().BeNull();
        request.Priority.Should().Be(EnvelopePriority.Normal);
        request.Agent.Should().BeNull();
        request.TimeoutMinutes.Should().BeNull();
    }

    [Fact]
    public void TryCreate_ToEqualsOwnMachine_Accepted()
    {
        // Act: ex-Q10 — TryCreate knows no sender; a self-addressed job is a request like any other
        JobRequestError? error = JobRequest.TryCreate("central", "calizr", null, null, null, null, "x", out JobRequest? request);

        // Assert
        error.Should().BeNull();
        request!.To.Value.Should().Be("central");
    }

    [Fact]
    public void TryCreate_FirstFailureWins_FieldOrderIsTheVerbs()
    {
        // Act: a bad `to` and a blank body
        JobRequestError? error = JobRequest.TryCreate("Home Laptop", "calizr", null, null, null, null, "", out _);

        // Assert
        error!.Field.Should().Be("to");
    }

    [Fact]
    public void Constants_AreTheDocumentedLimits()
    {
        JobRequest.MaxBodyBytes.Should().Be(65_536);
        JobRequest.MaxProjectLength.Should().Be(1024);
        JobRequest.MaxAgentLength.Should().Be(64);
        JobRequest.MaxToolLength.Should().Be(128);
        JobRequest.MaxTimeoutMinutes.Should().Be(1440);
    }
}
