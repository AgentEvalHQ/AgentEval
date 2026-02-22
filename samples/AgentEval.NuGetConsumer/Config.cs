// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Configuration for Azure OpenAI.
/// Set environment variables AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT.
/// </summary>
public static class Config
{
    private static readonly Lazy<(Uri Endpoint, AzureKeyCredential Key)?> _credentials = new(() =>
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        // Require all three: endpoint, key, AND deployment
        if (string.IsNullOrWhiteSpace(endpoint) || 
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(deployment))
            return null;

        return (new Uri(endpoint), new AzureKeyCredential(key));
    });

    /// <summary>
    /// True if Azure OpenAI credentials are configured (endpoint, key, AND deployment).
    /// </summary>
    public static bool IsConfigured => _credentials.Value.HasValue;
    
    /// <summary>Azure OpenAI endpoint URI.</summary>
    public static Uri Endpoint => _credentials.Value?.Endpoint 
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not configured");
    
    /// <summary>Azure OpenAI API key credential.</summary>
    public static AzureKeyCredential KeyCredential => _credentials.Value?.Key 
        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not configured");
    
    /// <summary>Primary model deployment name.</summary>
    public static string Model => 
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
    
    /// <summary>Secondary model for comparison testing.</summary>
    public static string SecondaryModel => 
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_2") ?? "gpt-4o-mini";
}
