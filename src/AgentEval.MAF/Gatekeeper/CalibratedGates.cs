// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Microsoft.Extensions.AI;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.MAF.Gatekeeper;

internal static class CalibrationEnforcement
{
    internal static GateVerdict Apply(GateVerdict verdict, IReadOnlyList<CalibrationBinding> bindings)
    {
        var uses = Array.AsReadOnly(bindings.Select(b => b.Evaluate(verdict)).ToArray());
        var provenance = (verdict.Provenance ?? GateProvenance.ForRule(verdict.PolicyName)) with { Calibration = uses };
        var rejected = uses.FirstOrDefault(u => u.Kind == CalibrationUseKind.Rejected);
        // Inconclusive/budget actions retain their explicitly configured failure policy. They are never approved.
        return rejected is null ? verdict with { Provenance = provenance }
            : verdict with { Action = GateAction.Block, RedactedText = null, Confidence = null, Provenance = provenance,
                Reason = $"Expected: calibration accepted for this runtime verdict. Actual: {rejected.Reason} Suggestions: check the model or explicitly allow compatible same-axis reuse. Because: calibration evidence must describe its actual scope." };
    }
}

internal sealed class CalibratedChatGate(IChatGate inner, IReadOnlyList<CalibrationBinding> bindings) : IChatGate, IDelegatingGate
{
    internal IChatGate Inner => inner;
    public string PolicyName => inner.PolicyName;
    IEnumerable<object> IDelegatingGate.InnerGates => [inner];
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default) =>
        CalibrationEnforcement.Apply(await inner.InspectAsync(text, cancellationToken).ConfigureAwait(false), bindings);
}

internal sealed class CalibratedApprovalGate(IToolApprovalGate inner, IReadOnlyList<CalibrationBinding> bindings, AgentTrace? trace)
    : IToolApprovalGate, IDelegatingGate
{
    private int _sequence;
    internal IToolApprovalGate Inner => inner;
    public string PolicyName => inner.PolicyName;
    IEnumerable<object> IDelegatingGate.InnerGates => [inner];
    public async ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        if (inner is ToolArgumentGoalCoherenceApprovalGate stock)
            return await stock.IsAutoApprovableAsync(call, Record, cancellationToken).ConfigureAwait(false);
        // Custom legacy gates expose only a boolean. Record axis-only scope without inventing model observations.
        var approved = await inner.IsAutoApprovableAsync(call, cancellationToken).ConfigureAwait(false);
        var result = Record(approved ? GateVerdict.Allow(PolicyName) : GateVerdict.Block(PolicyName, "Approval required."));
        return approved && result.Provenance!.Calibration!.All(c => c.IsAccepted);

        GateVerdict Record(GateVerdict v)
        {
            var verdict = CalibrationEnforcement.Apply(v, bindings);
            trace?.SetMetadata($"calibration.approval.{Interlocked.Increment(ref _sequence)}.{PolicyName}",
                new Dictionary<string, object?> { ["function"] = call.Name, ["calibration"] = verdict.Provenance!.Calibration });
            return verdict;
        }
    }
}
