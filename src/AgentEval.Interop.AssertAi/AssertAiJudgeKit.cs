// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using Microsoft.Extensions.AI;

namespace AgentEval.Interop.AssertAi;

/// <summary>
/// A conversation AgentEval already has (a labelled golden case, a stored run) to hand to ASSERT's judge.
/// </summary>
/// <param name="CaseId">AgentEval's id for the case; kept in the case map, since ASSERT numbers cases by position.</param>
/// <param name="Messages">The conversation: system, user and assistant text, function calls and their results.</param>
/// <param name="ExpectedVerdict"><c>pass</c> or <c>fail</c> when the case is labelled, for calibration.</param>
public sealed record AssertAiTranscript(string CaseId, IReadOnlyList<ChatMessage> Messages, string? ExpectedVerdict = null)
{
    /// <summary>A transcript from an eval input: its system message, query, tool calls and response.</summary>
    /// <param name="caseId">The case id.</param>
    /// <param name="input">The input.</param>
    /// <param name="expectedVerdict"><c>pass</c> or <c>fail</c>, when labelled.</param>
    /// <param name="earlierTurns">Earlier (role, text) turns, oldest first, when the case is multi-turn.</param>
    public static AssertAiTranscript FromEvalInput(string caseId, EvalInput input, string? expectedVerdict = null, IEnumerable<(string Role, string Text)>? earlierTurns = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(input.SystemMessage))
        {
            messages.Add(new ChatMessage(ChatRole.System, input.SystemMessage));
        }

        foreach (var (role, text) in earlierTurns ?? [])
        {
            messages.Add(new ChatMessage(string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User, text));
        }

        messages.Add(new ChatMessage(ChatRole.User, input.Query));
        var i = 0;
        foreach (var call in input.ToolCalls ?? [])
        {
            var id = $"call-{++i}";
            var args = call.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, call.Name, args)]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(id, call.Result ?? call.Error ?? string.Empty)]));
        }

        if (input.Response is not null)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, input.Response));
        }

        return new AssertAiTranscript(caseId, messages, expectedVerdict);
    }
}

/// <summary>Which AgentEval case each ASSERT test case is, and its label (<c>agenteval-cases.json</c>).</summary>
/// <param name="LabelSet">What the cases are (e.g. the golden files they came from).</param>
/// <param name="Cases">One entry per case, in file order.</param>
/// <param name="InferenceSetSha256">The SHA-256 of the <c>inference_set.jsonl</c> written with the map. ASSERT numbers
/// cases by position, so verdicts on any other file would be matched to the wrong cases.</param>
public sealed record AssertAiCaseMap(string LabelSet, IReadOnlyList<AssertAiCaseMapEntry> Cases, string? InferenceSetSha256 = null)
{
    /// <summary>The hex SHA-256 of a file's bytes.</summary>
    public static string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>The file name the judge kit writes.</summary>
    public const string FileName = "agenteval-cases.json";

