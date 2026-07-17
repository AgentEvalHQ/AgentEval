// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Builds an <see cref="IEvaluableAgent"/> from <c>AZURE_OPENAI_*</c> env vars by wrapping an
/// Azure OpenAI <c>ChatClient</c> in <see cref="ChatClientAgentAdapter"/>. Used by the CLI
/// stub-only commands (<c>bench owasp</c> / <c>bench mitre</c> / <c>bench perf</c>) when the
/// caller passes <c>--azure-from-env</c>.
/// </summary>
/// <remarks>
/// <para>
/// Same env-var convention as <see cref="JudgeFactory"/>: <c>AZURE_OPENAI_ENDPOINT</c>,
/// <c>AZURE_OPENAI_API_KEY</c>, <c>AZURE_OPENAI_DEPLOYMENT</c>. All three are required;
/// partial config is treated as a misconfiguration and surfaces a friendly error rather
/// than silently falling back to the stub.
/// </para>
/// <para>
/// This is the v1.1 honest fix for the "CLI scans stub agents" gap (plan-13 T0.2). A
/// richer agent-manifest schema (multi-provider, tool-using, custom-shape) is deferred to
/// a dedicated ADR (T3.11 in plan-13 / future Tier 3 work).
/// </para>
/// </remarks>
internal static class AzureChatAgentFactory
{
    /// <summary>
    /// Attempts to build an <see cref="IEvaluableAgent"/> from the Azure OpenAI env vars.
    /// </summary>
    /// <param name="subject">The subject identifier; passed as the agent's <c>Name</c>.</param>
    /// <param name="systemPrompt">
    /// Optional system prompt to seed every conversation. When null, the agent passes
    /// only the user prompt — useful for red-team scans where the target's posture is
    /// already encoded in the deployment.
    /// </param>
    /// <returns>
    /// A tuple of <c>(agent, exitCode)</c>. When successful, <c>agent</c> is non-null and
    /// <c>exitCode</c> is 0. On any failure (missing env vars, construction failure) the
    /// method writes a friendly error to <c>stderr</c>, returns a null agent, and sets
    /// <c>exitCode</c> to 2.
    /// </returns>
    public static (IEvaluableAgent? Agent, int ExitCode) TryBuildFromEnv(
        string subject,
        string? systemPrompt = null)
    {
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey     = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        var allConfigured =
               !string.IsNullOrWhiteSpace(endpoint)
            && !string.IsNullOrWhiteSpace(apiKey)
            && !string.IsNullOrWhiteSpace(deployment);

        if (!allConfigured)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(endpoint))   missing.Add("AZURE_OPENAI_ENDPOINT");
            if (string.IsNullOrWhiteSpace(apiKey))     missing.Add("AZURE_OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(deployment)) missing.Add("AZURE_OPENAI_DEPLOYMENT");
            Console.Error.WriteLine(
                "✖ --azure-from-env was passed but the following env vars are not set: " +
                string.Join(", ", missing) +
                "\n  Set all three and retry, or drop --azure-from-env to use the built-in stub agent.");
            return (null, 2);
        }

