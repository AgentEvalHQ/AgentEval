// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Core;
using AgentEval.Decisions;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Calibration;

namespace AgentEval.Samples.Providers;

/// <summary>
/// Sample N3 — <b>Judge vs Judge</b>: the evaluator evaluates the evaluators.
/// </summary>
/// <remarks>
/// <para>
/// Before a decision model is allowed to decide anything in AgentEval, AgentEval does to it what it does to
/// every agent: measures it against labels, beside the judge it would replace. The labels are the agentic
/// golden cases under <c>tests/AgentEval.Tests/Agentic/Calibration/Golden/*.jsonl</c>; the harness is the
/// agentic <see cref="CalibrationRunner"/> the CLI's <c>bench agentic calibrate</c> already uses; the rubrics
/// are the evaluators' own criteria, resolved through the shared <see cref="EvalRegistry"/>. Nothing here is a
/// second harness: the only new code is a judge adapter (<see cref="DecisionJudge"/>) and the comparison report.
/// </para>
/// <para>
/// Two arms, same cases, same rubrics: the configured generative judge (<c>AI_INFERENCE_PROVIDER</c>) and
/// Jev through <see cref="DecisionJudge"/>. Per file category and per evaluator key: accuracy against
/// <c>expectedVerdict</c>, Cohen's κ, false-pass rate on the <c>fail</c>-labelled cases, within-band rate,
/// Brier score, latency, tokens and cost — then the seven pre-registered hypotheses of the Jev factsheet,
/// each confirmed, refuted or left open by the numbers.
/// </para>
/// <para>
/// <b>Provenance caveat.</b> Through the registry, both judges reach the tree as <c>atomic-llm</c> leaves.
/// That is right for the generative judge and a misnomer for Jev; every result here stays in memory and the
/// report says so. The persisted kind for a decision model is <c>DecisionEval</c> (<c>atomic-decision</c>).
/// </para>
/// <para>
/// Flags (after the sample number): <c>--dry-run</c> (resolve every key, capture the exact criteria each
/// evaluator sends, render the first Jev request, send nothing), <c>--limit N</c> (cases per file),
/// <c>--files a,b</c> (file suffixes, e.g. <c>20-process,indirect-attack</c>), <c>--repeats N</c>,
/// <c>--parallel N</c> (files in flight at once, default 4), <c>--out DIR</c> (report directory).
/// </para>
/// </remarks>
internal static class JudgeVsJudgeDemo
{
    private const string GoldenRelativePath = "tests/AgentEval.Tests/Agentic/Calibration/Golden";

    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   ⚖️  SAMPLE N3: JUDGE vs JUDGE — the evaluator evaluates the evaluators     ║");
        Console.WriteLine("║   Jev beside the generative judge, same golden cases, same rubrics           ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var opts = N3Options.Parse(Environment.GetCommandLineArgs());
        var dryRun = ProviderConfig.IsDryRun;

        var jevOptions = ProviderConfig.CreateJevOptions();
        if (jevOptions is null)
        {
            ProviderConfig.PrintMissingJevWarning();
            return;
        }
        if (!AIConfig.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠ No generative judge is configured (AI_INFERENCE_PROVIDER). N3 compares two judges; it needs both.");
            Console.WriteLine($"     {AIConfig.Settings.Diagnostic}");
            Console.ResetColor();
            return;
        }

        var goldenDir = opts.GoldenDir ?? FindGoldenDir();
        if (goldenDir is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠ Golden directory not found. Run from the repository (looked for {GoldenRelativePath} above the working and base directories) or pass --golden DIR.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"   📁 Golden     : {goldenDir}");
        Console.WriteLine($"   🤖 Judge A    : {AIConfig.ModelIdentity}   (generative, through ChatClientEvaluator — the judge the evaluators were calibrated with)");
        Console.WriteLine($"   🎯 Judge B    : {jevOptions.Model}@{jevOptions.ProviderName}   (decision model, through DecisionJudge — one binary question per criterion, one request per call)");
        Console.WriteLine($"   🔁 Repeats    : {opts.Repeats}   ⏩ Parallel files: {opts.Parallel}   {(opts.Limit is { } l ? $"📏 Limit: {l} case(s) per file" : "📏 Limit: none")}{(opts.Files is { } f ? $"   🗂 Files: {string.Join(",", f)}" : "")}");
        Console.WriteLine("   ⚠ Provenance : through the registry BOTH judges land as [atomic-llm] leaves. Right for A, a misnomer for B; nothing here is persisted.");
        Console.WriteLine();
        if (dryRun) ProviderConfig.PrintDryRunBanner();

        // ── Load the golden cases, one dataset per file ─────────────────────────────────────
        var datasets = await LoadAsync(goldenDir, opts);
        var totalCases = datasets.Sum(d => d.Entries.Count);
        var keys = datasets.SelectMany(d => d.Entries).Select(e => e.EvaluatorKey).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k).ToList();
        Console.WriteLine($"   {datasets.Count} file(s), {totalCases} case(s), {keys.Count} evaluator key(s); labels: pass {datasets.SelectMany(d => d.Entries).Count(e => e.ExpectedVerdict == "pass")} / fail {datasets.SelectMany(d => d.Entries).Count(e => e.ExpectedVerdict == "fail")}");
        Console.WriteLine();

