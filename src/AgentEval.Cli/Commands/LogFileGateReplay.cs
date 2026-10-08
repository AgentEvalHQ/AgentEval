// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using AgentEval.Cli.Commands.Gatekeeper;
using AgentEval.Cli.Infrastructure;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// <c>agenteval log-file gate-replay</c>: what would a different tool-gate configuration have done to the tool calls
/// a run actually made? Reads a <c>--capture-fixture</c> JSONL capture, runs the real gates of two configurations over
/// every captured tool call with <see cref="GateReplayer"/> (no model, no network), and reports where they differ.
/// </summary>
/// <remarks>
/// <para>
/// A configuration is a JSON array of gates, by the ids <c>agenteval gatekeeper list-gates</c> prints, with the
/// parameters <c>gatekeeper inspect</c> takes as flags:
/// <c>[{"gate": "tool:forbidden-tool", "forbidden": ["send_email"]}, {"gate": "tool:argument-pattern", "pattern": "rm -rf"}]</c>.
/// <c>[]</c> is no gate at all.
/// </para>
/// <para>
/// Only the gates that read a call's own arguments are replayed. A capture keeps the text of the earlier messages but
/// not the tool results, so a gate that reads the conversation (referential integrity, taint tracking) would not
/// decide here what it decided live; such a gate is refused, not replayed on half a history.
/// </para>
/// </remarks>
internal static class LogFileGateReplay
{
    /// <summary>What <c>ToolResultSecretGate.MaskSecrets</c> writes in place of each character of a masked secret.</summary>
    private const char MaskCharacter = '\u2588';

    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // The parameter each argument gate takes, by the name a configuration file uses for it.
    private static readonly Dictionary<string, string> s_parameterOf = new(StringComparer.Ordinal)
    {
        ["tool:forbidden-tool"] = "forbidden",
        ["tool:argument-pattern"] = "pattern",
        ["tool:domain-allowlist"] = "allowedDomains",
    };

    public static Command Create()
    {
        var cmd = new Command(
            "gate-replay",
            "Run two tool-gate configurations over the tool calls in a --capture-fixture JSONL capture and show where " +
            "their verdicts differ: what a proposed gate change would have done to real traffic. No model, no network.");

        var capturedArg = new Argument<FileInfo>("captured") { Description = "Path to a JSONL file written by --capture-fixture." };
        var baselineOpt = new Option<FileInfo>("--baseline")
        {
            Required = true,
            Description = "JSON array of the gates in force today, e.g. [{\"gate\": \"tool:forbidden-tool\", \"forbidden\": [\"delete_db\"]}]. [] is no gate.",
        };
        var candidateOpt = new Option<FileInfo>("--candidate")
        {
            Required = true,
            Description = "JSON array of the proposed gates, in the same form.",
        };
        var jsonOpt = new Option<bool>("--json") { Description = "Print the comparison as JSON on stdout instead of a report." };

        cmd.Arguments.Add(capturedArg);
        cmd.Options.Add(baselineOpt);
        cmd.Options.Add(candidateOpt);
        cmd.Options.Add(jsonOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            try
            {
                return await RunAsync(
                    parseResult.GetValue(capturedArg)!,
                    parseResult.GetValue(baselineOpt)!,
                    parseResult.GetValue(candidateOpt)!,
                    parseResult.GetValue(jsonOpt),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"  Error: {ex.Message}");
                return ExitCodes.RuntimeError;
            }
        });

