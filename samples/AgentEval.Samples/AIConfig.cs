// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure;

namespace AgentEval.Samples;

/// <summary>
/// Configuration for Azure OpenAI.
/// Set environment variables AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY.
/// </summary>
public static class AIConfig
{
    private static readonly Lazy<(Uri Endpoint, AzureKeyCredential KeyCredential)?> s_values =
        new(() =>
        {
            var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
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
    
    public static string ModelDeployment => "gpt-5-mini";
    
    /// <summary>
    /// Secondary model deployment name for comparison testing.
    /// </summary>
    public static string SecondaryModelDeployment => "gpt-4o";
    
    /// <summary>
    /// Tertiary model deployment name for comparison testing.
    /// </summary>
    public static string TertiaryModelDeployment => "gpt-4.1";

    public static void PrintMissingCredentialsWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  Azure OpenAI credentials not configured                  ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║  Set these environment variables:                            ║");
        Console.WriteLine("║    AZURE_OPENAI_ENDPOINT = https://your-resource.openai...   ║");
        Console.WriteLine("║    AZURE_OPENAI_API_KEY  = your-api-key                      ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║  Some samples will run in mock mode without real AI.         ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }
}
