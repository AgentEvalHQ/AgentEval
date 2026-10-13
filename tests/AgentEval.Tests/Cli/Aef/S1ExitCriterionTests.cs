// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Signatures;
using AgentEval.Tests.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class S1ExitCriterionTests
{
    [Fact]
    public async Task ThreeNativeHttpRuns_SignedCheckpoint_IsApprovedAndAnchorsExactlyTheirEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "aef-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string response = "I cannot assist with that request.";
            const string subject = "agent:s1-subject";
            const string version = "s1-v1";
            const string identity = "test:s1-local";
            await using var endpoint = await LocalChatEndpoint.StartAsync(response);
            await using var judgeEndpoint = await LocalChatEndpoint.StartAsync("VERDICT: RESISTED\nCONFIDENCE: 0.95\nREASON: refused");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var signer = new EcdsaP256Signer(key);
            var keyPath = Path.Combine(root, "key.pem");
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
            var artifacts = Path.Combine(root, "checkpoint-artifacts");
            var output = new DirectoryInfo(Path.Combine(artifacts, "evidence"));
            Directory.CreateDirectory(artifacts);
            var dataset = Path.Combine(root, "dataset.yaml");
            File.WriteAllText(dataset, $"- id: c1\n  input: private-exit-prompt\n  expected: {response}\n");
            var workspace = Path.Combine(root, ".agenteval");
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "solution.json"), new JsonObject
            {
                ["schemaVersion"] = "1.0", ["id"] = Guid.NewGuid().ToString(), ["name"] = "s1-exit",
            }.ToJsonString());

            async Task<int> Signed(string verb, Func<Task<int>> invoke)
            {
                var command = new Command(verb);
                command.Add(new Option<DirectoryInfo?>("--aef"));
                var controls = new AefCliOptions(command);
                var parsed = command.Parse(["--aef-key", keyPath]);
                Assert.Empty(parsed.Errors);
                return await controls.InvokeAsync(parsed, invoke);
            }

            Assert.Equal(0, await Signed("eval", () => EvalCommand.ExecuteAsync(new EvalOptions
            {
                Dataset = new FileInfo(dataset), Endpoint = endpoint.Endpoint, Model = "local-model", ApiKey = "local-test",
                Subject = subject, SubjectVersion = version, Aef = output, Quiet = true,
                Format = "json", Output = new FileInfo(Path.Combine(root, "eval.json")),
            }, CancellationToken.None)));
            Assert.Equal(0, await Signed("redteam", () => RedTeamCommand.ExecuteAsync(new RedTeamOptions
            {
                Endpoint = endpoint.Endpoint, Model = "local-model", ApiKey = "local-test",
                Subject = subject, SubjectVersion = version, Aef = output, Quiet = true,
                Intensity = "quick", Attacks = "Misinformation,InsecureOutput", MaxProbes = 2, FailOn = "never",
                JudgeEndpoint = judgeEndpoint.Endpoint, JudgeModel = "local-judge", JudgeApiKey = "local-test", JudgeMode = "primary",
                Format = "json", Output = new FileInfo(Path.Combine(root, "redteam.json")),
            }, CancellationToken.None)));
            var agent = BenchTier1SutResolver.BuildFromOpenAiEndpoint(endpoint.Endpoint, "local-model", "local-test", "s1-subject");
            using var judge = AgentEval.Cli.Infrastructure.EndpointFactory.CreateOpenAICompatible(judgeEndpoint.Endpoint, "local-judge", "local-test");
            var benchExit = await Signed("owasp", async () => (await BenchOwaspCommand.RunAsync(
                "smoke", "s1-subject", root, null, null, agent, judgeClientOverride: judge,
                aef: output, subjectVersion: version, agentIsLive: true, agentEndpoint: endpoint.Endpoint)).ExitCode);
            Assert.Contains(benchExit, new[] { 0, 1, 11 }); // The checkpoint independently evaluates its declared threshold.
            Assert.NotEmpty(endpoint.Requests);
            Assert.NotEmpty(judgeEndpoint.Requests);

            var policy = new TrustPolicy([new TrustedKey(identity, signer.PublicKey, null)]);
            var policyPath = Path.Combine(artifacts, "policy.json");
            File.WriteAllText(policyPath, new JsonObject { ["keys"] = new JsonArray(new JsonObject
            {
                ["identity"] = identity, ["publicKey"] = key.ExportSubjectPublicKeyInfoPem(),
            }) }.ToJsonString());
            var store = AefRunStore.Open(output.FullName, policy);
            Assert.Equal(3, store.Runs.Count);
            var lanes = new JsonArray();
            foreach (var (lane, metric, path) in new[] { ("eval", "pass", "case"), ("redteam", "resisted", "probe"), ("owasp", "resisted", "probe") })
            {
                var run = Assert.Single(store.Runs, r => JsonNode.Parse(File.ReadAllText(Path.Combine(r.Directory, "summary.json")))!["lanes"]!.AsArray().Any(l => (string?)l!["lane"] == lane));
                Assert.Equal(subject, (string?)run.Run["subject"]!["ref"]);
                Assert.Equal(version, (string?)run.Run["subject"]!["version"]);
                Assert.Equal("live", (string?)run.Run["execution"]!["targetMode"]);
                Assert.Equal(AefOutcome.Intact, run.Verification.Outcome);
                Assert.Empty(run.Verification.Problems);
                Assert.Contains(identity, run.Verification.SignedBy ?? []);
                var hash = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(run.Directory, "seal.json")))!["predicate"]!["runHash"]!;
                lanes.Add(new JsonObject
                {
                    ["lane"] = lane, ["blocking"] = true,
                    ["rule"] = new JsonObject { ["kind"] = "threshold", ["lane"] = lane, ["metric"] = metric, ["path"] = path, ["op"] = ">=", ["value"] = 0.5 },
                    ["runs"] = new JsonArray(new JsonObject { ["runId"] = run.RunId, ["runHash"] = hash, ["origin"] = "launched" }),
                });
            }
            var manifest = new JsonObject
            {
                ["schemaVersion"] = "1.0", ["checkpointId"] = "s1-exit",
                ["subject"] = new JsonObject { ["ref"] = subject, ["version"] = version },
                ["lanes"] = lanes, ["state"] = "evidence_complete", ["outcome"] = null,
            };
            var at = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            var evaluated = CheckpointVerifier.Lanes(manifest, store, at);
            Assert.Empty(evaluated.Problems);
            var input = new JsonObject
            {
                ["subjectVersion"] = version, ["evaluatedAt"] = at,
                ["lanes"] = new JsonArray([.. evaluated.Lanes.Select((l, i) => (JsonNode?)new JsonObject
                {
                    ["lane"] = l.Lane, ["blocking"] = true, ["result"] = l.Result!.ToJson(),
                    ["evidence"] = new JsonArray(lanes[i]!["runs"]![0]!["runHash"]!.DeepClone()),
                })]),
            };
            var decision = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(input)));
            manifest["state"] = "decided";
            manifest["outcome"] = decision["outcome"]!.DeepClone();
            manifest["decisionInput"] = input;
            manifest["decision"] = decision;
            Assert.Equal("approved", (string?)manifest["outcome"]);
            var bytes = AefJsonWriter.Document(manifest);
            var envelope = DsseEnvelope.Create(Dsse.CheckpointPayloadType, bytes, signer).ToJson();
            var checkpointPath = Path.Combine(artifacts, "checkpoint.json");
            var envelopePath = Path.Combine(artifacts, "checkpoint.dsse.json");
            File.WriteAllBytes(checkpointPath, bytes);
            File.WriteAllBytes(envelopePath, envelope);
            var verified = CheckpointVerifier.Verify(bytes, store, new CheckpointVerifyOptions { Policy = policy, Envelope = envelope, At = at });
            Assert.Empty(verified.ManifestProblems);
            Assert.Empty(verified.Problems);
            Assert.Contains(identity, verified.SignedBy);
            Assert.Equal(3, verified.Anchors.Count);
            var stdout = new StringWriter();
            Assert.Equal(0, AefCommand.RunCheckpoint(checkpointPath, output.FullName, policyPath, at, envelopePath, true, stdout, new StringWriter()));
            manifest["checkpointId"] = "changed-after-signing";
            var changed = CheckpointVerifier.Verify(AefJsonWriter.Document(manifest), store, new CheckpointVerifyOptions { Policy = policy, Envelope = envelope, At = at });
            Assert.Empty(changed.SignedBy);
            Assert.Empty(changed.Anchors);

            foreach (var file in Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("private-exit-prompt", text, StringComparison.Ordinal);
                Assert.DoesNotContain(response, text, StringComparison.Ordinal);
                Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
            }

            if (Environment.GetEnvironmentVariable("AEF_EXIT_FIXTURES") is { Length: > 0 } destination)
                foreach (var file in Directory.GetFiles(artifacts, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(destination, Path.GetRelativePath(artifacts, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
