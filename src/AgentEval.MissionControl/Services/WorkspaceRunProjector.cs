// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Writing;

namespace AgentEval.MissionControl.Services;

// Projects recorded facts only. In particular, completed/aborted are not pass/fail decisions.
internal static class WorkspaceRunProjector
{
    internal static EvalResult? CapturedLegacyTree(AefWorkspaceRun source, string resultId) =>
        AgentEval.Results.Adapters.StoreV1.StoreV1CapturedTreeReader.Read(source.Evidence, resultId);

    internal static WorkspaceRunView Run(AefWorkspaceRun source)
    {
        var read = source.Evidence;
        var legacy = At(read.Header, "ext", "agenteval.store-v1");
        var cli = At(read.Header, "ext", "agenteval.cli");
        var subject = Subject(read);
        var solution = At(legacy, "solution"); var git = At(legacy, "git"); var env = At(legacy, "environment");
        return new(source.SourceId, Text(read.Header, "schemaVersion")!, Text(legacy, "contentHash"),
            solution is null ? null : new(Text(solution, "id"), Text(solution, "name")), subject,
            new(read.Info.RunId, read.StartedAt, read.Duration, Verdict(read), Text(legacy, "run", "kind"),
                (int?)Number(legacy, "run", "scenarioCount") ?? Roots(read).Count(), Text(legacy, "run", "harness"), (int?)Number(legacy, "run", "seed")),
            git is not null ? new(Text(git, "commit"), Text(git, "branch"), Boolean(git, "dirty"))
                : cli is not null ? new(Text(cli, "gitCommit"), Text(cli, "gitBranch"), Boolean(cli, "gitDirty")) : null,
            env is null ? null : new(Text(env, "os"), Text(env, "dotnetVersion"), Boolean(env, "ci")),
            new(Text(legacy, "agentEval", "version") ?? Text(read.Header, "producer", "version")), read.Info);
    }

    internal static WorkspaceSubjectIdentity Subject(AefRunEvidence read)
    {
        var legacy = At(read.Header, "ext", "agenteval.store-v1", "subject");
        var kindText = Text(read.Header, "subject", "kind")!;
        var kind = kindText switch
        {
            "agent" => AefSubjectKind.Agent, "workflow" => AefSubjectKind.Workflow,
            "model" => AefSubjectKind.Model, "endpoint" => AefSubjectKind.Endpoint,
            "mcp-server" => AefSubjectKind.McpServer, _ => AefSubjectKind.Other,
        };
        var name = Text(legacy, "name") ?? Uri.UnescapeDataString(read.Info.SubjectRef[(read.Info.SubjectRef.IndexOf(':') + 1)..]);
        return new(kind, name, read.Info.SubjectRef, Text(legacy, "version") ?? Text(read.Header, "subject", "version"),
            Text(legacy, "framework"), Text(legacy, "modelId"), Text(legacy, "sourceProject"), Text(legacy, "sourcePath")) { RawKind = kindText };
    }

    internal static WorkspaceRunPointer Pointer(AefWorkspaceRun source)
    {
        var read = source.Evidence; var subject = Subject(read);
        return new(source.SourceId, read.Info.RunId, subject.Name, subject.Kind, Verdict(read), read.StartedAt,
            SummaryScore(read), read.Duration?.TotalMilliseconds, Number(read.Summary, "cost", "totalUsd"))
            { SubjectRef = read.Info.SubjectRef, RawKind = subject.RawKind };
    }

