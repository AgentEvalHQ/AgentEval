// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

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
            var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

            // Require all three: endpoint, key, AND deployment
            if (string.IsNullOrWhiteSpace(endpoint) || 
                string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(deployment))
            {
                return null;
            }

            return (new Uri(endpoint), new AzureKeyCredential(key));
        }, isThreadSafe: true);

    /// <summary>
    /// Returns true only if AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT are all set.
    /// </summary>
    public static bool IsConfigured => s_values.Value.HasValue;
    
    public static Uri Endpoint => s_values.Value?.Endpoint 
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not configured");
    
    public static AzureKeyCredential KeyCredential => s_values.Value?.KeyCredential 
        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not configured");
    
    /// <summary>
    /// Primary model deployment name. Reads from AZURE_OPENAI_DEPLOYMENT or defaults to "gpt-4o".
    /// </summary>
    public static string ModelDeployment => 
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
    
    /// <summary>
    /// Secondary model deployment name for comparison testing.
    /// Reads from AZURE_OPENAI_DEPLOYMENT_2 or defaults to "gpt-4o-mini".
    /// </summary>
    public static string SecondaryModelDeployment => 
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_2") ?? "gpt-4o-mini";
    
    /// <summary>
    /// Tertiary model deployment name for comparison testing.
    /// Reads from AZURE_OPENAI_DEPLOYMENT_3 or defaults to "gpt-4.1".
    /// </summary>
    public static string TertiaryModelDeployment => 
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_3") ?? "gpt-4.1";
    
    /// <summary>
    /// Embedding model deployment name for RAG and similarity metrics.
    /// Reads from AZURE_OPENAI_EMBEDDING_DEPLOYMENT or defaults to "text-embedding-ada-002".
    /// </summary>
    public static string EmbeddingDeployment => 
        Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-ada-002";
    
    /// <summary>
    /// Returns true if embedding model is configured (endpoint + key + embedding deployment).
    /// </summary>
    public static bool IsEmbeddingConfigured => 
        IsConfigured && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT"));

    public static void PrintMissingCredentialsWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  Azure OpenAI credentials not configured                  ║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║  Set these environment variables:                            ║");
        Console.WriteLine("║    AZURE_OPENAI_ENDPOINT   = https://your-resource.openai... ║");
        Console.WriteLine("║    AZURE_OPENAI_API_KEY    = your-api-key                    ║");
        Console.WriteLine("║    AZURE_OPENAI_DEPLOYMENT = your-deployment-name (e.g. gpt-4o)║");
        Console.WriteLine("║                                                              ║");
        Console.WriteLine("║  All samples will run in MOCK MODE without real AI.          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }
}
