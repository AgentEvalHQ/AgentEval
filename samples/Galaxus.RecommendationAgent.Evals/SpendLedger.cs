// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// What a paid run actually cost, accumulated from the harness's own usage block — never from a
/// guess about how many turns "should" have run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Both hybrid evals ran live for the first time under the three-stage
/// protocol, whose stage three asks for the ACTUAL cost and forbids an estimate. Every number the
/// ledger needs was already on <c>TestResult.Performance</c> and was simply never printed, so the
/// only honest answer available before this type was "we do not know".
/// </para>
/// <para>
/// <b>The one thing it must never do is launder an estimate into a measurement.</b>
/// <c>MAFEvaluationHarness</c> falls back to <c>ModelPricing.EstimateTokensFromText</c> when the
/// provider returns no usage block, and records that fact in
/// <c>PerformanceMetrics.TokensAreEstimated</c>. The ledger counts the two kinds of turn
/// SEPARATELY and prints both counts, so a run whose provider went silent cannot be reported as a
/// measured one. A cost line is printed only for the measured share, and the estimated share is
/// named beside it.
/// </para>
/// <para>
/// <b>The rate is a fact about this repository's table, not about anyone's invoice.</b>
/// <c>ModelPricing</c> carries several rows its own comments mark as placeholders. The ledger
/// therefore prints the per-million rate it applied and where it came from, so a reader can see
/// whether the money figure is a price or an arithmetic exercise. Tokens are the measurement;
/// currency is a derivation from it.
/// </para>
/// </remarks>
public sealed class SpendLedger
{
    private readonly object _gate = new();

    /// <summary>Live turns whose provider returned a real usage block.</summary>
    public int MeasuredTurns { get; private set; }

    /// <summary>Live turns whose token counts came from the harness's text-length fallback.</summary>
    public int EstimatedTurns { get; private set; }

    /// <summary>Live turns that produced no performance metrics at all.</summary>
    public int UnaccountedTurns { get; private set; }

    /// <summary>Prompt tokens over the MEASURED turns only.</summary>
    public long MeasuredPromptTokens { get; private set; }

    /// <summary>Completion tokens over the MEASURED turns only.</summary>
    public long MeasuredCompletionTokens { get; private set; }

    /// <summary>Prompt tokens over the ESTIMATED turns only.</summary>
    public long EstimatedPromptTokens { get; private set; }

    /// <summary>Completion tokens over the ESTIMATED turns only.</summary>
    public long EstimatedCompletionTokens { get; private set; }

    /// <summary>Wall-clock time inside live agent turns.</summary>
    public TimeSpan Wall { get; private set; }

    /// <summary>The slowest single live turn.</summary>
    public TimeSpan SlowestTurn { get; private set; }

    /// <summary>Total live turns recorded.</summary>
    public int Turns => MeasuredTurns + EstimatedTurns + UnaccountedTurns;

    /// <summary>Total tokens over the measured turns.</summary>
    public long MeasuredTotalTokens => MeasuredPromptTokens + MeasuredCompletionTokens;

    /// <summary>
    /// Records one LIVE turn. Offline arms must not be passed here — they cost nothing and
    /// counting them would make the per-turn figures meaningless.
    /// </summary>
    /// <param name="metrics">The harness's performance block for the turn, or null.</param>
    public void Record(PerformanceMetrics? metrics)
    {
        lock (_gate)
        {
            if (metrics is null) { UnaccountedTurns++; return; }

            Wall += metrics.TotalDuration;
            if (metrics.TotalDuration > SlowestTurn) SlowestTurn = metrics.TotalDuration;

            int prompt = metrics.PromptTokens ?? 0;
            int completion = metrics.CompletionTokens ?? 0;

            if (metrics.PromptTokens is null && metrics.CompletionTokens is null)
            {
                UnaccountedTurns++;
                return;
            }

            if (metrics.TokensAreEstimated)
            {
                EstimatedTurns++;
                EstimatedPromptTokens += prompt;
                EstimatedCompletionTokens += completion;
            }
            else
            {
                MeasuredTurns++;
                MeasuredPromptTokens += prompt;
                MeasuredCompletionTokens += completion;
            }
        }
    }