    /// <summary>AgentEval case id → ASSERT case, for <see cref="AssertAiVerdictEval"/>.</summary>
    public IReadOnlyDictionary<string, AssertAiCaseKey> ToDictionary() =>
        Cases.GroupBy(c => c.CaseId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

    /// <summary>Reads a case map file.</summary>
    public static AssertAiCaseMap Read(string path)
    {
        var json = AssertAiJson.ReadObject(path);
        var cases = new List<AssertAiCaseMapEntry>();
        foreach (var item in json["cases"] as JsonArray ?? throw new InvalidDataException($"{path}: no cases list."))
        {
            if (item is not JsonObject c)
            {
                throw new InvalidDataException($"{path}: a case is not an object.");
            }

            cases.Add(new AssertAiCaseMapEntry(
                AssertAiJson.Str(c["caseId"]) ?? throw new InvalidDataException($"{path}: a case has no caseId."),
                new AssertAiCaseKey(AssertAiJson.Str(c["type"]) ?? "prompt", AssertAiJson.Str(c["testCaseId"]) ?? throw new InvalidDataException($"{path}: a case has no testCaseId.")),
                AssertAiJson.Str(c["expectedVerdict"])));
        }

        return new AssertAiCaseMap(AssertAiJson.Str(json["labelSet"]) ?? Path.GetFileName(path), cases, AssertAiJson.Str(json["inferenceSetSha256"]));
    }

    /// <summary>Writes the file.</summary>
    public void Write(string path)
    {
        var cases = new JsonArray();
        foreach (var c in Cases)
        {
            var item = new JsonObject { ["caseId"] = c.CaseId, ["type"] = c.Key.Type, ["testCaseId"] = c.Key.TestCaseId };
            if (c.ExpectedVerdict is not null) item["expectedVerdict"] = c.ExpectedVerdict;
            cases.Add(item);
        }

        AssertAiJson.WriteObject(path, new JsonObject { ["schemaVersion"] = 1, ["labelSet"] = LabelSet, ["inferenceSetSha256"] = InferenceSetSha256, ["cases"] = cases });
    }
}

/// <summary>One case of an <see cref="AssertAiCaseMap"/>.</summary>
public sealed record AssertAiCaseMapEntry(string CaseId, AssertAiCaseKey Key, string? ExpectedVerdict);

/// <summary>What <see cref="AssertAiJudgeKit.Write"/> needs besides the transcripts.</summary>
public sealed record AssertAiJudgeKitOptions
{
    /// <summary>The taxonomy ASSERT's judge grades against (ASSERT cannot judge without one).</summary>
    public required JsonObject Taxonomy { get; init; }

    /// <summary>The judge model, as ASSERT names it (LiteLLM: <c>azure/gpt-5.4</c>, <c>openai/…</c>).</summary>
    public required string JudgeModel { get; init; }

    /// <summary>The suite id. Letters, digits, <c>.</c>, <c>_</c>, <c>-</c>; default <c>agenteval</c>.</summary>
    public string Suite { get; init; } = "agenteval";

    /// <summary>The run id; default <c>judge-1</c>.</summary>
    public string Run { get; init; } = "judge-1";

    /// <summary>What the cases are, for the case map.</summary>
    public string LabelSet { get; init; } = "agenteval transcripts";

    /// <summary>The target name written on every row (what was evaluated).</summary>
    public string Target { get; init; } = "agenteval";

    /// <summary>The directory as ASSERT will see it, when it runs elsewhere (a container). Default: the output
    /// directory. Must be absolute (<c>/…</c> or <c>C:\…</c>): ASSERT resolves a relative <c>artifacts_root</c> against its own
    /// install.</summary>
    public string? AssertRoot { get; init; }
}

/// <summary>
/// Writes AgentEval transcripts as an ASSERT run that ASSERT's judge can grade without calling the agent again:
/// <c>results/&lt;suite&gt;/taxonomy.json</c>, <c>results/&lt;suite&gt;/&lt;run&gt;/inference_set.jsonl</c> and a
/// judge-only config (<c>assert-judge-config.yaml</c>), plus <c>agenteval-cases.json</c> mapping ASSERT's positional
/// ids back to AgentEval's. Run <c>assert-ai run --config assert-judge-config.yaml</c>, then read the run back with
/// <see cref="AssertAiRun.Read"/> and <see cref="AssertAiCalibration.Measure"/>. Writing again into the same directory
/// removes what an earlier ASSERT run left in the run directory (its scores and bookkeeping), so old verdicts are never
/// read against new cases.
/// </summary>
/// <remarks>
/// Every row is a prompt row (no tester model): ASSERT splits its metrics on the tester model, and these
/// conversations had no ASSERT tester. A system message becomes ASSERT's <c>set_system_message</c> event and each tool
/// call with its result one <c>tool_call</c> event, the shapes ASSERT's own inference stage writes.
/// </remarks>
public static class AssertAiJudgeKit
{
    /// <summary>The config file name.</summary>
    public const string ConfigFileName = "assert-judge-config.yaml";

