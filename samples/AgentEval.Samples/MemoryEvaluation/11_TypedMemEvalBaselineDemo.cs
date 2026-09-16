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
using Microsoft.Extensions.Logging;

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
    /// EVERY vertical, because the family's whole claim is that memory is not one skill. Running
    /// only Temporal would exercise 3 of 36 shapes and demonstrate the opposite of the point.
    ///
    /// DEPTH is a preset — `--preset smoke|standard|audit-grade`, matching every other benchmark
    /// sample. It sets questions PER VERTICAL, never which answers count, so a smaller preset
    /// buys a wider interval and not an easier test. Measured at 7.6s per question:
    ///
    ///   smoke        4 per vertical   ~40 questions   ~5 min
    ///   standard    12 per vertical  ~120 questions  ~15 min
    ///   audit-grade  the whole family    587          ~74 min
    ///
    /// ⚠ BREADTH IS NOT CONFIDENCE. 36 shapes share whatever budget the preset sets, so a smoke
    /// sweep leaves roughly one question per shape. Every row prints its own `n` for exactly this
    /// reason — read the width of the evidence before reading the rate.
    /// </summary>
    private static readonly TypedMemEvalVertical[] Verticals =
        Enum.GetValues<TypedMemEvalVertical>();

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

        // DEPTH, resolved the way every other benchmark sample resolves it:
        // CLI `--preset <name>` > AGENTEVAL_SAMPLES_PRESET > interactive prompt > Smoke.
        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        // Questions PER VERTICAL. A smaller preset samples fewer questions, not easier ones.
        var maxQuestions = preset switch
        {
            SamplePreset.Smoke => (int?)4,
            SamplePreset.Standard => 12,
            _ => null      // audit-grade: every question in every vertical
        };

        // PROGRESS. The runner already logs `[i/N] shape outcome` after every question; without a
        // logger it defaults to NullLogger and a 50-question run sits silent for minutes. This
        // wires the console up rather than adding a second reporting mechanism beside it.
        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information)   // AgentEval.Core has its own LogLevel
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            }));

        var runner = new TypedMemEvalRunner(chatClient, loggerFactory.CreateLogger<TypedMemEvalRunner>());
        var options = new TypedMemEvalOptions
        {
            RandomSeed = 42,      // reproducible selection
            IncludeTimestamps = true,
            MaxQuestions = maxQuestions,
        };

        Console.WriteLine($"   Verticals: {Verticals.Length} (the whole family)");
        Console.WriteLine($"   Questions: {(maxQuestions is { } cap ? $"up to {cap} per vertical" : "every question — 587")}");
        Console.WriteLine($"   Reader:    {deployment}");
        Console.WriteLine($"   Judge:     {deployment} (same model)");
        Console.WriteLine();
        // Judge retries are real calls. MaxJudgeRetries defaults to 1, so a judge that returns
        // an unparsable verdict costs another. Quoting the floor as if it were the total
        // understates what this run bills.
        Console.WriteLine("   Running... at least one call per question plus one judge call;");
        Console.WriteLine("   a judge retry adds more. The exact total is reported at the end.");
        Console.WriteLine("   Each question prints [i/N] shape outcome as it completes.");
        Console.WriteLine();

        // One run per vertical: the runner measures a single vertical per call, by design —
        // the corpora are independent and each carries its own sidecar and sha.
        var results = new List<ExternalBenchmarkResult>();
        foreach (var vertical in Verticals)
        {
            Console.WriteLine();
            Console.WriteLine($"── {vertical} " + new string('─', Math.Max(0, 62 - vertical.ToString().Length)));
            results.Add(await runner.RunAsync(agent, vertical, options).ConfigureAwait(false));
        }

        Console.WriteLine();
        foreach (var (vertical, result) in Verticals.Zip(results))
        {
            PrintPerShape(vertical, result, LoadSidecar(vertical), LoadShapeMap(vertical));
        }

        PrintReading(LoadSidecar(Verticals[0]));
        await WriteReportsAsync(results, agent, deployment).ConfigureAwait(false);
        PrintKeyTakeaways(results);
    }

    /// <summary>Closes the sample the way its siblings in this group do.</summary>
    private static void PrintKeyTakeaways(IReadOnlyList<ExternalBenchmarkResult> results)
    {
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("   * A score is not a result until you know its FLOOR and its CEILING.");
        Console.WriteLine("   * This reader had the whole haystack, so it was never asked to");
        Console.WriteLine("     remember anything — that is the V8 condition, not a memory test.");
        Console.WriteLine("   * V9 is where a system that must RETRIEVE lands. The gap is the point.");
        Console.WriteLine("   * A shape whose ranking class is non-ranking cannot separate two");
        Console.WriteLine("     systems at all; a good score there is not evidence.");
        Console.WriteLine($"   * Memory is not ONE skill: {results.Count} verticals measure different");
        Console.WriteLine("     constructs, which is why no single number is printed for the family.");
        Console.WriteLine($"   * Model calls billed by this run: {results.Sum(r => r.TotalLlmCalls)}"
                          + (results.Sum(r => r.TotalJudgeRetryLlmCalls) is var retries && retries > 0
                             ? $" (including {retries} judge retry call(s))"
                             : string.Empty));
        Console.WriteLine("   * --preset audit-grade runs all 587 questions (~74 min).");
        Console.WriteLine(new string('=', 70));
        Console.WriteLine();
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
        IReadOnlyList<ExternalBenchmarkResult> results,
        IEvaluableAgent agent,
        string deployment)
    {
        if (results.Any(r => r.TypedOutcomes is null))
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
                results,
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
                regulationOrBenchmark: $"TypedMemEval — {results.Count} verticals ({TypedMemEvalVerticalDescriptor.CorpusRevision})",
                includePdf: true,
                regulationCodeForEvidence: null,   // not a compliance benchmark
                presetLabel: $"{results.Count}-vertical sweep",
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
    {
        // Cloned while the document is alive. Returning `.RootElement` from an undisposed
        // JsonDocument roots its pooled buffer until finalization; a JsonElement from a DISPOSED
        // one throws on access. Clone is the only correct way to outlive the document.
        using var document = JsonDocument.Parse(TypedMemEvalCorpus.ReadMetadataJson(vertical));
        return document.RootElement.Clone();
    }

    private static void PrintPerShape(
        TypedMemEvalVertical vertical,
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
        Console.WriteLine($"{vertical.ToString().ToUpperInvariant()} — full haystack, no retrieval (the V8 condition)");
        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"  {"shape",-24} {"n",3} {"judged",6} {"model",7} {"V8 pub",7} {"floor",7} {"above ch.",9}");
        Console.WriteLine(new string('-', 88));

        var diverged = 0;
        var incomparable = 0;
        var compared = 0;        // same condition AND same denominator AND fully judged
        var publishesV8 = 0;     // has a V8 arm at all -- a DIFFERENT fact
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

            if (v8 is not null) publishesV8++;
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
                mark = $"  <- V8 covers {v8n:F0}, this run {n} — not comparable";
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
        // MUTUALLY EXCLUSIVE BRANCHES. The earlier version could print "no shape publishes a V8
        // arm" immediately below a V8 column reading 1.000, because it asked `compared == 0` --
        // which is true whenever the DENOMINATORS differ, not only when the arm is absent.
        // "Has a V8 arm" and "was comparable with this run" are two facts, and one counter was
        // answering for both.
        if (publishesV8 == 0)
        {
            Console.WriteLine($"  None of the {scored.Count} shape(s) here publishes a V8 arm, so the model column");
            Console.WriteLine("  stands on its own.");
        }
        else if (compared == 0)
        {
            Console.WriteLine($"  {publishesV8} shape(s) publish a V8 arm, but NONE was comparable with this run —");
            Console.WriteLine("  the published arm covers the whole vertical and this run sampled a subset, so the");
            Console.WriteLine("  two rates are over different question sets. The V8 column is shown for reference;");
            Console.WriteLine("  the agreement claim is withheld. Use --preset audit-grade to compare like with like.");
        }
        else if (diverged == 0 && incomparable == 0)
        {
            Console.WriteLine($"  All {compared} shape(s) with a published V8 arm reproduce it exactly"
                              + (scored.Count > publishesV8 ? $" ({scored.Count - publishesV8} of {scored.Count} publish none)." : "."));
            Console.WriteLine("  Same corpus, same condition, reached through a different entry point — a check on");
            Console.WriteLine("  this sample's wiring, NOT an independent confirmation of the score: the published");
            Console.WriteLine("  probe used this same deployment.");
        }
        else
        {
            if (diverged > 0)
            {
                Console.WriteLine($"  ⚠ {diverged} shape(s) differ from the published V8 arm. Same questions and the");
                Console.WriteLine("  same condition should give the same rate, so read this as a wiring or deployment");
                Console.WriteLine("  difference before reading it as a result.");
            }

            if (incomparable > 0)
            {
                Console.WriteLine($"  ⚠ {incomparable} of {publishesV8} shape(s) with a V8 arm could NOT be compared:");
                Console.WriteLine("  the arm covers a different number of questions than this run scored, so the two");
                Console.WriteLine("  rates are over different sets. The agreement claim is withheld for those.");
            }

            if (compared > 0)
            {
                Console.WriteLine($"  {compared} shape(s) WERE comparable and reproduce their published V8 arm.");
            }
        }
        Console.WriteLine();

        // ─── BLOCK 2: what a RETRIEVING system faces — which this reader is not ──────────
        Console.WriteLine($"{vertical} under a RETRIEVING system (this reader is NOT one)");
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