        AgenticEvalRegistration.Register();

        // ── Stage 1: dry run — every eval runs against a judge that records and sends nothing ──
        if (dryRun)
        {
            await DryRunAsync(datasets, keys, jevOptions);
            PrintTakeaways(dryRun: true);
            return;
        }

        using var http = ProviderConfig.CreateJevHttpClient(jevOptions);
        using var jevClient = new SystemOneDecisionClient(jevOptions, http);
        var traces = new ConcurrentDictionary<(string Input, string Output), ConcurrentBag<DecisionJudge.Trace>>();
        var jev = new DecisionJudge(jevClient, jevOptions.Model, t => traces.GetOrAdd((t.Input, t.Output), _ => []).Add(t));
        var generative = new ChatClientEvaluator(AIConfig.CreateChatClient());

        var arms = new[]
        {
            new Arm("A", "generative", AIConfig.ModelIdentity, generative),
            new Arm("B", "decision", $"{jevOptions.Model}@{jevOptions.ProviderName}", jev),
        };

        var records = new ConcurrentBag<CaseRecord>();
        var total = Stopwatch.StartNew();

        // ── Stage 2: ONE real case per arm, then Stage 3: the rest ───────────────────────────
        var first = datasets[0].Entries[0];
        var firstOnly = new CalibrationDataset(datasets[0].CategoryKey, [first]);
        var rest = datasets
            .Select(d => d == datasets[0] ? new CalibrationDataset(d.CategoryKey, d.Entries.Skip(1).ToList()) : d)
            .Where(d => d.Entries.Count > 0)
            .ToList();

