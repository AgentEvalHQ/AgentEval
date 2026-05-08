// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Azure;

namespace AgentEval.TravelDemo;

/// <summary>
/// Azure OpenAI configuration sourced from environment variables.
/// </summary>
/// <remarks>
/// Required environment variables:
/// <list type="bullet">
///   <item>AZURE_OPENAI_ENDPOINT</item>
///   <item>AZURE_OPENAI_API_KEY</item>
///   <item>AZURE_OPENAI_DEPLOYMENT</item>
/// </list>
/// </remarks>
public static class Config
{
    private static readonly Lazy<(Uri Endpoint, AzureKeyCredential Key)?> _credentials = new(() =>
    {
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var key        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        if (string.IsNullOrWhiteSpace(endpoint)   ||
            string.IsNullOrWhiteSpace(key)         ||
            string.IsNullOrWhiteSpace(deployment))
            return null;

        return (new Uri(endpoint), new AzureKeyCredential(key));
    });

    /// <summary>True when all three environment variables are present.</summary>
    public static bool IsConfigured => _credentials.Value.HasValue;

    /// <summary>Azure OpenAI endpoint URI.</summary>
    public static Uri Endpoint => _credentials.Value?.Endpoint
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");

    /// <summary>Azure OpenAI API key credential.</summary>
    public static AzureKeyCredential KeyCredential => _credentials.Value?.Key
        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is not set.");

    /// <summary>Primary model deployment name (default: gpt-4o).</summary>
    public static string Model =>
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o";
}
