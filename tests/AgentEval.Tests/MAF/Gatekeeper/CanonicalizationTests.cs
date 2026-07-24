// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>F-D + P1-2 (§6/§13): ArgumentCanonicalizer and the deterministic gates that opt into it.</summary>
public class CanonicalizationTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // ── F-D: ArgumentCanonicalizer ──

    [Fact]
    public void RawIsAlwaysTheFirstProjection()
    {
        var p = ArgumentCanonicalizer.Canonicalize("plain text with no encoding");
        Assert.Equal("plain text with no encoding", p[0]);
    }

    [Fact]
    public void PercentEncoding_Decoded()
        => Assert.Contains(ArgumentCanonicalizer.Canonicalize("http%3A%2F%2Fevil.com%2Fx"), s => s.Contains("http://evil.com/x", StringComparison.Ordinal));

    [Fact]
    public void HtmlEntities_Decoded()
        => Assert.Contains(ArgumentCanonicalizer.Canonicalize("&lt;script&gt;alert(1)&lt;/script&gt;"), s => s.Contains("<script>", StringComparison.Ordinal));

    [Fact]
    public void UnicodeEscapes_Decoded()
        => Assert.Contains(ArgumentCanonicalizer.Canonicalize(@"../../etc/passwd"), s => s.Contains("../../etc/passwd", StringComparison.Ordinal));

    [Fact]
    public void Base64Candidate_DecodedToPrintableText()
        => Assert.Contains(ArgumentCanonicalizer.Canonicalize($"payload={B64("exfiltrate-this-secret")}"), s => s.Contains("exfiltrate-this-secret", StringComparison.Ordinal));

    [Fact]
    public void PlainAlphanumerics_NotFalseDecodedAsBase64()
    {
        // A short/non-multiple-of-4 alphanumeric run isn't treated as base64; a plain string yields only itself.
        var p = ArgumentCanonicalizer.Canonicalize("orderid ABC123 lookup");
        Assert.Single(p);
    }

    [Fact]
    public void OversizedProjection_DroppedToBoundDecodeBombs()
    {
        // A projection that would exceed maxLength is dropped — the raw still returns.
        var raw = "%41" + new string('B', 100);   // decodes ~same length; force a tiny cap
        var p = ArgumentCanonicalizer.Canonicalize(raw, maxLength: 10);
        Assert.All(p, s => Assert.True(s.Length <= 10));
    }

    // ── ArgumentPatternGate (§13) ──

    private static GatedToolCall Args(string tool, params (string k, object? v)[] args)
        => new(tool, args.ToDictionary(a => a.k, a => a.v), "A", 0, 0, 1, false, null);

    [Fact]
    public async Task ArgumentPattern_EncodedTraversal_CaughtOnlyWithCanonicalize()
    {
        var pattern = new Regex(@"\.\./", RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var encoded = Args("read_file", ("path", "..%2f..%2fetc%2fpasswd"));

        var plain = new ArgumentPatternGate(pattern);                       // raw surface only
        Assert.Equal(ToolGateAction.Allow, (await plain.InspectAsync(encoded)).Action);

        var canon = new ArgumentPatternGate(pattern, canonicalize: true);   // + decoded projections
        Assert.Equal(ToolGateAction.Block, (await canon.InspectAsync(encoded)).Action);
    }

    // ── DomainAllowListGate (§13) ──

    [Fact]
    public async Task DomainAllowList_PercentEncodedOffListUrl_CaughtOnlyWithCanonicalize()
    {
        var encoded = Args("fetch", ("url", "http%3A%2F%2Fevil.com%2Fcollect"));

        var plain = new DomainAllowListGate(["example.com"]);
        Assert.Equal(ToolGateAction.Allow, (await plain.InspectAsync(encoded)).Action);

        var canon = new DomainAllowListGate(["example.com"], canonicalize: true);
        Assert.Equal(ToolGateAction.Block, (await canon.InspectAsync(encoded)).Action);
    }

    // ── TaintTrackingGate (§6 case-fold + canonicalize) ──

    private static ChatMessage AssistantCall(string callId, string name)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);

    private static ChatMessage ToolResult(string callId, object? result)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private static GatedToolCall Sink(string tool, string body, params ChatMessage[] history)
        => new(tool, new Dictionary<string, object?> { ["body"] = body }, "A", 0, 0, 1, false, history);

    [Fact]
    public async Task Taint_CaseFoldedTaintedValue_NowBlocked()
    {
        // §6: a re-cased copy of the tainted value must not slip past (source UPPER, sink lower).
        var gate = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        var call = Sink("http_post", "here: demo-9a8b7c6d5e4f",
            AssistantCall("c1", "read_secrets"),
            ToolResult("c1", "API_KEY=DEMO-9A8B7C6D5E4F"));

        Assert.Equal(ToolGateAction.Block, (await gate.InspectAsync(call)).Action);
    }

    [Fact]
    public async Task Taint_Base64EncodedTaintedValue_CaughtOnlyWithCanonicalize()
    {
        var secret = "exfiltrate-this-secret";
        var source = AssistantCall("c1", "read_secrets");
        var result = ToolResult("c1", $"SECRET={secret}");

        var plain = new TaintTrackingGate(["read_secrets"], ["http_post"]);
        Assert.Equal(ToolGateAction.Allow, (await plain.InspectAsync(Sink("http_post", B64(secret), source, result))).Action);

        var canon = new TaintTrackingGate(["read_secrets"], ["http_post"], canonicalize: true);
        Assert.Equal(ToolGateAction.Block, (await canon.InspectAsync(Sink("http_post", B64(secret), source, result))).Action);
    }
}
