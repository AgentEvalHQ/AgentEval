// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

public sealed class GateReplayCorpusTests
{
    private static GatedToolCall MakeCall(
        string functionName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        IReadOnlyList<ChatMessage>? messages = null)
        => new(
            functionName,
            arguments,
            AgentName: "test-agent",
            Iteration: 0,
            FunctionCallIndex: 0,
            FunctionCount: 1,
            IsStreaming: false,
            messages);

    [Fact]
    public async Task Serializer_SupportedCorpusRoundTrip_PreservesCanonicalBytesAndHistory()
    {
        var source = await LoadReviewedCorpusAsync();

        await using var first = new MemoryStream();
        await GateReplayCorpusSerializer.WriteAsync(first, source);
        first.Position = 0;
        var restored = await GateReplayCorpusSerializer.ReadAsync(first);
        await using var second = new MemoryStream();
        await GateReplayCorpusSerializer.WriteAsync(second, restored);

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal("phase0-reviewed-sanitized-v1", restored.CorpusId);
        Assert.Equal(["read-allow", "delete-block", "write-mutate"], restored.Fixtures.Select(f => f.Id));
        Assert.IsType<TextContent>(restored.Fixtures[0].Call.Messages![0].Contents[0]);
        Assert.IsType<FunctionCallContent>(restored.Fixtures[0].Call.Messages![1].Contents[0]);
        var result = Assert.IsType<FunctionResultContent>(
            restored.Fixtures[2].Call.Messages![0].Contents[0]);
        Assert.Equal(string.Empty, result.CallId);
    }

    [Fact]
    public async Task Serializer_EmptyCorpus_RoundTripsThroughMandatoryHeader()
    {
        var corpus = new GateReplayCorpus("empty-v1", []);
        await using var stream = new MemoryStream();

        await GateReplayCorpusSerializer.WriteAsync(stream, corpus);
        stream.Position = 0;
        var restored = await GateReplayCorpusSerializer.ReadAsync(stream);

        Assert.Equal("empty-v1", restored.CorpusId);
        Assert.Empty(restored.Fixtures);
        Assert.Contains("\"record\":\"header\"", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"schema\":\"gatekeeper.replay-corpus/1\",\"record\":\"header\"}")]
    [InlineData("{\"schema\":\"wrong\",\"record\":\"header\",\"corpusId\":\"x\"}")]
    [InlineData("{\"schema\":\"gatekeeper.replay-corpus/1\",\"schema\":\"gatekeeper.replay-corpus/1\",\"record\":\"header\",\"corpusId\":\"x\"}")]
    [InlineData("{\"schema\":\"gatekeeper.replay-corpus/1\",\"record\":\"header\",\"corpusId\":\"x\",\"unexpected\":true}")]
    public async Task ReadAsync_MalformedCorpus_ThrowsFormatException(string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var error = await Assert.ThrowsAsync<GateReplayCorpusFormatException>(
            () => GateReplayCorpusSerializer.ReadAsync(stream));

        Assert.Contains("Expected", error.Message);
        Assert.Contains("Actual", error.Message);
        Assert.Contains("Suggestions", error.Message);
    }