        return cmd;
    }

    /// <summary>
    /// Runs the replay. 0 when it ran; 2 for a configuration file that is missing or cannot be replayed, or a capture
    /// with no tool call; 3 when the capture file does not exist, as for the other <c>log-file</c> commands.
    /// </summary>
    internal static async Task<int> RunAsync(FileInfo captured, FileInfo baseline, FileInfo candidate, bool asJson, CancellationToken ct)
    {
        if (!captured.Exists)
        {
            Console.Error.WriteLine($"  Error: capture file not found: {captured.FullName}");
            return ExitCodes.RuntimeError;
        }

        if (LoadGates(baseline, "--baseline") is not { } baselineGates || LoadGates(candidate, "--candidate") is not { } candidateGates)
        {
            return ExitCodes.UsageError;
        }

        var entries = await LogFileCommand.LoadCapturedEntriesAsync(captured.FullName, ct).ConfigureAwait(false);
        var (calls, sources, masked) = ToolCallsOf(entries);
        if (calls.Count == 0)
        {
            // A replay over no call measured nothing; say so rather than print an empty table that reads as "no difference".
            Console.Error.WriteLine(
                $"  Error: {captured.Name} holds no tool call ({entries.Count} round-trip(s)); there is nothing to replay.");
            return ExitCodes.UsageError;
        }

        var comparison = await GateReplayer.CompareAsync(calls, baselineGates, candidateGates, ct).ConfigureAwait(false);

        // --capture-fixture masks credential shapes (keys, tokens) in what it writes, so a call whose arguments were
        // masked does not carry what the model sent. A gate that reads arguments would judge the mask: such a row is not
        // measured, and it stays out of the counts rather than reading as "no change".
        var argumentGates = baselineGates.Concat(candidateGates).Any(g => g is ArgumentPatternGate or DomainAllowListGate);
        bool Measured(int i) => !(argumentGates && masked[i]);
        var rows = comparison.Rows.Select((row, i) => (Row: row, Index: i)).ToList();
        var notMeasured = rows.Count(r => !Measured(r.Index));
        var diverged = rows.Count(r => Measured(r.Index) && r.Row.Diverged);
        var tightened = rows.Count(r => Measured(r.Index) && r.Row.Baseline.Action != ToolGateAction.Block && r.Row.Candidate.Action == ToolGateAction.Block);
        var loosened = rows.Count(r => Measured(r.Index) && r.Row.Baseline.Action == ToolGateAction.Block && r.Row.Candidate.Action != ToolGateAction.Block);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                calls = comparison.Rows.Count,
                diverged,
                tightened,
                loosened,
                notMeasured,
                rows = rows.Select(r => new
                {
                    round = sources[r.Index].Index,
                    label = sources[r.Index].Label,
                    tool = r.Row.Call.FunctionName,
                    baseline = Verdict(r.Row.Baseline),
                    candidate = Verdict(r.Row.Candidate),
                    diverged = r.Row.Diverged,
                    argumentsMasked = masked[r.Index] ? true : (bool?)null,
                    measured = Measured(r.Index),
                }),
            }, OutputJsonOptions));
            return ExitCodes.Success;
        }

        Console.WriteLine();
        Console.WriteLine($"agenteval log-file gate-replay — {captured.Name}");
        Console.WriteLine($"  baseline : {baseline.Name} ({baselineGates.Count} gate(s))");
        Console.WriteLine($"  candidate: {candidate.Name} ({candidateGates.Count} gate(s))");
        Console.WriteLine();
        Console.WriteLine($"Gate replay — {comparison.Rows.Count} call(s), {diverged} diverged");
        Console.WriteLine();
        foreach (var (row, i) in rows)
        {
            var mark = !Measured(i) ? "  ? " : row.Diverged ? "  ✗ " : "  · ";
            var tail = !Measured(i) ? "   ← arguments masked at capture: not measured" : row.Diverged ? "   ← DIVERGED" : "";
            Console.WriteLine($"{mark}{row.Call.FunctionName}: baseline={Describe(row.Baseline)}  candidate={Describe(row.Candidate)}{tail}");
        }

        Console.WriteLine();
        Console.WriteLine($"  The candidate blocks {tightened} call(s) the baseline let through, and lets through {loosened} the baseline blocked.");
        if (notMeasured > 0)
        {
            Console.WriteLine($"  {notMeasured} call(s) are not counted: their arguments were masked at capture (--capture-fixture masks");
            Console.WriteLine("  credential shapes), so an argument gate's verdict on them says nothing about what the model sent.");
        }

        Console.WriteLine();
        return ExitCodes.Success;

        static string Describe(ToolGateVerdict v) => v.Action is ToolGateAction.Allow ? "Allow" : $"{v.Action}({v.PolicyName})";

        static object Verdict(ToolGateVerdict v) => new { action = v.Action.ToString(), policy = v.PolicyName, reason = v.Reason };
    }

    /// <summary>
    /// Every tool call in the capture's responses, in order, with the round-trip it came from and whether its arguments
    /// were masked at capture (the capture writer replaces credential shapes with █).
    /// </summary>
    internal static (IReadOnlyList<GatedToolCall> Calls, IReadOnlyList<(int Index, string? Label)> Sources, IReadOnlyList<bool> Masked) ToolCallsOf(
        IReadOnlyList<FixtureCaptureEntry> entries)
    {
        var calls = new List<GatedToolCall>();
        var sources = new List<(int, string?)>();
        var masked = new List<bool>();
        foreach (var entry in entries)
        {
            if (entry.Kind != "response" || entry.Response?.ToolCalls is not { Count: > 0 } toolCalls)
            {
                continue;
            }

            // The history the model saw on this round-trip, as captured (text only: tool results are not kept).
            var messages = entry.Request.Messages.Select(m => new ChatMessage(new ChatRole(m.Role), m.Text)).ToList();
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var arguments = toolCalls[i].Arguments is { } args
                    ? new Dictionary<string, object?>(args)
                    : new Dictionary<string, object?>();
                // The name is printed: no control or format character from the capture reaches the terminal.
                var name = new string(toolCalls[i].Name
                    .Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format ? '?' : c)
                    .ToArray());
                calls.Add(new GatedToolCall(
                    name, arguments, entry.Label, entry.Index, i, toolCalls.Count, IsStreaming: false, messages));
                sources.Add((entry.Index, entry.Label));
                masked.Add(arguments.Values.Any(v => (v?.ToString() ?? "").Contains(MaskCharacter)));
            }
        }

        return (calls, sources, masked);
    }

    /// <summary>Reads a configuration file into live gates; prints the reason and returns null when it cannot.</summary>
    internal static IReadOnlyList<IToolGate>? LoadGates(FileInfo file, string option)
    {
        void Fail(string message) => Console.Error.WriteLine($"  Error: {option} {file.Name}: {message}");

        if (!file.Exists)
        {
            Fail($"file not found: {file.FullName}");
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
        }
        catch (JsonException ex)
        {
            Fail($"not valid JSON ({ex.Message})");
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Fail("must be a JSON array of gates, e.g. [{\"gate\": \"tool:forbidden-tool\", \"forbidden\": [\"send_email\"]}]; [] is no gate.");
                return null;
            }

            var gates = new List<IToolGate>();
            var position = 0;
            foreach (var spec in doc.RootElement.EnumerateArray())
            {
                var where = $"gate {position++}";
                if (spec.ValueKind != JsonValueKind.Object
                    || !spec.TryGetProperty("gate", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    Fail($"{where} must be an object with a \"gate\" id.");
                    return null;
                }

                var id = idElement.GetString()!;
                if (Unsupported(id) is { } reason)
                {
                    Fail($"{where}: {reason}");
                    return null;
                }

                var flags = new GateFlags();
                var parameter = s_parameterOf[id];
                foreach (var prop in spec.EnumerateObject())
                {
                    if (prop.Name == "gate")
                    {
                        continue;
                    }

                    if (prop.Name != parameter)
                    {
                        Fail($"{where} ('{id}'): unknown parameter \"{prop.Name}\"; this gate takes \"{parameter}\".");
                        return null;
                    }

                    if (!ReadParameter(id, prop.Value, flags))
                    {
                        Fail($"{where} ('{id}'): \"{parameter}\" must be {(id == "tool:argument-pattern" ? "a string" : "a non-empty array of strings")}.");
                        return null;
                    }
                }

                if (!spec.TryGetProperty(parameter, out _))
                {
                    Fail($"{where} ('{id}'): needs \"{parameter}\".");
                    return null;
                }

                var gate = GateRegistry.TryResolveToolGate(id, flags, out var error);
                if (gate is null)
                {
                    Fail($"{where}: {error}");
                    return null;
                }

                gates.Add(gate);
            }

            return gates;
        }
    }

    // Null when the gate can be replayed from a capture; otherwise why not.
    private static string? Unsupported(string id)
    {
        if (s_parameterOf.ContainsKey(id))
        {
            return null;
        }

        return GateRegistry.Find(id) switch
        {
            null => $"unknown gate '{id}' (agenteval gatekeeper list-gates shows the ids).",
            { Kind: GateKind.Chat } => $"'{id}' is a chat gate; it inspects text, not tool calls.",
            { StateClass: "needs-history" } =>
                $"'{id}' reads the conversation, and a capture keeps the text of earlier messages but not the tool " +
                "results, so its verdict here would not be the one it gave live. gate-replay replays the argument gates: " +
                string.Join(", ", s_parameterOf.Keys) + ".",
            _ => $"'{id}' needs run state a capture does not hold; gate-replay replays the argument gates: " +
                 string.Join(", ", s_parameterOf.Keys) + ".",
        };
    }

    private static bool ReadParameter(string id, JsonElement value, GateFlags flags)
    {
        if (id == "tool:argument-pattern")
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            flags.Pattern = value.GetString();
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
            || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
        {
            return false;
        }

        var target = id == "tool:forbidden-tool" ? flags.Forbidden : flags.AllowedDomains;
        target.AddRange(value.EnumerateArray().Select(v => v.GetString()!));
        return true;
    }
}
