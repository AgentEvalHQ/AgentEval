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
public sealed class CompositeJudgeGate<TRubric> : IChatGate
    where TRubric : IJudgeRubric
{
    private readonly TRubric _rubric;
    private readonly IChatClient _fastModel;
    private readonly JudgeGateOptions _options;

    /// <inheritdoc/>
    public string PolicyName { get; }

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
        PolicyName = $"judge:{rubric.Axis}";
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || !SafePrefilter(text))
        {
            return GateVerdict.Allow(PolicyName);   // cheap short-circuit — most turns never reach the model
        }

        var verdict = await JudgeAsync(text, cancellationToken).ConfigureAwait(false);

        return verdict.Decision switch
        {
            JudgeDecision.Blocked when verdict.Confidence >= _options.BlockThreshold =>
                GateVerdict.Block(PolicyName, verdict.Rationale ?? $"{_rubric.Axis} detected", verdict.Spans),
            JudgeDecision.Inconclusive when _options.FailClosedOnInconclusive =>
                GateVerdict.Block(PolicyName, verdict.Rationale ?? $"{_rubric.Axis} judge inconclusive (fail-closed)"),
            _ => GateVerdict.Allow(PolicyName),   // Allowed, low-confidence Blocked, or fail-open Inconclusive
        };
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
        cts.CancelAfter(_options.Timeout);

        try
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, _rubric.BuildPrompt(text)) };
            var options = new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens, Temperature = 0f };
            var response = await _fastModel.GetResponseAsync(messages, options, cts.Token).ConfigureAwait(false);
            return _rubric.Parse(response.Text ?? string.Empty) ?? JudgeVerdict.Inconclusive($"{_rubric.Axis} rubric returned null");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the CALLER cancelled — honor it (don't swallow into a verdict)
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
