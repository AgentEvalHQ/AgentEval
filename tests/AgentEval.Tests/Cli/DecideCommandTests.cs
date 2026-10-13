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

    private (int Exit, string Output) Decide(string kind = "approve", string identity = "reviewer-1", string reason = "Reviewed evidence")
    {
        var output = new StringWriter();
        var exit = DecideCommand.Run(_directory, kind, identity, reason, true, output, TextWriter.Null);
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
    [InlineData("waive", "reviewer", "reason")]
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
    public void Create_RequiredOptionsAndSupportedInvocation_ParseAsExpected()
    {
        Assert.NotEmpty(DecideCommand.Create().Parse([_directory]).Errors);
        Assert.Empty(DecideCommand.Create().Parse([_directory, "--kind", "approve", "--by", "reviewer", "--reason", "Reviewed", "--json"]).Errors);
    }
}
