// V3Check v2 — WITHIN-QUESTION separability probe of the published AgentEval 0.23.0-beta
// TypedMemEval v3 corpora. Fully offline; no API calls.
//
// Method (matches the corpus's own V7 definition, independently reimplemented):
//   single-feature AUC over (gold, distractor) pairs formed WITHIN a question, pooled across
//   questions, folded once to [0.5, 1.0]. Questions with no gold contribute no pairs.
// Additionally (coordinator's ask): the PER-QUESTION AUC DISTRIBUTION per feature — mean, median,
//   p90, max, share of questions >= 0.75, share at 1.0 — because a bimodal split hides in a mean.
// Finer slices per the maintainer's own §14 admission: per-role turn counts, first/last turn
//   lengths, punctuation-class counts, date-mention counts, digit/uppercase densities, and a
//   unigram+bigram presence screen restricted to n-grams recurring in >= 20% of questions.
//
// STOP rule: any non-exempt feature with pooled within-question folded AUC >= 0.75 is a
// corpus-blocking finding. position_in_haystack is exempt on workingmemory (documented: gold
// distance is the vertical's independent variable).

using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

var dateRegex = new Regex(
    @"\b(jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|jun(e)?|jul(y)?|aug(ust)?|sep(tember)?|oct(ober)?|nov(ember)?|dec(ember)?|monday|tuesday|wednesday|thursday|friday|saturday|sunday|today|tomorrow|yesterday|tonight|next week|next month|last week|last month|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}|\d{1,2}(st|nd|rd|th))\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);
var wordRegex = new Regex(@"[a-z]{2,}", RegexOptions.Compiled);

var report = new Dictionary<string, object>();

foreach (var descriptor in TypedMemEvalVerticals.All)
{
    var vertical = descriptor.Vertical;
    var options = new TypedMemEvalOptions();
    options.Validate();
    var entries = TypedMemEvalCorpus.Load(vertical, new ExternalBenchmarkOptions());

    // Per question: lists of per-session feature vectors, split gold/distractor.
    var featureNames = new List<string>();
    var perQuestionGold = new List<List<double[]>>();
    var perQuestionDis = new List<List<double[]>>();
    var perQuestionGoldTokens = new List<List<HashSet<string>>>();
    var perQuestionDisTokens = new List<List<HashSet<string>>>();

    foreach (var entry in entries)
    {
        var sessions = entry.HaystackSessions ?? new();
        var ids = entry.HaystackSessionIds ?? new();
        var gold = new HashSet<string>(entry.AnswerSessionIds ?? new());
        if (gold.Count == 0) continue; // abstention questions contribute no pairs
        var g = new List<double[]>(); var d = new List<double[]>();
        var gt = new List<HashSet<string>>(); var dt = new List<HashSet<string>>();

        for (int s = 0; s < sessions.Count && s < ids.Count; s++)
        {
            var turns = sessions[s];
            string all = string.Join("\n", turns.Select(t => t.Content ?? ""));
            string allLower = all.ToLowerInvariant();
            var userTurns = turns.Where(t => string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)).ToList();
            var asstTurns = turns.Where(t => !string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)).ToList();
            double chars = all.Length;
            double turnCount = turns.Count;

            var f = new Dictionary<string, double>
            {
                ["turn_count"] = turnCount,
                ["session_chars"] = chars,
                ["mean_turn_chars"] = turnCount > 0 ? turns.Average(t => (double)(t.Content?.Length ?? 0)) : 0,
                ["user_turn_count"] = userTurns.Count,
                ["assistant_turn_count"] = asstTurns.Count,
                ["user_chars"] = userTurns.Sum(t => (double)(t.Content?.Length ?? 0)),
                ["assistant_chars"] = asstTurns.Sum(t => (double)(t.Content?.Length ?? 0)),
                ["first_turn_chars"] = turns.Count > 0 ? turns[0].Content?.Length ?? 0 : 0,
                ["last_turn_chars"] = turns.Count > 0 ? turns[^1].Content?.Length ?? 0 : 0,
                ["first_user_chars"] = userTurns.Count > 0 ? userTurns[0].Content?.Length ?? 0 : 0,
                ["first_assistant_chars"] = asstTurns.Count > 0 ? asstTurns[0].Content?.Length ?? 0 : 0,
                ["last_assistant_chars"] = asstTurns.Count > 0 ? asstTurns[^1].Content?.Length ?? 0 : 0,
                ["full_stop_count"] = all.Count(c => c == '.'),
                ["comma_count"] = all.Count(c => c == ','),
                ["em_dash_count"] = all.Count(c => c == '—') + Regex.Matches(all, " -- | - ").Count,
                ["question_mark_count"] = all.Count(c => c == '?'),
                ["exclamation_count"] = all.Count(c => c == '!'),
                ["semicolon_count"] = all.Count(c => c == ';'),
                ["colon_count"] = all.Count(c => c == ':'),
                ["apostrophe_count"] = all.Count(c => c == '\'' || c == '’'),
                ["punct_density"] = chars > 0 ? all.Count(char.IsPunctuation) / chars : 0,
                ["digit_density"] = chars > 0 ? all.Count(char.IsDigit) / chars : 0,
                ["uppercase_density"] = chars > 0 ? all.Count(char.IsUpper) / chars : 0,
                ["date_mention_count"] = dateRegex.Matches(all).Count,
                ["position_in_haystack"] = sessions.Count > 1 ? (double)s / (sessions.Count - 1) : 0,
            };
            if (featureNames.Count == 0) featureNames.AddRange(f.Keys);
            var vec = featureNames.Select(n => f[n]).ToArray();

            var toks = new HashSet<string>();
            foreach (Match m in wordRegex.Matches(allLower)) toks.Add(m.Value);
            // bigrams over word sequence
            var words = wordRegex.Matches(allLower).Select(m => m.Value).ToArray();
            for (int w = 0; w + 1 < words.Length; w++) toks.Add(words[w] + " " + words[w + 1]);

            bool isGold = gold.Contains(ids[s]);
            (isGold ? g : d).Add(vec);
            (isGold ? gt : dt).Add(toks);
        }
        if (g.Count > 0 && d.Count > 0)
        {
            perQuestionGold.Add(g); perQuestionDis.Add(d);
            perQuestionGoldTokens.Add(gt); perQuestionDisTokens.Add(dt);
        }
    }

    Console.WriteLine($"== {descriptor.Slug} — {perQuestionGold.Count} gold-bearing questions with pairs");
    var verticalReport = new Dictionary<string, object>();

    // ---- numeric features: pooled within-question AUC + per-question distribution ----
    var rows = new List<(string name, double pooledFolded, double pooledRaw, double meanQ, double medQ, double p90Q, double maxQ, double shareGe075, double shareEq1)>();
    for (int fi = 0; fi < featureNames.Count; fi++)
    {
        double win = 0, pairs = 0;
        var perQ = new List<double>();
        for (int q = 0; q < perQuestionGold.Count; q++)
        {
            double qwin = 0, qpairs = 0;
            foreach (var gv in perQuestionGold[q])
                foreach (var dv in perQuestionDis[q])
                {
                    qpairs++;
                    if (gv[fi] > dv[fi]) qwin += 1;
                    else if (gv[fi] == dv[fi]) qwin += 0.5;
                }
            win += qwin; pairs += qpairs;
            if (qpairs > 0) perQ.Add(Math.Max(qwin / qpairs, 1 - qwin / qpairs)); // folded per question
        }
        double raw = pairs > 0 ? win / pairs : 0.5;
        double folded = Math.Max(raw, 1 - raw);
        perQ.Sort();
        double MeanQ = perQ.Average();
        double MedQ = perQ[perQ.Count / 2];
        double P90Q = perQ[(int)Math.Min(perQ.Count - 1, Math.Ceiling(0.9 * perQ.Count) - 1)];
        double MaxQ = perQ[^1];
        double ShareGe = perQ.Count(v => v >= 0.75) / (double)perQ.Count;
        double ShareEq1 = perQ.Count(v => v >= 0.9999) / (double)perQ.Count;
        rows.Add((featureNames[fi], folded, raw, MeanQ, MedQ, P90Q, MaxQ, ShareGe, ShareEq1));
    }

    foreach (var r in rows.OrderByDescending(r => r.pooledFolded))
    {
        string mark = r.pooledFolded >= 0.75 ? " <<< BLOCKING (>=0.75 pooled within-question)" :
                      r.pooledFolded >= 0.65 ? " << elevated" : "";
        Console.WriteLine($"   {r.name,-24} pooled={r.pooledFolded:F3} (raw {r.pooledRaw:F3}) | perQ mean={r.meanQ:F3} med={r.medQ:F3} p90={r.p90Q:F3} max={r.maxQ:F3} shareAUC>=.75={r.shareGe075:P0} share=1.0={r.shareEq1:P0}{mark}");
    }
    verticalReport["numeric"] = rows.Select(r => new { r.name, r.pooledFolded, r.pooledRaw, r.meanQ, r.medQ, r.p90Q, r.maxQ, r.shareGe075, r.shareEq1 }).ToList();

    // ---- n-gram presence screen (unigrams + bigrams recurring in >=20% of questions) ----
    int qCount = perQuestionGold.Count;
    var questionPresence = new Dictionary<string, int>();
    for (int q = 0; q < qCount; q++)
    {
        var seen = new HashSet<string>();
        foreach (var set in perQuestionGoldTokens[q]) seen.UnionWith(set);
        foreach (var set in perQuestionDisTokens[q]) seen.UnionWith(set);
        foreach (var t in seen)
            questionPresence[t] = questionPresence.GetValueOrDefault(t) + 1;
    }
    var candidates = questionPresence.Where(kv => kv.Value >= 0.2 * qCount).Select(kv => kv.Key).ToList();
    Console.WriteLine($"   ngram screen: {candidates.Count} candidates (recurring in >=20% of questions)");

    var ngramRows = new List<(string ngram, double folded, double raw, int inQuestions)>();
    foreach (var cand in candidates)
    {
        double win = 0, pairs = 0;
        for (int q = 0; q < qCount; q++)
        {
            int gp = perQuestionGoldTokens[q].Count(s => s.Contains(cand));
            int ga = perQuestionGoldTokens[q].Count - gp;
            int dp = perQuestionDisTokens[q].Count(s => s.Contains(cand));
            int da = perQuestionDisTokens[q].Count - dp;
            win += gp * (double)da + 0.5 * (gp * (double)dp + ga * (double)da);
            pairs += (gp + ga) * (double)(dp + da);
        }
        double raw = pairs > 0 ? win / pairs : 0.5;
        double folded = Math.Max(raw, 1 - raw);
        ngramRows.Add((cand, folded, raw, questionPresence[cand]));
    }
    foreach (var r in ngramRows.OrderByDescending(r => r.folded).Take(10))
    {
        string mark = r.folded >= 0.75 ? " <<< BLOCKING" : r.folded >= 0.65 ? " << elevated" : "";
        Console.WriteLine($"   ngram '{r.ngram}' pooled={r.folded:F3} (raw {r.raw:F3}, in {r.inQuestions}/{qCount} questions){mark}");
    }
    verticalReport["ngramsTop25"] = ngramRows.OrderByDescending(r => r.folded).Take(25)
        .Select(r => new { r.ngram, r.folded, r.raw, r.inQuestions }).ToList();
    report[descriptor.Slug] = verticalReport;
    Console.WriteLine();
}

