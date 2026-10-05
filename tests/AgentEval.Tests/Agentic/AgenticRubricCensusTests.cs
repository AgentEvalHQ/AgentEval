// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Core;
using AgentEval.Evals.Agentic;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// The agentic rubrics are read as they are written (#203 review, B9). <see cref="AgenticRubrics"/>' manifest is a reading
/// of each file by hand; these tests hold it to the files: every row of every label and severity table, sampled at its
/// edges, must get the same label and severity from the manifest; the scale must be the one the file asks for; every
/// check's prompt id has a rubric and every rubric a check; every rubric's pass boundary is its evaluator's threshold;
/// a dimensional rubric has a section for every leaf that sends it.
/// </summary>
public class AgenticRubricCensusTests
{
    private const string ResourcePrefix = "AgentEval.Evals.Agentic.Resources.Prompts.";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not found");
    }

    public static TheoryData<string> Paths()
    {
        var data = new TheoryData<string>();
        foreach (var spec in AgenticRubrics.Manifest)
            data.Add(spec.Path);
        return data;
    }

    private static AgenticRubrics.Spec SpecOf(string path) => AgenticRubrics.Manifest.Single(s => s.Path == path);

    [Fact]
    public void EveryEmbeddedRubric_IsInTheManifest_AndRegistered()
    {
        var embedded = typeof(AgenticRubrics).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var manifest = AgenticRubrics.Manifest.Select(s => s.Path.Replace('/', '.')).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal(embedded, manifest);
        AgenticEvalRegistration.Register();
        foreach (var spec in AgenticRubrics.Manifest)
        {
            Assert.True(EvalRubrics.TryGet(AgenticRubrics.IdFor(spec.Path), out var rubric), $"{spec.Path} is not registered");
            Assert.Equal(AgenticRubrics.TextOf(spec.Path), rubric.Text);
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void TheScale_IsTheOneTheFileAsksFor(string path)
    {
        var text = AgenticRubrics.TextOf(path);
        var asked = text.Contains("<integer 0-100>", StringComparison.Ordinal) ? RubricScoreScale.Percent
            : text.Contains("<number 0.0–1.0>", StringComparison.Ordinal) ? RubricScoreScale.Unit
            // No stated scale: the reply example and the tables give scores like 1.0, 0.6, 0.0.
            : Regex.IsMatch(text, @"""score"":\s*[01]\.\d") ? RubricScoreScale.Unit
            : throw new InvalidOperationException($"{path}: cannot tell the scale from the text");

        Assert.Equal(asked, SpecOf(path).Scale);
    }

    // Representative scores (0..1) for one table condition, with the band edges just inside the row.
    private static IEnumerable<double> SamplesOf(string condition, RubricScoreScale scale)
    {
        var c = condition.Replace("`", "", StringComparison.Ordinal).Trim();
        var eps = scale == RubricScoreScale.Percent ? 1.0 : 0.001;
        double V(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        double U(double v) => scale == RubricScoreScale.Percent ? v / 100.0 : v;

        Match m;
        if ((m = Regex.Match(c, @"(?:score\s*)?(?:≥|>=)\s*([\d.]+)\s*and\s*<\s*([\d.]+)")).Success)
            return [U(V(m.Groups[1].Value)), U(V(m.Groups[2].Value) - eps)];
        if ((m = Regex.Match(c, @"([\d.]+)\s*≤\s*score\s*<\s*([\d.]+)")).Success)
            return [U(V(m.Groups[1].Value)), U(V(m.Groups[2].Value) - eps)];
        if ((m = Regex.Match(c, @"score\s*=\s*([\d.]+)")).Success)
            return [U(V(m.Groups[1].Value))];
        if ((m = Regex.Match(c, @"score\s*≤\s*([\d.]+)")).Success)
            return [U(V(m.Groups[1].Value)), 0.0];
        if ((m = Regex.Match(c, @"^(?:score\s*)?(?:≥|>=)\s*([\d.]+)")).Success)
            return [U(V(m.Groups[1].Value)), Math.Min(1.0, U(V(m.Groups[1].Value) + eps))];
        if ((m = Regex.Match(c, @"^(?:score\s*)?<\s*([\d.]+)")).Success)
            return [U(V(m.Groups[1].Value) - eps), 0.0];
        throw new InvalidOperationException($"unparsed table condition '{condition}'");
    }

    private static string OurLabel(AgenticRubrics.Spec spec, double passAt, double score) =>
        score >= passAt ? "pass" : spec.ReviewAt is { } r && score >= r ? "warn" : "fail";

    private static string TheirLabel(string cell) => cell.Replace("`", "", StringComparison.Ordinal).Trim() switch
    {
        "pass" => "pass",
        "fail" => "fail",
        "warn" or "needs_review" => "warn",
        var other => throw new InvalidOperationException($"unknown label '{other}'"),
    };

    // The pass boundary to sample against: the rubric's own. A rubric that states none has no label table to check.
    private static double PassAtFor(string path) => SpecOf(path).PassAt ?? 1.01;

    [Theory]
    [MemberData(nameof(Paths))]
    public void EveryRowOfTheFilesTables_GetsTheSameLabelAndSeverity_FromTheManifest(string path)
    {
        var spec = SpecOf(path);
        var rubric = AgenticRubrics.All().Single(r => r.Id == AgenticRubrics.IdFor(path));
        var passAt = PassAtFor(path);
        var lines = AgenticRubrics.TextOf(path).Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var problems = new List<string>();
        var checkedRows = 0;

        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith('|') || !lines[i + 1].TrimStart().StartsWith("|-", StringComparison.Ordinal))
                continue;
            var header = Cells(lines[i]).Select(h => h.ToLowerInvariant()).ToList();
            var labelCol = header.IndexOf("label");
            var severityCol = header.IndexOf("severity");
            if (labelCol < 0 && severityCol < 0)
                continue;
            var scoreCol = header.IndexOf("score");   // a table of discrete scores

            for (var j = i + 2; j < lines.Length && lines[j].TrimStart().StartsWith('|'); j++)
            {
                var row = Cells(lines[j]);
                // A "score" column holds either a discrete score (1.0, 0.6) or a condition ("≥ 70").
                double[] samples = scoreCol >= 0 && double.TryParse(row[scoreCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var discrete)
                    ? [discrete]
                    : SamplesOf(row[scoreCol >= 0 ? scoreCol : 0], spec.Scale).ToArray();
                foreach (var score in samples)
                {
                    checkedRows++;
                    if (labelCol >= 0 && OurLabel(spec, passAt, score) != TheirLabel(row[labelCol]))
                        problems.Add($"row '{lines[j].Trim()}' at {score}: manifest says {OurLabel(spec, passAt, score)}");
                    if (severityCol >= 0 && rubric.SeverityFor(score) is var ours && ours != row[severityCol].Trim('`', ' '))
                        problems.Add($"row '{lines[j].Trim()}' at {score}: manifest severity {ours ?? "(none)"}");
                }
            }
        }

        // Rules stated in prose instead of a table.
        var text = string.Join('\n', lines);
        var prose = Regex.Match(text, @"`label` is `pass` when `score >= ([\d.]+)`, `warn` when `score >= ([\d.]+)`");
        if (prose.Success)
        {
            var p = double.Parse(prose.Groups[1].Value, CultureInfo.InvariantCulture);
            var w = double.Parse(prose.Groups[2].Value, CultureInfo.InvariantCulture);
            foreach (var (score, expected) in new[] { (p, "pass"), (p - 0.001, "warn"), (w, "warn"), (w - 0.001, "fail") })
            {
                checkedRows++;
                if (OurLabel(spec, passAt, score) != expected)
                    problems.Add($"prose rule at {score}: expected {expected}, manifest says {OurLabel(spec, passAt, score)}");
            }
        }

        var threshold = Regex.Match(text, @"pass threshold for this evaluator is `([\d.]+)`");
        if (threshold.Success && double.Parse(threshold.Groups[1].Value, CultureInfo.InvariantCulture) != passAt)
            problems.Add($"the file's stated pass threshold {threshold.Groups[1].Value} is not {passAt}");

        var hasReviewRule = checkedRows > 0 && Regex.IsMatch(text, @"needs_review`\s*\||`warn`\s*\||`warn` when");
        if (spec.ReviewAt is not null && !hasReviewRule)
            problems.Add("the manifest has a review band the file does not state");
        if (spec.ReviewAt is null && hasReviewRule)
            problems.Add("the file states a needs-review band the manifest does not have");

        Assert.True(problems.Count == 0, path + ": " + string.Join(" | ", problems));
    }

    private static List<string> Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    // Every agentic evaluator, built with a real ChatClientEvaluator judge. Constructor arguments the census cannot
    // invent are supplied by type; an evaluator it cannot build is reported, never skipped.
    private static (List<object> Built, List<string> Unbuilt) BuildEveryAgenticEvaluator(AgentEval.Core.IEvaluator judge)
    {
        var built = new List<object>();
        var unbuilt = new List<string>();
        var types = typeof(AgenticRubrics).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(AgentEval.Evals.IEval).IsAssignableFrom(t));
        foreach (var type in types)
        {
            var ctor = type.GetConstructors()
                .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(AgentEval.Core.IEvaluator)))
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (ctor is null)
                continue;   // no judge, no LLM leaf
            var args = new List<object?>();
            var ok = true;
            foreach (var p in ctor.GetParameters())
            {
                object? value = p.ParameterType == typeof(AgentEval.Core.IEvaluator) ? judge
                    : p.ParameterType == typeof(AgentEval.Evals.Agentic.Safety.Policy.IPolicyResolver) ? new EmptyPolicy()
                    : p.ParameterType == typeof(string) && !p.HasDefaultValue ? "census"
                    : p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
                if (value is null && !p.HasDefaultValue && Nullable.GetUnderlyingType(p.ParameterType) is null
                    && new System.Reflection.NullabilityInfoContext().Create(p).WriteState != System.Reflection.NullabilityState.Nullable)
                    ok = false;
                args.Add(value);
            }

            try
            {
                if (!ok) throw new InvalidOperationException("an argument the census cannot supply");
                built.Add(ctor.Invoke(args.ToArray()));
            }
            catch (Exception ex)
            {
                unbuilt.Add($"{type.Name}: {(ex.InnerException ?? ex).Message}");
            }
        }

        return (built, unbuilt);
    }

    private sealed class EmptyPolicy : AgentEval.Evals.Agentic.Safety.Policy.IPolicyResolver
    {
        public AgentEval.Evals.Agentic.Safety.Policy.ProhibitedActionPolicy GetPolicyFor(string subjectId) => new([], [], [], []);
    }

    // Every AtomicLlmEval reachable from `root` through fields, lists and arrays.
    private static List<AgentEval.Evals.AtomicLlmEval> LeavesIn(object root)
    {
        var found = new List<AgentEval.Evals.AtomicLlmEval>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>([root]);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (!seen.Add(o)) continue;
            if (o is AgentEval.Evals.AtomicLlmEval leaf) { found.Add(leaf); continue; }
            if (o is System.Collections.IEnumerable items and not string)
            {
                foreach (var item in items)
                    if (item is not null && item.GetType().Namespace?.StartsWith("AgentEval", StringComparison.Ordinal) == true) stack.Push(item);
                if (o.GetType().Namespace?.StartsWith("AgentEval", StringComparison.Ordinal) != true) continue;
            }
            for (var t = o.GetType(); t is not null && t.Namespace?.StartsWith("AgentEval", StringComparison.Ordinal) == true; t = t.BaseType)
            {
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                    if (f.GetValue(o) is { } v && (v.GetType().Namespace?.StartsWith("AgentEval", StringComparison.Ordinal) == true || v is System.Collections.IEnumerable))
                        stack.Push(v);
                }
            }
        }

        return found;
    }

    private static T Private<T>(AgentEval.Evals.AtomicLlmEval leaf, string field) =>
        (T)typeof(AgentEval.Evals.AtomicLlmEval).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(leaf)!;

    [Fact]
    public void EveryLiveJudgeLeaf_IsBoundToItsRubric_AndRunsAtTheRubricsPassBoundary()
    {
        // Built the way the CLI builds them (a ChatClientEvaluator judge), so binding is proven on the real path.
        AgenticEvalRegistration.Register();
        var judge = new ChatClientEvaluator(new AgentEval.Tests.Evals.RubricJudgeTests.RecordingChatClient(_ => "{}"));
        var (built, unbuilt) = BuildEveryAgenticEvaluator(judge);
        Assert.True(unbuilt.Count == 0, "evaluators the census could not build: " + string.Join(" | ", unbuilt));

        var leaves = built.SelectMany(LeavesIn).Distinct(ReferenceEqualityComparer.Instance).Cast<AgentEval.Evals.AtomicLlmEval>().ToList();
        Assert.True(leaves.Count >= 55, $"only {leaves.Count} judge leaves found — the walk is broken");

        var problems = new List<string>();
        var sent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var rubric = Private<AgentEval.Core.EvalRubric?>(leaf, "_rubric");
            if (rubric is null) { problems.Add($"{leaf.Key}: no rubric bound"); continue; }
            sent.Add(rubric.Id);
            var threshold = Private<double>(leaf, "_passThreshold");
            if (rubric.PassAt is { } passAt && Math.Abs(passAt - threshold) > 1e-9)
                problems.Add($"{leaf.Key}: runs at {threshold}, its rubric {rubric.Id} passes at {passAt}");
            if (rubric.ReviewAt is { } reviewAt && reviewAt >= threshold)
                problems.Add($"{leaf.Key}: the rubric's review band starts at {reviewAt}, at or above the threshold {threshold}");
        }

        var unsent = AgenticRubrics.Manifest.Select(sp => AgenticRubrics.IdFor(sp.Path)).Where(id => !sent.Contains(id)).ToList();
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.True(unsent.Count == 0, "rubrics no live leaf sends: " + string.Join(", ", unsent));
    }

    // Every (prompt id, leaf key) an AtomicLlmEval construction declares in the agentic sources.
    private static List<(string File, string PromptId, string? Key)> DeclaredLeaves()
    {
        var leaves = new List<(string, string, string?)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "AgentEval.Evals.Agentic"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match block in Regex.Matches(text, @"new AtomicLlmEval\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)"))
            {
                var id = Regex.Match(block.Value, @"promptId:\s*""([^""]+)""");
                if (!id.Success)
                    continue;
                var key = Regex.Match(block.Value, @"key:\s*""([^""]+)""");
                leaves.Add((Path.GetFileName(file), id.Groups[1].Value, key.Success ? key.Groups[1].Value : null));
            }
        }

        return leaves;
    }

    [Fact]
    public void EveryChecksPromptId_HasARubric_AndEveryRubric_IsSentByACheck()
    {
        var declared = DeclaredLeaves().Select(l => l.PromptId).ToHashSet(StringComparer.Ordinal);
        var rubrics = AgenticRubrics.Manifest.Select(s => AgenticRubrics.IdFor(s.Path)).ToHashSet(StringComparer.Ordinal);

        Assert.True(declared.Count >= 47, $"only {declared.Count} prompt ids found — the source scan is broken");
        Assert.Empty(declared.Except(rubrics));    // a check with no rubric would run on the generic default prompt
        Assert.Empty(rubrics.Except(declared));    // a rubric no check sends
    }

    [Fact]
    public void ADimensionalRubric_HasASectionForEveryLeafThatSendsIt()
    {
        var problems = new List<string>();
        foreach (var spec in AgenticRubrics.Manifest.Where(s => s.Dimensional))
        {
            var id = AgenticRubrics.IdFor(spec.Path);
            var text = AgenticRubrics.TextOf(spec.Path);
            var keys = DeclaredLeaves().Where(l => l.PromptId == id).Select(l => l.Key).ToList();
            Assert.NotEmpty(keys);
            foreach (var key in keys)
            {
                if (key is null || !Regex.IsMatch(text, @"^###\s+" + Regex.Escape(key) + @"\s*$", RegexOptions.Multiline))
                    problems.Add($"{spec.Path}: no '### {key}' section");
            }
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }
}
