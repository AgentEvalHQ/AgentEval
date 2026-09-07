// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;

namespace AgentEval.MafEvalFoundryAlongsideLocal;

/// <summary>
/// Offline invariants for <see cref="ThreeDayItineraryEval"/>, runnable with no credentials.
/// </summary>
/// <remarks>
/// <para>
/// This sample THROWS without <c>AZURE_OPENAI_*</c> — the judge is constructed before anything else
/// runs — so without this entry point nothing in it can be exercised unconfigured. It runs before
/// the first environment read, and its exit code is the sample's exit code.
/// </para>
/// <para>
/// Four arms. A pass/empty pair would only show that the eval can decline; it would never show that
/// it can FAIL, and an eval that cannot fail catches nothing. The fourth arm pins the applicability
/// rule: the second query the sample sends is NOT a 3-day request, and the leaf must decline it on
/// the QUERY rather than score it 0.
/// </para>
/// </remarks>
public static class ThreeDayItinerarySelfTest
{
    private const string Kyoto = "Plan a 3-day trip to Kyoto for a family with two kids.";
    private const string Cheapest = "What's the cheapest way to get from Paris to London next week?";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("\n== --selftest -- ThreeDayItineraryEval, offline ==\n");

        var runner = await new AgentEvalBuilder()
            .AddEval(new ThreeDayItineraryEval(), ThreeDayItineraryEval.DeclaredFloor)
            .BuildAsync(CancellationToken.None);

        async Task<EvalResult> RunAsync(string query, string response) =>
            (await runner.EvaluateEvalsAsync(
                new EvalInput(Query: query, Response: response), CancellationToken.None))[0];

        // 1 - three days, mixed spellings on purpose (the three rows are independent) => pass
        var full = await RunAsync(Kyoto,
            "Day 1: Fushimi Inari and Gion. Day Two: Arashiyama bamboo grove and the monkey park. "
            + "On the third day, Nijo Castle and the Kyoto Railway Museum, which the kids will love.");

        // 2 - fluent, on-topic, and never reaches day three => a MEASURED fail, not a decline
        var partial = await RunAsync(Kyoto,
            "Day 1: Fushimi Inari and Gion. Day 2: Arashiyama and the monkey park. "
            + "Kyoto is wonderful with children and there is plenty more to see.");

        // 3 - empty => UNDECIDABLE. The door collapses a missing response into "" (:74).
        var empty = await RunAsync(Kyoto, "");

        // 4 - the sample's OTHER query. Not a 3-day request => declined on the QUERY, not scored 0.
        var otherQuery = await RunAsync(Cheapest,
            "The coach is cheapest at around 25 GBP; Eurostar is faster at about 60 GBP if booked early.");

        var okFull = full.Score.Passed && Math.Abs(full.Score.Value - 1.0) < 1e-9;
        var okPartial = !partial.Score.Passed
                     && partial.Score.CensusBucket() == MeasurementState.Measured
                     && Math.Abs(partial.Score.Value - (2.0 / 3.0)) < 1e-9;
        var okEmpty = !empty.Score.Passed && empty.Score.CensusBucket() == MeasurementState.NotApplicable;
        var okOther = !otherQuery.Score.Passed
                   && otherQuery.Score.CensusBucket() == MeasurementState.NotApplicable;

        // The floor the leaf was ADMITTED under must reach the result, and a not-derivable floor must
        // write its reason and NO number. An absent floor rendered as 0.0 is a bar everything clears.
        var floorEvidence = full.Details.Evidence?.FirstOrDefault(
            e => string.Equals(e.Source, ComparabilityFacts.ChanceFloorEvidenceSource, StringComparison.Ordinal));
        var okFloor = floorEvidence is not null
                   && string.Equals(floorEvidence.Reference, ChanceFloor.KindNotDerivable, StringComparison.Ordinal)
                   && !string.IsNullOrWhiteSpace(floorEvidence.Message)
                   && full.Details.Dimensions?.ContainsKey(ComparabilityFacts.ChanceFloorDimension) != true;

        Console.WriteLine($"  three days, mixed spellings  -> {full.Score.Label} {full.Score.Value:0.00}       (expect pass 1.00)        {Ok(okFull)}");
        Console.WriteLine($"  stops at day 2               -> {partial.Score.Label} {partial.Score.Value:0.00} / {partial.Score.CensusBucket()}  (expect fail 0.67, Measured) {Ok(okPartial)}");
        Console.WriteLine($"  empty response               -> {empty.Score.Label} / {empty.Score.CensusBucket()}    (expect inapplicable)     {Ok(okEmpty)}");
        Console.WriteLine($"  the sample's OTHER query     -> {otherQuery.Score.Label} / {otherQuery.Score.CensusBucket()}    (expect inapplicable)     {Ok(okOther)}");
        Console.WriteLine($"  floor through the door       -> {floorEvidence?.Reference ?? "(none)"}, no number written   (expect not-derivable)    {Ok(okFloor)}");

        var all = okFull && okPartial && okEmpty && okOther && okFloor;
        Console.WriteLine(all ? "\n  selftest OK" : "\n  selftest FAILED");
        return all ? 0 : 1;
    }

    private static string Ok(bool b) => b ? "OK" : "FAIL";
}