File.WriteAllText(Path.Combine(outDir, "within-question-probe.json"),
    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("wrote within-question-probe.json");

// ---- detail pass on the flagged/near-miss n-grams: rates + relevance-channel check ----
var details = new (string slug, TypedMemEvalVertical vertical, string ngram)[]
{
    ("workingmemory", TypedMemEvalVertical.WorkingMemory, "have"),
    ("workingmemory", TypedMemEvalVertical.WorkingMemory, "has"),
    ("episodic", TypedMemEvalVertical.Episodic, "on the"),
    ("arithmetic", TypedMemEvalVertical.Arithmetic, "today"),
};
Console.WriteLine("\n== detail: flagged n-grams ==");
foreach (var (slug, vertical, ngram) in details)
{
    var entries = TypedMemEvalCorpus.Load(vertical, new ExternalBenchmarkOptions());
    int goldWith = 0, goldTotal = 0, disWith = 0, disTotal = 0, qWith = 0, qTotal = 0;
    var perQ = new List<double>();
    foreach (var entry in entries)
    {
        var sessions = entry.HaystackSessions ?? new();
        var ids = entry.HaystackSessionIds ?? new();
        var gold = new HashSet<string>(entry.AnswerSessionIds ?? new());
        if (gold.Count == 0) continue;
        qTotal++;
        if (ContainsNgram(entry.Question.ToLowerInvariant(), ngram)) qWith++;
        int gp = 0, ga = 0, dp = 0, da = 0;
        for (int s = 0; s < sessions.Count && s < ids.Count; s++)
        {
            string text = string.Join("\n", sessions[s].Select(t => t.Content ?? "")).ToLowerInvariant();
            bool present = ContainsNgram(text, ngram);
            bool isGold = gold.Contains(ids[s]);
            if (isGold) { goldTotal++; if (present) { goldWith++; gp++; } else ga++; }
            else { disTotal++; if (present) { disWith++; dp++; } else da++; }
        }
        double pairs = (gp + ga) * (double)(dp + da);
        if (pairs > 0)
        {
            double auc = (gp * (double)da + 0.5 * (gp * (double)dp + ga * (double)da)) / pairs;
            perQ.Add(auc);
        }
    }
    perQ.Sort();
    Console.WriteLine($"   {slug} '{ngram}': gold {goldWith}/{goldTotal} ({(double)goldWith / goldTotal:P0}) vs distractor {disWith}/{disTotal} ({(double)disWith / disTotal:P0}); " +
        $"in QUESTION text {qWith}/{qTotal} ({(double)qWith / qTotal:P0}); perQ raw AUC med={perQ[perQ.Count / 2]:F3} min={perQ[0]:F3} max={perQ[^1]:F3} share>=0.75={perQ.Count(v => v >= 0.75) / (double)perQ.Count:P0} share<=0.25={perQ.Count(v => v <= 0.25) / (double)perQ.Count:P0}");
}

static bool ContainsNgram(string textLower, string ngram)
{
    var words = Regex.Matches(textLower, "[a-z]{2,}").Select(m => m.Value).ToArray();
    var parts = ngram.Split(' ');
    if (parts.Length == 1) return words.Contains(parts[0]);
    for (int i = 0; i + parts.Length <= words.Length; i++)
    {
        bool match = true;
        for (int p = 0; p < parts.Length; p++)
            if (words[i + p] != parts[p]) { match = false; break; }
        if (match) return true;
    }
    return false;
}