    /// <summary>Writes the kit to <paramref name="outputDirectory"/> and returns its case map.</summary>
    public static AssertAiCaseMap Write(string outputDirectory, IReadOnlyList<AssertAiTranscript> transcripts, AssertAiJudgeKitOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(options);
        if (transcripts.Count == 0)
        {
            throw new ArgumentException("There are no transcripts to write.", nameof(transcripts));
        }

        foreach (var id in new[] { options.Suite, options.Run })
        {
            if (!IsAssertId(id))
            {
                throw new ArgumentException($"'{id}' is not an ASSERT suite or run id: letters, digits, '.', '_' and '-', starting with a letter or digit.", nameof(options));
            }
        }

        var taxonomy = AssertAiTaxonomy.FromJson(options.Taxonomy);
        if (taxonomy.Categories.Count == 0)
        {
            throw new ArgumentException("The taxonomy has no behavior_categories; ASSERT's judge cannot run without them.", nameof(options));
        }

        // ASSERT skips categories without a name and refuses two with the same one (core/judge.py).
        var duplicate = taxonomy.Categories.Where(c => c.Name.Length > 0).GroupBy(c => c.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"The taxonomy names '{duplicate.Key}' twice; ASSERT's judge refuses that.", nameof(options));
        }

        if (options.AssertRoot is { } assertRootOption && !(assertRootOption.StartsWith('/') || Path.IsPathFullyQualified(assertRootOption)))
        {
            throw new ArgumentException($"AssertRoot must be an absolute path ('{assertRootOption}' is not): ASSERT resolves a relative artifacts_root against its own install.", nameof(options));
        }

        var root = Path.GetFullPath(outputDirectory);
        var suiteDir = Path.Combine(root, "results", options.Suite);
        var runDir = Path.Combine(suiteDir, options.Run);
        Directory.CreateDirectory(runDir);
        foreach (var stale in new[] { "scores.jsonl", ".judge_config_hash", "manifest.json", "artifacts.json", "config.yaml", "metrics.json" })
        {
            File.Delete(Path.Combine(runDir, stale));
        }

        if (Directory.Exists(Path.Combine(runDir, ".viewer")))
        {
            Directory.Delete(Path.Combine(runDir, ".viewer"), recursive: true);
        }

        var behavior = taxonomy.BehaviorName ?? "agenteval";
        var rows = new List<JsonNode>();
        var cases = new List<AssertAiCaseMapEntry>();
        for (var i = 0; i < transcripts.Count; i++)
        {
            var t = transcripts[i];
            var key = new AssertAiCaseKey("prompt", $"test_case_{i + 1:000000}");
            rows.Add(InferenceRow(key, behavior, options.Target, t));
            cases.Add(new AssertAiCaseMapEntry(t.CaseId, key, t.ExpectedVerdict));
        }

        AssertAiJson.WriteObject(Path.Combine(suiteDir, "taxonomy.json"), options.Taxonomy.DeepClone());
        var inferenceSet = Path.Combine(runDir, "inference_set.jsonl");
        AssertAiJson.WriteJsonLines(inferenceSet, rows);
        var map = new AssertAiCaseMap(options.LabelSet, cases, AssertAiCaseMap.Sha256(inferenceSet));
        map.Write(Path.Combine(root, AssertAiCaseMap.FileName));

        var assertRoot = (options.AssertRoot ?? root).Replace('\\', '/');
        var config = new StringBuilder()
            .Append("# ASSERT judge-only run over transcripts written by AgentEval (agenteval assert-ai export).\n")
            .Append("# Run: assert-ai run --config ").Append(ConfigFileName).Append('\n')
            .Append("suite: ").Append(Yaml(options.Suite)).Append('\n')
            .Append("run: ").Append(Yaml(options.Run)).Append('\n')
            .Append("artifacts_root: ").Append(Yaml(assertRoot)).Append('\n')
            .Append("results_dir: ").Append(Yaml(assertRoot + "/results")).Append('\n')
            .Append("pipeline:\n  judge:\n    model:\n      name: ").Append(Yaml(options.JudgeModel)).Append('\n')
            .ToString();
        File.WriteAllText(Path.Combine(root, ConfigFileName), config, new UTF8Encoding(false));
        return map;
    }

