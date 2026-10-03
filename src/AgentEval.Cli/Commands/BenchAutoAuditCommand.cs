// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Cli.Infrastructure;
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

        var settings = ProviderChatClientFactory.Settings;
        if (clientOverride is null && !settings.IsConfigured)
            return MockTarget.RefuseWithoutTarget(Command, RealTargets);

        var targets = (models is { Count: > 0 } ? models : new[] { settings.Model, settings.SecondaryModel, settings.TertiaryModel })
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim())
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
                    Console.Error.WriteLine($"Error: no client for {model}: {diagnostic}");
                    return ExitCodes.RuntimeError;
                }
                client = built;
            }

            var endpoint = $"{model} ({provider})";
            Console.Error.WriteLine($"  auditing {endpoint} ...");
            var (result, failure) = await AutoAuditLive.EvaluateAsync(endpoint, client, ct);
            if (failure is not null)
                Console.Error.WriteLine($"  {endpoint} did not complete: {failure.GetType().Name}: {failure.Message}");
            results.Add(result);
        }

        var report = AutoAuditRunner.Compare(results);
        await PrintAndWriteAsync(
            $"> Live run on {provider}: {targets.Count} model(s), one support task each (the Lookup tool returns a " +
            "record with a test SSN the instructions forbid repeating).", report, outPath, ct);

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
