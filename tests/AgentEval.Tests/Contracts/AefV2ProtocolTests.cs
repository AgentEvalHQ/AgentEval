// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Runner;
using Xunit;

namespace AgentEval.Tests.Contracts;

/// <summary>
/// AEF v2 run plans, runner capability manifests and runner event streams. The expected problems of each stream are
/// written by hand from contracts/aef/v2/README.md; the Python reference (tools/aef_stream.py) and the .NET verifier
/// (AgentEval.Results) both have to reproduce them.
/// </summary>
public class AefV2ProtocolTests
{
    private static readonly string Protocol = Path.Combine(AefSchemaSet.V2, "conformance", "protocol");

    public static TheoryData<string, string> Documents()
    {
        var data = new TheoryData<string, string>();
        foreach (var folder in new[] { "plans", "runners" })
        {
            foreach (var dir in Directory.GetDirectories(Path.Combine(Protocol, folder)))
                data.Add(folder, Path.GetFileName(dir));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void APlanOrRunnerManifest_IsAcceptedOrRefused_AsItsExpectationSays(string folder, string name)
    {
        var dir = Path.Combine(Protocol, folder, name);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")))!;
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "document.json")));
        var schema = (string)expected["schema"]!;

        Assert.Equal((string)expected["writer"]! == "valid", AefSchemaSet.Writer.Value.IsValid(schema, document, out var w));
        Assert.Equal((string)expected["reader"]! == "valid", AefSchemaSet.Reader.Value.IsValid(schema, document, out var r));
        _ = (w, r);
    }

    public static TheoryData<string> Streams() =>
        new(Directory.GetDirectories(Path.Combine(Protocol, "streams")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Streams))]
    public void EveryEvent_IsValidAgainstTheWriterAndReaderSchemas(string name)
    {
        foreach (var (e, i) in Events(name).Select((e, i) => (e, i + 1)))
        {
            Assert.True(AefSchemaSet.Writer.Value.IsValid("runner-event", e, out var w), $"{name} event {i} (writer): {w}");
            Assert.True(AefSchemaSet.Reader.Value.IsValid("runner-event", e, out var r), $"{name} event {i} (reader): {r}");
        }
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void TheVerifier_ReportsExactlyTheExpectedProblems(string name)
    {
        var plan = JsonNode.Parse(File.ReadAllText(Path.Combine(Protocol, "streams", "plan.json")));
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Protocol, "streams", name, "expected.json")))!["problems"]!.AsArray()
            .Select(p => ((string)p!["where"]!, (string)p["problem"]!));

        Assert.Equal(expected, RunnerEventStream.Verify(Events(name), plan));
    }

    [Fact]
    public void TheStreamVectors_CoverEveryProblem()
    {
        var problems = Directory.GetFiles(Path.Combine(Protocol, "streams"), "expected.json", SearchOption.AllDirectories)
            .SelectMany(f => JsonNode.Parse(File.ReadAllText(f))!["problems"]!.AsArray().Select(p => (string)p!["problem"]!))
            .Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(["after-terminal", "first", "job-id", "no-terminal", "over-budget", "plan-id", "seq", "spend-decreased", "time", "unannounced-run"],
            problems);
    }

    [Fact]
    public void AnUnknownEventKind_IsRefusedByTheWriter_AndReadAsOtherByAReader()
    {
        var e = JsonNode.Parse("""{"schemaVersion":"2.0","seq":2,"kind":"job.paused","jobId":"job-7","at":"2026-10-08T12:00:01Z"}""");

        Assert.False(AefSchemaSet.Writer.Value.IsValid("runner-event", e, out _));
        Assert.True(AefSchemaSet.Reader.Value.IsValid("runner-event", e, out var errors), errors);
    }

    private static List<JsonNode> Events(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Protocol, "streams", name, "events.ndjson"));
        return [.. Encoding.UTF8.GetString(bytes).Split('\n').Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!)];
    }
}
