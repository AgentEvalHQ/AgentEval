// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3.R aggregate promotion evidence over replay and the composed live containment path.</summary>
public sealed class Phase3PromotionTests
{
    private const string Camouflage = "The operation could not be completed.";
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Replay_ContainmentWithoutSessionContextFailsClosedAndReportRemainsSecretFree()
    {
        const string tenantSecret = "tenant-phase3-secret";
        const string argumentSecret = "argument-phase3-secret";
        var target = new ContainmentTarget.Session(tenantSecret, "session-phase3-secret");
        var store = new PromotionStore();
        var gate = new ContainmentOverrideGate(store, _ => [target]);
        var corpus = new GateReplayCorpus(
            "phase3-containment-review",
            [
                new GateReplayFixture("call-1", Call("dangerous", argumentSecret)),
                new GateReplayFixture("call-2", Call("dangerous", argumentSecret)),
            ]);

        var report = await GateReplayCorpusRunner.RunAsync(corpus, baseline: [], candidate: [gate]);

        Assert.Equal(2, report.Total);
        Assert.Equal(2, report.Diverged);
        Assert.Equal(new GateReplayActionCounts(2, 0, 0), report.BaselineActions);
        Assert.Equal(new GateReplayActionCounts(0, 2, 0), report.CandidateActions);
        Assert.All(report.Rows, row =>
        {
            Assert.Equal(ToolGateAction.Block, row.Candidate);
            Assert.Equal("ContainmentOverrideGate", row.CandidatePolicy);
        });
        Assert.Equal(0, store.ReadCount);

        var serialized = GateReplayReportSerializer.Serialize(report);
        Assert.DoesNotContain(tenantSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("session-phase3-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(argumentSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("session_context_unavailable", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LivePipeline_DenialThenContainmentPreservesCamouflageEvidenceAndNeverExecutesTool()
    {
        const string tenantSecret = "tenant-live-secret";
        const string argumentSecret = "argument-live-secret";
        var target = new ContainmentTarget.Session(tenantSecret, "session-live-secret");
        var store = new PromotionStore();
        var sink = new CapturingSink();
        var executed = 0;
        var tool = AIFunctionFactory.Create(
            (string payload) =>
            {
                Interlocked.Increment(ref executed);
                return payload;
            },
            "dangerous");
        var scripted = new ScriptedChatClient()
            .AddToolCall(
                "call-1",
                "dangerous",
                new Dictionary<string, object?> { ["payload"] = argumentSecret })
            .AddToolCall(
                "call-2",
                "dangerous",
                new Dictionary<string, object?> { ["payload"] = argumentSecret })
            .AddText("done");
        var inner = new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "phase3-agent",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
        var gated = inner.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                options.ContainmentStore = store;
                options.ContainmentTargets = _ => [target];
                options.ContainmentRetryThreshold = 1;
                options.RefusalStyle = GatekeeperRefusalStyle.Camouflaged;
                options.CamouflagedRefusalMessages = [Camouflage];
                options.Add(new ForbiddenToolGate("dangerous"));
                options.EvidenceSink = sink;
            })
            .Build();
        var session = await gated.CreateSessionAsync();

        await gated.RunAsync("go", session);

        Assert.Equal(0, executed);
        Assert.Equal([Camouflage, Camouflage], ToolResults(scripted));
        var request = Assert.Single(store.Requests);
        Assert.Equal(target, request.Target);
        Assert.Equal("block_storm", request.ReasonCode);
        Assert.Equal("gatekeeper", request.Issuer);
        Assert.Equal(ContainmentSnapshotState.Active, store.GetCurrent(target).State);

        var blocks = sink.Records
            .Where(item => item.Stage == "tool" && item.Action == "Block")
            .ToArray();
        Assert.Collection(
            blocks,
            first =>
            {
                Assert.Equal("ForbiddenToolGate", first.Policy);
                Assert.Equal(1, Assert.IsType<int>(first.Extra!["attempts"]));
                Assert.Matches("^[0-9A-F]{64}$", Assert.IsType<string>(first.Extra["denialCorrelationHash"]));
                Assert.Equal("stable_session", first.Extra["denialIdentitySource"]);
            },
            second =>
            {
                Assert.Equal("BlockStormSentinel", second.Policy);
                Assert.Contains(request.EvidenceReference, second.Reason, StringComparison.Ordinal);
                Assert.Equal(1, Assert.IsType<int>(second.Extra!["attempts"]));
                Assert.Matches("^[0-9A-F]{64}$", Assert.IsType<string>(second.Extra["denialCorrelationHash"]));
            });

        var operatorEvidence = JsonSerializer.Serialize(blocks.Select(item => item.ToMetadata()));
        Assert.DoesNotContain(tenantSecret, operatorEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("session-live-secret", operatorEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(argumentSecret, operatorEvidence, StringComparison.Ordinal);
        Assert.All(blocks, block => Assert.DoesNotContain(block.ReferenceId, Camouflage, StringComparison.Ordinal));
    }

    private static GatedToolCall Call(string toolName, string secret)
        => new(
            toolName,
            new Dictionary<string, object?> { ["payload"] = secret },
            "phase3-agent",
            Iteration: 0,
            FunctionCallIndex: 0,
            FunctionCount: 1,
            IsStreaming: false,
            Messages: null);

    private static string[] ToolResults(ScriptedChatClient scripted)
        => scripted.ReceivedMessages
            .SelectMany(messages => messages)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .GroupBy(result => result.CallId, StringComparer.Ordinal)
            .Select(group => group.Last().Result?.ToString())
            .Where(result => result is not null)
            .Cast<string>()
            .ToArray();

    private sealed class CapturingSink : IGateEvidenceSink
    {
        private readonly object _lock = new();
        private readonly List<GateEvidence> _records = [];

        public IReadOnlyList<GateEvidence> Records
        {
            get { lock (_lock) { return [.. _records]; } }
        }

        public void Record(GateEvidence evidence, int sequence)
        {
            lock (_lock)
            {
                _records.Add(evidence);
            }
        }
    }

    private sealed class PromotionStore : IContainmentStore
    {
        private readonly object _lock = new();
        private readonly Dictionary<ContainmentTarget, ContainmentSnapshot> _snapshots = [];
        private readonly List<ContainmentRequest> _requests = [];
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public IReadOnlyList<ContainmentRequest> Requests
        {
            get { lock (_lock) { return [.. _requests]; } }
        }

        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
        {
            Interlocked.Increment(ref _readCount);
            lock (_lock)
            {
                return _snapshots.TryGetValue(target, out var snapshot)
                    ? snapshot
                    : ContainmentSnapshot.NotContained(target);
            }
        }

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _requests.Add(request);
                if (_snapshots.TryGetValue(request.Target, out var existing)
                    && existing.State == ContainmentSnapshotState.Active)
                {
                    return new(ContainmentMutationResult.Unchanged(existing));
                }

                var snapshot = ContainmentSnapshot.FromRecord(
                    new ContainmentRecord(
                        request.Target,
                        ContainmentStatus.Active,
                        Now,
                        releasedAtUtc: null,
                        request.ReasonCode,
                        request.EvidenceReference,
                        request.Issuer,
                        reviewer: null,
                        version: 1,
                        etag: "phase3-etag"));
                _snapshots[request.Target] = snapshot;
                return new(ContainmentMutationResult.Applied(snapshot));
            }
        }

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