    internal static WorkspaceRunSummary? Summary(AefWorkspaceRun source)
    {
        var read = source.Evidence;
        if (read.Summary is null) return null;
        var legacy = At(read.Summary, "ext", "agenteval.store-v1", "stats");
        var roots = Roots(read).ToArray();
        var stats = legacy is not null
            ? new WorkspaceRunStats((int)Number(legacy, "total")!, (int)Number(legacy, "passed")!, (int)Number(legacy, "failed")!,
                (int)Number(legacy, "warnings")!, (int)Number(legacy, "skipped")!)
            : new(roots.Length, roots.Count(r => Text(r, "state") == "passed"), roots.Count(r => Text(r, "state") == "failed"),
                roots.Count(r => Text(r, "state") == "warn"), roots.Count(r => Text(r, "state") == "skipped"));
        var usage = At(read.Summary, "usage");
        var cost = At(read.Summary, "cost");
        return new(read.Info.RunId, Verdict(read), stats,
            usage is null && cost is null ? null : new(Number(cost, "totalUsd"), SumTokens(usage, "inputTokens"), SumTokens(usage, "outputTokens")))
        {
            States = roots.GroupBy(r => Text(r, "state")!, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new WorkspaceStateCount(g.Key, g.Count())).ToArray(),
        };
    }

    internal static IReadOnlyList<WorkspaceScenario> Scenarios(AefWorkspaceRun source) => Roots(source.Evidence).Select(r =>
    {
        var state = Text(r, "state")!; var id = Text(r, "caseId")!;
        var score = Array(At(r, "scores")).SingleOrDefault(s => Text(s, "metric") == "score");
        var measured = state is "passed" or "failed" or "warn" or "scored" or "inconclusive";
        var duration = Number(r, "durationMs");
        var usage = Array(At(r, "usage")).ToArray();
        return new WorkspaceScenario(id, Text(r, "resultId")!, ScenarioName(r, id), state,
            state == "passed" ? true : state == "failed" ? false : null,
            measured ? Number(score, "value") : null, measured ? null : state == "not_applicable" ? "not_applicable" : "not_measured",
            duration is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
            usage.Length > 0 && usage.All(u => Number(u, "costUsd") is not null) ? usage.Sum(u => Number(u, "costUsd")!.Value) : null);
    }).ToArray();

    private static string ScenarioName(JsonElement result, string id)
    {
        if (Text(result, "ext", "agenteval.store-v1", "name") is { } legacy) return legacy;
        var comparison = At(result, "ext", "agenteval.comparison");
        if (Number(comparison, "version") == 1 && At(comparison, "name") is { } name)
        {
            if (name.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Expected: a recorded comparison name string. Actual: a malformed name. Suggestions: restore the producer's evidence. Because: presentation must not coerce or invent a label.");
            return name.GetString()!;
        }
        return id;
    }

    private static IEnumerable<JsonElement> Roots(AefRunEvidence read) => read.Results.Where(r => At(r, "parentResultId") is null && At(r, "trial") is null);
    private static double? SummaryScore(AefRunEvidence read)
    {
        // A benchmark's own preset score is authoritative, including an explicit null.
        if (At(read.Summary, "ext", "agenteval.bench") is { } bench) return Number(bench, "score");
        var values = Array(At(read.Summary, "lanes")).SelectMany(l => Array(At(l, "metrics")))
            .Where(m => Text(m, "metric") == "score").ToArray();
        return values.Length == 1 ? Number(values[0], "value") : null;
    }
    private static string? Verdict(AefRunEvidence read) => (Text(read.Summary, "ext", "agenteval.store-v1", "verdict")
        ?? Text(read.Summary, "ext", "agenteval.bench", "label") ?? Text(read.Header, "ext", "agenteval.store-v1", "run", "verdict"))?.ToUpperInvariant();

    private static long? SumTokens(JsonElement? usage, string name)
    {
        var lines = Array(usage).ToArray();
        if (lines.Length == 0) return null;
        long total = 0;
        foreach (var line in lines)
        {
            if (At(line, name) is not { } value || !value.TryGetInt64(out var number)) return null;
            try { total = checked(total + number); } catch (OverflowException) { return null; }
        }
        return total;
    }

    private static JsonElement? At(JsonElement? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(key, out var next)) return null;
            node = next;
        }
        return node is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : node;
    }
    private static string? Text(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static double? Number(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static bool? Boolean(JsonElement? node, params string[] path) => At(node, path) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;
    private static IEnumerable<JsonElement> Array(JsonElement? node) => node is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];
}
