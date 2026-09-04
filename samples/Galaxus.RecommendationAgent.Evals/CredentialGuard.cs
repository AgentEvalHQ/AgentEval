// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// The ONE place this suite decides what happens when an eval that needs a model is run without
/// one. Every eval that calls a language model routes its missing-credentials branch through
/// <see cref="Blocks"/>; every eval that calls none announces itself through
/// <see cref="DeclareModelFree"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule, in one sentence.</b> An eval that needs a model and does not have one has
/// <i>measured nothing</i>, and an eval that has measured nothing must not exit 0 and must not
/// print a number produced by anything else.
/// </para>
/// <para>
/// <b>The defect this replaces, and it was in every model-backed eval at once.</b> Each of Evals
/// 01, 02, 05, 06, 08 and 09 ended its credentials check with <c>return ci ? 3 : 0;</c>. Outside
/// CI — which is how a human runs them — a missing key produced <b>exit code 0</b>. Exit 0 is the
/// same code a passing gate returns, so `dotnet run -- 5; echo $?` printed `0` for "recommendation
/// quality is excellent" and for "no model was ever contacted", and nothing downstream of the
/// process could tell those apart. The <c>ci</c> parameter existed only to select between those two
/// codes, so it has been removed from all six entry points rather than left as a switch someone can
/// flip back.
/// </para>
/// <para>
/// <b>Why 3 and not 1.</b> A failed gate (1) and an unmeasured eval (3) are different facts and
/// collapsing them loses the one a reader needs. 3 says "there is no verdict here"; 1 says "there is
/// a verdict and it is bad". <c>Program.cs</c> ranks 1 above 3 when it folds the suite's codes
/// together, so a real failure can never be reported as an absence of measurement.
/// </para>
/// <para>
/// <b>The substitution this forbids.</b> Several of these evals have deterministic arms — a
/// tag-join oracle, a popularity baseline, a rubber-stamp loop, Demo 2's loop on its offline path —
/// that run perfectly well with no credentials at all. Running those and printing their numbers
/// under a panel headed "single agent" or "recommendation quality" would be reporting a control as
/// if it were the agent. That is the exact confusion this suite exists to remove, so the guard
/// refuses to measure anything rather than measure the wrong thing.
/// </para>
/// <para>
/// <b>What is still allowed.</b> <c>--dry-run</c>. It replaces the model with a deliberately
/// implausible stub, spends nothing, writes no snapshot, and every eval labels its output as
/// plumbing rather than as a result. The guard lets a dry run through precisely because the dry run
/// is honest about what it is.
/// </para>
/// </remarks>
public static class CredentialGuard
{
    /// <summary>
    /// The exit code for "this eval needed a model, had none, and therefore measured nothing".
    /// Distinct from 0 (passed) and from 1 (failed a gate).
    /// </summary>
    public const int NotMeasuredExitCode = 3;

    /// <summary>The exact phrase every unmeasured eval prints, so it can be grepped for in a log.</summary>
    public const string NotMeasuredBanner = "NOT MEASURED — no credentials";

