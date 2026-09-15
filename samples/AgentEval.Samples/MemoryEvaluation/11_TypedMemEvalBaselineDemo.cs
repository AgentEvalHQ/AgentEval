// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.External;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentEval.Output;
using AgentEval.Samples.Benchmarks;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample G11: TypedMemEval baseline — what a plain model scores, and what that score is WORTH.
///
/// The LongMemEval samples answer "how did the model do". This one answers the question that
/// follows, which TypedMemEval's sidecars can support and LongMemEval's cannot:
///
///   - a score beside its CHANCE FLOOR, because a shape whose question names its own alternatives
///     hands a guesser points for free, and "0.733" and "0.400 above chance" are different claims;
///   - a score beside the CEILING FOR ITS OWN CONDITION. This sample hands the reader the whole
///     haystack, which is the corpus's published `v8_full_haystack` arm -- so the number to read
///     it against is V8, not a retrieval figure. Printing headroom (V1-V9, a property of
///     RETRIEVAL) beside a no-retrieval score would invite exactly the misreading this sample
///     exists to prevent, so the two are reported in separate blocks;
///   - the shape's RETRIEVER AGREEMENT, so a good result on a shape that cannot separate two
///     systems is visibly not evidence of much;
///   - the RETRIEVER NAMED in the output, because every dense figure in this family is conditional
///     on one and saying so is the whole point of the v0.37/v0.38 work.
///
/// WHAT THIS SAMPLE DELIBERATELY DOES NOT PRINT: a single "TypedMemEval score". The family's own
/// quality board refuses one, because a mean hides exactly the per-shape structure the corpus
/// exists to expose -- four verticals sit below the recovered-scale mean, and averaging them away
/// would be the defect this benchmark was built to make visible.
///
/// No new grading machinery: the run goes through <see cref="TypedMemEvalRunner"/>, the same path
/// `agenteval bench typedmemeval` uses. Everything below the run is presentation, joined to the
/// sidecar that ships inside the package.
///
/// REPORTS: the result is projected to an <see cref="EvalResult"/> by
/// <see cref="TypedMemEvalEvalResultAdapter"/> -- which lives in CORE and is the same projection
/// the CLI uses -- and written through the same artifact path the LongMemEval benchmark sample
/// takes. That yields the canonical `.agenteval/` run record plus sidecar JSON, HTML and PDF.
/// Nothing here renders or scores anything itself; a sample that grew its own reporting stack
/// would be the defect, not the feature.
///
/// REAL ONLY. There is no mock path. With no credentials this sample PRINTS A WARNING AND STOPS,
/// because a memory benchmark answered by a canned agent measures nothing and a green tick from
/// one would be a lie.
///
/// Prerequisites:
/// - AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY
/// - a chat deployment (AIConfig.ModelDeployment)
/// The corpora ship embedded in AgentEval.Memory, so there is nothing to download.
/// </summary>
public static class TypedMemEvalBaselineDemo
{
    /// <summary>
    /// One vertical by default. Temporal is the useful demonstrator: three shapes that land in two
    /// different retriever-agreement classes, so the output shows the distinction rather than
    /// describing it. Raising this to the full family is ~587 questions and roughly 1,200 calls.
    /// </summary>
    private const TypedMemEvalVertical Vertical = TypedMemEvalVertical.Temporal;

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        var deployment = AIConfig.ModelDeployment;
        var azure = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azure.GetChatClient(deployment).AsIChatClient();

        // Reader and judge are the same model here, as in the LongMemEval baseline sample. The
        // family's own judge-family bias question is closed separately: two non-OpenAI judges agree
        // with the shipped judge at 0.99910 and 0.99852 re-weighted.
        var agent = chatClient.AsEvaluableAgent(
            name: $"TypedMemEval-{deployment}",
            systemPrompt: "You are a helpful assistant. Answer using our conversation history. "
                        + "If the history does not contain the answer, say so plainly.",
            includeHistory: true);

        var runner = new TypedMemEvalRunner(chatClient);
        var options = new TypedMemEvalOptions
        {
            RandomSeed = 42,      // reproducible selection
            IncludeTimestamps = true,
        };

        Console.WriteLine($"   Vertical:  {Vertical}");
        Console.WriteLine($"   Reader:    {deployment}");
        Console.WriteLine($"   Judge:     {deployment} (same model)");
        Console.WriteLine();
        Console.WriteLine("   Running... one call per question plus one judge call.");
        Console.WriteLine();

        var result = await runner.RunAsync(agent, Vertical, options).ConfigureAwait(false);

