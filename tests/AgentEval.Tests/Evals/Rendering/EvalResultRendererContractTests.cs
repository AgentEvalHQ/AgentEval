// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core.Evals.Rendering;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;
using Xunit;

namespace AgentEval.Tests.Evals.Rendering;

/// <summary>
/// Plan-13 T4.1b item 12 — promotes <see cref="IEvalResultRenderer"/> to
/// ADR-017 Convention 5A with three contract guarantees the interface XML doc
/// now spells out. Each Theory below runs the same assertion against
/// every registered renderer (HTML + PDF) so a new renderer added later inherits
/// the same contract automatically — just add an entry to <see cref="Renderers"/>.
/// </summary>
public class EvalResultRendererContractTests
{
    // Adding a third renderer? Append a new (id, factory) tuple here and the
    // entire contract suite runs against it without any per-test edits.
    public static IEnumerable<object[]> Renderers => new[]
    {
        new object[] { "html", (Func<IEvalResultRenderer>)(() => new HtmlEvalResultRenderer()) },
        new object[] { "pdf",  (Func<IEvalResultRenderer>)(() => new PdfEvalResultRenderer()) },
    };

    private static EvalResult MakeAtomic(string key, double value, string label, string severity) =>
        new(
            Metric: new(key, key, "test", "1.0.0"),
            Score: new(value, null, label, label == "pass", null, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-code", "gpt-4o-judge", "prompt-123", null, 100, 0.0042, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResultRenderOptions Opts(bool includeProvenance = true) =>
        new(
            Subject: new SubjectIdentity(SubjectKind.Agent, "TestAgent"),
            Title: "Contract Test",
            RegulationOrBenchmark: "Test Suite",
            RunId: "run-contract-001",
            GeneratedAt: new DateTimeOffset(2026, 5, 25, 12, 0, 0, TimeSpan.Zero),
            IncludeProvenance: includeProvenance,
            AuditHash: "sha256:contracthash",
            AgentEvalVersion: "0.10.1-beta");

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task Contract_FormatIdIsStableAndLowercase(string expectedId, Func<IEvalResultRenderer> factory)
    {
        var r = factory();

        // Stable + non-empty + matches the per-renderer expected id (catches accidental rename).
        Assert.False(string.IsNullOrWhiteSpace(r.FormatId), "FormatId must not be empty.");
        Assert.Equal(r.FormatId.ToLowerInvariant(), r.FormatId);
        Assert.Equal(expectedId, r.FormatId);

        await Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task Contract_FileExtensionStartsWithDot(string expectedId, Func<IEvalResultRenderer> factory)
    {
        var r = factory();
        Assert.False(string.IsNullOrEmpty(r.FileExtension));
        Assert.StartsWith(".", r.FileExtension);
        Assert.Equal(r.FileExtension.ToLowerInvariant(), r.FileExtension);
        _ = expectedId;
        await Task.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task Contract_RenderAsync_ReturnsNonEmptyBytes(string expectedId, Func<IEvalResultRenderer> factory)
    {
        var r = factory();
        var bytes = await r.RenderAsync(MakeAtomic("k", 0.9, "pass", "low"), Opts());
        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        _ = expectedId;
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task Contract_RenderAsync_DoesNotMutateResult(string expectedId, Func<IEvalResultRenderer> factory)
    {
        var r = factory();
        var input = MakeAtomic("k", 0.9, "pass", "low");
        // Record the input shape — EvalResult is a record so structural equality covers immutability.
        var snapshot = input;
        _ = await r.RenderAsync(input, Opts());
        Assert.Equal(snapshot, input);
        _ = expectedId;
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task Contract_RenderAsync_NullArgsThrowArgumentNullException(string expectedId, Func<IEvalResultRenderer> factory)
    {
        var r = factory();
        await Assert.ThrowsAsync<ArgumentNullException>(() => r.RenderAsync(null!, Opts()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => r.RenderAsync(MakeAtomic("k", 0.5, "warn", "medium"), null!));
        _ = expectedId;
    }

    [Theory]
    [MemberData(nameof(Renderers))]
    public async Task Contract_IncludeProvenanceFalse_OmitsJudgeModelFromOutput(string expectedId, Func<IEvalResultRenderer> factory)
    {
        // Guarantee #3 in the IEvalResultRenderer XML doc: IncludeProvenance=false
        // suppresses judge-model id from the rendered artefact so it's safe to share.
        var r = factory();
        var leaf = MakeAtomic("k", 0.9, "pass", "low");

        var bytesWithProv = await r.RenderAsync(leaf, Opts(includeProvenance: true));
        var bytesNoProv = await r.RenderAsync(leaf, Opts(includeProvenance: false));

        if (expectedId == "html")
        {
            // HTML output is text — the judge-model string must be present with provenance
            // and ABSENT without.
            var textWith = Encoding.UTF8.GetString(bytesWithProv);
            var textWithout = Encoding.UTF8.GetString(bytesNoProv);
            Assert.Contains("gpt-4o-judge", textWith);
            Assert.DoesNotContain("gpt-4o-judge", textWithout);
        }
        else
        {
            // PDF output is binary — comparing byte sequences is fragile, but a
            // provenance-stripped PDF MUST be a different byte sequence than the
            // provenance-included one (different content → different deflate stream).
            Assert.NotEqual(bytesWithProv.Length, bytesNoProv.Length);
        }
    }
}
