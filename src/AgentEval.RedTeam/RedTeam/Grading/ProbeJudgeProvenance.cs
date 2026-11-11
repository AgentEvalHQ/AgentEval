// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.RedTeam;

internal static class ProbeJudgeProvenance
{
    internal static string Digest(object identity) => "sha256:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(identity))).ToLowerInvariant();

    internal static ProbeJudgeInstrument FromGate(AgentEval.Guardrails.Judges.JudgeGateInstrument instrument,
        AgentEval.Guardrails.Judges.JudgeGateOptions settings) => new(instrument.Prompt,
        Digest(new { instrument.ConfigurationDigest, models = instrument.Calls.Where(c => c.ResponseReceived)
            .Select(c => c.ModelId).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToArray() }),
        Array.AsReadOnly(instrument.Calls.Select(c => new ProbeJudgeCall(c.ModelId, settings.Temperature,
            settings.MaxOutputTokens, c.ResponseReceived)).ToArray()))
    {
        ConfigurationDigest = instrument.ConfigurationDigest, RequestedModelId = instrument.RequestedModelId,
        Measured = instrument.Measured, BudgetExhausted = instrument.BudgetExhausted
    };

    internal static IReadOnlyList<ProbeJudgeInstrument>? Observed(IEnumerable<EvaluationResult> results)
    {
        var instruments = results.SelectMany(r => r.JudgeInstruments ?? []).ToArray();
        return instruments.Length == 0 ? null : Array.AsReadOnly(instruments);
    }

    internal static string? CommonGraphDigest(IEnumerable<EvaluationResult> results)
    {
        var identities = results.Select(r => r.JudgeGraphDigest).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return identities.Length == 1 ? identities[0] : null;
    }

    internal static IReadOnlyList<ProbeJudgeInstrument>? WithoutContent(IReadOnlyList<ProbeJudgeInstrument>? instruments) =>
        instruments is null ? null : Array.AsReadOnly(instruments.Select(i => i with { Prompt = i.Prompt with { Content = null } }).ToArray());
}
