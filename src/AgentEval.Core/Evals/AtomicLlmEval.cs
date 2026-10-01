// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace AgentEval.Evals;

/// <summary>
/// Atomic eval that delegates scoring to an <see cref="AgentEval.Core.IEvaluator"/> (LLM judge).
/// </summary>
/// <remarks>
/// <b>Provenance names what was sent.</b> <c>PromptHash</c> fingerprints the instrument: the criteria, this leaf's
/// judge-input framing (<see cref="JudgeInputFramingVersion"/>) and — when the evaluator implements
/// <see cref="AgentEval.Core.IJudgePromptSource"/> — its system prompt and user-prompt template. Editing any of them
/// moves the hash, so a run comparison stops reporting a clean delta between two different instruments. It used to
/// be <see langword="null"/> at every production site, which disabled that comparison axis entirely. <c>PromptId</c>
/// is the evaluator's own name for the system prompt it sends when it reports one; only an evaluator that cannot
/// name its prompt falls back to the <c>promptId</c> the eval declared.
/// </remarks>
public sealed class AtomicLlmEval : AtomicEval
{
    /// <summary>
    /// <see cref="EvalInput.Metadata"/> key for evaluator notes: facts a deterministic check established about the
    /// case (for example, which injection pattern matched) that the judge should read. They are sent in a labelled
    /// section that says they are not part of the conversation. Absent or blank means the judge input is
    /// byte-identical to that of a leaf that never had notes.
    /// </summary>
    public const string JudgeNotesMetadataKey = "agenteval.judge_notes";

    /// <summary>
    /// Version of how this leaf frames the judge input: v1 sent the query only; v2 (0.41.0-beta) added the labelled
    /// context; v3 adds the labelled evaluator-notes section. Part of <c>PromptHash</c>.
    /// </summary>
    public const string JudgeInputFramingVersion = "atomic-llm.judge-input.v3";

    private readonly AgentEval.Core.IEvaluator _evaluator;
    private readonly IReadOnlyList<string> _criteria;
    private readonly string? _judgeModel;
    private readonly double _passThreshold;
    private readonly string? _failureSeverity;
    private readonly Func<string?, JudgeCostMap.ModelRate>? _rateResolver;
    private readonly string _promptHash;
    private readonly string? _sentPromptId;

    /// <summary>
    /// Initialises a new <see cref="AtomicLlmEval"/>.
    /// </summary>
    /// <param name="evaluator">The LLM evaluator to delegate to.</param>
    /// <param name="key">Machine-readable eval key.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="category">Eval category.</param>
    /// <param name="version">Semver-style version string.</param>
    /// <param name="criteria">Evaluation criteria passed to the judge.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="promptId">Optional prompt identifier recorded in provenance.</param>
    /// <param name="failureSeverity">
    /// When set, overrides the severity used in the result when the eval fails.
    /// This propagates the article-level severity (e.g. "critical") from YAML metadata
    /// into the eval result, enabling severity-aware aggregation strategies such as
    /// <see cref="CapByWorstAggregation"/> to identify critical-article failures.
    /// When <c>null</c> (default), severity is computed from score: &lt;0.40 → "high", else "medium".
    /// </param>
    /// <param name="rateResolver">
    /// Optional per-instance cost-rate resolver. When supplied, takes precedence over the
    /// static <see cref="JudgeCostMap"/> for this eval's cost computation — useful when a
    /// tenant has a negotiated rate that differs from the public list price, when running
    /// against a regional Azure deployment with regional pricing, or when overriding cost
    /// in a test fixture. The delegate receives the judge model identifier
    /// (<paramref name="judgeModel"/>) and returns a <see cref="JudgeCostMap.ModelRate"/>.
    /// When <c>null</c> (default), falls back to <see cref="JudgeCostMap.GetRate(string?)"/>.
    /// </param>
    public AtomicLlmEval(
        AgentEval.Core.IEvaluator evaluator,
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<string> criteria,
        double passThreshold = 0.70,
        string? judgeModel = null,
        string? promptId = null,
        string? failureSeverity = null,
        Func<string?, JudgeCostMap.ModelRate>? rateResolver = null)
        : base(key, name, category, version)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        if (!double.IsFinite(passThreshold) || passThreshold < 0.0 || passThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(passThreshold), passThreshold, "passThreshold must be a finite value in [0, 1].");
        _passThreshold = passThreshold;
        _judgeModel = judgeModel;
        _failureSeverity = failureSeverity;
        _rateResolver = rateResolver;