    /// <summary>
    /// Decides whether an eval that needs a language model may proceed, and prints the refusal when
    /// it may not.
    /// </summary>
    /// <param name="evalName">Display name, e.g. <c>"Eval 05"</c>.</param>
    /// <param name="subject">
    /// What would have been measured, as a noun phrase that reads naturally after "…was":
    /// <c>"Recommendation quality"</c>, <c>"The agent's tool trajectory"</c>. It is printed in the
    /// sentence that says the thing was NOT measured, so it must name the missing measurement and
    /// not the eval.
    /// </param>
    /// <param name="dryRun">
    /// True when the caller is running against a stub model. A dry run needs no credentials and is
    /// never blocked — it spends nothing, writes nothing, and labels its own output as plumbing.
    /// </param>
    /// <param name="alsoTrue">
    /// Optional extra lines specific to this eval — typically which of its arms would have run
    /// without a key, and why running them anyway would misreport a control as the agent.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the eval may proceed; otherwise the exit code the eval MUST
    /// return without printing any score. Use it as
    /// <c>if (CredentialGuard.Blocks("Eval 05", "Recommendation quality", dryRun) is { } stop) return stop;</c>
    /// </returns>
    public static int? Blocks(string evalName, string subject, bool dryRun, params string[] alsoTrue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (dryRun || Config.IsConfigured) return null;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  ⚠️  {evalName} — {NotMeasuredBanner}.");
        Console.ResetColor();

        // The subject goes on its OWN line. Interpolating it into a sentence made the first line
        // run to 115 characters for the longer subjects, past the 82-column frame every other
        // panel in this suite keeps to, and a wrapped-by-the-terminal refusal reads as debris.
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"     NOT MEASURED: {subject}.");
        Console.WriteLine("     No model was called, so there is no number to print — and none will be");
        Console.WriteLine("     substituted from a deterministic arm. An offline number under this heading");
        Console.WriteLine("     would be a control reported as if it were the agent.");

        foreach (string line in alsoTrue ?? [])
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Console.WriteLine($"     {line}");
        }

        Console.WriteLine();
        Console.WriteLine("     Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY (AZURE_OPENAI_DEPLOYMENT is");
        Console.WriteLine($"     optional and defaults to {Config.PreferredDeployment}) — or add --dry-run, which runs the whole");
        Console.WriteLine("     code path against a stub model and spends nothing.");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"     Exit code {NotMeasuredExitCode}, never 0. An eval that measured nothing has not passed.");
        Console.ResetColor();
        Console.WriteLine();

        return NotMeasuredExitCode;
    }

    /// <summary>
    /// The positive half of the same rule: an eval that makes NO model call says so out loud, and
    /// says what its numbers are about, before it prints any of them.
    /// </summary>
    /// <remarks>
    /// Silence here is what let the confusion start. Evals 03, 04 and 07 produce real, useful,
    /// perfectly credible numbers with no model anywhere in the process, and a reader who arrives at
    /// a table of green rows part-way down a scrollback has nothing telling them which kind of run
    /// they are looking at. This prints that sentence in one voice for all of them.
    /// </remarks>
    /// <param name="evalName">Display name, e.g. <c>"Eval 03"</c>.</param>
    /// <param name="whatTheNumbersAreAbout">
    /// A noun phrase naming the subject of this eval's numbers — <c>"the instrument's ability to
    /// fail"</c>, <c>"the structural containment constraint"</c>, <c>"the workflow graph's
    /// mechanics"</c>. It is printed as "…is a fact about &lt;this&gt;".
    /// </param>
    public static void DeclareModelFree(string evalName, string whatTheNumbersAreAbout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(whatTheNumbersAreAbout);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ℹ️  {evalName} makes NO model call and needs no credentials.");
        Console.WriteLine("     Every number below is a fact about");

        // The subject is wrapped rather than interpolated: Eval 03's phrase is 62 characters and
        // Eval 07's is 58, and both blew straight through the 82-column frame the rest of this
        // suite keeps to when they sat inside a sentence.
        foreach (string line in Wrap(whatTheNumbersAreAbout, 68))
            Console.WriteLine($"       {line}");

        Console.WriteLine("     and none of them is a fact about the agent: this eval cannot pass or fail");
        Console.WriteLine("     on the agent's behalf, in either direction.");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>Greedy word wrap. Long enough for this file's two callers and nothing more.</summary>
    /// <param name="text">Text to wrap.</param>
    /// <param name="width">Maximum line width.</param>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }

    /// <summary>
    /// One line naming which evals in this suite need a model, for the menu and the usage text.
    /// Kept beside the rule so the list and the enforcement cannot drift apart.
    /// </summary>
    public const string NeedsModelSummary = "Evals 1, 2, 5, 6, 8, 9 need a model. Evals 3, 4, 7 need none.";
}