        try
        {
            var azureClient = new AzureOpenAIClient(new Uri(endpoint!), new AzureKeyCredential(apiKey!), BuildClientOptions());
            IChatClient chatClient = VerboseLog.Wrap(azureClient.GetChatClient(deployment!).AsIChatClient(), "azure-env");
            IEvaluableAgent agent = new ChatClientAgentAdapter(
                chatClient,
                name: subject,
                systemPrompt: systemPrompt);
            Console.Error.WriteLine(
                $"✔ Azure OpenAI agent configured — endpoint={endpoint}, deployment={deployment}, subject={subject}");
            return (agent, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"✖ Failed to construct Azure OpenAI agent: {ex.Message}\n" +
                "  Check AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT values.");
            return (null, 2);
        }
    }

    /// <summary>
    /// Attempts to build a raw <see cref="IChatClient"/> from Azure OpenAI env vars. Used
    /// by CLI commands like <c>bench longmemeval</c> and <c>bench memory</c> that need
    /// a chat client directly (e.g., to feed both the agent-under-test and the judge),
    /// rather than the pre-wrapped <see cref="IEvaluableAgent"/> from
    /// <see cref="TryBuildFromEnv(string, string?)"/>. Same env-var convention.
    /// </summary>
    public static (IChatClient? ChatClient, string? Deployment, int ExitCode) TryBuildChatClientFromEnv()
    {
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey     = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(endpoint))   missing.Add("AZURE_OPENAI_ENDPOINT");
            if (string.IsNullOrWhiteSpace(apiKey))     missing.Add("AZURE_OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(deployment)) missing.Add("AZURE_OPENAI_DEPLOYMENT");
            Console.Error.WriteLine(
                "✖ Azure OpenAI not configured. Missing: " + string.Join(", ", missing) +
                "\n  This benchmark requires a real LLM and cannot fall back to a stub.");
            return (null, null, 2);
        }

        try
        {
            var azureClient = new AzureOpenAIClient(new Uri(endpoint!), new AzureKeyCredential(apiKey!), BuildClientOptions());
            IChatClient chatClient = VerboseLog.Wrap(azureClient.GetChatClient(deployment!).AsIChatClient(), "azure-env");
            return (chatClient, deployment, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✖ Failed to construct Azure OpenAI chat client: {ex.Message}");
            return (null, null, 2);
        }
    }

    /// <summary>
    /// Builds Azure OpenAI client options with a generous network timeout. The default
    /// per-attempt NetworkTimeout (100s) can fire when the agent under test is a slow real
    /// agent fronted by an adapter — a deliberate turn plus a model cooldown can exceed it,
    /// and the resulting "operation was canceled" aborts the whole red-team scan. Allowing
    /// more time per attempt keeps a single slow probe from failing the entire benchmark.
    /// Override with <c>AGENTEVAL_AGENT_NETWORK_TIMEOUT_S</c>.
    /// </summary>
    private static AzureOpenAIClientOptions BuildClientOptions()
    {
        var raw = Environment.GetEnvironmentVariable("AGENTEVAL_AGENT_NETWORK_TIMEOUT_S");
        var seconds = double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : 180.0;
        return new AzureOpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(seconds) };
    }

    /// <summary>
    /// Prints a prominent banner warning the operator that the built-in stub agent is in use
    /// — call from any CLI command that defaults to a stub agent when neither <c>--azure-from-env</c>
    /// nor an explicit override was provided. The banner explains how to scan a real agent so
    /// operators don't unwittingly publish a "PASS" verdict that only reflects the stub's
    /// hardcoded refusal posture.
    /// </summary>
    /// <param name="benchmarkName">Friendly benchmark name (e.g., "OWASP LLM Top 10").</param>
    /// <param name="stubAgentDescription">Short label for the stub (e.g., "SafeRefusalAgent stub").</param>
    /// <param name="sampleFileName">
    /// Name of the canonical sample file demonstrating real-agent wiring for this benchmark
    /// (e.g., "06_OwaspBenchmark.cs"). Surfaces the right file to read for the operator's
    /// specific command, not always OWASP.
    /// </param>
    public static void PrintStubAgentWarning(string benchmarkName, string stubAgentDescription, string sampleFileName = "06_OwaspBenchmark.cs")
    {
        const int innerWidth = 77; // total line width minus the two "│" borders + spacing
        var prevColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine();
            Console.Error.WriteLine("┌" + new string('─', innerWidth) + "┐");
            WriteLine($"⚠  {benchmarkName} is scanning the built-in {stubAgentDescription}.", innerWidth);
            WriteLine("   This is a smoke-test stub, NOT your agent. Results will not reflect your", innerWidth);
            WriteLine("   agent's real behaviour.", innerWidth);
            WriteLine("", innerWidth);
            WriteLine("   To scan a real Azure OpenAI agent: pass --azure-from-env and set", innerWidth);
            WriteLine("     AZURE_OPENAI_ENDPOINT / AZURE_OPENAI_API_KEY / AZURE_OPENAI_DEPLOYMENT", innerWidth);
            WriteLine("   To scan any other agent: write a small program — see", innerWidth);
            WriteLine($"     samples/AgentEval.Samples/Benchmarks/{sampleFileName}", innerWidth);
            Console.Error.WriteLine("└" + new string('─', innerWidth) + "┘");
            Console.Error.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = prevColor;
        }

        // Per-line writer that handles overflow gracefully — if the text is longer than the
        // inner width minus borders, it's truncated with an ellipsis so the box stays aligned
        // regardless of benchmark-name length.
        static void WriteLine(string text, int width)
        {
            const int padding = 1; // single space inside each border
            var maxText = width - 2 * padding;
            string content = text.Length <= maxText
                ? text.PadRight(maxText)
                : text.Substring(0, maxText - 1) + "…";
            Console.Error.WriteLine("│" + new string(' ', padding) + content + new string(' ', padding) + "│");
        }
    }
}