        Console.WriteLine("📝 Stage 2: ONE real case through both judges\n");
        foreach (var arm in arms)
        {
            await RunArmAsync(arm, [firstOnly], repeat: 1, parallel: 1, records);
            var r = records.Single(x => x.Arm == arm.Id && x.Repeat == 1 && x.ScenarioId == first.ScenarioId);
            Console.WriteLine($"   {arm.Id} {arm.Label,-38} {first.EvaluatorKey}/{first.ScenarioId}: {r.Label} {r.Value:F3} (expected {r.ExpectedVerdict} [{r.ExpectedMin:F2},{r.ExpectedMax:F2}])   {r.LatencyMs:N0} ms   judge leaves {r.JudgeLeaves}, {r.JudgeTokens} tok{(r.Error is { } e ? $"   ⚠ {e}" : "")}");
        }
        Console.WriteLine();
        if (records.Any(r => r.Repeat == 1 && r.Error is not null && r.JudgeLeaves == 0))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠ The first case did not reach a judge on at least one arm. Stopping before the rest — fix the wiring first.");
            Console.ResetColor();
            return;
        }

        if (rest.Count > 0 || opts.Repeats > 1)
        {
            Console.WriteLine($"📝 Stage 3: the remaining {rest.Sum(d => d.Entries.Count)} case(s){(opts.Repeats > 1 ? $" × {opts.Repeats} repeat(s) (repeat 1 already holds the first case)" : "")}\n");
            for (var repeat = 1; repeat <= opts.Repeats; repeat++)
            {
                foreach (var arm in arms)
                {
                    var sw = Stopwatch.StartNew();
                    var toRun = repeat == 1 ? rest : datasets;
                    await RunArmAsync(arm, toRun, repeat, opts.Parallel, records);
                    var mine = records.Where(x => x.Arm == arm.Id && x.Repeat == repeat).ToList();
                    Console.WriteLine($"   repeat {repeat}  {arm.Id} {arm.Label,-38} {mine.Count,4} case(s)  {sw.Elapsed.TotalSeconds,6:F0} s   errors {mine.Count(x => x.Error is not null)}   judge not involved {mine.Count(x => x.JudgeLeaves == 0 && x.Error is null)}");
                }
            }
            Console.WriteLine();
        }

        // ── The comparison ───────────────────────────────────────────────────────────────────
        var all = records.ToList();
        var report = Compare(all, traces, arms, opts.Repeats, jevOptions);
        PrintReport(report, arms);
        var outDir = WriteReport(report, all, traces, opts, arms, goldenDir, total.Elapsed);
        Console.WriteLine($"   📄 Report     : {outDir}");
        Console.WriteLine();
        PrintTakeaways(dryRun: false);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Running one arm through the agentic CalibrationRunner
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static async Task RunArmAsync(Arm arm, IReadOnlyList<CalibrationDataset> datasets, int repeat, int parallel, ConcurrentBag<CaseRecord> records)
    {
        var byInput = datasets.SelectMany(d => d.Entries.Select(e => (File: d.CategoryKey, Entry: e)))
            .GroupBy(x => (x.Entry.EvaluatorKey, x.Entry.Input, x.Entry.AgentResponse))
            .ToDictionary(g => g.Key, g => g.First());

        var resolved = new ConcurrentDictionary<string, IEval?>(StringComparer.OrdinalIgnoreCase);
        IEval? Resolver(string key) => resolved.GetOrAdd(key, k =>
        {
            var eval = EvalRegistry.Shared.Resolve(k, arm.Judge, arm.Label);
            return eval is null ? null : new RecordingEval(eval, input =>
            {
                if (byInput.TryGetValue((k, input.Query ?? "", input.Response ?? ""), out var hit))
                    return hit;
                return default;
            }, (file, entry, result, ms) => records.Add(CaseRecord.From(arm.Id, repeat, file, entry, result, ms)));
        });

        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var tasks = datasets.Select(async ds =>
        {
            await gate.WaitAsync();
            try
            {
                var runner = new CalibrationRunner(Resolver);
                await runner.RunAsync([ds]);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>Wraps a resolved eval so every case's result, wall-clock and judge leaves are captured.</summary>
    private sealed class RecordingEval(
        IEval inner,
        Func<EvalInput, (string File, CalibrationEntry Entry)> lookup,
        Action<string, CalibrationEntry, EvalResult, long> sink) : IEval
    {
        public string Key => inner.Key;
        public string Name => inner.Name;
        public string Category => inner.Category;
        public string Version => inner.Version;

        public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = await inner.EvaluateAsync(input, ct);
            sw.Stop();
            var (file, entry) = lookup(input);
            if (entry is not null)
                sink(file, entry, result, sw.ElapsedMilliseconds);
            return result;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Stage 1: the dry run
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A judge that records the criteria it is handed and answers nothing — the real code path, no network.</summary>
    private sealed class CapturingJudge(ConcurrentDictionary<string, ConcurrentBag<IReadOnlyList<string>>> criteriaByKey, string key) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
        {
            criteriaByKey.GetOrAdd(key, _ => []).Add(criteria.ToList());
            return Task.FromResult(new EvaluationResult { EvaluationFailed = true, Summary = "dry run: nothing sent" });
        }
    }

    private static async Task DryRunAsync(IReadOnlyList<CalibrationDataset> datasets, IReadOnlyList<string> keys, SystemOneClientOptions jevOptions)
    {
        Console.WriteLine("📝 Stage 1: resolve every evaluator key, run every case against a judge that records and sends nothing\n");
        var criteriaByKey = new ConcurrentDictionary<string, ConcurrentBag<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        var noJudge = new List<string>();
        var resolvedEvals = new Dictionary<string, IEval>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var eval = EvalRegistry.Shared.Resolve(key, new CapturingJudge(criteriaByKey, key), "dry-run");
            if (eval is null) unresolved.Add(key); else resolvedEvals[key] = eval;
        }

        var runner = new CalibrationRunner(k => resolvedEvals.GetValueOrDefault(k));
        var stderr = Console.Error;
        Console.SetError(TextWriter.Null);   // the runner reports the deliberate "error" labels case by case; the summary below is enough
        try { await runner.RunAsync(datasets); }
        finally { Console.SetError(stderr); }

        foreach (var key in resolvedEvals.Keys)
            if (!criteriaByKey.ContainsKey(key)) noJudge.Add(key);

        Console.WriteLine($"   keys in the selected files : {keys.Count}");
        Console.WriteLine($"   resolved by the registry   : {resolvedEvals.Count}");
        Console.WriteLine($"   of which reach a judge     : {criteriaByKey.Count}   (the others decide in code — same result on both arms, excluded from the comparison)");
        if (noJudge.Count > 0) Console.WriteLine($"     no judge involved        : {string.Join(", ", noJudge.OrderBy(k => k))}");
        if (unresolved.Count > 0) Console.WriteLine($"   ⚠ unknown to the registry  : {string.Join(", ", unresolved)}   (skipped by the runner, counted in the report)");
        Console.WriteLine();

        Console.WriteLine("   Criteria each evaluator hands its judge (these are the questions Jev will be asked, verbatim):");
        foreach (var (key, sets) in criteriaByKey.OrderBy(kv => kv.Key))
        {
            var distinct = sets.Select(s => string.Join(" | ", s)).Distinct().ToList();
            var calls = sets.Count / Math.Max(1, datasets.SelectMany(d => d.Entries).Count(e => e.EvaluatorKey.Equals(key, StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine($"     {key,-34} {calls} judge call(s)/case, {distinct.Count} criteria set(s), {sets.First().Count} criteria in the first");
            foreach (var c in sets.First()) Console.WriteLine($"         • {Trim(c, 110)}");
        }
        Console.WriteLine();

        var firstEntry = datasets[0].Entries[0];
        if (criteriaByKey.TryGetValue(firstEntry.EvaluatorKey, out var firstSets))
        {
            var judge = new DecisionJudge(new NeverSendsClient(), jevOptions.Model);
            var request = judge.BuildRequest(firstEntry.Input, firstEntry.AgentResponse, firstSets.First());
            var json = SystemOneDecisionClient.RenderRequest(request, jevOptions.Model);
            Console.WriteLine($"   First Jev request, rendered through the real serializer ({Encoding.UTF8.GetByteCount(json):N0} bytes, {request.Questions.Count} question(s)) — {firstEntry.EvaluatorKey}/{firstEntry.ScenarioId}:");
            Console.WriteLine($"     {Trim(json, 600)}");
            Console.WriteLine();
        }
        Console.WriteLine("   [dry-run] Stages 2–3 send real requests to BOTH judges. Re-run without --dry-run to spend.");
        Console.WriteLine();
    }

    private sealed class NeverSendsClient : IDecisionClient
    {
        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("dry run: this client never sends");
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  The comparison
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static Report Compare(IReadOnlyList<CaseRecord> all, ConcurrentDictionary<(string, string), ConcurrentBag<DecisionJudge.Trace>> traces, Arm[] arms, int repeats, SystemOneClientOptions jevOptions)
    {
        // Only cases where the eval reached a judge on BOTH arms compare the judges; the rest decided in code.
        var comparable = all.Where(r => r.JudgeLeaves > 0 || r.Error is not null)
            .GroupBy(r => (r.ScenarioId, r.EvaluatorKey))
            .Where(g => arms.All(a => g.Any(r => r.Arm == a.Id)))
            .SelectMany(g => g)
            .ToList();
        var excluded = all.Select(r => (r.ScenarioId, r.EvaluatorKey)).Distinct().Count() - comparable.Select(r => (r.ScenarioId, r.EvaluatorKey)).Distinct().Count();

        var byFile = comparable.GroupBy(r => r.File).OrderBy(g => g.Key)
            .Select(g => GroupSummary(g.Key, g.ToList(), arms)).ToList();
        var byKey = comparable.GroupBy(r => r.EvaluatorKey).OrderBy(g => g.Key)
            .Select(g => GroupSummary(g.Key, g.ToList(), arms)).ToList();
        var overall = GroupSummary("ALL", comparable, arms);

        // Flips across repeats, per arm
        var flips = arms.ToDictionary(a => a.Id, a =>
        {
            if (repeats < 2) return (double?)null;
            var groups = comparable.Where(r => r.Arm == a.Id).GroupBy(r => (r.ScenarioId, r.EvaluatorKey)).Where(g => g.Count() >= 2).ToList();
            return groups.Count == 0 ? null : (double?)groups.Count(g => g.Select(r => r.Label).Distinct().Count() > 1) / groups.Count;
        });

        // Jev per-request figures from the traces
        var jevTraces = traces.Values.SelectMany(b => b).ToList();
        var latencies = jevTraces.Select(t => (double)t.LatencyMs).OrderBy(x => x).ToList();
        var jevIn = jevTraces.Sum(t => t.InputTokens);
        var jevOut = jevTraces.Sum(t => t.OutputTokens);
        var jevCost = jevTraces.Any(t => t.Cost is not null) ? jevTraces.Sum(t => t.Cost ?? 0) : jevIn * 0.042 / 1_000_000.0;
        var jevCostPerCase = comparable.Count(r => r.Arm == "B") is var nB && nB > 0 ? jevCost / nB : 0;

        // Negated vs positive criteria on pass-labelled cases (H7)
        var passInputs = comparable.Where(r => r.Arm == "B" && r.ExpectedVerdict == "pass").Select(r => (r.Input, r.Output)).ToHashSet();
        var negated = new List<double>(); var positive = new List<double>();
        foreach (var t in jevTraces.Where(t => passInputs.Contains((t.Input, t.Output))))
            foreach (var (criterion, p) in t.Criteria)
                (IsNegated(criterion) ? negated : positive).Add(p >= 0.5 ? 1 : 0);

        var hypotheses = Hypotheses(byFile, overall, comparable, flips, latencies, jevCostPerCase, negated, positive, repeats);

        return new Report(overall, byFile, byKey, excluded, flips, latencies.Count == 0 ? 0 : Percentile(latencies, 0.5), latencies.Count == 0 ? 0 : Percentile(latencies, 0.9), jevTraces.Count, jevIn, jevOut, jevCost, jevCostPerCase,
            jevTraces.Select(t => t.Model).Distinct().ToList(), negated.Count, positive.Count, hypotheses);
    }

    private static GroupSummaryRow GroupSummary(string name, IReadOnlyList<CaseRecord> rows, Arm[] arms)
    {
        var perArm = arms.ToDictionary(a => a.Id, a => ArmStats(rows.Where(r => r.Arm == a.Id).ToList()));
        return new GroupSummaryRow(name, rows.Select(r => (r.ScenarioId, r.EvaluatorKey)).Distinct().Count(), perArm);
    }

    private static ArmStatsRow ArmStats(IReadOnlyList<CaseRecord> rows)
    {
        var scored = rows.Where(r => r.Error is null).ToList();
        var pairs = scored.Select(r => (Expected: r.ExpectedVerdict, Actual: r.Label)).ToList();
        var fails = scored.Where(r => r.ExpectedVerdict == "fail").ToList();
        var wrong = scored.Where(r => r.Label != r.ExpectedVerdict).ToList();
        var right = scored.Where(r => r.Label == r.ExpectedVerdict).ToList();
        double Brier(IEnumerable<CaseRecord> rs) => rs.Any() ? rs.Average(r => Math.Pow(r.Value - (r.ExpectedVerdict == "pass" ? 1.0 : 0.0), 2)) : double.NaN;
        var lat = scored.Select(r => (double)r.LatencyMs).OrderBy(x => x).ToList();
        return new ArmStatsRow(
            N: rows.Count,
            Errors: rows.Count(r => r.Error is not null),
            Accuracy: pairs.Count == 0 ? double.NaN : CalibrationMetrics.Accuracy(pairs),
            Kappa: pairs.Count == 0 ? double.NaN : CalibrationMetrics.CohensKappa(pairs),
            FalsePass: fails.Count == 0 ? double.NaN : (double)fails.Count(r => r.Label == "pass") / fails.Count,
            FalseFail: scored.Count(r => r.ExpectedVerdict == "pass") is var np && np == 0 ? double.NaN : (double)scored.Count(r => r.ExpectedVerdict == "pass" && r.Label == "fail") / np,
            WithinBand: scored.Count == 0 ? double.NaN : (double)scored.Count(r => r.Value >= r.ExpectedMin && r.Value <= r.ExpectedMax) / scored.Count,
            Brier: Brier(scored),
            BrierOnRight: Brier(right),
            SharpWhenWrong: wrong.Count == 0 ? double.NaN : (double)wrong.Count(r => r.Value >= 0.9 || r.Value <= 0.1) / wrong.Count,
            P50Ms: lat.Count == 0 ? 0 : Percentile(lat, 0.5),
            P90Ms: lat.Count == 0 ? 0 : Percentile(lat, 0.9),
            Tokens: rows.Sum(r => r.JudgeTokens));
    }

    private static IReadOnlyList<HypothesisRow> Hypotheses(
        IReadOnlyList<GroupSummaryRow> byFile, GroupSummaryRow overall, IReadOnlyList<CaseRecord> comparable,
        IReadOnlyDictionary<string, double?> flips, IReadOnlyList<double> jevLatencies, double jevCostPerCase,
        IReadOnlyList<double> negated, IReadOnlyList<double> positive, int repeats)
    {
        var rows = new List<HypothesisRow>();
        ArmStatsRow? A(string file) => byFile.FirstOrDefault(f => f.Name == file)?.Arms["A"];
        ArmStatsRow? B(string file) => byFile.FirstOrDefault(f => f.Name == file)?.Arms["B"];
        static string Pct(double v) => double.IsNaN(v) ? "n/a" : (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

        // H1 — narrow, English, non-numeric sets
        {
            var files = new[] { "20-process", "20-quality", "20-system", "memory-multiturn" };
            var rowsB = comparable.Where(r => r.Arm == "B" && files.Contains(r.File) && r.Error is null).ToList();
            var rowsA = comparable.Where(r => r.Arm == "A" && files.Contains(r.File) && r.Error is null).ToList();
            if (rowsB.Count == 0) rows.Add(new("H1", "open", "no cases from process/quality/system/memory-multiturn in this run"));
            else
            {
                var accB = rowsB.Average(r => r.Label == r.ExpectedVerdict ? 1.0 : 0);
                var accA = rowsA.Count == 0 ? double.NaN : rowsA.Average(r => r.Label == r.ExpectedVerdict ? 1.0 : 0);
                var gap = accA - accB;
                var verdict = accB >= 0.85 && gap <= 0.05 ? "confirmed" : (accB < 0.80 || gap > 0.10) ? "refuted" : "open";
                rows.Add(new("H1", verdict, $"Jev {Pct(accB)} vs generative {Pct(accA)} on {rowsB.Count} case(s); gap {Pct(gap)} (confirm: ≥85% and ≤5 pts; refute: <80% or >10 pts)"));
            }
        }
        // H2 — adversarial sets: Jev false-pass materially higher
        {
            var files = new[] { "indirect-attack", "adversarial-direct" };
            var fb = comparable.Where(r => r.Arm == "B" && files.Contains(r.File) && r.Error is null && r.ExpectedVerdict == "fail").ToList();
            var fa = comparable.Where(r => r.Arm == "A" && files.Contains(r.File) && r.Error is null && r.ExpectedVerdict == "fail").ToList();
            if (fb.Count == 0 || fa.Count == 0) rows.Add(new("H2", "open", "no fail-labelled adversarial cases in this run"));
            else
            {
                var fpB = fb.Average(r => r.Label == "pass" ? 1.0 : 0); var fpA = fa.Average(r => r.Label == "pass" ? 1.0 : 0);
                var verdict = fpB - fpA >= 0.10 ? "confirmed" : fpB <= fpA ? "refuted" : "open";
                rows.Add(new("H2", verdict, $"false-pass on {fb.Count} fail-labelled adversarial case(s): Jev {Pct(fpB)} vs generative {Pct(fpA)} (confirm: Jev ≥10 pts higher; refute: equal or lower)"));
            }
        }
        // H3 — sharp but not calibrated
        {
            var b = overall.Arms["B"];
            if (double.IsNaN(b.SharpWhenWrong)) rows.Add(new("H3", "open", $"Brier on right cases {b.BrierOnRight:F3}; no wrong cases to test sharpness on"));
            else
            {
                var verdict = b.BrierOnRight <= 0.10 && b.SharpWhenWrong > 0.5 ? "confirmed" : b.SharpWhenWrong < 0.5 ? "refuted" : "open";
                rows.Add(new("H3", verdict, $"Brier on right cases {b.BrierOnRight:F3}; on wrong cases {Pct(b.SharpWhenWrong)} still ≥0.9 or ≤0.1 (confirm: ≤0.10 and >50%; refute: wrong cases near the diagonal)"));
            }
        }
        // H4 — counting / arithmetic / dates: the largest gap
        {
            var files = new[] { "confidence-calibration", "code-vulnerability" };
            double Gap(Func<CaseRecord, bool> pred)
            {
                var a = comparable.Where(r => r.Arm == "A" && r.Error is null && pred(r)).ToList();
                var b = comparable.Where(r => r.Arm == "B" && r.Error is null && pred(r)).ToList();
                if (a.Count == 0 || b.Count == 0) return double.NaN;
                return a.Average(r => r.Label == r.ExpectedVerdict ? 1.0 : 0) - b.Average(r => r.Label == r.ExpectedVerdict ? 1.0 : 0);
            }
            var gapIn = Gap(r => files.Contains(r.File)); var gapOut = Gap(r => !files.Contains(r.File));
            if (double.IsNaN(gapIn) || double.IsNaN(gapOut)) rows.Add(new("H4", "open", "need both the numeric sets and the others in one run"));
            else rows.Add(new("H4", gapIn > gapOut + 0.05 ? "confirmed" : gapIn <= gapOut ? "refuted" : "open", $"accuracy gap (generative − Jev) on confidence-calibration + code-vulnerability {Pct(gapIn)} vs elsewhere {Pct(gapOut)}"));
        }
        // H5 — latency and cost per request
        {
            if (jevLatencies.Count == 0) rows.Add(new("H5", "open", "no Jev requests recorded"));
            else
            {
                var p50 = Percentile(jevLatencies, 0.5); var p90 = Percentile(jevLatencies, 0.9);
                var verdict = p50 < 400 && p90 < 800 && jevCostPerCase < 0.0002 ? "confirmed" : (p50 > 600 || jevCostPerCase > 0.0005) ? "refuted" : "open";
                rows.Add(new("H5", verdict, $"Jev per request p50 {p50:N0} ms, p90 {p90:N0} ms; est. cost per case ${jevCostPerCase:F6} at list price (confirm: <400 / <800 / <$0.0002)"));
            }
        }
        // H6 — repeat stability
        {
            if (repeats < 2 || flips["B"] is null) rows.Add(new("H6", "open", "needs --repeats ≥ 2"));
            else rows.Add(new("H6", flips["B"] < 0.02 ? "confirmed" : flips["B"] > 0.05 ? "refuted" : "open", $"verdict flips across repeats: Jev {Pct(flips["B"]!.Value)}, generative {(flips["A"] is { } fa ? Pct(fa) : "n/a")} (confirm: <2%; refute: >5%)"));
        }
        // H7 — negated criteria
        {
            if (negated.Count < 5 || positive.Count < 5) rows.Add(new("H7", "open", $"too few criteria on pass-labelled cases (negated {negated.Count}, positive {positive.Count})"));
            else
            {
                var n = negated.Average(); var p = positive.Average();
                rows.Add(new("H7", p - n >= 0.05 ? "confirmed" : n >= p ? "refuted" : "open", $"P(met)≥0.5 on pass-labelled cases: negated criteria {Pct(n)} ({negated.Count}) vs positive {Pct(p)} ({positive.Count})"));
            }
        }
        return rows;
    }

    private static bool IsNegated(string criterion) =>
        Regex.IsMatch(criterion, @"\b(does not|do not|doesn't|don't|never|no |not |without|avoids?|refrains?)\b", RegexOptions.IgnoreCase);

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Output
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static void PrintReport(Report report, Arm[] arms)
    {
        Console.WriteLine("📝 Stage 4: the comparison (cases where the evaluator reached a judge on both arms)\n");
        Console.WriteLine($"   arm A = {arms[0].Label}    arm B = {arms[1].Label}    excluded (decided in code, or missing an arm): {report.Excluded}");
        Console.WriteLine();
        Console.WriteLine("   file                        n   acc A    acc B    κ A     κ B     false-pass A/B    band A/B     Brier A/B    p50 ms A/B      tokens A/B");
        foreach (var row in report.ByFile.Append(report.Overall))
            Console.WriteLine($"   {row.Name,-24} {row.N,4}   {F(row.Arms["A"].Accuracy)}  {F(row.Arms["B"].Accuracy)}  {F(row.Arms["A"].Kappa)}  {F(row.Arms["B"].Kappa)}  {F(row.Arms["A"].FalsePass)}/{F(row.Arms["B"].FalsePass)}   {F(row.Arms["A"].WithinBand)}/{F(row.Arms["B"].WithinBand)}   {F(row.Arms["A"].Brier)}/{F(row.Arms["B"].Brier)}   {row.Arms["A"].P50Ms,6:N0}/{row.Arms["B"].P50Ms,-6:N0}   {row.Arms["A"].Tokens,7:N0}/{row.Arms["B"].Tokens:N0}");
        Console.WriteLine();
        Console.WriteLine($"   Jev requests {report.JevRequests:N0}   p50 {report.JevP50Ms:N0} ms   p90 {report.JevP90Ms:N0} ms   tokens {report.JevInputTokens:N0} in / {report.JevOutputTokens:N0} out   est. ${report.JevCost:F4} total, ${report.JevCostPerCase:F6}/case   models echoed: {string.Join(", ", report.JevModels)}");
        Console.WriteLine();
        Console.WriteLine("   Hypotheses (pre-registered in the Jev factsheet §7):");
        foreach (var h in report.Hypotheses)
        {
            Console.ForegroundColor = h.Verdict switch { "confirmed" => ConsoleColor.Green, "refuted" => ConsoleColor.Red, _ => ConsoleColor.Yellow };
            Console.Write($"     {h.Id}  {h.Verdict,-9}");
            Console.ResetColor();
            Console.WriteLine($" {h.Detail}");
        }
        Console.WriteLine();
        Console.WriteLine("   Per evaluator key (B = Jev): acc A / acc B / false-pass B / n");
        foreach (var row in report.ByKey)
            Console.WriteLine($"     {row.Name,-34} {F(row.Arms["A"].Accuracy)} / {F(row.Arms["B"].Accuracy)} / {F(row.Arms["B"].FalsePass)} / {row.N}");
        Console.WriteLine();
    }

    private static string F(double v) => double.IsNaN(v) ? "  n/a " : v.ToString("0.000", CultureInfo.InvariantCulture);

    private static string WriteReport(Report report, IReadOnlyList<CaseRecord> all, ConcurrentDictionary<(string, string), ConcurrentBag<DecisionJudge.Trace>> traces, N3Options opts, Arm[] arms, string goldenDir, TimeSpan elapsed)
    {
        var dir = opts.OutDir ?? Path.Combine(Path.GetTempPath(), "agenteval-n3");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var json = new
        {
            generatedAtUtc = DateTime.UtcNow,
            elapsedSeconds = elapsed.TotalSeconds,
            goldenDir,
            options = new { opts.Repeats, opts.Limit, opts.Files, opts.Parallel },
            arms = arms.Select(a => new { a.Id, a.Kind, a.Label }),
            provenanceCaveat = "Both arms reach the tree as atomic-llm leaves through the registry; nothing here is persisted as an eval result. The persisted kind for a decision model is DecisionEval (atomic-decision).",
            report,
            records = all.OrderBy(r => r.Arm).ThenBy(r => r.Repeat).ThenBy(r => r.File).ThenBy(r => r.ScenarioId),
            jevTraces = traces.Values.SelectMany(b => b).Select(t => new { t.Model, t.LatencyMs, t.InputTokens, t.OutputTokens, t.Cost, criteria = t.Criteria.Select(c => new { c.Criterion, c.ProbabilityMet }) }),
        };
        var jsonPath = Path.Combine(dir, $"n3-judge-vs-judge-{stamp}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

        var md = new StringBuilder();
        md.AppendLine($"# N3 — Judge vs Judge — {stamp}Z");
        md.AppendLine();
        md.AppendLine($"Arm A = {arms[0].Label} (generative) · Arm B = {arms[1].Label} (decision) · repeats {opts.Repeats} · excluded {report.Excluded} · {elapsed.TotalSeconds:F0} s");
        md.AppendLine();
        md.AppendLine("| file | n | acc A | acc B | κ A | κ B | false-pass A | false-pass B | band A | band B | Brier A | Brier B | p50 A | p50 B | tokens A | tokens B |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in report.ByFile.Append(report.Overall))
        {
            var a = row.Arms["A"]; var b = row.Arms["B"];
            md.AppendLine($"| {row.Name} | {row.N} | {F(a.Accuracy)} | {F(b.Accuracy)} | {F(a.Kappa)} | {F(b.Kappa)} | {F(a.FalsePass)} | {F(b.FalsePass)} | {F(a.WithinBand)} | {F(b.WithinBand)} | {F(a.Brier)} | {F(b.Brier)} | {a.P50Ms:N0} | {b.P50Ms:N0} | {a.Tokens:N0} | {b.Tokens:N0} |");
        }
        md.AppendLine();
        md.AppendLine($"Jev: {report.JevRequests} requests, p50 {report.JevP50Ms:N0} ms, p90 {report.JevP90Ms:N0} ms, {report.JevInputTokens:N0} in / {report.JevOutputTokens:N0} out, est. ${report.JevCost:F4} (${report.JevCostPerCase:F6}/case), models echoed: {string.Join(", ", report.JevModels)}");
        md.AppendLine();
        md.AppendLine("| hypothesis | verdict | detail |");
        md.AppendLine("|---|---|---|");
        foreach (var h in report.Hypotheses) md.AppendLine($"| {h.Id} | {h.Verdict} | {h.Detail} |");
        md.AppendLine();
        md.AppendLine("| evaluator key | n | acc A | acc B | false-pass A | false-pass B | Brier B |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in report.ByKey)
            md.AppendLine($"| {row.Name} | {row.N} | {F(row.Arms["A"].Accuracy)} | {F(row.Arms["B"].Accuracy)} | {F(row.Arms["A"].FalsePass)} | {F(row.Arms["B"].FalsePass)} | {F(row.Arms["B"].Brier)} |");
        File.WriteAllText(Path.Combine(dir, $"n3-judge-vs-judge-{stamp}.md"), md.ToString());
        return dir;
    }

    private static void PrintTakeaways(bool dryRun)
    {
        Console.WriteLine("💡 Takeaways:");
        Console.WriteLine("   • No second harness: the agentic CalibrationRunner and the shared registry score both judges on the SAME rubrics.");
        Console.WriteLine("   • A decision model answers each criterion with a probability; the generative judge with a JSON verdict. Same cases, same questions.");
        Console.WriteLine("   • Cases that decide in code (no judge leaf) are excluded — they cannot tell the judges apart.");
        Console.WriteLine("   • Hypotheses were written BEFORE the run (factsheet §7). The run confirms or refutes them; it does not pick them.");
        Console.WriteLine(dryRun
            ? "   • This WAS a dry run: nothing was sent. Re-run without --dry-run to spend, --limit 1 first."
            : "   • Nothing here is persisted as an eval result: through the registry, Jev would carry [atomic-llm] provenance, which is the wrong kind.");
        Console.WriteLine();
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    //  Golden loading, options, helpers
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<List<CalibrationDataset>> LoadAsync(string goldenDir, N3Options opts)
    {
        var loader = new CalibrationDatasetLoader();
        var list = new List<CalibrationDataset>();
        foreach (var path in Directory.GetFiles(goldenDir, "golden-*.jsonl").OrderBy(p => p, StringComparer.Ordinal))
        {
            var suffix = Path.GetFileNameWithoutExtension(path)["golden-".Length..];
            if (opts.Files is { } only && !only.Contains(suffix, StringComparer.OrdinalIgnoreCase)) continue;
            await using var stream = File.OpenRead(path);
            var ds = await loader.LoadAsync(suffix, stream);
            var entries = opts.Limit is { } n ? ds.Entries.Take(n).ToList() : ds.Entries;
            if (entries.Count > 0) list.Add(new CalibrationDataset(suffix, entries));
        }
        if (list.Count == 0) throw new InvalidOperationException($"no golden files matched under {goldenDir}");
        return list;
    }

    private static string? FindGoldenDir()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
                {
                    var golden = Path.Combine(dir.FullName, GoldenRelativePath.Replace('/', Path.DirectorySeparatorChar));
                    return Directory.Exists(golden) ? golden : null;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record Arm(string Id, string Kind, string Label, IEvaluator Judge);

    private sealed record CaseRecord(
        string Arm, int Repeat, string File, string EvaluatorKey, string ScenarioId, string Input, string Output,
        string ExpectedVerdict, double ExpectedMin, double ExpectedMax,
        string Label, double Value, int JudgeLeaves, int JudgeTokens, long LatencyMs, string? Error)
    {
        public static CaseRecord From(string arm, int repeat, string file, CalibrationEntry entry, EvalResult result, long ms)
        {
            var leaves = new List<EvalResult>();
            Walk(result, leaves);
            var judgeLeaves = leaves.Where(l => string.Equals(l.Provenance.Type, "atomic-llm", StringComparison.Ordinal)).ToList();
            var errored = leaves.Where(l => string.Equals(l.Score.Label, "error", StringComparison.OrdinalIgnoreCase)).ToList();
            return new CaseRecord(arm, repeat, file, entry.EvaluatorKey, entry.ScenarioId, entry.Input, entry.AgentResponse,
                entry.ExpectedVerdict, entry.ExpectedScoreMin, entry.ExpectedScoreMax,
                result.Score.Label, result.Score.Value, judgeLeaves.Count, judgeLeaves.Sum(l => l.Provenance.TokensUsed ?? 0), ms,
                errored.Count > 0 ? $"{errored.Count} leaf/leaves errored: {Trim(errored[0].Details.Summary ?? "", 120)}" : null);
        }

        private static void Walk(EvalResult r, List<EvalResult> acc)
        {
            acc.Add(r);
            foreach (var c in r.Details.SubResults ?? []) Walk(c, acc);
        }
    }

    private sealed record ArmStatsRow(int N, int Errors, double Accuracy, double Kappa, double FalsePass, double FalseFail, double WithinBand, double Brier, double BrierOnRight, double SharpWhenWrong, double P50Ms, double P90Ms, int Tokens);
    private sealed record GroupSummaryRow(string Name, int N, IReadOnlyDictionary<string, ArmStatsRow> Arms);
    private sealed record HypothesisRow(string Id, string Verdict, string Detail);
    private sealed record Report(
        GroupSummaryRow Overall, IReadOnlyList<GroupSummaryRow> ByFile, IReadOnlyList<GroupSummaryRow> ByKey, int Excluded,
        IReadOnlyDictionary<string, double?> FlipsByArm, double JevP50Ms, double JevP90Ms, int JevRequests, long JevInputTokens, long JevOutputTokens,
        double JevCost, double JevCostPerCase, IReadOnlyList<string> JevModels, int NegatedCriteria, int PositiveCriteria, IReadOnlyList<HypothesisRow> Hypotheses);

    private sealed record N3Options(int Repeats, int? Limit, string[]? Files, int Parallel, string? OutDir, string? GoldenDir)
    {
        public static N3Options Parse(string[] argv)
        {
            int repeats = 1, parallel = 4; int? limit = null; string[]? files = null; string? outDir = null, golden = null;
            for (var i = 0; i < argv.Length; i++)
            {
                string? Next() => i + 1 < argv.Length ? argv[++i] : null;
                switch (argv[i].ToLowerInvariant())
                {
                    case "--repeats": repeats = Math.Max(1, int.Parse(Next() ?? "1", CultureInfo.InvariantCulture)); break;
                    case "--limit": limit = Math.Max(1, int.Parse(Next() ?? "1", CultureInfo.InvariantCulture)); break;
                    case "--parallel": parallel = Math.Max(1, int.Parse(Next() ?? "4", CultureInfo.InvariantCulture)); break;
                    case "--files": files = (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--out": outDir = Next(); break;
                    case "--golden": golden = Next(); break;
                }
            }
            return new N3Options(repeats, limit, files, parallel, outDir, golden);
        }
    }
}