        var sidecar = LoadSidecar(Vertical);
        PrintPerShape(result, sidecar, LoadShapeMap(Vertical));
        PrintReading(sidecar);

        await WriteReportsAsync(result, agent, deployment).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the run: canonical <c>.agenteval/</c> record plus sidecar JSON / HTML / PDF.
    ///
    /// The projection is <see cref="TypedMemEvalEvalResultAdapter.ToEvalResult"/> from CORE -- the
    /// same one `agenteval bench typedmemeval` uses -- and the writing is the shared benchmark
    /// helper the LongMemEval sample uses. No rendering, scoring or persistence logic is defined
    /// in this file.
    ///
    /// The adapter REQUIRES <see cref="ExternalBenchmarkResult.TypedOutcomes"/> and throws without
    /// it. <see cref="TypedMemEvalRunner"/> populates it via its report builder on the same call
    /// made above, so it is present -- but a future runner change could silently stop populating
    /// it, and a sample that died with an ArgumentException after paying for ~100 model calls
    /// would be a poor trade. Hence the explicit check: the measurement is already printed, and a
    /// failure to FILE it must not read as a failure to MAKE it.
    /// </summary>
    private static async Task WriteReportsAsync(
        ExternalBenchmarkResult result,
        IEvaluableAgent agent,
        string deployment)
    {
        if (result.TypedOutcomes is null)
        {
            Console.WriteLine("  \u26a0 The run carries no TypedOutcomes, so no report was written. The per-shape");
            Console.WriteLine("  table above still stands — it is read from the run and the shipped sidecar.");
            Console.WriteLine();
            return;
        }

        Console.WriteLine("WRITING REPORTS");
        Console.WriteLine();

        try
        {
            var evalTree = TypedMemEvalEvalResultAdapter.ToEvalResult(
                result,
                judgeModel: deployment);

            var subject = new SubjectIdentity(
                Kind: SubjectKind.Agent,
                Name: agent.Name,
                ModelId: deployment,
                Framework: "MAF");

            var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
                evalTree,
                subject,
                benchmarkName: "typedmemeval",
                regulationOrBenchmark: $"TypedMemEval — {Vertical} ({TypedMemEvalVerticalDescriptor.CorpusRevision})",
                includePdf: true,
                regulationCodeForEvidence: null,   // not a compliance benchmark
                presetLabel: Vertical.ToString(),
                judgeModel: deployment).ConfigureAwait(false);

            BenchmarkSampleHelpers.PrintReportPaths(evalTree, paths);

            Console.WriteLine();
            Console.WriteLine("  The report carries the TYPED OUTCOME VECTOR, which is the citable form. The single");
            Console.WriteLine("  score on the root node exists for tooling compatibility and is not the result —");
            Console.WriteLine("  read the per-shape nodes, for the reason the table above prints no aggregate.");
            Console.WriteLine();

            BenchmarkSampleHelpers.OfferToOpenReports(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The measurement succeeded and is already on screen. Losing the file is not losing it.
            Console.WriteLine($"  \u26a0 The run completed but its report could not be written: {ex.Message}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// The shipped sidecar for this vertical, read from the package rather than from disk so the
    /// numbers beside the score are the ones the consumer's copy carries.
    /// </summary>
    private static JsonElement LoadSidecar(TypedMemEvalVertical vertical)
        => JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical)).RootElement;

    private static void PrintPerShape(
        ExternalBenchmarkResult result,
        JsonElement sidecar,
        IReadOnlyDictionary<string, string> shapeOf)
    {
        var probes = sidecar.GetProperty("probes");
        var byShape = probes.GetProperty("by_shape");
        var sensitivity = probes.TryGetProperty("retriever_sensitivity", out var rs) ? rs : default;
        var sensitivityByShape = sensitivity.ValueKind == JsonValueKind.Object
            ? sensitivity.GetProperty("by_shape")
            : default;

        // Grouped by the shape THE CORPUS DECLARES, joined on question_id -- never by splitting
        // `question_type`, which carries the shape for `temporal` and for almost nothing else.
        //
        // EVERY SELECTED QUESTION REACHES THE TABLE. Filtering on `Correct is not null` first
        // would drop agent errors and inconclusive verdicts BEFORE grouping, so a partial run
        // would print a clean rate over the survivors and use that shrunken count as the V8
        // denominator. A question that failed to produce a verdict is not absent from the run;
        // it is a hole in it, and the table has to show the hole.
        var attempted = result.QuestionResults.ToList();
        var unresolved = attempted.Count(q => !shapeOf.ContainsKey(q.QuestionId));

        var scored = attempted
            .Where(q => shapeOf.ContainsKey(q.QuestionId))
            .GroupBy(q => shapeOf[q.QuestionId])
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (unresolved > 0)
        {
            // NO SILENT DROPS. A question whose shape the corpus does not declare is not a
            // question worth zero -- it is one this table cannot place, and saying so beats
            // quietly shrinking the denominator.
            Console.WriteLine($"  \u26a0 {unresolved} question(s) carry no declared shape and are NOT in the");
            Console.WriteLine("  table below. The corpus and the run have diverged; treat both blocks as partial.");
            Console.WriteLine();
        }

        // ─── BLOCK 1: this reader, against the ceiling for THIS reader's condition ───────
        //
        // The runner injects every haystack session, so nothing is retrieved and nothing is
        // selected. That is the corpus's own `v8_full_haystack` arm, and V8 is therefore the
        // ceiling that applies here. A retrieval figure would not be.
        Console.WriteLine("THE READER — full haystack, no retrieval (the corpus's V8 condition)");
        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"  {"shape",-24} {"n",3} {"judged",6} {"model",7} {"V8 pub",7} {"floor",7} {"above ch.",9}");
        Console.WriteLine(new string('-', 88));

        var diverged = 0;
        var incomparable = 0;
        var compared = 0;
        var totalAttempted = 0;
        var totalJudged = 0;
        foreach (var group in scored)
        {
            var n = group.Count();                                   // attempted
            var judged = group.Count(q => q.Correct is not null);     // reached a verdict
            if (judged > 0) totalJudged += judged;
            totalAttempted += n;

            // The rate names its own denominator: correct over JUDGED, never over attempted.
            // An unjudged question is not a wrong answer, and it is not a free pass either.
            double? score = judged > 0 ? group.Count(q => q.Correct == true) / (double)judged : null;

            var v8 = Rate(byShape, group.Key, "v8_passed", "v8_applicable");
            var floor = ReadDouble(byShape, group.Key, "chance_floor");

            // ABOVE CHANCE, not raw. A guesser scores the floor for free, so the raw figure
            // overstates by exactly that much on any shape whose question names its alternatives.
            // A floor that is ABSENT is not a floor of zero — it prints as "-" and stays out of
            // the arithmetic rather than flattering the row by 0.000.
            var above = score is { } sc && floor is { } f ? $"{sc - f,9:F3}" : "        -";

            // V8 was measured on the same corpus through the CLI's probe path. A divergence here
            // is a signal about the wiring, not about the model: same condition, same questions.
            //
            // ONLY IF THE DENOMINATORS MATCH. Two rates over different question sets are not
            // comparable, and calling them equal because the quotients agree is the same mistake
            // as comparing a count to a population. When V8's applicable set is not this run's
            // set, the figure is still shown — the CLAIM of agreement is what gets withheld.
            // COMPARABLE NEEDS BOTH HALVES: the published arm must cover the same number of
            // questions, AND this run must have judged all of them. A rate over 12 of 15 is not
            // the same measurement as a rate over 15, however close the quotients look.
            var v8n = ReadDouble(byShape, group.Key, "v8_applicable");
            var sameCount = v8 is not null && v8n is { } vn && Math.Abs(vn - n) < 0.5;
            var complete = judged == n;
            var comparable = sameCount && complete && score is not null;

            if (comparable) compared++;

            string mark;
            if (comparable && Math.Abs(score!.Value - v8!.Value) > 0.0005)
            {
                mark = "  <- differs from V8";
                diverged++;
            }
            else if (v8 is not null && !complete)
            {
                mark = $"  <- {n - judged} unjudged, not comparable";
                incomparable++;
            }
            else if (v8 is not null && !sameCount)
            {
                mark = $"  <- not comparable: V8 covered {v8n:F0}, this run {n}";
                incomparable++;
            }
            else
            {
                mark = string.Empty;
            }

            Console.WriteLine(
                $"  {group.Key,-24} {n,3} {judged,6} "
                + $"{(score is { } sv ? $"{sv,7:F3}" : "      -")} "
                + $"{(v8 is { } vv ? $"{vv,7:F3}" : "      -")} "
                + $"{(floor is { } fl ? $"{fl,7:F3}" : "      -")} {above}{mark}");
        }

        Console.WriteLine(new string('-', 88));
        if (totalJudged < totalAttempted)
        {
            Console.WriteLine($"  \u26a0 PARTIAL RUN: {totalAttempted - totalJudged} of {totalAttempted} question(s) never reached a verdict");
            Console.WriteLine("  (agent error, or a judge outcome that was neither yes nor no). Their shapes are");
            Console.WriteLine("  still listed, with `judged` below `n`. Rates are over JUDGED questions only, and");
            Console.WriteLine("  no shape with a hole in it is compared against V8.");
        }
        // APPLICABILITY COMES FROM THE INPUT, NOT THE RESULT. "Every shape reproduces V8" is a
        // claim about the shapes that HAVE a V8 arm to reproduce. A shape whose arm was never
        // applicable (forgetting/never-known publishes v8_applicable: 0) is not a silent pass,
        // so the count is stated rather than the word "every".
        if (diverged == 0 && incomparable == 0 && compared > 0)
        {
            Console.WriteLine($"  All {compared} of {scored.Count} shape(s) with a published V8 arm reproduce it exactly.");
            Console.WriteLine("  Same corpus, same condition, reached through a different entry point — a check on");
            Console.WriteLine("  this sample's wiring, NOT an independent confirmation of the score: the published");
            Console.WriteLine("  probe used this same deployment.");
        }
        if (compared == 0)
        {
            Console.WriteLine($"  No shape here publishes a V8 arm, so none of the {scored.Count} rows above was checked");
            Console.WriteLine("  against one. The model column stands on its own.");
        }
        if (diverged > 0)
        {
            Console.WriteLine($"  ⚠ {diverged} shape(s) differ from the published V8 arm. Same questions and the same");
            Console.WriteLine("  condition should give the same rate, so read this as a wiring or deployment");
            Console.WriteLine("  difference before reading it as a result.");
        }
        if (incomparable > 0)
        {
            Console.WriteLine($"  ⚠ {incomparable} shape(s) could NOT be compared: the published V8 arm covers a");
            Console.WriteLine("  different number of questions than this run scored, so the two rates are over");
            Console.WriteLine("  different sets. The V8 column is still shown; the agreement claim is withheld.");
        }
        Console.WriteLine();

        // ─── BLOCK 2: what a RETRIEVING system faces — which this reader is not ──────────
        Console.WriteLine("A RETRIEVING SYSTEM — what these shapes do to one (this reader is NOT one)");
        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"  {"shape",-24} {"V9 ref",7} {"headroom",9}  ranking");
        Console.WriteLine(new string('-', 88));