    /// <summary>
    /// Prints the ledger. Nothing is printed when no live turn ran — an empty ledger under a
    /// credentials-missing run must not look like a free run of the real thing.
    /// </summary>
    /// <param name="model">The deployment the live arm ran against.</param>
    /// <param name="label">The eval's name, for the panel title.</param>
    public void Print(string model, string label)
    {
        if (Turns == 0) return;

        var rate = ModelPricing.GetPricing(model);
        decimal? measuredCost = rate is null
            ? null
            : (MeasuredPromptTokens / 1000m * rate.Value.InputPer1K)
            + (MeasuredCompletionTokens / 1000m * rate.Value.OutputPer1K);
        decimal? estimatedCost = rate is null || EstimatedTurns == 0
            ? null
            : (EstimatedPromptTokens / 1000m * rate.Value.InputPer1K)
            + (EstimatedCompletionTokens / 1000m * rate.Value.OutputPer1K);

        var lines = new List<string>
        {
            $"model      : {model}",
            $"live turns : {Turns}  (measured usage {MeasuredTurns} · token-estimated {EstimatedTurns} · unaccounted {UnaccountedTurns})",
            $"wall clock : {Wall.TotalSeconds,8:0.0} s in live turns · slowest turn {SlowestTurn.TotalSeconds:0.0} s"
                + (MeasuredTurns + EstimatedTurns > 0
                    ? $" · mean {Wall.TotalSeconds / Math.Max(1, MeasuredTurns + EstimatedTurns):0.0} s"
                    : ""),
            "",
            $"MEASURED (provider usage block) — {MeasuredTurns} turn(s)",
            $"  prompt     {MeasuredPromptTokens,10:N0} tok",
            $"  completion {MeasuredCompletionTokens,10:N0} tok",
            $"  total      {MeasuredTotalTokens,10:N0} tok",
        };

        if (EstimatedTurns > 0)
        {
            lines.Add("");
            lines.Add($"⚠ TOKEN-ESTIMATED (provider returned NO usage block) — {EstimatedTurns} turn(s)");
            lines.Add($"  prompt     {EstimatedPromptTokens,10:N0} tok   ← counted from text length, NOT a measurement");
            lines.Add($"  completion {EstimatedCompletionTokens,10:N0} tok   ← counted from text length, NOT a measurement");
        }

        lines.Add("");
        if (rate is null)
        {
            lines.Add($"cost: NOT COMPUTED — ModelPricing has no row matching '{model}', and this ledger");
            lines.Add("      will not invent a rate. The token counts above stand on their own.");
        }
        else
        {
            lines.Add($"rate applied: USD {rate.Value.InputPricePerMillion:0.####} / 1M in · "
                    + $"USD {rate.Value.OutputPricePerMillion:0.####} / 1M out   [source: AgentEval ModelPricing table]");
            lines.Add($"cost on MEASURED tokens : USD {measuredCost!.Value.ToString("0.0000", CultureInfo.InvariantCulture)}");
            if (estimatedCost is not null)
                lines.Add($"cost on ESTIMATED tokens: USD {estimatedCost.Value.ToString("0.0000", CultureInfo.InvariantCulture)}  (derived from estimated counts — not a measurement)");
            lines.Add("⚠ the rate is this repository's table, NOT an invoice. Several of its rows are marked");
            lines.Add("  '(placeholder)' in source, so read the TOKENS as the result and the currency as arithmetic.");
        }

        EvalPanel.Open($"Spend — {label} (accumulated from the harness's own usage blocks)");
        EvalPanel.Section("  TOKENS ARE THE MEASUREMENT; CURRENCY IS DERIVED FROM THEM AT A TABLE RATE");
        EvalPanel.Divider();
        foreach (string line in lines)
        {
            if (line.Length == 0) { EvalPanel.Row(""); continue; }
            EvalPanel.Row("  " + line,
                line.StartsWith('⚠') || line.StartsWith("  '") || line.StartsWith("      ")
                    ? ConsoleColor.Yellow
                    : ConsoleColor.White);
        }
        EvalPanel.Close();
    }
}
