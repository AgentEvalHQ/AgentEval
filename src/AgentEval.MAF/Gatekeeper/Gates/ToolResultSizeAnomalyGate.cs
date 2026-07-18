// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper next-wave item — a PER-TOOL, PER-SESSION statistical-outlier detector, explicitly NOT the same
/// gate as <see cref="ToolResultSizeGate"/> (which truncates against one fixed, global character threshold).
/// This gate flags a tool result whose size is anomalous relative to <b>that same tool's own</b> running size
/// history THIS RUN — a tool that has returned ~200-character results all run suddenly returning 50,000
/// characters is suspicious even though 50,000 characters might be entirely unremarkable, and routinely
/// allowed, for a DIFFERENT tool (e.g. a bulk file-read tool). Complements <see cref="ToolResultSizeGate"/>
/// rather than replacing it — register both; they catch different things (context/cost exhaustion vs.
/// per-tool behavioral drift).
/// <para><b>v1 (this gate): fixed-multiplier-per-tool, no real statistics.</b> Cheap, deterministic, no
/// statistics library needed — a true rolling mean/stddev or median-absolute-deviation is deferred as a
/// documented v2 follow-on (more robust to the first few calls skewing a small-N mean), not built here.</para>
/// <para><b>Experimental:</b> shipped 2026-07-17 as v1 of a heuristic its own doc comment already flags as
/// likely to be superseded by a real-statistics v2 — the detection approach, not just the implementation, may
/// still change.</para>
/// </summary>
[Experimental("AGENTEVAL_GATEKEEPER_PREVIEW001")]
public sealed class ToolResultSizeAnomalyGate : IToolResultGate
{
    /// <summary>Default: a result &gt; 5x this tool's own running average this run is flagged.</summary>
    public const double DefaultAnomalyMultiplier = 5.0;

    /// <summary>Default: at least 3 prior calls to a tool are required before an anomaly can fire — two data points aren't a distribution.</summary>
    public const int DefaultMinBaselineCalls = 3;

    /// <summary>Default: results under 500 characters are never flagged, however large the multiplier — avoids false alarms on trivially small results where a percentage swing is meaningless (e.g. 10 → 60 characters is "6x" but both are noise).</summary>
    public const int DefaultMinFlagSize = 500;

    private readonly double _anomalyMultiplier;
    private readonly int _minBaselineCalls;
    private readonly int _minFlagSize;

    /// <summary>Creates the gate with the default thresholds, or caller-supplied overrides.</summary>
    public ToolResultSizeAnomalyGate(
        double anomalyMultiplier = DefaultAnomalyMultiplier,
        int minBaselineCalls = DefaultMinBaselineCalls,
        int minFlagSize = DefaultMinFlagSize)
    {
        if (anomalyMultiplier <= 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(anomalyMultiplier), anomalyMultiplier, "anomalyMultiplier must be greater than 1.0 (a result must be LARGER than its baseline to be anomalous).");
        }

        if (minBaselineCalls < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minBaselineCalls), minBaselineCalls, "minBaselineCalls must be at least 1.");
        }

        if (minFlagSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minFlagSize), minFlagSize, "minFlagSize must not be negative.");
        }

        _anomalyMultiplier = anomalyMultiplier;
        _minBaselineCalls = minBaselineCalls;
        _minFlagSize = minFlagSize;
    }

    /// <inheritdoc/>
    public string PolicyName => "tool-result-size-anomaly";

    /// <inheritdoc/>
    public GateCost Cost => GateCost.PureCode;

    /// <inheritdoc/>
    public ValueTask<ToolResultVerdict> InspectAsync(GatedToolResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = GateText.Stringify(result.Result);
        var size = text.Length;

        // Atomically reads the baseline as it stood BEFORE this call AND records this call's size into that
        // running baseline — UNLESS this call turns out to be anomalous. A flagged result must NOT be folded
        // into its own tool's future baseline: doing so would let a REPEATED attack of the same size evade
        // detection the second time, since the first (correctly flagged) call would have already dragged the
        // average up past the threshold that should have caught the second one — the detector defeating itself
        // after exactly one detection. The ledger's own locking makes this correct even if MAF dispatches
        // sibling tool calls concurrently.
        var flagged = false;
        var baseline = RunLedger.ForCurrentRun().RecordToolResultSizeUnlessAnomalous(result.FunctionName, size, b =>
        {
            flagged = b.Count >= _minBaselineCalls && size >= _minFlagSize && b.AverageSize > 0 && size > b.AverageSize * _anomalyMultiplier;
            return flagged;
        });

        if (flagged)
        {
            var average = baseline.AverageSize;
            var reason =
                $"tool '{result.FunctionName}' result was {size} characters — {size / average:F1}x this tool's " +
                $"own running average of {average:F0} characters over its prior {baseline.Count} call(s) this " +
                $"run, exceeding the {_anomalyMultiplier:F1}x anomaly threshold";
            var redacted = text[..Math.Min(text.Length, _minFlagSize)] +
                $"…[redacted by Gatekeeper's {PolicyName} gate — this result's size is a statistical outlier " +
                "for this tool this run; see trace evidence for detail]";
            return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Redact(PolicyName, redacted, reason));
        }

        return new ValueTask<ToolResultVerdict>(ToolResultVerdict.Allow(PolicyName));
    }
}
