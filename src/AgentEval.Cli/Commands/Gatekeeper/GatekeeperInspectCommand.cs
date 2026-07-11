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
        var model = new ModelOptions();
        var modelReplyOpt = new Option<string?>("--model-reply") { Description = "Judge gates: a file with the model's reply — evaluate without a model call" };
        var attestOpt = new Option<string?>("--attest-fingerprint") { Description = "With --model-reply: operator-vouched model fingerprint for the certificate lookup" };
        var allowUncalibratedOpt = new Option<bool>("--allow-uncalibrated") { Description = "Judge gates: run advisory-only if not certified inline-ready (stamps inlineReady:false)" };

        var cmd = new Command("inspect", "Run a Gatekeeper gate over a JSON payload and emit a verdict.");
        model.AddTo(cmd);
        foreach (var o in new Option[] { gateOpt, inputOpt, policyOpt, keywordOpt, keywordsFileOpt, forbiddenOpt, patternOpt,
            allowedDomainOpt, idArgOpt, guardedToolOpt, trustedToolOpt, sourceToolOpt, sinkToolOpt, minTaintOpt,
            modelReplyOpt, attestOpt, allowUncalibratedOpt })
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

            var (azure, endpoint, deployment, modelName, apiKey) = model.Read(parse);
            var judgeArgs = new JudgeArgs(azure, endpoint, deployment, modelName, apiKey,
                parse.GetValue(modelReplyOpt), parse.GetValue(attestOpt), parse.GetValue(allowUncalibratedOpt), CertDir: null);

            return await RunAsync(parse.GetValue(gateOpt)!, parse.GetValue(inputOpt), parse.GetValue(policyOpt) ?? "block",
                flags, Console.In, Console.Out, Console.Error, ct, judgeArgs);
        });

        return cmd;
    }

    /// <summary>Testable core: resolve the gate, read the payload(s), run, emit verdict(s), return the exit code.</summary>
    public static async Task<int> RunAsync(
        string gateId, string? inputFile, string policy, GateFlags flags,
        TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct, JudgeArgs? judgeArgs = null)
    {
        var warn = string.Equals(policy, "warn", StringComparison.OrdinalIgnoreCase);
        if (!warn && !string.Equals(policy, "block", StringComparison.OrdinalIgnoreCase))
        {
            stderr.WriteLine($"  Error: --policy must be 'block' or 'warn' (got '{policy}').");
            return ExitCodes.UsageError;
        }

        if (gateId.StartsWith("judge:", StringComparison.Ordinal))
        {
            return await RunJudgeSingleAsync(gateId, inputFile, warn, judgeArgs ?? JudgeArgs.Empty, stdin, stdout, stderr, ct);
        }

        if (gateId.StartsWith("panel:", StringComparison.Ordinal))
        {
            stderr.WriteLine("  Error: 'panel:' gates arrive in the next build slice (the judge fan-out).");
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

    // Judge gates (single stdin payload). --model runs the judge; --model-reply evaluates a caller-supplied reply.
    // The honesty guard: a judge is inlineReady only when a matching certificate says so, else it refuses (exit 7)
    // unless --allow-uncalibrated. --model-reply can never claim inlineReady:true without an explicit operator
    // attestation (--attest-fingerprint), because the reply's model provenance is unknown.
    private static async Task<int> RunJudgeSingleAsync(
        string gateId, string? inputFile, bool warn, JudgeArgs args,
        TextReader stdin, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var axis = gateId["judge:".Length..];
        var entry = JudgeAxisRegistry.For(axis);
        if (entry is null)
        {
            stderr.WriteLine($"  Error: unknown judge axis '{axis}'. Run 'agenteval gatekeeper list-gates'.");
            return ExitCodes.UsageError;
        }

        if (inputFile is not null)
        {
            stderr.WriteLine("  Error: judge gates in --input batch are not supported in this build (single stdin payload only).");
            return ExitCodes.UsageError;
        }

        var json = await stdin.ReadToEndAsync(ct).ConfigureAwait(false);
        var payload = InspectPayload.Parse(json, out var perr);
        if (payload?.Text is null)
        {
            stderr.WriteLine($"  Error: judge gate requires a payload with a 'text' field{(perr is null ? "" : $" ({perr})")}.");
            return ExitCodes.UsageError;
        }

        // ── --model-reply: evaluate the caller's reply, no model call (provenance rule applies) ──
        if (args.ModelReply is not null)
        {
            if (args.ModelReply == "-")
            {
                stderr.WriteLine("  Error: --model-reply cannot be '-' (stdin carries the payload); pass a file path.");
                return ExitCodes.UsageError;
            }

            string reply;
            try { reply = await File.ReadAllTextAsync(args.ModelReply, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine($"  Error reading --model-reply: {ex.Message}");
                return ExitCodes.UsageError;
            }

            var v = ParseOnlyJudge.Evaluate(entry.Rubric(), payload.Text, reply);
            var d = GateVerdictDto.FromChat(v, gateId);

            if (args.AttestFingerprint is not null)
            {
                var cert = InlineReadinessStore.TryLoad(axis, args.AttestFingerprint, args.CertDir);
                if (cert?.IsInlineReady == true)
                {
                    d = d with { InlineReady = true, Certificate = Provenance(cert), Warning = "inline readiness is operator-attested (--attest-fingerprint), not observed by the CLI" };
                }
                else if (args.AllowUncalibrated)
                {
                    d = d with { InlineReady = false, Warning = "uncalibrated — advisory only" };
                }
                else
                {
                    stderr.WriteLine(NotCertifiedMsg(axis, args.AttestFingerprint));
                    return ExitCodes.NotCertified;
                }
            }
            else
            {
                d = d with { InlineReady = false, Warning = "model-reply: unknown model provenance — advisory only, cannot certify inline readiness" };
            }

            stdout.WriteLine(d.ToJson(indented: true));
            return warn ? ExitCodes.Success : ExitOf(d);
        }

        // ── --model: the CLI makes the judge call ──
        var mr = GatekeeperModelResolver.Resolve(args.Azure, args.Endpoint, args.Deployment, args.Model, args.ApiKey, stderr);
        if (mr.Client is null)
        {
            return mr.ExitCode;
        }

        var stored = InlineReadinessStore.TryLoad(axis, mr.Fingerprint!, args.CertDir);
        bool inlineReady;
        object? certObj = null;
        string? warning = null;
        if (stored?.IsInlineReady == true)
        {
            inlineReady = true;
            certObj = Provenance(stored);
        }
        else if (args.AllowUncalibrated)
        {
            inlineReady = false;
            warning = "uncalibrated — advisory only";
        }
        else
        {
            stderr.WriteLine(NotCertifiedMsg(axis, mr.Fingerprint!));
            return ExitCodes.NotCertified;   // 7 — the honesty guard, distinct from a policy Block (5) or bad flags (2)
        }

        GateVerdict jv;
        try
        {
            jv = await entry.Create(mr.Client, true).InspectAsync(payload.Text, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"  Error: the judge model call failed: {ex.Message}");
            return ExitCodes.RuntimeError;
        }

        var jd = GateVerdictDto.FromChat(jv, gateId) with { InlineReady = inlineReady, Certificate = certObj, Warning = warning };
        stdout.WriteLine(jd.ToJson(indented: true));
        return warn ? ExitCodes.Success : ExitOf(jd);
    }

    private static CertProvenance Provenance(CalibrationCertificate c) =>
        new(c.ModelFingerprint, c.GoldSetHash, c.IsInlineReady, c.CertifiedAtUtc);

    private static string NotCertifiedMsg(string axis, string fingerprint) =>
        $"  Error: axis '{axis}' is not certified inline-ready for model '{fingerprint}'. " +
        $"Run: agenteval gatekeeper calibrate --gate judge:{axis} --model … --certify, or pass --allow-uncalibrated for advisory-only.";

    private static Option<string[]> MultiOpt(string name, string description) =>
        new(name) { Description = description, AllowMultipleArgumentsPerToken = true };
}

/// <summary>The model-source + honesty-guard flags for judge <c>inspect</c>.</summary>
internal sealed record JudgeArgs(
    bool Azure, string? Endpoint, string? Deployment, string? Model, string? ApiKey,
    string? ModelReply, string? AttestFingerprint, bool AllowUncalibrated, string? CertDir)
{
    public static readonly JudgeArgs Empty = new(false, null, null, null, null, null, null, false, null);
}

/// <summary>Compact certificate provenance surfaced in a judge verdict's <c>certificate</c> field.</summary>
internal sealed record CertProvenance(string ModelFingerprint, string GoldSetHash, bool IsInlineReady, string CertifiedAtUtc);