    [Fact]
    public async Task ReadAsync_OversizedCorpus_RejectsBeforeJsonParsing()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 513));
        await using var stream = new MemoryStream(bytes);
        var limits = new GateReplayCorpusLimits(MaxLineBytes: 256, MaxTotalBytes: 512);

        var error = await Assert.ThrowsAsync<GateReplayCorpusFormatException>(
            () => GateReplayCorpusSerializer.ReadAsync(stream, limits));

        Assert.Contains("total-size limit", error.Message);
    }

    [Fact]
    public async Task ReadAsync_CallCountAboveLimit_RejectsCorpus()
    {
        var source = await LoadReviewedCorpusAsync();
        await using var stream = new MemoryStream();
        await GateReplayCorpusSerializer.WriteAsync(stream, source);
        stream.Position = 0;
        var limits = new GateReplayCorpusLimits(MaxCalls: 1);

        var error = await Assert.ThrowsAsync<GateReplayCorpusFormatException>(
            () => GateReplayCorpusSerializer.ReadAsync(stream, limits));

        Assert.Contains("call count exceeds", error.Message);
    }

    [Fact]
    public async Task WriteAsync_UnsupportedContent_ThrowsBeforeWritingAnything()
    {
        var message = new ChatMessage(ChatRole.User, [new AIContent()]);
        var corpus = new GateReplayCorpus(
            "unsupported-v1",
            [new GateReplayFixture("call-1", MakeCall("inspect", messages: [message]))]);
        await using var destination = new MemoryStream();
        destination.WriteByte(0x2A);

        var error = await Assert.ThrowsAsync<GateReplayCorpusFormatException>(
            () => GateReplayCorpusSerializer.WriteAsync(destination, corpus));

        Assert.Equal([0x2A], destination.ToArray());
        Assert.Contains("unsupported message content type", error.Message);
    }

    [Fact]
    public async Task WriteAsync_TotalSizeExceeded_DoesNotPartiallyWriteCorpus()
    {
        var payload = new string('x', 180);
        var corpus = new GateReplayCorpus(
            "bounded-writer-v1",
            [
                new GateReplayFixture(
                    "one",
                    MakeCall("write_file", new Dictionary<string, object?> { ["content"] = payload })),
                new GateReplayFixture(
                    "two",
                    MakeCall("write_file", new Dictionary<string, object?> { ["content"] = payload })),
            ]);
        await using var destination = new MemoryStream();
        destination.WriteByte(0x2A);
        var limits = new GateReplayCorpusLimits(MaxLineBytes: 512, MaxTotalBytes: 512);

        var error = await Assert.ThrowsAsync<GateReplayCorpusFormatException>(
            () => GateReplayCorpusSerializer.WriteAsync(destination, corpus, limits));

        Assert.Equal([0x2A], destination.ToArray());
        Assert.Contains("serialized corpus", error.Message);
    }

    [Fact]
    public async Task WriteAsync_DuplicateJsonElementProperty_RejectsUnreadableOutput()
    {
        using var document = JsonDocument.Parse("""{"duplicate":1,"duplicate":2}""");
        var corpus = new GateReplayCorpus(
            "duplicate-json-v1",
            [
                new GateReplayFixture(
                    "one",
                    MakeCall(
                        "inspect",
                        new Dictionary<string, object?> { ["payload"] = document.RootElement.Clone() })),
            ]);
        await using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<GateReplayCorpusFormatException>(
            () => GateReplayCorpusSerializer.WriteAsync(destination, corpus));

        Assert.Empty(destination.ToArray());
        Assert.Contains("duplicate property", error.Message);
    }
    [Fact]
    public async Task Runner_ReviewedCorpus_ReportsAllowBlockMutateAndThresholds()
    {
        var corpus = await LoadReviewedCorpusAsync();
        var report = await GateReplayCorpusRunner.RunAsync(
            corpus,
            baseline: [],
            candidate: [new ReviewedFixturePolicyGate()],
            baselineConfigId: "baseline-v1",
            candidateConfigId: "candidate-v1");

        Assert.Equal(3, report.Total);
        Assert.Equal(2, report.Diverged);
        Assert.Equal(new GateReplayActionCounts(3, 0, 0), report.BaselineActions);
        Assert.Equal(new GateReplayActionCounts(1, 1, 1), report.CandidateActions);

        var json1 = GateReplayReportSerializer.Serialize(report);
        var json2 = GateReplayReportSerializer.Serialize(report);
        Assert.Equal(json1, json2);
        Assert.Contains("\"schema\":\"gatekeeper.replay-report/1\"", json1);
        Assert.DoesNotContain("\"arguments\"", json1);
        Assert.DoesNotContain("\"messages\"", json1);
        Assert.DoesNotContain("\"reason\"", json1);

        var strict = GateReplayThresholdEvaluator.Evaluate(
            report,
            new GateReplayThresholds(0, 0, 0));
        Assert.False(strict.Passed);
        Assert.Equal(3, strict.Violations.Count);
        Assert.Equal(1, GateReplayBuildAdapter.GetExitCode(strict));

        var accepted = GateReplayThresholdEvaluator.Evaluate(
            report,
            new GateReplayThresholds(2, 1, 1));
        Assert.True(accepted.Passed);
        Assert.Empty(accepted.Violations);
        Assert.Equal(0, GateReplayBuildAdapter.GetExitCode(accepted));
    }

    [Fact]
    public async Task Runner_SecretArgumentsReasonsAndMutations_AreExcludedFromReport()
    {
        const string secret = "phase0-secret-do-not-report";
        var corpus = new GateReplayCorpus(
            "secret-minimization-v1",
            [
                new GateReplayFixture(
                    "opaque-1",
                    MakeCall("send_data", new Dictionary<string, object?> { ["token"] = secret })),
            ]);

        var report = await GateReplayCorpusRunner.RunAsync(
            corpus,
            baseline: [],
            candidate: [new SecretMutationGate(secret)]);
        var json = GateReplayReportSerializer.Serialize(report);

        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("token", json);
        Assert.Equal(ToolGateAction.Mutate, report.Rows[0].Candidate);
    }

    [Fact]
    public async Task Runner_ThrowingGate_FailsClosedWithoutDroppingLaterCorpusRows()
    {
        var corpus = await LoadReviewedCorpusAsync();

        var report = await GateReplayCorpusRunner.RunAsync(
            corpus,
            baseline: [],
            candidate: [new ThrowingGate()]);

        Assert.Equal(3, report.Rows.Count);
        Assert.Equal(new GateReplayActionCounts(0, 3, 0), report.CandidateActions);
        Assert.All(report.Rows, row => Assert.Equal("throwing-gate", row.CandidatePolicy));
    }

    [Fact]
    public async Task Runner_SharedStatefulGateInstance_IsolatesBaselineAndCandidateScopes()
    {
        var corpus = new GateReplayCorpus(
            "stateful-isolation-v1",
            [
                new GateReplayFixture("one", MakeCall("read_file")),
                new GateReplayFixture("two", MakeCall("read_file")),
            ]);
        var gate = new PerScopeSecondCallBlockGate();

        using var outer = AgentRunScope.Begin(session: null, agentName: "outer-live-run", trace: null);
        var report = await GateReplayCorpusRunner.RunAsync(corpus, [gate], [gate]);

        Assert.Equal(0, report.Diverged);
        Assert.Equal(new GateReplayActionCounts(1, 1, 0), report.BaselineActions);
        Assert.Equal(new GateReplayActionCounts(1, 1, 0), report.CandidateActions);
        Assert.Equal(2, gate.ScopeCount);
        Assert.Same(outer, AgentRunScope.Current);
    }

    [Fact]
    public async Task Runner_EmptyCorpusAndGateLists_ReturnsZeroedReport()
    {
        var report = await GateReplayCorpusRunner.RunAsync(
            new GateReplayCorpus("empty-run-v1", []),
            baseline: [],
            candidate: []);

        Assert.Equal(0, report.Total);
        Assert.Equal(0, report.Diverged);
        Assert.Equal(new GateReplayActionCounts(0, 0, 0), report.BaselineActions);
        Assert.Equal(new GateReplayActionCounts(0, 0, 0), report.CandidateActions);
        Assert.Empty(report.Rows);
    }

    private static async Task<GateReplayCorpus> LoadReviewedCorpusAsync()
    {
        await using var stream = typeof(GateReplayCorpusTests).Assembly.GetManifestResourceStream(
            "AgentEval.Tests.GatekeeperReplayCorpus.jsonl")
            ?? throw new InvalidOperationException("Embedded Gatekeeper replay corpus was not found.");
        return await GateReplayCorpusSerializer.ReadAsync(stream);
    }

    private sealed class ReviewedFixturePolicyGate : IToolGate
    {
        public string PolicyName => "reviewed-fixture-policy";
        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            if (call.FunctionName == "delete_database")
            {
                return new(ToolGateVerdict.Block(PolicyName, "destructive call requires separate approval"));
            }

            if (call.FunctionName == "write_file")
            {
                var rewritten = new Dictionary<string, object?>(call.Arguments ?? new Dictionary<string, object?>())
                {
                    ["path"] = "/sandbox/out.txt",
                };
                return new(ToolGateVerdict.Mutate(PolicyName, rewritten, "confine output"));
            }

            return new(ToolGateVerdict.Allow(PolicyName));
        }
    }

    private sealed class SecretMutationGate(string secret) : IToolGate
    {
        public string PolicyName => "secret-minimization-policy";
        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => new(ToolGateVerdict.Mutate(
                PolicyName,
                new Dictionary<string, object?> { ["replacement"] = secret },
                $"redacted {secret}"));
    }

    private sealed class ThrowingGate : IToolGate
    {
        public string PolicyName => "throwing-gate";
        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("synthetic fixture failure");
    }

    private sealed class PerScopeSecondCallBlockGate : IToolGate
    {
        private readonly Dictionary<string, int> _callsByScope = new(StringComparer.Ordinal);

        public string PolicyName => "per-scope-second-call";
        public GateCost Cost => GateCost.PureCode;
        public GateRequirements Requirements => GateRequirements.RunScope;
        public int ScopeCount => _callsByScope.Count;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
        {
            var scopeId = AgentRunScope.Current?.RunId
                ?? throw new InvalidOperationException("Replay configuration has no run scope.");
            _callsByScope.TryGetValue(scopeId, out var calls);
            _callsByScope[scopeId] = ++calls;
            return new(calls == 2
                ? ToolGateVerdict.Block(PolicyName, "second call in this configuration")
                : ToolGateVerdict.Allow(PolicyName));
        }
    }
}
