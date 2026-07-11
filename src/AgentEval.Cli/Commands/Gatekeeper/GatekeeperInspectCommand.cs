// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// <c>agenteval gatekeeper inspect</c> — run one gate over a JSON payload and emit a versioned verdict. Slice 1
/// covers the deterministic, credential-free gates (text keyword/rendered-exfil + tool/history gates); judge gates
/// (<c>judge:*</c>/<c>panel:*</c>) need the model path and report a clear message.
/// </summary>
internal static class GatekeeperInspectCommand
{
    // exit categories for aggregation (batch): worst wins, structural(2) > block(5) > inconclusive(6) > allow(0)
    private const int CatAllow = ExitCodes.Success;         // 0
    private const int CatStructural = ExitCodes.UsageError;  // 2
    private const int CatBlock = ExitCodes.GateBlocked;      // 5
    private const int CatInconclusive = ExitCodes.GateInconclusive; // 6

    public static Command Create()
    {
        var gateOpt = new Option<string?>("--gate") { Description = "Gate id (see 'gatekeeper list-gates')", Required = true };
        var inputOpt = new Option<string?>("--input") { Description = "Batch: a .jsonl file, one payload per line (else one JSON object on stdin)" };
        var policyOpt = new Option<string>("--policy") { Description = "block (default) | warn (always exit 0; verdict still emitted)", DefaultValueFactory = _ => "block" };

        var keywordOpt = MultiOpt("--keyword", "Keyword to match (keyword / keyword-injection gate); repeatable");
        var keywordsFileOpt = new Option<string?>("--keywords") { Description = "File of keywords, one per line (keyword gate)" };
        var forbiddenOpt = MultiOpt("--forbidden", "Forbidden tool name (tool:forbidden-tool); repeatable");
        var patternOpt = new Option<string?>("--pattern") { Description = "Forbidden argument regex (tool:argument-pattern)" };
        var allowedDomainOpt = MultiOpt("--allowed-domain", "Allowed URL host (tool:domain-allowlist); repeatable");
        var idArgOpt = MultiOpt("--id-arg", "Id argument name (tool:referential-integrity); repeatable");
        var guardedToolOpt = MultiOpt("--guarded-tool", "Guarded tool name (tool:referential-integrity); repeatable");
        var trustedToolOpt = MultiOpt("--trusted-tool", "Trusted lookup tool (tool:referential-integrity); repeatable");
        var sourceToolOpt = MultiOpt("--source-tool", "Confidential source tool (tool:taint-tracking); repeatable");
        var sinkToolOpt = MultiOpt("--sink-tool", "External sink tool (tool:taint-tracking); repeatable");
        var minTaintOpt = new Option<int?>("--min-taint-length") { Description = "Minimum tainted-value length (tool:taint-tracking)" };

        var cmd = new Command("inspect", "Run a Gatekeeper gate over a JSON payload and emit a verdict.");
        foreach (var o in new Option[] { gateOpt, inputOpt, policyOpt, keywordOpt, keywordsFileOpt, forbiddenOpt, patternOpt,
            allowedDomainOpt, idArgOpt, guardedToolOpt, trustedToolOpt, sourceToolOpt, sinkToolOpt, minTaintOpt })
        {
            cmd.Options.Add(o);
        }

        cmd.SetAction(async (parse, ct) =>
        {
            var flags = new GateFlags { Pattern = parse.GetValue(patternOpt), MinTaintLength = parse.GetValue(minTaintOpt) };
            flags.Keywords.AddRange(parse.GetValue(keywordOpt) ?? []);
            var keywordsFile = parse.GetValue(keywordsFileOpt);
            if (!string.IsNullOrEmpty(keywordsFile))
            {
                try { flags.Keywords.AddRange(File.ReadAllLines(keywordsFile).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim())); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"  Error reading --keywords file: {ex.Message}"); return ExitCodes.UsageError; }
            }

            flags.Forbidden.AddRange(parse.GetValue(forbiddenOpt) ?? []);
            flags.AllowedDomains.AddRange(parse.GetValue(allowedDomainOpt) ?? []);
            flags.IdArgs.AddRange(parse.GetValue(idArgOpt) ?? []);
            flags.GuardedTools.AddRange(parse.GetValue(guardedToolOpt) ?? []);
            flags.TrustedTools.AddRange(parse.GetValue(trustedToolOpt) ?? []);
            flags.SourceTools.AddRange(parse.GetValue(sourceToolOpt) ?? []);
            flags.SinkTools.AddRange(parse.GetValue(sinkToolOpt) ?? []);

            return await RunAsync(parse.GetValue(gateOpt)!, parse.GetValue(inputOpt), parse.GetValue(policyOpt) ?? "block",
                flags, Console.In, Console.Out, Console.Error, ct);
        });

        return cmd;
    }

    /// <summary>Testable core: resolve the gate, read the payload(s), run, emit verdict(s), return the exit code.</summary>
    public static async Task<int> RunAsync(
        string gateId, string? inputFile, string policy, GateFlags flags,
        TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var warn = string.Equals(policy, "warn", StringComparison.OrdinalIgnoreCase);
        if (!warn && !string.Equals(policy, "block", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine($"  Error: --policy must be 'block' or 'warn' (got '{policy}').");
            return ExitCodes.UsageError;
        }

        var desc = GateRegistry.Find(gateId) ?? (gateId.StartsWith("keyword:", StringComparison.Ordinal) ? GateRegistry.Find("keyword") : null);
        if (desc is null && !gateId.StartsWith("keyword:", StringComparison.Ordinal) && !gateId.StartsWith("judge:", StringComparison.Ordinal) && !gateId.StartsWith("panel:", StringComparison.Ordinal))
        {
            stderr.WriteLine($"  Error: unknown gate '{gateId}'. Run 'agenteval gatekeeper list-gates'.");
            return ExitCodes.UsageError;
        }

        var isTool = gateId.StartsWith("tool:", StringComparison.Ordinal);
        var stateClass = desc?.StateClass ?? "stateless-text";
        if (stateClass == "needs-run-state")
        {
            stderr.WriteLine($"  Error: gate '{gateId}' needs held run state — available via 'gatekeeper serve', not stateless inspect.");
            return ExitCodes.UsageError;
        }

        // Resolve the gate ONCE (reused across batch lines; deterministic gates recompute per call from the payload).
        IChatGate? chatGate = null;
        IToolGate? toolGate = null;
        string? resolveErr;
        if (isTool)
        {
            toolGate = GateRegistry.TryResolveToolGate(gateId, flags, out resolveErr);
            if (toolGate is null) { stderr.WriteLine($"  Error: {resolveErr}"); return ExitCodes.UsageError; }
        }
        else
        {
            chatGate = GateRegistry.TryResolveChatGate(gateId, flags, out resolveErr);
            if (chatGate is null) { stderr.WriteLine($"  Error: {resolveErr}"); return ExitCodes.UsageError; }
        }

        if (inputFile is null)
        {
            // Single: one JSON object on stdin, pretty-printed verdict.
            var json = await stdin.ReadToEndAsync(ct).ConfigureAwait(false);
            var (verdict, exit) = await EvaluateOneAsync(json, gateId, stateClass, chatGate, toolGate, ct).ConfigureAwait(false);
            stdout.WriteLine(verdict.ToJson(indented: true));
            return warn ? ExitCodes.Success : exit;
        }

        // Batch: one payload per line → one compact verdict per line (JSONL), aggregate exit.
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(inputFile, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stderr.WriteLine($"  Error reading --input: {ex.Message}"); return ExitCodes.UsageError; }

        var worst = CatAllow;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            var (verdict, exit) = await EvaluateOneAsync(line, gateId, stateClass, chatGate, toolGate, ct).ConfigureAwait(false);
            stdout.WriteLine(verdict.ToJson(indented: false));
            worst = WorseOf(worst, exit);
        }

        return warn ? ExitCodes.Success : worst;
    }

    private static async Task<(GateVerdictDto verdict, int exit)> EvaluateOneAsync(
        string json, string gateId, string stateClass, IChatGate? chatGate, IToolGate? toolGate, CancellationToken ct)
    {
        var payload = InspectPayload.Parse(json, out var parseErr);
        if (payload is null)
        {
            return (Structural(gateId, chatGate is not null ? "chat" : "tool", parseErr ?? "invalid payload"), CatStructural);
        }

        if (toolGate is not null)
        {
            if (string.IsNullOrEmpty(payload.Tool))
            {
                return (Structural(gateId, "tool", "tool gate requires a 'tool' field in the payload"), CatStructural);
            }

            IReadOnlyList<ChatMessage> messages;
            if (stateClass == "needs-history")
            {
                var mapped = GateHistoryMapper.ToChatMessages(payload.Messages, out var mapErr);
                if (mapped is null || mapped.Count == 0)
                {
                    return (GateVerdictDto.FailClosed(gateId, "tool", toolGate.PolicyName,
                        $"fail-closed: required history missing or unmappable{(mapErr is null ? "" : $" ({mapErr})")}"), CatInconclusive);
                }

                messages = mapped;
            }
            else
            {
                messages = GateHistoryMapper.ToChatMessages(payload.Messages, out _) ?? Array.Empty<ChatMessage>();
            }

            // positional: FunctionName, Arguments, AgentName, Iteration, FunctionCallIndex, FunctionCount, IsStreaming, Messages
            var call = new GatedToolCall(payload.Tool!, payload.Args, null, 0, 0, 1, false, messages);
            var tv = await toolGate.InspectAsync(call, ct).ConfigureAwait(false);
            var dto = GateVerdictDto.FromTool(tv, gateId);
            return (dto, ExitOf(dto));
        }

        // chat gate
        if (payload.Text is null)
        {
            return (Structural(gateId, "chat", "chat gate requires a 'text' field in the payload"), CatStructural);
        }

        var cv = await chatGate!.InspectAsync(payload.Text, ct).ConfigureAwait(false);
        var cdto = GateVerdictDto.FromChat(cv, gateId);
        return (cdto, ExitOf(cdto));
    }

    private static int ExitOf(GateVerdictDto v) =>
        v.Action == "Allow" ? CatAllow : v.Inconclusive ? CatInconclusive : CatBlock;

    private static GateVerdictDto Structural(string gateId, string kind, string message) => new()
    {
        Gate = gateId,
        Kind = kind,
        Action = "Block",
        Policy = gateId,
        Reason = message,
        Inconclusive = true,
        Warning = $"structural: {message}",
    };

    // aggregation precedence: structural(2) > block(5) > inconclusive(6) > allow(0)
    private static int WorseOf(int a, int b)
    {
        int Rank(int c) => c switch { CatStructural => 3, CatBlock => 2, CatInconclusive => 1, _ => 0 };
        return Rank(b) > Rank(a) ? b : a;
    }

    private static Option<string[]> MultiOpt(string name, string description) =>
        new(name) { Description = description, AllowMultipleArgumentsPerToken = true };
}
