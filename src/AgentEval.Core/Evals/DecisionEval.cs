// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.Decisions;

namespace AgentEval.Evals;

/// <summary>
/// Atomic eval that asks a decision model ONE yes/no question about the input and scores the
/// probability it answers with. The third evaluator kind beside <see cref="AtomicCodeEval"/> and
/// <see cref="AtomicLlmEval"/> (ADR-033): a deterministic check proves, a generative judge reasons,
/// a decision model puts a calibrated probability on a narrow claim.
/// </summary>
/// <remarks>
/// <para>
/// <b>The probability is the score.</b> <c>Score.Value</c> is <c>P(yes)</c> exactly as the model
/// returned it, and <c>Passed</c> is <c>P(yes) &gt;= passThreshold</c>. The raw value is also written to
/// <c>Details.Dimensions["decision.probability_yes"]</c> so a later threshold sweep, calibration run or
/// ROC analysis can re-read it without re-asking. Nothing here rounds 0.69 to "fail" and throws the
/// 0.69 away.
/// </para>
/// <para>
/// <b><c>Score.Confidence</c> is <see langword="null"/>, on purpose.</b> A yes/no (noul) answer in
/// this protocol is a single probability; the provider attaches a separate confidence only to
/// choice and score answers. Manufacturing one here — from the distance to 0.5, say — would be a
/// number nobody measured wearing the name of one that is.
/// </para>
/// <para>
/// <b>Provenance is truthful about what answered.</b> <c>Provenance.Type</c> is
/// <see cref="ProvenanceType"/>, not <c>atomic-llm</c>, because "GPT wrote 87/100 in generated JSON" and
/// "Jev returned P(yes)=0.87" are different kinds of evidence even when they land on the same value.
/// <c>Provenance.JudgeModel</c> is the model id the PROVIDER reports in its reply — the resolved build
/// behind any alias — not the alias that was requested.
/// </para>
/// <para>
/// <b>A transport failure is not a verdict.</b> A <see cref="DecisionClientException"/> propagates.
/// An eval that could not ask its question has measured nothing, and a 0.0 in its place would read
/// as "the model looked and found nothing good".
/// </para>
/// <para>
/// This leaf is <b>not calibrated</b> on any AgentEval dataset yet. Use it beside an
/// <see cref="AtomicLlmEval"/> inside a <see cref="CompositeEval"/> (independent evidence), or in
/// shadow — not as a gate that decides whether the stronger judge runs — until it is.
/// </para>
/// </remarks>
public sealed class DecisionEval : AtomicEval
{
    /// <summary>The <c>Provenance.Type</c> this eval writes. Listed in the v1 result schema's enum.</summary>
    public const string ProvenanceType = "atomic-decision";

    /// <summary>The <c>Details.Dimensions</c> key under which the raw <c>P(yes)</c> is carried.</summary>
    public const string ProbabilityDimension = "decision.probability_yes";

    private readonly IDecisionClient _client;
    private readonly string _instructions;
    private readonly string? _trueCriteria;
    private readonly string? _falseCriteria;
    private readonly string? _model;
    private readonly double _passThreshold;
    private readonly string? _failureSeverity;
    private readonly Func<EvalInput, object>? _stateProjector;
    private readonly Func<string?, JudgeCostMap.ModelRate>? _rateResolver;
    private readonly string _promptHash;

    /// <summary>Initialises a new <see cref="DecisionEval"/>.</summary>
    /// <param name="client">The decision-model transport.</param>
    /// <param name="key">Machine-readable eval key. Also the question id on the wire.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="category">Eval category.</param>
    /// <param name="version">Semver-style version string.</param>
    /// <param name="instructions">The yes/no question the model answers about the input.</param>
    /// <param name="passThreshold">
    /// <c>P(yes)</c> at or above which the eval passes. Defaults to 0.70, matching
    /// <see cref="AtomicLlmEval"/>. Nothing about this default is calibrated; it is a starting point.
    /// </param>
    /// <param name="trueCriteria">Optional: what a <c>yes</c> means, to sharpen the boundary.</param>
    /// <param name="falseCriteria">Optional: what a <c>no</c> means.</param>
    /// <param name="model">Optional model override; <see langword="null"/> uses the client's default.</param>
    /// <param name="failureSeverity">
    /// When set, the severity a FAILED result carries is at least this (via <see cref="SeverityRollup.Max"/>),
    /// so a critical criterion is not downgraded by a middling probability. When <see langword="null"/>,
    /// severity is computed from the score: <c>&lt; 0.40 → "high"</c>, else <c>"medium"</c>.
    /// </param>
    /// <param name="stateProjector">
    /// Optional: builds the state object the model judges from the <see cref="EvalInput"/>. The default
    /// sends <c>query</c>, <c>response</c>, <c>context</c>, <c>groundTruth</c> and <c>systemMessage</c>
    /// (nulls omitted). Supply one to send less, or to add tool calls the default leaves out.
    /// </param>
    /// <param name="rateResolver">
    /// Optional per-instance cost-rate resolver, used only when the provider does not report a cost
    /// itself. When <see langword="null"/>, falls back to <see cref="JudgeCostMap.GetRate(string?)"/>.
    /// </param>
    public DecisionEval(
        IDecisionClient client,
        string key,
        string name,
        string category,
        string version,
        string instructions,
        double passThreshold = 0.70,
        string? trueCriteria = null,
        string? falseCriteria = null,
        string? model = null,
        string? failureSeverity = null,
        Func<EvalInput, object>? stateProjector = null,
        Func<string?, JudgeCostMap.ModelRate>? rateResolver = null)
        : base(key, name, category, version)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        if (!double.IsFinite(passThreshold) || passThreshold < 0.0 || passThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(passThreshold), passThreshold, "passThreshold must be a finite value in [0, 1].");

