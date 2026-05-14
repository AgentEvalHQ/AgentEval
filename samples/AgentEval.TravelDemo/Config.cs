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

    /// <summary>
    /// Prints a "Azure target" header block to the console showing the
    /// endpoint, deployment, and a redacted key fingerprint. Useful at the
    /// top of demos and evals so the operator sees at a glance which model
    /// (and which Foundry / OpenAI resource) is about to be charged.
    /// </summary>
    /// <remarks>
    /// The key is never printed in full — only its length and a
    /// first-4-…-last-4 fingerprint. That's enough to confirm "yes, this
    /// is the key I expected" without leaking the secret to a screen
    /// recording, a posted screenshot, or a shoulder-surfer.
    /// </remarks>
    public static void PrintAzureTarget()
    {
        var endpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var key        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Azure target ────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  Endpoint   : {endpoint ?? "(unset)"}");
        Console.WriteLine($"  Deployment : {deployment ?? "(unset — falls back to 'gpt-4o')"}");
        Console.WriteLine($"  Model      : {Model}");
        Console.WriteLine($"  API key    : {FingerprintKey(key)}");
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    private static string FingerprintKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(unset)";
        if (key.Length <= 8)           return $"(set, {key.Length} chars — too short to fingerprint)";
        return $"{key[..4]}…{key[^4..]} ({key.Length} chars)";
    }
}
