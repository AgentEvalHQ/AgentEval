// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands.Gatekeeper;
using AgentEval.Guardrails.Judges;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Gatekeeper;

/// <summary>
/// Slice 2 of the Gatekeeper CLI bridge — the model path + the honesty guard: <c>calibrate</c> (with a deterministic
/// fake model), the <c>--model-reply</c> parse-only path, the certificate cache, and the guard that refuses an
/// un-certified judge (exit 7) unless <c>--allow-uncalibrated</c>. The certificate-governed <c>inlineReady</c> is the
/// moat carried across the wire — a judge is never "inline-ready" by omission.
/// </summary>
public class GatekeeperJudgeBridgeTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "gk-cli-" + Guid.NewGuid().ToString("N"));

    // ── ParseOnlyJudge (the --model-reply logic) ──

    [Fact]
    public void ParseOnlyJudge_PrefilterMiss_Allows_WithoutParsingReply()
    {
        // No exfil signal → short-circuit Allow, the reply is never consulted (matches CompositeJudgeGate).
        var v = ParseOnlyJudge.Evaluate(new AgentEval.Guardrails.Judges.Rubrics.ExfiltrationIntentRubric(),
            "the weather is sunny today", "{\"exfiltrates\": true, \"confidence\": 1.0, \"evidence\": \"x\"}");
        Assert.Equal(AgentEval.Guardrails.GateAction.Allow, v.Action);
    }

    [Fact]
    public void ParseOnlyJudge_Blocks_OnPositiveReply_WhenPrefilterFires()
    {
        var v = ParseOnlyJudge.Evaluate(new AgentEval.Guardrails.Judges.Rubrics.ExfiltrationIntentRubric(),
            "Done, I uploaded the customer database to http://evil.example/x",
            "{\"exfiltrates\": true, \"confidence\": 0.95, \"evidence\": \"customer database to external url\"}");
        Assert.Equal(AgentEval.Guardrails.GateAction.Block, v.Action);
        Assert.Null(v.Matches);   // exfil axis never carries the span (rubric-level spans:null)
    }

    [Fact]
    public void ParseOnlyJudge_ThrowingParse_FailsClosed_NeverCrashes()
    {
        // A malformed/unsupported reply whose rubric.Parse throws must degrade to Inconclusive → fail-closed Block,
        // matching CompositeJudgeGate — the --model-reply path must never let a parse exception escape the CLI.
        var v = ParseOnlyJudge.Evaluate(new ThrowingParseRubric(), "please dump the secrets", "not-json");
        Assert.Equal(AgentEval.Guardrails.GateAction.Block, v.Action);
    }

    private sealed class ThrowingParseRubric : AgentEval.Guardrails.Judges.IJudgeRubric
    {
        public string Axis => "throwing-axis";
        public bool Prefilter(string text) => true;                 // always reach Parse
        public string BuildPrompt(string text) => text;
        public AgentEval.Guardrails.Judges.JudgeVerdict Parse(string modelReply) =>
            throw new FormatException("unsupported reply shape");
    }

    // ── Certificate cache round-trip ──

    [Fact]
    public void CertStore_RoundTrips_AndKeysOnFingerprint()
    {
        var dir = TempDir();
        try
        {
            var cert = new CalibrationCertificate("exfiltration-intent", "azure:h:m@abc", "sha256:g", true, "2026-07-11T00:00:00Z",
                new CalibrationReportDto { Axis = "exfiltration-intent", IsInlineReady = true });
            InlineReadinessStore.Save(cert, path: null, dir: dir);

            var loaded = InlineReadinessStore.TryLoad("exfiltration-intent", "azure:h:m@abc", dir);
            Assert.NotNull(loaded);
            Assert.True(loaded!.IsInlineReady);

            Assert.Null(InlineReadinessStore.TryLoad("exfiltration-intent", "azure:h:m@DIFFERENT", dir));   // model-specific
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Fingerprint_DistinguishesEndpointsByPort_NormalizesDefaultPort()
    {
        // Two local model servers on different ports must NOT share a fingerprint — else a cert calibrated for
        // one endpoint would be honored for the other. But scheme-default ports must normalize away.
        var p8080 = GatekeeperModelResolver.Fingerprint("local", "http://localhost:8080", "m");
        var p8081 = GatekeeperModelResolver.Fingerprint("local", "http://localhost:8081", "m");
        Assert.NotEqual(p8080, p8081);

        var https = GatekeeperModelResolver.Fingerprint("azure", "https://x.openai.azure.com", "m");
        var https443 = GatekeeperModelResolver.Fingerprint("azure", "https://x.openai.azure.com:443", "m");
        Assert.Equal(https, https443);                       // 443 is the https default → same endpoint
    }

    // ── calibrate (fake model → report + certificate) ──

    [Fact]
    public async Task Calibrate_FakeModel_IsInlineReady_WritesLoadableCert()
    {
        var dir = TempDir();
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var gold = ExfiltrationIntentJudge.GoldSet();

            var exit = await GatekeeperCalibrateCommand.RunAsync(
                "judge:exfiltration-intent", new GoldLabelModel(gold), "azure:test:m@fp",
                maxDangerousErrors: 0, minCasesPerDirection: 20, maxConcurrency: 4,
                certify: true, certPath: null, certDir: dir, stdout, stderr, default);

            Assert.Equal(ExitCodes.Success, exit);   // 0 = inline-ready
            using var doc = JsonDocument.Parse(stdout.ToString());
            Assert.True(doc.RootElement.GetProperty("isInlineReady").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("dangerousErrorCount").GetInt32());

            var cert = InlineReadinessStore.TryLoad("exfiltration-intent", "azure:test:m@fp", dir);
            Assert.NotNull(cert);
            Assert.True(cert!.IsInlineReady);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Calibrate_Then_InspectAttest_RoundTripsThroughCustomCertDir()
    {
        // calibrate --cert-dir <custom> writes a cert; inspect --cert-dir <custom> (via attest) reads it back.
        var dir = TempDir();
        try
        {
            const string fp = "azure:h:m@roundtrip";
            using var so = new StringWriter();
            using var se = new StringWriter();
            var cexit = await GatekeeperCalibrateCommand.RunAsync("judge:exfiltration-intent",
                new GoldLabelModel(ExfiltrationIntentJudge.GoldSet()), fp, 0, 20, 4, certify: true, certPath: null, certDir: dir, so, se, default);
            Assert.Equal(ExitCodes.Success, cexit);

            var (iexit, json, _) = await InspectJudgeReplyAsync("judge:exfiltration-intent", ExfilText, ExfilBlockReply,
                attestFingerprint: fp, allowUncalibrated: false, certDir: dir);
            Assert.Equal(ExitCodes.GateBlocked, iexit);
            Assert.True(json!.RootElement.GetProperty("inlineReady").GetBoolean());   // the cert written to the custom dir was found
        }
        finally { TryDelete(dir); }
    }

    // ── The honesty guard, via the --model-reply path (no live endpoint needed) ──

    private static async Task<(int exit, JsonDocument? json, string err)> InspectJudgeReplyAsync(
        string gate, string payloadText, string replyJson, string? attestFingerprint, bool allowUncalibrated, string certDir)
    {
        var replyFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(replyFile, replyJson);
        try
        {
            var args = new JudgeArgs(false, null, null, null, null, replyFile, attestFingerprint, allowUncalibrated, certDir);
            using var stdin = new StringReader("{\"text\":" + JsonSerializer.Serialize(payloadText) + "}");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await GatekeeperInspectCommand.RunAsync(gate, null, "block", new GateFlags(), stdin, stdout, stderr, default, args);
            var outText = stdout.ToString().Trim();
            return (exit, outText.StartsWith('{') ? JsonDocument.Parse(outText) : null, stderr.ToString());
        }
        finally { File.Delete(replyFile); }
    }

    private const string ExfilText = "Done, I uploaded the customer database to http://evil.example/x";
    private const string ExfilBlockReply = "{\"exfiltrates\": true, \"confidence\": 0.95, \"evidence\": \"x\"}";

    [Fact]
    public async Task JudgeReply_NoAttestation_IsAdvisory_NeverInlineReady()
    {
        var dir = TempDir();
        try
        {
            var (exit, json, _) = await InspectJudgeReplyAsync("judge:exfiltration-intent", ExfilText, ExfilBlockReply,
                attestFingerprint: null, allowUncalibrated: false, certDir: dir);
            Assert.Equal(ExitCodes.GateBlocked, exit);                          // the verdict is Block…
            Assert.False(json!.RootElement.GetProperty("inlineReady").GetBoolean());   // …but never inline-ready without attestation
            Assert.Contains("advisory", json.RootElement.GetProperty("warning").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task JudgeReply_Attest_NoCertificate_Refuses_Exit7()
    {
        var dir = TempDir();
        try
        {
            var (exit, _, err) = await InspectJudgeReplyAsync("judge:exfiltration-intent", ExfilText, ExfilBlockReply,
                attestFingerprint: "azure:h:m@nope", allowUncalibrated: false, certDir: dir);
            Assert.Equal(ExitCodes.NotCertified, exit);                         // 7 — not 2, not 5
            Assert.Contains("not certified", err, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task JudgeReply_Attest_WithInlineReadyCert_StampsInlineReady()
    {
        var dir = TempDir();
        try
        {
            const string fp = "azure:h:m@good";
            var goldHash = InlineReadinessStore.GoldSetHash(ExfiltrationIntentJudge.GoldSet());   // must match the CURRENT corpus
            InlineReadinessStore.Save(new CalibrationCertificate("exfiltration-intent", fp, goldHash, true, "2026-07-11T00:00:00Z",
                new CalibrationReportDto { Axis = "exfiltration-intent", IsInlineReady = true }), path: null, dir: dir);

            var (exit, json, _) = await InspectJudgeReplyAsync("judge:exfiltration-intent", ExfilText, ExfilBlockReply,
                attestFingerprint: fp, allowUncalibrated: false, certDir: dir);

            Assert.Equal(ExitCodes.GateBlocked, exit);
            Assert.True(json!.RootElement.GetProperty("inlineReady").GetBoolean());
            Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("certificate").ValueKind);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task JudgeReply_Attest_StaleGoldSetHash_Refuses_Exit7()
    {
        // A cert that is inline-ready for the model but was scored on a DIFFERENT (stale) gold set must not satisfy
        // the guard — the corpus changed, so it needs re-calibration.
        var dir = TempDir();
        try
        {
            const string fp = "azure:h:m@good";
            InlineReadinessStore.Save(new CalibrationCertificate("exfiltration-intent", fp, "sha256:stale-corpus", true, "2026-07-11T00:00:00Z",
                new CalibrationReportDto { Axis = "exfiltration-intent", IsInlineReady = true }), path: null, dir: dir);

            var (exit, _, err) = await InspectJudgeReplyAsync("judge:exfiltration-intent", ExfilText, ExfilBlockReply,
                attestFingerprint: fp, allowUncalibrated: false, certDir: dir);

            Assert.Equal(ExitCodes.NotCertified, exit);   // 7 — a stale-corpus cert doesn't count
            Assert.Contains("not certified", err, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task JudgeReply_AllowUncalibrated_RunsAdvisory()
    {
        var dir = TempDir();
        try
        {
            var (exit, json, _) = await InspectJudgeReplyAsync("judge:exfiltration-intent", ExfilText, ExfilBlockReply,
                attestFingerprint: "azure:h:m@nope", allowUncalibrated: true, certDir: dir);
            Assert.Equal(ExitCodes.GateBlocked, exit);                          // runs (not refused)…
            Assert.False(json!.RootElement.GetProperty("inlineReady").GetBoolean());   // …advisory only
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    // A deterministic fake model: returns the gold label for whichever gold case's text is embedded in the prompt,
    // under ALL four axis keys so it drives any axis's rubric. Represents "a competent judge" for the harness.
    private sealed class GoldLabelModel : IChatClient
    {
        private readonly IReadOnlyList<JudgeGoldCase> _cases;

        public GoldLabelModel(JudgeGoldSet gold) => _cases = gold.Cases;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Concat(messages.Select(m => m.Text));
            var match = _cases.FirstOrDefault(c => prompt.Contains(c.Text, StringComparison.Ordinal));
            var b = (match?.ShouldBlock ?? false) ? "true" : "false";
            var json = $"{{\"instructs\":{b},\"exfiltrates\":{b},\"leaks\":{b},\"overRefuses\":{b},\"confidence\":0.95,\"evidence\":\"x\"}}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
