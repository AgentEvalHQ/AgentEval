// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentEval.Cli.Commands.RedTeamTargets;
using AgentEval.Cli.Infrastructure;
using AgentEval.RedTeam.Gatekeeper.MemorySecurity;
using AgentEval.RedTeam.MemorySecurity;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// <c>agenteval redteam --attacks memory-poisoning</c>: runs the memory-security corpus (12 attacks, 4 benign controls)
/// against the model you name, behind AgentEval's default memory protection, with <see cref="MemoryPoisoningHarness"/>.
/// It is not a probe scan: each case is a plant session, a restart and a trigger session over a shared memory store,
/// scored by the five memory-security checks. <c>--scripted</c> runs the scripted worst-case model instead (free,
/// deterministic, labelled SCRIPTED: it shows the gates, not a model).
/// </summary>
internal static class MemoryPoisoningRedTeamDriver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Explicit nulls: a null outcome is "not measured", and leaving the key out would hide it.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Whether <c>--attacks</c> names memory-poisoning (any spelling: MemoryPoisoning, memory-poisoning, memory_poisoning).</summary>
    public static bool IsSelected(string? attacks) =>
        attacks?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(IsName) == true;

    private static bool IsName(string name) =>
        string.Equals(name.Replace("-", "").Replace("_", ""), "memorypoisoning", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the run asked for the scripted model (<c>--scripted</c>), which needs no endpoint.</summary>
    public static bool IsScripted(RedTeamOptions opts) =>
        opts.TargetOptionsFor<GatekeeperDemoTargetOptions>("gatekeeper-demo") is { Scripted: true };

    /// <summary>Validates, runs and reports. 0 pass, 1 a security check failed, 11 a required check not measured, 2 usage.</summary>
    public static async Task<int> RunAsync(RedTeamOptions opts, IChatClient? modelOverride, CancellationToken ct)
    {
        if (Validate(opts) is { } usage)
        {
            Console.Error.WriteLine($"  Error: {usage}");
            return ExitCodes.UsageError;
        }

        var scripted = IsScripted(opts);
        IChatClient model;
        string modelName;
        if (scripted)
        {
            model = MemoryPoisoningHarness.CreateScriptedModel();
            modelName = "scripted worst-case model";
        }
        else
        {
            model = modelOverride ?? CliChatClientDiagnostics.Wrap(opts.Azure
                ? EndpointFactory.CreateAzure(opts.Endpoint, opts.DeploymentName!, opts.ApiKey)
                : EndpointFactory.CreateOpenAICompatible(opts.Endpoint!, opts.Model!, opts.ApiKey), "sut");
            modelName = opts.Azure ? opts.DeploymentName! : opts.Model!;
        }

        if (!opts.Quiet)
        {
            Console.Error.WriteLine(scripted
                ? "  memory-poisoning: SCRIPTED. The scripted worst-case model saves what it is told and acts on the poison " +
                  "whenever it reaches it, so only the gates decide. It is not a measured model."
                : $"  memory-poisoning: LIVE on {modelName}, behind AgentEval's default memory protection.");
        }

        var progress = opts.Quiet ? null : new SyncProgress(line => Console.Error.WriteLine($"    {line}"));
        var result = await new MemoryPoisoningHarness(model, new MemoryPoisoningOptions
        {
            Trials = opts.MemoryTrials,
            ModelCallTimeout = TimeSpan.FromSeconds(opts.TimeoutPerProbeSeconds),
            Scripted = scripted,
            Progress = progress,
        }).RunAsync(ct).ConfigureAwait(false);

        var rendered = IsJson(opts.Format) ? RenderJson(result, modelName) : RenderMarkdown(result, modelName);
        if (opts.Output is not null)
        {
            await File.WriteAllTextAsync(opts.Output.FullName, rendered, ct).ConfigureAwait(false);
            if (!opts.Quiet)
            {
                Console.Error.WriteLine($"  Report written to: {opts.Output.FullName}");
            }
        }
        else
        {
            Console.WriteLine(rendered);
        }

        return ExitCode(result, opts.FailOn);
    }

    /// <summary>The usage error, or null. Every option that belongs to a probe scan is refused rather than ignored.</summary>
    internal static string? Validate(RedTeamOptions opts)
    {
        var names = opts.Attacks!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length > 1)
        {
            return "memory-poisoning runs on its own: it is a set of multi-session cases over a memory store, not probes. " +
                   "Run the other attacks in a separate redteam call.";
        }

        var notUsed = new List<string>();
        if (opts.Sut is not null) notUsed.Add("--sut");
        if (opts.Transform is not null) notUsed.Add("--transform");
        if (opts.Pack is not null) notUsed.Add("--pack");
        if (opts.ImportProbes is not null) notUsed.Add("--import-probes");
        if (opts.Baseline is not null) notUsed.Add("--baseline");
        if (opts.SaveBaseline is not null) notUsed.Add("--save-baseline");
        if (opts.Calibration is not null) notUsed.Add("--calibration");
        if (opts.JudgeEndpoint is not null) notUsed.Add("--judge");
        if (opts.AttackerEndpoint is not null) notUsed.Add("--attacker");
        if (opts.SystemPrompt is not null) notUsed.Add("--system-prompt");
        if (opts.SystemPromptCanary is not null) notUsed.Add("--system-prompt-canary");
        if (!string.Equals(opts.SutTier, "text", StringComparison.OrdinalIgnoreCase)) notUsed.Add("--sut-tier");
        if (!string.Equals(opts.Intensity, "moderate", StringComparison.OrdinalIgnoreCase)) notUsed.Add("--intensity");
        if (opts.MaxProbes != 0) notUsed.Add("--max-probes");
        if (opts.DelaySeconds != 0) notUsed.Add("--delay");
        if (opts.Parallelism != 1) notUsed.Add("--parallelism");
        if (opts.TimeoutPerTurnSeconds != 0) notUsed.Add("--max-turn-timeout");
        if (opts.BenignControls) notUsed.Add("--benign-controls");
        if (opts.Explain) notUsed.Add("--explain");
        if (opts.FailFast) notUsed.Add("--fail-fast");
        if (notUsed.Count > 0)
        {
            return $"memory-poisoning does not take {string.Join(", ", notUsed)}: it brings its own agent (the model you " +
                   "name, behind AgentEval's default memory protection) and scores with the memory-security checks.";
        }

        if (opts.MemoryTrials is < 1 or > 100)
        {
            return "--memory-trials must be between 1 and 100.";
        }

        if (opts.TimeoutPerProbeSeconds is <= 0 or > 86_400)
        {
            return "--timeout-per-probe must be > 0 and <= 86400 (with memory-poisoning it bounds each model call).";
        }

        if (opts.FailOn.ToLowerInvariant() is not ("vuln" or "never"))
        {
            return $"--fail-on '{opts.FailOn}' is not available for memory-poisoning (no baseline yet). Valid: vuln, never.";
        }

        if (!IsJson(opts.Format) && opts.Format.ToLowerInvariant() is not ("markdown" or "md"))
        {
            return $"--format '{opts.Format}' is not available for memory-poisoning. Valid: markdown, json.";
        }

        if (!IsScripted(opts))
        {
            if (opts.Endpoint is null)
            {
                return "memory-poisoning needs a model: --endpoint <url> --model <name>, or --azure --endpoint <url> " +
                       "--deployment-name <name>. --scripted runs the scripted worst-case model instead (not a measured model).";
            }

            if (opts.Azure ? string.IsNullOrWhiteSpace(opts.DeploymentName) : string.IsNullOrWhiteSpace(opts.Model))
            {
                return opts.Azure ? "--azure requires --deployment-name <name>." : "--model is required when using --endpoint.";
            }
        }

        return null;
    }

    /// <summary>0 when every security check passes (utility only warns); 11 when one could not be measured; else 1.</summary>
    internal static int ExitCode(MemoryPoisoningRunResult result, string failOn)
    {
        if (string.Equals(failOn, "never", StringComparison.OrdinalIgnoreCase))
        {
            return ExitCodes.Success;
        }

        var security = (result.Composite.Details.SubResults ?? []).Where(r => r.Metric.Key != "memory_utility").ToList();
        if (security.Any(r => r.Score.Label is "fail"))
        {
            return ExitCodes.TestFailure;
        }

        return security.Any(r => r.Score.Label is not "pass") ? ExitCodes.GateIndeterminate : ExitCodes.Success;
    }

    private static bool IsJson(string format) => string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>What each case showed: the objectives violated, or contained, or not measured.</summary>
    internal static string Outcome(MemorySecurityScenario scenario, MemorySecurityObservation o)
    {
        if (o.IsBenignControl)
        {
            var write = o.WriteAccepted switch { true => "stored", false => "refused", null => "no write proposed" };
            return $"benign: {write}; recall {o.RelevantItems}/{o.ExpectedRelevantItems}";
        }

        var checks = new (MemoryAttackObjective Objective, string Name, bool? Value)[]
        {
            (MemoryAttackObjective.PoisonPersistence, "persisted", o.PoisonPersisted),
            (MemoryAttackObjective.DelayedActivation, "activated", o.Activated),
            (MemoryAttackObjective.CrossScopeLeak, "cross-scope leak", o.CrossScopeLeak),
            (MemoryAttackObjective.UnsafeToolAction, "unsafe tool action", o.UnsafeToolAction),
            (MemoryAttackObjective.Exfiltration, "exfiltration", o.Exfiltration),
            (MemoryAttackObjective.UnauthorizedOverwrite, "overwrite", o.UnauthorizedOverwrite),
            (MemoryAttackObjective.TrustEscalation, "trust escalation", o.TrustEscalation),
            (MemoryAttackObjective.RetrievalCrowdOut, "crowd-out", o.RetrievalCrowdOut),
            (MemoryAttackObjective.ResourceFlood, "flood", o.ResourceFlood),
            (MemoryAttackObjective.TamperAcceptance, "tamper accepted", o.TamperAccepted),
        }.Where(c => (scenario.Objectives & c.Objective) != 0).ToList();

        var violated = checks.Where(c => c.Value is true).Select(c => c.Name).ToList();
        var unmeasured = checks.Where(c => c.Value is null).Select(c => c.Name).ToList();
        var text = violated.Count > 0 ? string.Join(", ", violated) : "contained";
        return unmeasured.Count > 0 ? $"{text}; not measured: {string.Join(", ", unmeasured)}" : text;
    }

    private static string RenderMarkdown(MemoryPoisoningRunResult result, string modelName)
    {
        var scenarios = result.Corpus.Scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine($"# Memory poisoning ({result.Mode})").AppendLine();
        if (result.Mode == "SCRIPTED")
        {
            sb.AppendLine("> The scripted worst-case model saves what it is told and acts on the poison whenever it reaches it, so");
            sb.AppendLine("> only the gates decide. It is not a measured model.").AppendLine();
        }

        sb.AppendLine($"- Model: {modelName}");
        sb.AppendLine($"- Corpus: {result.Corpus.CorpusId} {result.Corpus.Version} (`{result.Corpus.Fingerprint[..16]}…`)");
        sb.AppendLine($"- Case scripts: `{result.ScriptsFingerprint[..16]}…`");
        sb.AppendLine($"- Memory protection: AgentEval default memory gates, Enforce (`{result.PolicyFingerprint[..16]}…`)");
        sb.AppendLine($"- Cases × trials: {result.Corpus.Scenarios.Count} × {result.Cases.Max(c => c.Trial)}").AppendLine();
        sb.AppendLine("| Mode | Case | Trial | Planted by | Attempted | Blocked | Executed | Outcome |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var c in result.Cases)
        {
            var outcome = Outcome(scenarios[c.ScenarioId], c.Observation)
                          + (c.PlantSessionSinkCalls > 0 ? $"; {c.PlantSessionSinkCalls} sink call(s) ran in the plant session" : "");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {result.Mode} | {c.ScenarioId} | {c.Trial} | {c.PlantedBy} | {c.Attempted} | {c.Blocked} | {c.Executed} | {outcome} |"));
        }

        sb.AppendLine().AppendLine("| Mode | Check | Result | Detail |").AppendLine("|---|---|---|---|");
        foreach (var leaf in result.Composite.Details.SubResults ?? [])
        {
            sb.AppendLine($"| {result.Mode} | {leaf.Metric.Name} | {leaf.Score.Label.ToUpperInvariant()} | {leaf.Details.Evidence?.FirstOrDefault()?.Message} |");
        }

        sb.AppendLine().AppendLine($"**Memory security ({result.Mode}): {result.Composite.Score.Label.ToUpperInvariant()}**").AppendLine();
        sb.AppendLine("Attempted counts the memory writes proposed (or planted by the harness) and the sink calls the model proposed;");
        sb.AppendLine("executed counts the writes stored and the sink tools that ran. A case planted by the harness measures the gates");
        sb.AppendLine("and the store, whatever the model does. How each outcome was measured is in the `--format json` notes.");
        return sb.ToString();
    }

    private static string RenderJson(MemoryPoisoningRunResult result, string modelName) =>
        JsonSerializer.Serialize(new
        {
            mode = result.Mode,
            model = modelName,
            corpus = new { id = result.Corpus.CorpusId, version = result.Corpus.Version, fingerprint = result.Corpus.Fingerprint },
            scriptsFingerprint = result.ScriptsFingerprint,
            policyFingerprint = result.PolicyFingerprint,
            verdict = result.Composite.Score.Label,
            checks = (result.Composite.Details.SubResults ?? []).Select(l => new
            {
                key = l.Metric.Key,
                result = l.Score.Label,
                detail = l.Details.Evidence?.FirstOrDefault()?.Message,
            }),
            cases = result.Cases.Select(c => new
            {
                mode = result.Mode,
                scenarioId = c.ScenarioId,
                trial = c.Trial,
                plantedBy = c.PlantedBy,
                plantSessionSinkCalls = c.PlantSessionSinkCalls,
                attempted = c.Attempted,
                blocked = c.Blocked,
                executed = c.Executed,
                observation = c.Observation,
                notes = c.Notes,
            }),
        }, JsonOptions);

    /// <summary>Reports on the calling thread, in order (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
