// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Results;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;
using AgentEval.Tests.Contracts;

namespace AgentEval.Tests.Cli;

public sealed class DecideCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "aef-decide-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private void CreateRun(bool seal = true)
    {
        var sample = Path.Combine(AefSchemaSet.RepoRoot(), "samples", "interop", "assert-ai", "example-run", "results", "billing-safety", "run-1");
        Assert.Equal(ExitCodes.Success, AefCommand.RunImportAssertAi(sample, _directory,
            null, null, null, null, null, "off", !seal, null, true, TextWriter.Null, TextWriter.Null));
    }

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_directory, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static readonly DateTimeOffset DecisionTime = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private sealed class DecisionClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DecisionTime;
    }

    private (int Exit, string Output) Decide(string kind = "approve", string identity = "reviewer-1", string reason = "Reviewed evidence", string? result = null,
        string? requirement = null, string? expires = null)
    {
        var output = new StringWriter();
        var exit = DecideCommand.Run(_directory, kind, identity, reason, true, output, TextWriter.Null,
            new DecisionClock(), result, requirement, expires);
        return (exit, output.ToString());
    }

    [Fact]
    public void Run_ApproveThenReject_SealsTwoBatchesWithoutChangingOriginalEvidence()
    {
        CreateRun();
        var before = Snapshot();
        foreach (var (kind, number) in new[] { ("approve", 1), ("reject", 2) })
        {
            var (exit, output) = Decide(kind);
            Assert.Equal(ExitCodes.Success, exit);
            var report = JsonNode.Parse(output)!;
            Assert.Equal(number, (int)report["batch"]!);
            Assert.Equal("self-attested", (string?)report["assurance"]);
            Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(_directory).Outcome);
        }
        var after = Snapshot();
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
        var events = File.ReadAllLines(Path.Combine(_directory, "overlays", "events.ndjson")).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal("approve", (string?)events[0]["kind"]);
        Assert.Equal("reject", (string?)events[1]["kind"]);
        Assert.Equal("reviewer-1", (string?)events[0]["by"]!["identity"]);
        Assert.Equal("self-attested", (string?)events[0]["by"]!["assurance"]);
        Assert.Equal("Reviewed evidence", (string?)events[0]["reason"]);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public void Run_ResultReview_ChangesOnlySelectedReviewAndPreservesEvidence(string kind)
    {
        CreateRun();
        var before = Snapshot();
        var id = (string)JsonNode.Parse(File.ReadLines(Path.Combine(_directory, "results.ndjson")).First())!["resultId"]!;
        Assert.Equal(ExitCodes.Success, Decide("reject").Exit);
        var (exit, output) = Decide(kind, result: id);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(id, (string?)JsonNode.Parse(output)!["resultId"]);
        var view = EffectiveView.Compute(_directory, DateTimeOffset.UtcNow, null);
        Assert.Equal(2, view.Reviews.Count);
        Assert.Contains(view.Reviews, r => r.Target == "run" && r.Status == "reject");
        Assert.Contains(view.Reviews, r => r.Target == id && r.Status == kind);
        Assert.Empty(view.Results);
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(_directory).Outcome);
        var after = Snapshot();
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
    }

    [Theory]
    [InlineData("missing-result")]
    [InlineData("")]
    [InlineData(" ")]
    public void Run_InvalidResult_RefusesWithoutMutation(string result)
    {
        CreateRun();
        var before = Snapshot();
        Assert.Equal(ExitCodes.UsageError, Decide(result: result).Exit);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("redact", "reviewer", "reason")]
    [InlineData("APPROVE", "reviewer", "reason")]
    [InlineData("approve", "", "reason")]
    [InlineData("approve", "reviewer", " ")]
    public void Run_InvalidDecision_RefusesWithoutMutation(string kind, string identity, string reason)
    {
        CreateRun();
        var before = Snapshot();
        Assert.Equal(ExitCodes.UsageError, Decide(kind, identity, reason).Exit);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_UnsealedOrTamperedRun_RefusesWithoutMutation(bool tamper)
    {
        CreateRun(seal: tamper);
        if (tamper) File.AppendAllText(Path.Combine(_directory, "summary.json"), " ");
        var before = Snapshot();
        Assert.Equal(ExitCodes.TestFailure, Decide().Exit);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Fact]
    public void Run_ExistingUnsealedEvent_DoesNotAdoptAnotherReviewersEvent()
    {
        CreateRun();
        AefOverlayWriter.Open(_directory).Append(new AefOverlayEvent
        {
            Kind = AefOverlayKind.Approve, Target = AefOverlayTarget.TheRun,
            By = new AefIdentity("another-reviewer", AefAssurance.SelfAttested), At = DateTimeOffset.UtcNow,
        });
        var before = Snapshot();
        Assert.Equal(ExitCodes.TestFailure, Decide().Exit);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Fact]
    public void Run_Waivers_RequireBoundedTimeAndPreserveOriginalEvidence()
    {
        CreateRun();
        var before = Snapshot();
        var id = (string)JsonNode.Parse(File.ReadLines(Path.Combine(_directory, "results.ndjson")).First())!["resultId"]!;
        const string expiry = "2026-10-14T12:00:00Z";
        Assert.Equal(ExitCodes.Success, Decide("waive", result: id, expires: expiry).Exit);
        var (exit, output) = Decide("waive", requirement: "REQ-42", expires: expiry);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("REQ-42", (string?)JsonNode.Parse(output)!["requirementId"]);
        Assert.Equal(expiry, (string?)JsonNode.Parse(output)!["expires"]);
        foreach (var (time, active) in new[] { (DecisionTime.AddSeconds(-1), false), (DecisionTime, true), (DecisionTime.AddDays(1), false) })
        {
            var view = EffectiveView.Compute(_directory, time, null);
            Assert.Equal(2, view.Waivers.Count);
            Assert.All(view.Waivers, w => Assert.Equal(active, w.Active));
            Assert.Equal(id, (string?)view.Waivers[0].Target["result"]);
            Assert.Equal("REQ-42", (string?)view.Waivers[1].Target["requirement"]);
            Assert.Empty(view.Results);
            Assert.Empty(view.Reviews);
        }
        Assert.Equal(AefOutcome.Intact, AefRunVerifier.Verify(_directory).Outcome);
        var after = Snapshot();
        foreach (var entry in before) Assert.Equal(entry.Value, after[entry.Key]);
    }

    [Theory]
    [InlineData("waive", null, null, "2026-10-14T12:00:00Z")]
    [InlineData("waive", "result", "REQ-42", "2026-10-14T12:00:00Z")]
    [InlineData("waive", null, "REQ-42", null)]
    [InlineData("waive", null, "REQ-42", "2026-10-13T12:00:00Z")]
    [InlineData("waive", null, "REQ-42", "2026-10-12T12:00:00Z")]
    [InlineData("waive", null, "REQ-42", "2026-10-14")]
    [InlineData("waive", null, "REQ-42", "2026-10-14T12:00:00+02:00")]
    [InlineData("waive", null, " ", "2026-10-14T12:00:00Z")]
    [InlineData("waive", "missing-result", null, "2026-10-14T12:00:00Z")]
    [InlineData("approve", null, "REQ-42", null)]
    [InlineData("reject", null, null, "2026-10-14T12:00:00Z")]
    public void Run_InvalidWaiverOptions_RefuseWithoutMutation(string kind, string? result, string? requirement, string? expires)
    {
        CreateRun();
        var before = Snapshot();
        Assert.Equal(ExitCodes.UsageError, Decide(kind, result: result, requirement: requirement, expires: expires).Exit);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot().OrderBy(p => p.Key));
    }

    [Fact]
    public void Create_RequiredOptionsAndSupportedInvocation_ParseAsExpected()
    {
        Assert.NotEmpty(DecideCommand.Create().Parse([_directory]).Errors);
        Assert.Empty(DecideCommand.Create().Parse([_directory, "--kind", "approve", "--by", "reviewer", "--reason", "Reviewed", "--json"]).Errors);
        Assert.Empty(DecideCommand.Create().Parse([_directory, "--kind", "reject", "--by", "reviewer", "--reason", "Reviewed", "--result", "result-1"]).Errors);
        Assert.Empty(DecideCommand.Create().Parse([_directory, "--kind", "waive", "--by", "reviewer", "--reason", "Reviewed", "--requirement", "REQ-42", "--expires", "2026-10-14T12:00:00Z"]).Errors);
    }
}