    /// <summary>One <c>inference_set.jsonl</c> row, in the shape ASSERT's inference stage writes for a prompt case.</summary>
    /// <summary>The tool result written for a call that got none, so the judge sees the call without reading it as run.</summary>
    public const string NoResult = "(no result: the call was not run)";

    internal static JsonObject InferenceRow(AssertAiCaseKey key, string behavior, string target, AssertAiTranscript transcript)
    {
        var events = new JsonArray();
        var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var answered = transcript.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet(StringComparer.Ordinal);
        foreach (var message in transcript.Messages)
        {
            var text = string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
            if (message.Role == ChatRole.System)
            {
                events.Add(Event(["system", "target", "combined"], "tester", new JsonObject
                {
                    ["type"] = "set_system_message",
                    ["message"] = new JsonObject { ["role"] = "system", ["content"] = text },
                }, null));
                continue;
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call when !answered.Contains(call.CallId):
                        events.Add(ToolEvent(call.Name, call, NoResult));   // in place: the judge still sees it was made
                        break;
                    case FunctionCallContent call:
                        calls[call.CallId] = call;
                        break;
                    case FunctionResultContent result:
                        calls.Remove(result.CallId, out var made);
                        events.Add(ToolEvent(made?.Name ?? "tool", made, ResultText(result)));
                        break;
                }
            }

            if (message.Role == ChatRole.User || (message.Role == ChatRole.Assistant && text.Length > 0))
            {
                var role = message.Role == ChatRole.User ? "user" : "assistant";
                var payload = new JsonObject { ["role"] = role, ["content"] = text };
                events.Add(Event(["target", "combined"], "target", new JsonObject { ["type"] = "add_message", ["message"] = payload },
                    role == "user" ? new JsonObject { ["message"] = payload.DeepClone() } : null));
            }
        }

        return new JsonObject
        {
            ["type"] = key.Type,
            ["test_case_id"] = key.TestCaseId,
            ["behavior"] = behavior,
            ["events"] = events,
            ["llm_calls"] = new JsonArray(),
            ["stop_reason"] = "completed",
            ["target"] = target,
            ["tester_model"] = string.Empty,
            ["target_reasoning_effort"] = null,
            ["tester_reasoning_effort"] = null,
        };
    }

    private static JsonObject ToolEvent(string name, FunctionCallContent? call, string result) =>
        Event(["target", "combined"], "tool", new JsonObject
        {
            ["type"] = "tool_call",
            ["tool_name"] = name,
            ["tool_args"] = call?.Arguments is { } a ? JsonSerializer.SerializeToNode(a, AIJsonUtilities.DefaultOptions) as JsonObject ?? new JsonObject() : new JsonObject(),
            ["tool_result"] = result,
        }, null);

    private static JsonObject Event(string[] view, string actor, JsonObject edit, JsonObject? raw) => new()
    {
        ["view"] = new JsonArray(view.Select(v => (JsonNode)v).ToArray()),
        ["actor"] = actor,
        ["edit"] = edit,
        ["raw"] = raw,
    };

    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        null when result.Exception is { } ex => $"Error: {ex.Message}",
        null => string.Empty,
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText(),
        var other => JsonSerializer.Serialize(other, AIJsonUtilities.DefaultOptions),
    };

    // ASSERT's suite and run ids: [A-Za-z0-9][A-Za-z0-9._-]*, at most 255 characters (config.py).
    private static bool IsAssertId(string id) =>
        id.Length is > 0 and <= 255 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // A YAML scalar written as a JSON string, which YAML reads as the same string.
    private static string Yaml(string value) => JsonValue.Create(value)!.ToJsonString();
}