        foreach (var group in scored)
        {
            var v9 = Rate(byShape, group.Key, "v9_passed", "v9_applicable");
            var headroom = ReadDouble(byShape, group.Key, "headroom_perfect_selector");
            var ranking = ReadString(sensitivityByShape, group.Key, "retriever_agreement") ?? "-";

            Console.WriteLine(
                $"  {group.Key,-24} "
                + $"{(v9 is { } v ? $"{v,7:F3}" : "      -")} "
                + $"{(headroom is { } hr ? $"{hr,9:F3}" : "        -")}  {ranking}");
        }

        Console.WriteLine(new string('-', 88));
        Console.WriteLine("  These columns do NOT describe the run above. The reader was handed everything;");
        Console.WriteLine("  it selected nothing, so it left nothing on the table. Headroom is the gap a system");
        Console.WriteLine("  that RETRIEVES has to close, and it is the reason the corpus exists.");
        Console.WriteLine();
    }

    private static void PrintReading(JsonElement sidecar)
    {
        var probes = sidecar.GetProperty("probes");

        Console.WriteLine("HOW TO READ THIS");
        Console.WriteLine();
        Console.WriteLine("  n         questions this run ATTEMPTED on that shape.");
        Console.WriteLine("  judged    how many reached a yes/no verdict. Below n means a partial run;");
        Console.WriteLine("            those shapes are not compared against V8.");
        Console.WriteLine("  model     the reader's pass rate on that shape, with the whole haystack in");
        Console.WriteLine("            context. No retrieval happens, so this is the V8 condition.");
        Console.WriteLine("  V8 pub    the corpus's published full-haystack arm — the SAME condition, so");
        Console.WriteLine("            this is the ceiling that applies to the run above.");
        Console.WriteLine("  floor     what a guesser scores for free, where the question names its");
        Console.WriteLine("            own alternatives. Subtract it before claiming anything. A shape");
        Console.WriteLine("            with no floor prints a dash: absent is not zero.");
        Console.WriteLine("  V9 ref    the same questions under the reference retriever, top-K. This is");
        Console.WriteLine("            the arm a real memory system sits in.");
        Console.WriteLine("  headroom  V1 minus V9: what a PERFECT gold selector leaves on the table for");
        Console.WriteLine("            a RETRIEVING system. Below 0.15 the shape cannot separate two.");
        Console.WriteLine("  ranking   whether the shape discriminates under BOTH published retrievers:");
        Console.WriteLine("              robust-ranking       it ranks you under either");
        Console.WriteLine("              retriever-sensitive  the two disagree -- read cautiously");
        Console.WriteLine("              non-ranking          neither; a good score here proves little");
        Console.WriteLine("              not-applicable       empty gold, so the operand is undefined");
        Console.WriteLine();

        if (probes.TryGetProperty("retriever_sensitivity", out var rs))
        {
            Console.WriteLine("  Retrievers these columns are conditional on:");
            if (rs.TryGetProperty("reference_retriever", out var bm)) Console.WriteLine($"    reference : {bm.GetString()}");
            if (rs.TryGetProperty("dense_retriever", out var d1)) Console.WriteLine($"    dense     : {d1.GetString()}");
            if (rs.TryGetProperty("second_dense_retriever", out var d2)) Console.WriteLine($"    dense (2) : {d2.GetString()}");
            Console.WriteLine();
        }

        Console.WriteLine("  WHY THE READER SCORES SO HIGH: it is not being asked to remember anything. The");
        Console.WriteLine("  whole haystack is in its context, so this measures reading, not memory. The gap");
        Console.WriteLine("  between the two blocks above IS the benchmark — hand a system everything and the");
        Console.WriteLine("  shapes look solved; make it retrieve and V9 is where it actually lands.");
        Console.WriteLine();
        Console.WriteLine("  NO AGGREGATE SCORE IS PRINTED, deliberately. A mean over these shapes would");
        Console.WriteLine("  hide the structure they exist to expose -- across the family, 44% of questions");
        Console.WriteLine("  sit in shapes that cannot rank a dense-retrieval reader at all, and averaging");
        Console.WriteLine("  them into the rest is the defect this benchmark was built to make visible.");
        Console.WriteLine();
    }

    /// <summary>
    /// question_id -&gt; the shape the corpus declares for it, read from the packaged corpus.
    ///
    /// NOT derived from <c>question_type</c>. That string carries the shape for `temporal` and
    /// for almost nothing else: `workingmemory` files every question as `workingmemory-recall`
    /// while the sidecar keys on five `distance-*` shapes, and `bitemporal-belief` has to reach
    /// `belief-at-instant`. Splitting the type yields keys that match no sidecar row, so every
    /// column renders "-" and the table looks empty rather than wrong. The shape is a FIELD.
    /// </summary>
    private static Dictionary<string, string> LoadShapeMap(TypedMemEvalVertical vertical)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        using var doc = JsonDocument.Parse(TypedMemEvalCorpus.ReadJson(vertical));
        var root = doc.RootElement;
        var questions = root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty("questions", out var wrapped)
            ? wrapped
            : root;

        if (questions.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var q in questions.EnumerateArray())
        {
            if (q.TryGetProperty("question_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && q.TryGetProperty("typedmemeval", out var block)
                && block.TryGetProperty("shape", out var shape)
                && shape.ValueKind == JsonValueKind.String)
            {
                map[id.GetString()!] = shape.GetString()!;
            }
        }

        return map;
    }

    /// <summary>
    /// A published arm as a rate. Returns null when either operand is missing OR the denominator
    /// is zero — an arm that was never applicable has no rate, and 0/0 must not print as 0.000.
    /// </summary>
    private static double? Rate(JsonElement byShape, string shape, string passedField, string applicableField)
    {
        var passed = ReadDouble(byShape, shape, passedField);
        var applicable = ReadDouble(byShape, shape, applicableField);
        return passed is { } p && applicable is { } a && a > 0 ? p / a : null;
    }

    private static double? ReadDouble(JsonElement byShape, string shape, string field)
        => byShape.ValueKind == JsonValueKind.Object
           && byShape.TryGetProperty(shape, out var row)
           && row.TryGetProperty(field, out var value)
           && value.ValueKind is JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string? ReadString(JsonElement byShape, string shape, string field)
        => byShape.ValueKind == JsonValueKind.Object
           && byShape.TryGetProperty(shape, out var row)
           && row.TryGetProperty(field, out var value)
           && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("==========================================================================");
        Console.WriteLine("  TypedMemEval baseline — a score, and what it is worth");
        Console.WriteLine("==========================================================================");
        Console.WriteLine();
    }
}
