// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Azure;

namespace AgentEval.TravelDemo;

/// <summary>
/// Azure OpenAI configuration sourced from environment variables.
/// </summary>
/// <remarks>
/// <para>Required environment variables:</para>
/// <list type="bullet">
///   <item>AZURE_OPENAI_ENDPOINT</item>
///   <item>AZURE_OPENAI_API_KEY</item>
/// </list>
/// <para>Optional environment variables:</para>
/// <list type="bullet">
///   <item>AZURE_OPENAI_DEPLOYMENT — overrides <see cref="PreferredDeployment"/> (default: <c>gpt-5-mini</c>).</item>
/// </list>
/// </remarks>
public static class Config
{
    /// <summary>
    /// Recommended deployment for TravelDemo + TravelDemo.Evals — picked for
    /// a balance of fast per-turn latency and reliable function-calling on
    /// Azure AI Foundry. Used when <c>AZURE_OPENAI_DEPLOYMENT</c> is unset.
    /// </summary>
    /// <remarks>
    /// Empirical baseline on the <c>msfoundryjose</c> Foundry resource:
    /// <list type="bullet">
    ///   <item><c>gpt-5-chat</c>: ~3 min for the full 18-tool TravelAgent flow (reasoning-heavy, slow per turn).</item>
    ///   <item><c>gpt-5-mini</c>: ~30–60 s for the same flow — same reasoning family, faster turns. RECOMMENDED.</item>
    ///   <item><c>gpt-4o</c> / <c>gpt-4o-mini</c>: ~15–30 s — battle-tested function-calling, fastest overall.</item>
    /// </list>
    /// Override with <c>AZURE_OPENAI_DEPLOYMENT</c> to pin a specific deployment.
    /// </remarks>
    public const string PreferredDeployment = "gpt-5-mini";

    /// <summary>
    /// Programmatic model override. When non-null, takes precedence over BOTH
    /// the <c>AZURE_OPENAI_DEPLOYMENT</c> env var AND <see cref="PreferredDeployment"/>.
    /// Set at the top of <c>Program.cs</c> (or before a demo's <c>RunAsync</c>)
    /// to pin a specific deployment for an entire process without juggling
    /// shell env vars across runs.
    /// </summary>
    /// <example>
    /// <code>
    /// // Force gpt-4o-mini for the whole demo session:
    /// Config.ModelOverride = "gpt-4o-mini";
    /// await Demo01_TravelAgent.RunAsync();
    /// </code>
    /// </example>
    public static string? ModelOverride { get; set; }

    /// <summary>
    /// Convenience setter for <see cref="ModelOverride"/>. Returns the previous
    /// override value so callers can restore it (useful inside a <c>try/finally</c>
    /// when only one demo should use a different model).
    /// </summary>
    public static string? UseModel(string? deployment)
    {
        var prev = ModelOverride;
        ModelOverride = deployment;
        return prev;
    }

    private static readonly Lazy<(Uri Endpoint, AzureKeyCredential Key)?> _credentials = new(() =>
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var key      = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        // AZURE_OPENAI_DEPLOYMENT is optional now — `Model` falls back to
        // `PreferredDeployment` if it's unset. Endpoint + key remain required.

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(key))
            return null;

        return (new Uri(endpoint), new AzureKeyCredential(key));
    });

    /// <summary>True when endpoint and key are both set. Deployment is optional and falls back to <see cref="PreferredDeployment"/>.</summary>
    public static bool IsConfigured => _credentials.Value.HasValue;

    /// <summary>Azure OpenAI endpoint URI.</summary>
    public static Uri Endpoint => _credentials.Value?.Endpoint
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");

    /// <summary>Azure OpenAI API key credential.</summary>
    public static AzureKeyCredential KeyCredential => _credentials.Value?.Key
        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is not set.");

    /// <summary>
    /// Resolved model deployment name. Resolution order (first non-empty wins):
    /// <list type="number">
    ///   <item><see cref="ModelOverride"/> — programmatic override.</item>
    ///   <item><c>AZURE_OPENAI_DEPLOYMENT</c> env var.</item>
    ///   <item><see cref="PreferredDeployment"/> — built-in default (<c>gpt-5-mini</c>).</item>
    /// </list>
    /// </summary>
    public static string Model =>
        !string.IsNullOrEmpty(ModelOverride)
            ? ModelOverride
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") is { Length: > 0 } env
                ? env
                : PreferredDeployment;

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
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var key      = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var envDep   = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var resolved = Model;

        string source;
        if (!string.IsNullOrEmpty(ModelOverride))         source = "Config.ModelOverride (code)";
        else if (!string.IsNullOrEmpty(envDep))           source = "AZURE_OPENAI_DEPLOYMENT (env)";
        else                                              source = $"default ({PreferredDeployment})";

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Azure target ────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  Endpoint   : {endpoint ?? "(unset)"}");
        Console.WriteLine($"  Model      : {resolved}  [source: {source}]");
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