        _instructions = instructions;
        _trueCriteria = trueCriteria;
        _falseCriteria = falseCriteria;
        _model = model;
        _passThreshold = passThreshold;
        _failureSeverity = failureSeverity;
        _stateProjector = stateProjector;
        _rateResolver = rateResolver;
        _promptHash = HashPrompt(instructions, trueCriteria, falseCriteria);
    }

    /// <summary>The yes/no question this eval asks.</summary>
    public string Instructions => _instructions;

    /// <summary><c>P(yes)</c> at or above which the eval passes.</summary>
    public double PassThreshold => _passThreshold;

    /// <inheritdoc/>
    public override async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Response is null)
            throw new InvalidOperationException("DecisionEval requires EvalInput.Response to be set.");

        var state = _stateProjector?.Invoke(input) ?? DefaultState(input);
        var request = new DecisionRequest(
            state,
            new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
            {
                [Key] = new BinaryQuestion(_instructions, _trueCriteria, _falseCriteria),
            },
            _model);

        var response = await _client.DecideAsync(request, ct).ConfigureAwait(false);

        // The transport guarantees the pairing; this guards a hand-rolled IDecisionClient that does not.
        if (!response.Answers.TryGetValue(Key, out var answer) || answer is not BinaryAnswer noul)
        {
            throw new DecisionClientException(
                DecisionFailureKind.InvalidResponse,
                $"The decision client returned no yes/no answer for question '{Key}'.");
        }

        var value = noul.TrueProbability;
        var passed = value >= _passThreshold;
        var label = passed ? "pass" : "fail";
        var scoreSeverity = value < 0.40 ? "high" : "medium";
        var severity = passed
            ? "none"
            : (_failureSeverity is null ? scoreSeverity : SeverityRollup.Max([scoreSeverity, _failureSeverity]));

        int? tokensUsed = null;
        double estimatedCost = 0;
        if (response.Usage is { } usage)
        {
            var total = usage.InputTokens + usage.OutputTokens;
            tokensUsed = total > int.MaxValue ? int.MaxValue : (int)total;
            estimatedCost = usage.Cost ?? EstimateCost(response.Model, usage);
        }

        var evidence = new List<EvalEvidence>
        {
            new(
                Source: "decision",
                Reference: Key,
                Message: $"P(yes) = {value:F3} against a pass threshold of {_passThreshold:F2}; answered by {response.Model}."),
        };

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(value, null, label, passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double> { [ProbabilityDimension] = value },
                Evidence: evidence,
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null)
            {
                Summary = $"P(yes) = {value:F3} for \"{_instructions}\"",
            },
            Provenance: new(
                Type: ProvenanceType,
                JudgeModel: response.Model,
                PromptId: null,
                PromptHash: _promptHash,
                TokensUsed: tokensUsed,
                EstimatedCost: estimatedCost,
                CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private double EstimateCost(string model, DecisionUsage usage)
    {
        if (_rateResolver is null)
            return JudgeCostMap.EstimateCost(model, usage.InputTokens, usage.OutputTokens);

        var rate = _rateResolver(model);
        return (usage.InputTokens / 1000.0) * rate.InputRatePer1K
             + (usage.OutputTokens / 1000.0) * rate.OutputRatePer1K;
    }

    /// <summary>What the model sees when no <c>stateProjector</c> is supplied. Nulls are omitted on the wire.</summary>
    internal static DefaultDecisionState DefaultState(EvalInput input) => new(
        input.Query,
        input.Response,
        input.Context,
        input.GroundTruth,
        input.SystemMessage);

    private static string HashPrompt(string instructions, string? trueCriteria, string? falseCriteria)
    {
        var bytes = Encoding.UTF8.GetBytes(instructions + "\u001f" + (trueCriteria ?? "") + "\u001f" + (falseCriteria ?? ""));
        return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }
}

/// <summary>The default state shape <see cref="DecisionEval"/> sends: the input's text fields, serialised camelCase, nulls omitted.</summary>
internal sealed record DefaultDecisionState(
    string Query,
    string? Response,
    string? Context,
    string? GroundTruth,
    string? SystemMessage);
