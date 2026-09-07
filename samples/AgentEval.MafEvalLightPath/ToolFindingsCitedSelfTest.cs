// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;

namespace AgentEval.MafEvalLightPath;

/// <summary>
/// Offline invariants for <see cref="ToolFindingsCitedEval"/>, runnable with no credentials.
/// </summary>
/// <remarks>
/// <para>
/// This is the only part of the sample that can be exercised with nothing configured — the rest of
/// it needs a real Azure OpenAI agent — so it runs BEFORE the credential check, and its exit code is
/// the sample's exit code.
/// </para>
/// <para>
/// Three arms, not two. A pass/empty pair would only show that the eval can decline; it would never
/// show that it can FAIL, and an eval that cannot fail catches nothing. The middle arm is a
/// plausible, on-topic, fluent answer that cites nothing the tools returned.
/// </para>
/// </remarks>
public static class ToolFindingsCitedSelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("\n══ --selftest — ToolFindingsCitedEval, offline ═══════════════════════════\n");

        var runner = await new AgentEvalBuilder()
            .AddEval(new ToolFindingsCitedEval(), ToolFindingsCitedEval.DeclaredFloor)
            .BuildAsync(CancellationToken.None);

        async Task<EvalResult> RunAsync(string response) =>
            (await runner.EvaluateEvalsAsync(
                new EvalInput(Query: "Find flights from Seattle to Paris and a hotel near the Eiffel Tower.",
                              Response: response),
                CancellationToken.None))[0];

        // 1 · cites a flight the tool returned AND a hotel the tool returned ⇒ pass
        var cited = await RunAsync(
            "Best option: AA101 at $450 (10h) from Seattle to Paris, and Hotel Le Marais at $180/night, 4★.");

        // 2 · fluent, on-topic, and cites NOTHING the tools returned ⇒ a MEASURED fail, not a decline
        var uncited = await RunAsync(
            "I found several good flights to Paris and a lovely hotel a short walk from the Eiffel Tower. "
            + "Prices look reasonable for next Friday and I would book early.");

        // 3 · empty ⇒ UNDECIDABLE. The door collapses a missing response into "" (:74), so this is
        //     ambiguous by construction and must not be scored as a failure.
        var empty = await RunAsync("");

        var okCited = cited.Score.Passed && Math.Abs(cited.Score.Value - 1.0) < 1e-9;
        var okUncited = !uncited.Score.Passed
                     && uncited.Score.CensusBucket() == MeasurementState.Measured
                     && uncited.Score.Value == 0.0;
        var okEmpty = !empty.Score.Passed
                   && empty.Score.CensusBucket() == MeasurementState.NotApplicable;

        // The floor the leaf was ADMITTED under must reach the result, and a not-derivable floor must
        // write its reason and NO number. An absent floor rendered as 0.0 is a bar everything clears.
        var floorEvidence = cited.Details.Evidence?.FirstOrDefault(
            e => string.Equals(e.Source, ComparabilityFacts.ChanceFloorEvidenceSource, StringComparison.Ordinal));
        var okFloor = floorEvidence is not null
                   && string.Equals(floorEvidence.Reference, ChanceFloor.KindNotDerivable, StringComparison.Ordinal)
                   && !string.IsNullOrWhiteSpace(floorEvidence.Message)
                   && cited.Details.Dimensions?.ContainsKey(ComparabilityFacts.ChanceFloorDimension) != true;

        Console.WriteLine($"  cites AA101 + Hotel Le Marais → {cited.Score.Label} {cited.Score.Value:0.00}      (expect pass 1.00)      {Ok(okCited)}");
        Console.WriteLine($"  fluent but cites nothing      → {uncited.Score.Label} {uncited.Score.Value:0.00} / {uncited.Score.CensusBucket()}  (expect fail 0.00, Measured) {Ok(okUncited)}");
        Console.WriteLine($"  empty response                → {empty.Score.Label} / {empty.Score.CensusBucket()}   (expect inapplicable)   {Ok(okEmpty)}");
        Console.WriteLine($"  floor through the door        → {floorEvidence?.Reference ?? "(none)"}, no number written  (expect not-derivable)  {Ok(okFloor)}");

        var all = okCited && okUncited && okEmpty && okFloor;
        Console.WriteLine(all ? "\n  selftest OK" : "\n  selftest FAILED");
        return all ? 0 : 1;
    }

    private static string Ok(bool b) => b ? "OK" : "FAIL";
}
