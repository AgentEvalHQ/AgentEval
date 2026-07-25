// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// Turns a single-axis <see cref="IJudgeRubric"/> into a runtime <see cref="IChatGate"/> backed by a fast model —
/// the core primitive of "The Tribunal". It slots into the run-pre / run-post seam (which accepts LLM cost,
/// unlike the inline tool gate).
/// <para>Flow: <b>prefilter</b> (skip the model, allow — most turns cost 0 tokens) → <b>fast model</b> under a
/// hard <see cref="JudgeGateOptions.Timeout"/> → <b>parse</b> into a decisive <see cref="JudgeVerdict"/>. A
/// <see cref="JudgeDecision.Blocked"/> at or above the confidence threshold blocks (citing evidence spans in
/// <see cref="GateVerdict.Matches"/>); an <see cref="JudgeDecision.Inconclusive"/> (timeout / model error /
/// unparseable reply) is <b>fail-closed</b> by default. Never stalls the hot path; never throws out of the gate
/// except to honor the caller's own cancellation.</para>
/// <para><b>Trust before deployment.</b> A judge should be calibrated against a per-axis gold set (the
/// calibration harness) before it goes inline — an uncalibrated inline judge is a fabrication risk.</para>
/// </summary>
public sealed class CompositeJudgeGate<TRubric> : IChatGate, IRequiresCalibration
    where TRubric : IJudgeRubric
{
    private readonly TRubric _rubric;
    private readonly IChatClient _fastModel;
    private readonly JudgeGateOptions _options;

    /// <inheritdoc/>
    public string PolicyName { get; }

    /// <inheritdoc/>
    public string AxisName => _rubric.Axis;

    /// <summary>Creates the gate from a single-axis rubric and the fast model that answers it.</summary>
    public CompositeJudgeGate(TRubric rubric, IChatClient fastModel, JudgeGateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        ArgumentNullException.ThrowIfNull(fastModel);
        if (string.IsNullOrWhiteSpace(rubric.Axis))
        {
            throw new ArgumentException("rubric.Axis must be non-empty.", nameof(rubric));
        }

        _rubric = rubric;
        _fastModel = fastModel;
        _options = options ?? new JudgeGateOptions();
        if (_options.Timeout <= TimeSpan.Zero || _options.Timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JudgeGateOptions.Timeout must be positive and within ~24.8 days.");
        }

        if (double.IsNaN(_options.BlockThreshold) || _options.BlockThreshold < 0.0 || _options.BlockThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JudgeGateOptions.BlockThreshold must be in [0, 1].");
        }

        if (_options.MaxInputChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JudgeGateOptions.MaxInputChars must be non-negative (0 = unbounded).");
        }

        PolicyName = $"judge:{rubric.Axis}";
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || !SafePrefilter(text))
        {
            return GateVerdict.Allow(PolicyName);   // cheap short-circuit — most turns never reach the model
        }

        var ruleName = $"judge:{_rubric.Axis}";

        // P5-2: reserve against the shared token+call wallet BEFORE the model call. On exhaustion, skip the model
        // and degrade to a recorded "unjudged — budget exhausted" verdict (fail-open advisory by default).
        if (_options.SpendGovernor is { } governor && !governor.TryReserve(EstimateTokens(text)))
        {
            var exhaustedReason = $"{_rubric.Axis} judge unjudged — spend budget exhausted";
            var exhaustedProvenance = new GateProvenance($"{ruleName}:budget-exhausted", Array.Empty<string>());
            return _options.FailClosedOnBudgetExhausted
                ? GateVerdict.Block(PolicyName, $"{exhaustedReason} (fail-closed)") with { Provenance = exhaustedProvenance }
                : GateVerdict.Allow(PolicyName) with { Provenance = exhaustedProvenance };
        }

        var verdict = await JudgeAsync(text, cancellationToken).ConfigureAwait(false);
        var evidence = verdict.Spans ?? Array.Empty<string>();

        return verdict.Decision switch
        {
            // !(<) rather than (>=) so a NaN confidence BLOCKS (fail-closed) instead of falling through to Allow.
            JudgeDecision.Blocked when !(verdict.Confidence < _options.BlockThreshold) =>
                GateVerdict.Block(PolicyName, verdict.Rationale ?? $"{_rubric.Axis} detected", verdict.Spans) with
                {
                    Provenance = new GateProvenance(ruleName, evidence, Threshold: _options.BlockThreshold, ActualValue: verdict.Confidence),
                },
            JudgeDecision.Inconclusive when _options.FailClosedOnInconclusive =>
                GateVerdict.Block(PolicyName, verdict.Rationale ?? $"{_rubric.Axis} judge inconclusive (fail-closed)") with
                {
                    Provenance = new GateProvenance(ruleName, evidence, Threshold: _options.BlockThreshold, ActualValue: verdict.Confidence),
                },
            // A genuine Allowed decision carries NO confidence through, deliberately: JudgeVerdict.Confidence is
            // confidence IN THE DECISION, not confidence that something is wrong — JudgeVerdict.Allowed()
            // defaults to Confidence=1.0, meaning "very sure this is fine," the OPPOSITE of a near-miss signal.
            // Attaching it here would make FleetCorrelator treat every confidently-clean turn from 2+ judges as
            // a "soft signal" and false-positive-block essentially all benign multi-judge traffic (found in
            // review: a genuine bug, not a style choice — see CompositeJudgeGateTests for the regression test).
            JudgeDecision.Allowed => GateVerdict.Allow(PolicyName),
            // Low-confidence Blocked (would have blocked at a lower threshold) or fail-open Inconclusive — a
            // GENUINE near-miss/uncertain signal. Carry the judge's own confidence through instead of
            // discarding it, so a fleet-wide correlator can later notice several near-miss verdicts. Provenance
            // makes that near-miss reconstructable too (threshold it almost crossed, evidence it saw), not just
            // a bare number.
            _ => GateVerdict.Allow(PolicyName) with
            {
                Confidence = verdict.Confidence,
                Provenance = new GateProvenance(ruleName, evidence, Threshold: _options.BlockThreshold, ActualValue: verdict.Confidence),
            },
        };
    }

    // P5-3: bound the text the model sees to a head + tail sandwich when it exceeds MaxInputChars, so a
    // pathologically large turn can't blow up cost/latency/context — while an injection payload at EITHER
    // boundary is still visible. The prefilter has already run on the full text; only the prompt is bounded.
    private string BoundInput(string text)
    {
        var max = _options.MaxInputChars;
        if (max <= 0 || text.Length <= max)
        {
            return text;   // unbounded, or already within the cap
        }

        var half = max / 2;
        var droppedCount = text.Length - (half * 2);
        return string.Concat(
            text.AsSpan(0, half),
            $"\n…[judge input truncated: {droppedCount} chars omitted]…\n",
            text.AsSpan(text.Length - half));
    }

    // P5-2: a deliberately cheap, dependency-free token estimate for the spend reservation — input chars/4 (the
    // usual rough chars-per-token ratio) plus the output-token cap. It only needs to bound spend, not be exact;
    // the reservation is a ceiling, so over-estimating is the safe direction. The input length is capped at
    // MaxInputChars (P5-3) because that is all the model actually sees — otherwise a huge benign turn would
    // over-reserve on chars that BoundInput will drop, and could falsely exhaust the wallet.
    private long EstimateTokens(string text)
    {
        var chars = _options.MaxInputChars > 0 ? Math.Min(text.Length, _options.MaxInputChars) : text.Length;
        return ((long)chars / 4) + _options.MaxOutputTokens;
    }

    private bool SafePrefilter(string text)
    {
        try
        {
            return _rubric.Prefilter(text);
        }
        catch
        {
            return true;   // a broken prefilter must not silently skip the judge — fail toward inspecting
        }
    }

    private async ValueTask<JudgeVerdict> JudgeAsync(string text, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            cts.CancelAfter(_options.Timeout);   // inside the try so a bad value degrades to Inconclusive, never escapes
            var messages = new List<ChatMessage> { new(ChatRole.User, _rubric.BuildPrompt(BoundInput(text))) };
            var options = new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens, Temperature = 0f };
            var response = await _fastModel.GetResponseAsync(messages, options, cts.Token).ConfigureAwait(false);
            return _rubric.Parse(response.Text ?? string.Empty) ?? JudgeVerdict.Inconclusive($"{_rubric.Axis} rubric returned null");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();   // honor the CALLER — rethrow with the caller's own token
            throw;   // unreachable in this branch (line above always throws); satisfies the compiler
        }
        catch (OperationCanceledException)
        {
            return JudgeVerdict.Inconclusive($"{_rubric.Axis} judge timed out after {_options.Timeout.TotalMilliseconds:0}ms");
        }
        catch (Exception ex)
        {
            return JudgeVerdict.Inconclusive($"{_rubric.Axis} judge error: {ex.GetType().Name}");
        }
    }
}
