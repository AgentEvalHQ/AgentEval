// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Eval 02b's ORACLE — the SQL an interviewer writes for a stated need once the constraints are
/// known. It calls the gold, so it is the ceiling of the metric, never an entrant.
/// </summary>
/// <remarks>
/// <para>
/// <c>SELECT sku FROM products WHERE price &lt;= ? AND stock &gt; 0 AND seller = 'galaxus' AND
/// compat &amp;&amp; (SELECT compat FROM owned WHERE sku = ?) …</c> — trivially 1.000 once the
/// constraints are a filter. The whole question Eval 02b asks is whether an arm can TURN a
/// shopper's sentence INTO that filter; this arm is handed the filter and shows the ceiling is
/// reachable. On every applicable case it must score exactly 1.000, and that is the grader's
/// ACCEPTING direction being verified — a grader that rejected everything would put the
/// constraint-blind draw "at floor" for the wrong reason.
/// </para>
/// <para>
/// Presents at most five, round-robin across the case's slots so an assembly case gets one of
/// each before a second of any.
/// </para>
/// </remarks>
public sealed class Baseline_ConstraintFilter : IEvaluableAgent
{
    /// <summary>How many satisfying products it presents at most.</summary>
    public const int PresentationCount = 5;

    /// <inheritdoc/>
    public string Name => nameof(Baseline_ConstraintFilter);

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        string? userId = ScriptedTrace.PersonaIdFrom(prompt);
        StatedNeedCase testCase = StatedNeedCases.ForPersona(userId)
            ?? throw new InvalidOperationException(
                $"The constraint-filter oracle was asked about '{userId ?? "(no customer)"}', which has no stated-need case. " +
                "It can only answer the twelve authored needs; it is an oracle, not a recommender.");

        var trace = new ScriptedTrace();
        foreach (Product product in Selection(testCase))
        {
            string? citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product);
            if (citation is null) continue;

            trace.Present(product.Id,
                $"Satisfies every stated constraint — {product.Name}.",
                citation,
                outOfStock: product.StockUnits == 0);
        }

        trace.Say("Constraint filter, no model. The constraints were handed to me; I did not parse them.");
        return Task.FromResult(trace.ToResponse());
    }

    /// <summary>The products the oracle presents for a case: satisfying set, round-robin over slots, at most five.</summary>
    /// <param name="testCase">The case.</param>
    public static IReadOnlyList<Product> Selection(StatedNeedCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);

        var catalogue = Catalogue.Default;
        var customer = UserProfiles.Require(testCase.PersonaId);

        var bySlot = ConstraintSatisfactionGrader.SatisfyingSet(testCase)
            .GroupBy(p => testCase.FirstSatisfiedSlot(p, customer, catalogue))
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();

        var chosen = new List<Product>(PresentationCount);
        for (int round = 0; chosen.Count < PresentationCount; round++)
        {
            bool any = false;
            foreach (var slot in bySlot)
            {
                if (round >= slot.Count) continue;
                any = true;
                chosen.Add(slot[round]);
                if (chosen.Count == PresentationCount) break;
            }
            if (!any) break;
        }

        return chosen;
    }
}
