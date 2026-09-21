// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Decisions;
using AgentEval.Memory.External;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;

namespace AgentEval.Samples.Providers;

/// <summary>
/// Sample N4 — <b>Memory judge vs judge</b>: can a decision model do the memory judge's job?
/// </summary>
/// <remarks>
/// <para>
/// The memory judges answer one narrow question per item — <i>does this response give the gold answer?</i> —
/// which is the shape a decision model is built for. This measures whether it can, against the generative
/// judge, on LongMemEval's own 500 labelled questions.
/// </para>
/// <para>
/// <b>The design, and its honest limit.</b> A judge-vs-judge study needs responses to judge. Running an agent
/// over LongMemEval first would cost hours and mix the agent's errors into the judges' scores, so this sample
/// does not use one: for every question it presents the <b>gold answer</b> (a judge must say correct) and a
/// <b>distractor</b> — another question's gold answer, of the same question type (a judge must say wrong).
/// Both judges see identical items, so the comparison is fair, and the design has a real <b>chance floor of
/// 50%</b>: a judge that says "correct" to everything scores exactly 0.500. What it measures is judge
/// <i>discrimination</i> on memory items. It is NOT agreement on a real run's messy, partially-correct
/// answers, which is a harder problem and is still owed.
/// </para>
/// <para>
/// Flags: <c>--dry-run</c> (build every item, render the first request, send nothing), <c>--limit N</c>
/// (questions, default 40), <c>--types a,b</c> (question types), <c>--out DIR</c>.
/// </para>
/// </remarks>
internal static class MemoryJudgeVsJudgeDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║   🧠 SAMPLE N4: MEMORY JUDGE vs JUDGE                                        ║");
        Console.WriteLine("║   Can a decision model do the memory judge's job? Chance floor 50%           ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var argv = Environment.GetCommandLineArgs();
        var limit = IntArg(argv, "--limit", 40);
        var outDir = StringArg(argv, "--out") ?? Path.Combine(Path.GetTempPath(), "agenteval-n4");
        var types = StringArg(argv, "--types")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dryRun = ProviderConfig.IsDryRun;

        var jevOptions = ProviderConfig.CreateJevOptions();
        if (jevOptions is null) { ProviderConfig.PrintMissingJevWarning(); return; }
        if (!AIConfig.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠ No generative judge is configured. {AIConfig.Settings.Diagnostic}");
            Console.ResetColor();
            return;
        }

        var dataset = FindDataset();
        if (dataset is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠ LongMemEval oracle dataset not found (src/AgentEval.Memory/Data/longmemeval/longmemeval_oracle.json). Run from the repository.");
            Console.ResetColor();
            return;
        }

        var items = BuildItems(dataset, limit, types);
        if (items.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠ No questions matched{(types is { Length: > 0 } ? $" --types {string.Join(",", types)}" : "")}. Known types: knowledge-update, multi-session, single-session-assistant, single-session-preference, single-session-user, temporal-reasoning.");
            Console.ResetColor();
            return;
        }
        Console.WriteLine($"   📁 Dataset    : {dataset}");
        Console.WriteLine($"   🤖 Judge A    : {AIConfig.ModelIdentity}   (generative, LongMemEvalJudge)");
        Console.WriteLine($"   🎯 Judge B    : {jevOptions.Model}@{jevOptions.ProviderName}   (decision, DecisionBenchmarkJudge)");
        Console.WriteLine($"   🧪 Items      : {items.Count} ({items.Count / 2} question(s) × gold + distractor)   chance floor 50.0%");
        Console.WriteLine();
        if (dryRun) ProviderConfig.PrintDryRunBanner();

        using var http = ProviderConfig.CreateJevHttpClient(jevOptions);
        using var jevClient = new SystemOneDecisionClient(jevOptions, http);
        var jev = new DecisionBenchmarkJudge(jevClient, jevOptions.Model);

        if (dryRun)
        {
            Console.WriteLine("📝 Stage 1: every item built; the first request rendered through the real serializer\n");
            var first = items[0];
            var json = SystemOneDecisionClient.RenderRequest(jev.BuildRequest(first.Response, first.Question), jevOptions.Model);
            Console.WriteLine($"   {first.Question.QuestionId} ({first.Question.QuestionType}), expected {(first.ExpectedCorrect ? "correct" : "wrong")}, {json.Length:N0} bytes:");
            Console.WriteLine($"     {Trim(json, 600)}");
            Console.WriteLine();
            Console.WriteLine($"   [dry-run] {items.Count} item(s) would be judged by BOTH judges. Nothing was sent.");
            Console.WriteLine();
            PrintTakeaways(true);
            return;
        }

        IExternalBenchmarkJudge generative = new LongMemEvalJudge(
            AIConfig.CreateChatClient(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LongMemEvalJudge>.Instance);
        var arms = new (string Id, string Label, IExternalBenchmarkJudge Judge)[]
        {
            ("A", AIConfig.ModelIdentity, generative),
            ("B", $"{jevOptions.Model}@{jevOptions.ProviderName}", jev),
        };

        // Stage 2: one real item per arm, then the rest — the standing three-stage rule for paid runs.
        var records = new List<Record>();
        Console.WriteLine("📝 Stage 2: ONE real item through both judges\n");
        foreach (var arm in arms)
        {
            var r = await JudgeOneAsync(arm, items[0]);
            records.Add(r);
            Console.WriteLine($"   {arm.Id} {arm.Label,-34} {r.QuestionId} expected {(r.ExpectedCorrect ? "correct" : "wrong"),-7} → {r.Status,-7} {r.LatencyMs,6:N0} ms{(r.Error is { } e ? $"   ⚠ {e}" : "")}");
        }
        Console.WriteLine();
        if (records.Any(r => r.Error is not null))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠ The first item failed on an arm. Stopping before the rest.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"📝 Stage 3: the remaining {items.Count - 1} item(s) on each arm\n");
        var total = Stopwatch.StartNew();
        foreach (var arm in arms)
        {
            var sw = Stopwatch.StartNew();
            foreach (var item in items.Skip(1))
                records.Add(await JudgeOneAsync(arm, item));
            var mine = records.Where(r => r.Arm == arm.Id).ToList();
            Console.WriteLine($"   {arm.Id} {arm.Label,-34} {mine.Count,4} item(s)   {sw.Elapsed.TotalSeconds,6:F0} s   errors {mine.Count(x => x.Error is not null)}");
        }
        Console.WriteLine();

        Report(records, arms, items.Count, outDir, total.Elapsed);
        PrintTakeaways(false);
    }

    private static async Task<Record> JudgeOneAsync((string Id, string Label, IExternalBenchmarkJudge Judge) arm, Item item)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await arm.Judge.JudgeAsync(item.Response, item.Question);
            sw.Stop();
            return new Record(arm.Id, item.Question.QuestionId, item.Question.QuestionType, item.ExpectedCorrect,
                r.Status, r.Correct, r.RawScore, r.TokensUsed, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Record(arm.Id, item.Question.QuestionId, item.Question.QuestionType, item.ExpectedCorrect,
                JudgeOutcomeStatus.Invalid, null, null, 0, sw.ElapsedMilliseconds, $"{ex.GetType().Name}: {Trim(ex.Message, 120)}");
        }
    }

    // ── Report ──────────────────────────────────────────────────────────────────────────

    private static void Report(List<Record> records, (string Id, string Label, IExternalBenchmarkJudge Judge)[] arms, int itemCount, string outDir, TimeSpan elapsed)
    {
        Console.WriteLine("📝 Stage 4: the comparison (chance floor 50.0% — a judge that always says 'correct' scores exactly that)\n");
        Console.WriteLine("   arm  judge                                 n   accuracy   on gold   on distractor   p50 ms   tokens");
        foreach (var arm in arms)
        {
            var rs = records.Where(r => r.Arm == arm.Id && r.Error is null && r.Correct is not null).ToList();
            if (rs.Count == 0) { Console.WriteLine($"   {arm.Id}    {arm.Label,-34} no scored items"); continue; }
            var acc = rs.Average(r => r.Correct == r.ExpectedCorrect ? 1.0 : 0.0);
            var gold = rs.Where(r => r.ExpectedCorrect).ToList();
            var dist = rs.Where(r => !r.ExpectedCorrect).ToList();
            var lat = rs.Select(r => (double)r.LatencyMs).OrderBy(x => x).ToList();
            Console.WriteLine($"   {arm.Id}    {arm.Label,-34} {rs.Count,3}   {acc,8:P1}   {(gold.Count == 0 ? double.NaN : gold.Average(r => r.Correct == true ? 1.0 : 0.0)),7:P1}   {(dist.Count == 0 ? double.NaN : dist.Average(r => r.Correct == false ? 1.0 : 0.0)),13:P1}   {Percentile(lat, 0.5),6:N0}   {rs.Sum(r => r.Tokens),6:N0}");
        }
        Console.WriteLine();

        // Agreement between the judges on the same item.
        var byItem = records.Where(r => r.Error is null && r.Correct is not null)
            .GroupBy(r => (r.QuestionId, r.ExpectedCorrect))
            .Where(g => g.Select(r => r.Arm).Distinct().Count() == 2)
            .ToList();
        if (byItem.Count > 0)
        {
            var agree = byItem.Count(g => g.Select(r => r.Correct).Distinct().Count() == 1);
            Console.WriteLine($"   judge-vs-judge agreement on {byItem.Count} item(s): {(double)agree / byItem.Count:P1}");
            Console.WriteLine();
        }

        Console.WriteLine("   Per question type (accuracy against the 50% floor):");
        Console.WriteLine("     type                          n     A        B");
        foreach (var type in records.Select(r => r.QuestionType).Distinct().OrderBy(t => t))
        {
            var a = records.Where(r => r.Arm == "A" && r.QuestionType == type && r.Error is null && r.Correct is not null).ToList();
            var b = records.Where(r => r.Arm == "B" && r.QuestionType == type && r.Error is null && r.Correct is not null).ToList();
            static string F(List<Record> rs) => rs.Count == 0 ? "  n/a" : $"{rs.Average(r => r.Correct == r.ExpectedCorrect ? 1.0 : 0.0):P1}";
            Console.WriteLine($"     {type,-28} {a.Count,3}   {F(a),6}   {F(b),6}");
        }
        Console.WriteLine();

        Directory.CreateDirectory(outDir);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(outDir, $"n4-memory-judge-vs-judge-{stamp}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTime.UtcNow,
            elapsedSeconds = elapsed.TotalSeconds,
            design = "Each question contributes two items: its own gold answer (expected correct) and another " +
                     "question's gold answer of the same type (expected wrong). Chance floor 50%. Judge " +
                     "discrimination, not agreement on a real run's partially-correct answers.",
            itemCount,
            arms = arms.Select(a => new { a.Id, a.Label }),
            records,
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        Console.WriteLine($"   📄 Report     : {path}");
        Console.WriteLine();
    }

    private static void PrintTakeaways(bool dryRun)
    {
        Console.WriteLine("💡 Takeaways:");
        Console.WriteLine("   • The memory judge's question is narrow and binary — the shape a decision model is built for.");
        Console.WriteLine("   • A 50% chance floor is built in: a judge that says 'correct' to everything scores exactly 0.500.");
        Console.WriteLine("   • An empty agent response is scored WRONG, locally, without spending a call — silence");
        Console.WriteLine("     satisfies no rubric. 'Empty' is reserved for a silent JUDGE: it carries no verdict,");
        Console.WriteLine("     so the scorers would drop the case from the denominator instead of scoring it zero.");
        Console.WriteLine("   • This is judge DISCRIMINATION on clean items, not agreement on a real run's messy answers.");
        Console.WriteLine(dryRun
            ? "   • This WAS a dry run: nothing was sent. Re-run without --dry-run to spend."
            : "   • Nothing is persisted as an eval result; the adapter is for comparison, not for grading a citable run.");
        Console.WriteLine();
    }

    // ── Items ───────────────────────────────────────────────────────────────────────────

    private sealed record Item(ExternalBenchmarkQuestion Question, string Response, bool ExpectedCorrect);

    private sealed record Record(
        string Arm, string QuestionId, string QuestionType, bool ExpectedCorrect,
        JudgeOutcomeStatus Status, bool? Correct, double? RawScore, int Tokens, long LatencyMs, string? Error);

    private static List<Item> BuildItems(string datasetPath, int limit, string[]? types)
    {
        using var stream = File.OpenRead(datasetPath);
        using var doc = JsonDocument.Parse(stream);
        var all = doc.RootElement.EnumerateArray()
            .Select(e => (
                Id: e.GetProperty("question_id").GetString()!,
                Type: e.GetProperty("question_type").GetString()!,
                Q: e.GetProperty("question").GetString()!,
                // 32 of the 500 gold answers are JSON NUMBERS, not strings (a count, a year). GetString()
                // throws on those, and skipping them would silently drop every counting question — the
                // shape a decision model is documented to be worst at, so exactly the ones to keep.
                A: e.TryGetProperty("answer", out var a)
                    ? (a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText())
                    : null))
            .Where(x => !string.IsNullOrWhiteSpace(x.A))
            .ToList();

        if (types is { Length: > 0 })
            all = all.Where(x => types.Contains(x.Type, StringComparer.OrdinalIgnoreCase)).ToList();
        else
        {
            // single-session-preference's "gold answer" is a RUBRIC describing what a good personalised
            // answer does, not an answer. This design feeds the gold back as the correct response, so for
            // that type the item would ask a judge whether a rubric satisfies itself — which measures
            // nothing and produced a misleading row in the first run. Ask for it explicitly with --types if
            // you want to see it; it is not part of the default comparison.
            all = all.Where(x => x.Type != "single-session-preference").ToList();
        }

        // Deterministic selection, stratified by type. The per-type quota ROUNDS UP: integer division gave
        // 6 per type for --limit 40 across 6 types and returned 36 questions while the banner promised 40.
        var typeCount = Math.Max(1, all.Select(y => y.Type).Distinct().Count());
        var perType = (int)Math.Ceiling((double)limit / typeCount);
        var chosen = all.GroupBy(x => x.Type)
            .SelectMany(g => g.OrderBy(x => x.Id, StringComparer.Ordinal).Take(perType))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var items = new List<Item>(chosen.Count * 2);
        foreach (var x in chosen)
        {
            // LongMemEval marks abstention items with an `_abs` question id, and the judges branch on the
            // flag to a different rubric. Dropping it would send "does this contain the gold answer?" at a
            // question whose correct answer is "I cannot answer", scoring every right response wrong.
            var question = new ExternalBenchmarkQuestion
            {
                QuestionId = x.Id,
                QuestionType = x.Type,
                Question = x.Q,
                GoldAnswer = x.A!,
                IsAbstention = x.Id.EndsWith("_abs", StringComparison.Ordinal),
            };
            items.Add(new Item(question, x.A!, true));

            // The distractor is another question's gold answer of the SAME type, so "wrong" cannot be
            // detected by register or shape alone. Deterministic: chosen by a stable hash, wrapping.
            //
            // It must never be another ABSTENTION item's gold text. An abstention question is judged by
            // "does the response recognise it cannot answer?", and another abstention item's gold is an
            // explanation of unanswerability — a CORRECT refusal. Using it as the negative half would put
            // valid answers in the half that must be wrong, and the 50% chance floor would stop holding.
            // A concrete assertion is unambiguously negative under both rubrics, so the distractor always
            // comes from the non-abstention pool.
            var sameType = all
                .Where(y => y.Type == x.Type && y.Id != x.Id && !y.Id.EndsWith("_abs", StringComparison.Ordinal))
                .OrderBy(y => y.Id, StringComparer.Ordinal)
                .ToList();
            if (sameType.Count == 0) continue;   // no usable negative for this question; its gold item still counts
            // string.GetHashCode is randomised PER PROCESS: it would have picked different distractors on
            // every run while the report claimed a deterministic design, and Math.Abs(int.MinValue) is
            // still negative. A stable hash keeps a rerun comparable with the run already published.
            var idx = (int)(StableHash(x.Id) % (uint)sameType.Count);
            items.Add(new Item(question, sameType[idx].A!, false));
        }
        return items;
    }

    /// <summary>FNV-1a. Stable across processes and runtimes, unlike <see cref="string.GetHashCode()"/>.</summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    private static string? FindDataset()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
                {
                    var p = Path.Combine(dir.FullName, "src", "AgentEval.Memory", "Data", "longmemeval", "longmemeval_oracle.json");
                    return File.Exists(p) ? p : null;
                }
                dir = dir.Parent;
            }
        }
        return null;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static int IntArg(string[] argv, string name, int fallback)
    {
        for (var i = 0; i < argv.Length - 1; i++)
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(argv[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                return Math.Max(1, v);
        return fallback;
    }

    private static string? StringArg(string[] argv, string name)
    {
        for (var i = 0; i < argv.Length - 1; i++)
            if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase))
                return argv[i + 1];
        return null;
    }
}
