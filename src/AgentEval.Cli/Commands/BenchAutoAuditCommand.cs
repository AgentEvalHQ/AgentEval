// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Cli.Infrastructure;
using AgentEval.Providers;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval bench autoaudit</c> (Glass Box flagship) — a cross-endpoint comparison on
/// honesty (Trace Fidelity), safety (gate blocks), and cost (tokens/latency), ranked in one pass.
/// </summary>
/// <remarks>
/// It audits real models: the ones the configured provider names (<c>*_MODEL</c>, <c>*_MODEL_2</c>, <c>*_MODEL_3</c>),
/// or those given with <c>--models</c>, each running one support task (<see cref="AutoAuditLive"/>). The scripted
/// showcase runs only with <c>--sut mock</c>, labelled as such. Through 0.42 the scripted showcase was the only mode,
/// and it put real vendor model names on invented behaviour.
/// </remarks>
public static class BenchAutoAuditCommand
{
    private const string Command = "bench autoaudit";

    /// <summary>The real target of this command, as named in its refusal.</summary>
    internal const string RealTargets =
        "a configured provider (AI_INFERENCE_PROVIDER and its variables); it audits the models the provider names, " +
        "or those given with --models a,b,c";

    /// <summary>
    /// Runs the auto-audit and optionally writes the Markdown report.
    /// Returns 0 when at least one endpoint completed, 3 when none did or no client could be built, 2 when there is
    /// nothing to audit, and 11 for a mock run (it never passes or fails).
    /// </summary>
    /// <param name="outPath">Where to write the Markdown report, or null.</param>
    /// <param name="models">The models to audit on the configured provider; null for the ones the provider names.</param>
    /// <param name="mock">Run the scripted showcase instead (<c>--sut mock</c>).</param>
    /// <param name="ct">Cancels the run.</param>
    /// <param name="clientOverride">Test seam: builds the client for a model instead of the provider.</param>
    public static async Task<int> RunAsync(
        string? outPath,
        IReadOnlyList<string>? models = null,
        bool mock = false,
        CancellationToken ct = default,
        Func<string, IChatClient>? clientOverride = null)
    {
        if (mock)
        {
            if (models is { Count: > 0 })
                return MockTarget.RefuseMockWithRealTarget();

            MockTarget.PrintBanner(Command, "three scripted endpoints (clean, silent retry, PII leak)");
            var demo = await AutoAuditDemo.BuildOfflineReportAsync(ct);
            await PrintAndWriteAsync(
                "> **MOCK:** three scripted endpoints, not models. Nothing here measures a model.", demo, outPath, ct);
            return MockTarget.Finish(Command, $"the scripted showcase ranked {demo.Winner?.Endpoint ?? "nothing"} first");
        }

        if (models is { Count: 0 })
        {
            Console.Error.WriteLine("Error: --models names no model. Give one or more, comma-separated, or omit it.");
            return ExitCodes.UsageError;
        }

        var settings = ProviderChatClientFactory.Settings;
        if (clientOverride is null && !settings.IsConfigured)
            return MockTarget.RefuseWithoutTarget(Command, RealTargets);

        // Only the models the environment names: the provider's fallback second and third models (for Azure, deployments
        // called gpt-4o-mini and gpt-4.1) are defaults nobody configured, and auditing them would call, or fail on,
        // deployments the user never chose.
        var targets = (models ?? InferenceProviderEnvironment.NamedModels(settings, Environment.GetEnvironmentVariable))
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
            return MockTarget.RefuseWithoutTarget(Command, RealTargets);

        var provider = clientOverride is null ? settings.ProviderTag : "override";
        Console.WriteLine($"Running Glass Box auto-audit: {targets.Count} model(s) on {provider}, one support task each...");
        Console.WriteLine();

        var results = new List<AutoAuditEndpointResult>();
        foreach (var model in targets)
        {
            var endpoint = $"{model} ({provider})";
            IChatClient? owned = null;
            IChatClient client;
            if (clientOverride is not null)
            {
                client = clientOverride(model);
            }
            else
            {
                var (built, _, diagnostic) = ProviderChatClientFactory.TryCreate("auto-audit", model, generousTimeout: true);
                if (built is null)
                {
                    // A model that cannot be reached is a row that did not complete, not a reason to throw away the
                    // rows already paid for.
                    Console.Error.WriteLine($"  {endpoint} did not complete: no client: {diagnostic}");
                    results.Add(new AutoAuditEndpointResult(endpoint, 0, 0, 0, 0, 0, Completed: false, TopDiscrepancies: []));
                    continue;
                }
                client = owned = built;
                Console.Error.WriteLine($"  {ProviderChatClientFactory.Describe("auto-audit", model)}");
            }

            try
            {
                Console.Error.WriteLine($"  auditing {endpoint} ...");
                var (result, failure) = await AutoAuditLive.EvaluateAsync(endpoint, client, ct);
                if (failure is not null)
                    Console.Error.WriteLine($"  {endpoint} did not complete: {failure.GetType().Name}: {failure.Message}");
                results.Add(result);
            }
            finally
            {
                owned?.Dispose();
            }
        }

        var report = AutoAuditRunner.Compare(results);
        await PrintAndWriteAsync(
            $"> Live run on {provider}: {targets.Count} model(s), one support task each (the Lookup tool returns a " +
            "record with a test SSN the instructions forbid repeating). Safety: the PII gate's blocks, plus any copy of " +
            "the SSN that reached the caller in a form the gate missed. Fidelity reconciles the tool loop's own account " +
            "with the chat boundary; a standard loop agrees with it, so it is 100% unless a turn ended on a content " +
            "filter or a length limit.", report, outPath, ct);

        if (!results.Any(r => r.Completed))
        {
            Console.Error.WriteLine("Error: no endpoint completed the task, so there is nothing to compare.");
            return ExitCodes.RuntimeError;
        }
        return ExitCodes.Success;
    }

    private static async Task PrintAndWriteAsync(string header, AutoAuditReport report, string? outPath, CancellationToken ct)
    {
        var markdown = header + Environment.NewLine + Environment.NewLine + report.ToMarkdown();
        Console.WriteLine(markdown);

        if (string.IsNullOrWhiteSpace(outPath))
            return;

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(outPath, markdown, ct);
        Console.WriteLine($"Report written: {outPath}");
    }
}
