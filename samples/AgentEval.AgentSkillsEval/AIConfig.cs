// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure;

namespace AgentEval.AgentSkillsEval;

/// <summary>
/// Azure OpenAI configuration for this sample, sourced from environment variables. Mirrors the
/// convention in <c>samples/AgentEval.Samples/AIConfig.cs</c> — set
/// <c>AZURE_OPENAI_ENDPOINT</c> / <c>AZURE_OPENAI_API_KEY</c> / <c>AZURE_OPENAI_DEPLOYMENT</c>.
/// </summary>
public static class AIConfig
{
    private static readonly Lazy<(Uri Endpoint, AzureKeyCredential KeyCredential)?> s_values =
        new(() =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
            var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

            if (string.IsNullOrWhiteSpace(endpoint) ||
                string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(deployment))
            {
                return null;
            }

            return (new Uri(endpoint), new AzureKeyCredential(key));
        }, isThreadSafe: true);

    public static bool IsConfigured => s_values.Value.HasValue;

    public static Uri Endpoint => s_values.Value?.Endpoint
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not configured");

    public static AzureKeyCredential KeyCredential => s_values.Value?.KeyCredential
        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not configured");

    public static string ModelDeployment =>
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";

    public static void PrintMissingCredentialsWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  Azure OpenAI credentials not configured                      ║");
        Console.WriteLine("║                                                                ║");
        Console.WriteLine("║  Set these environment variables:                              ║");
        Console.WriteLine("║    AZURE_OPENAI_ENDPOINT   = https://your-resource.openai...  ║");
        Console.WriteLine("║    AZURE_OPENAI_API_KEY    = your-api-key                     ║");
        Console.WriteLine("║    AZURE_OPENAI_DEPLOYMENT = your-deployment-name (e.g. gpt-4o)║");
        Console.WriteLine("║                                                                ║");
        Console.WriteLine("║  This sample runs a REAL agent — it will not fake a run.      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }
}