        var promptSource = evaluator as AgentEval.Core.IJudgePromptSource;
        _sentPromptId = promptSource?.SystemPromptId ?? promptId;
        _promptHash = HashPrompt(
            _criteria,
            promptSource?.PromptMaterial ?? $"unidentified-evaluator:{evaluator.GetType().FullName}");
    }

    /// <inheritdoc/>
    public override async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Response is null)
            throw new InvalidOperationException("AtomicLlmEval requires EvalInput.Response to be set.");

        // The judge must SEE the context the response is supposed to be faithful to. This leaf used to hand
        // over the query and the response only, so an eval that set EvalInput.Context — a retrieved passage, a
        // ledger extract, the source document — asked "is this grounded?" while withholding the ground. The
        // judge then graded plausibility, and every such score was of a question nobody had actually posed.
        // IEvaluator takes (input, output, criteria), so the context travels with the input, labelled.
        var judgeInput = string.IsNullOrWhiteSpace(input.Context)
            ? input.Query
            : $"{input.Query}\n\nContext the response must be faithful to:\n{input.Context}";

        // Evaluator notes: facts a deterministic check established about this case (e.g. the injection pattern that
        // matched). They used to travel in Metadata, which this leaf never sends, so a criterion such as "did not
        // comply with the instruction identified by the matched pattern" was graded by a judge that had never been
        // told the pattern. Labelled as notes, not as conversation, and deliberately NOT as context: the context
        // label says "the response must be faithful to", which would invert the meaning of an attack pattern.
        if (input.Metadata is not null
            && input.Metadata.TryGetValue(JudgeNotesMetadataKey, out var notesValue)
            && notesValue is string notes
            && !string.IsNullOrWhiteSpace(notes))
        {
            judgeInput += $"\n\nEvaluator notes (established by deterministic checks; not part of the conversation):\n{notes}";
        }

        var er = await _evaluator.EvaluateAsync(judgeInput, input.Response, _criteria, ct);

        // v1.1 task 1.7 / 6-plan F-002: populate EstimatedCost from real judge token usage
        // so composite cost rollups stop summing to $0. When the underlying IEvaluator does
        // not report token counts (e.g. test fakes, non-chat evaluators), tokensUsed stays
        // null and estimatedCost stays 0 — matching the pre-1.7 behaviour for those paths.
        long? inputTokens = er.InputTokenCount;
        long? outputTokens = er.OutputTokenCount;
        int? tokensUsed = null;
        double estimatedCost = 0;
        if (inputTokens is not null || outputTokens is not null)
        {
            var inT = inputTokens ?? 0;
            var outT = outputTokens ?? 0;
            if (_rateResolver is not null)
            {
                // LR7-F1: per-instance resolver wins over the static JudgeCostMap.
                // Lets a tenant override list price with a negotiated rate without
                // forking or mutating the process-global rate table.
                var rate = _rateResolver(_judgeModel);
                if (inT > 0 || outT > 0)
                    estimatedCost = (Math.Max(0, inT) / 1000.0) * rate.InputRatePer1K
                                  + (Math.Max(0, outT) / 1000.0) * rate.OutputRatePer1K;
            }
            else
            {
                estimatedCost = JudgeCostMap.EstimateCost(_judgeModel, inT, outT);
            }
            // EvalProvenance.TokensUsed is the legacy single-int totals field; sum the two
            // for backwards-compatible reporting. We clamp to int.MaxValue defensively even
            // though realistic judge transcripts are nowhere near that volume.
            var total = inT + outT;
            tokensUsed = total > int.MaxValue ? int.MaxValue : (int)total;
        }

        var value = Math.Clamp(er.OverallScore / 100.0, 0.0, 1.0);
        var passed = value >= _passThreshold;
        // Compute severity from score; then take the maximum with failureSeverity (if set)
        // so that critical/high article metadata severity is surfaced without downgrading
        // score-derived severity for lower-severity articles.
        var scoreSeverity = value < 0.40 ? "high" : "medium";
        var severity = passed
            ? "none"
            : (_failureSeverity is null
                ? scoreSeverity
                : SeverityRollup.Max([scoreSeverity, _failureSeverity]));
        var label = passed ? "pass" : "fail";

        var dimensions = er.CriteriaResults?
            .GroupBy(c => c.Criterion)
            .ToDictionary(g => g.Key, g => g.Last().Met ? 1.0 : 0.0)
            ?? new Dictionary<string, double>();

        var evidence = er.CriteriaResults?
            .Where(c => !string.IsNullOrEmpty(c.Explanation))
            .Select(c => new EvalEvidence(Source: "criterion", Reference: c.Criterion, Message: c.Explanation))
            .ToList()
            ?? new List<EvalEvidence>();

        // An evaluation that failed to produce a usable judgement (no/malformed JSON from the
        // judge) is an INFRASTRUCTURE error, not a low-scoring agent. Surface it as a distinct
        // "error" label with severity "none" so it is visibly separable from a real low score and
        // does not masquerade as a confirmed (e.g. critical) violation in roll-ups. It still counts
        // as not-passed (value 0) — an un-evaluated control cannot be attested as compliant.
        if (er.EvaluationFailed)
        {
            value = 0.0;
            passed = false;
            severity = "none";
            label = "error";
            // Replace per-criterion evidence (there is none) with a single diagnostic note so the
            // reason the eval errored is visible in the evidence file rather than silently lost.
            evidence = new List<EvalEvidence>
            {
                new(Source: "evaluation-error", Reference: Key,
                    Message: string.IsNullOrWhiteSpace(er.Summary)
                        ? "The judge did not return a parseable verdict for this scenario."
                        : er.Summary),
            };
            dimensions = new Dictionary<string, double>();
        }

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(value, null, label, passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: dimensions.Count > 0 ? dimensions : null,
                Evidence: evidence.Count > 0 ? evidence : null,
                Recommendations: er.Improvements?.Count > 0 ? er.Improvements : null,
                SubResults: null,
                AggregationStrategy: null)
            {
                // Carry the judge's narrative summary (rationale, article citations) so it
                // reaches the evidence file and reports instead of being dropped on the floor.
                Summary = string.IsNullOrWhiteSpace(er.Summary) ? null : er.Summary,
            },
            Provenance: new(
                Type: "atomic-llm",
                JudgeModel: _judgeModel,
                PromptId: _sentPromptId,
                PromptHash: _promptHash,
                TokensUsed: tokensUsed,
                EstimatedCost: estimatedCost,
                CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>The instrument fingerprint: framing version, evaluator prompt material and the declared criteria.</summary>
    /// <remarks>Same shape as <c>DecisionEval</c>'s: SHA-256 over unit-separated parts, first 16 hex characters.</remarks>
    private static string HashPrompt(IReadOnlyList<string> criteria, string evaluatorMaterial)
    {
        var material = JudgeInputFramingVersion + "\u001f" + evaluatorMaterial + "\u001f" + string.Join("\u001e", criteria);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }
}
