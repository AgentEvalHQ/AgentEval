// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using System.ClientModel.Primitives;
using AgentEval.Samples.Providers;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.Samples;

/// <summary>
/// Provider configuration for the samples. Every sample obtains its model through
/// <see cref="CreateChatClient"/>, so the whole catalogue runs on whichever host
/// <c>AI_INFERENCE_PROVIDER</c> selects — no sample knows which provider it is on.
///
/// <code>
/// AI_INFERENCE_PROVIDER = bitdeer | openai | foundry | azure | openai-compatible
///
/// bitdeer             BITDEER_API_KEY                     (BITDEER_ENDPOINT, BITDEER_MODEL default to Bitdeer's GLM-5.3 Flash)
/// openai              OPENAI_API_KEY                      (OPENAI_BASE_URL, OPENAI_MODEL default to api.openai.com, gpt-4o-mini)
/// foundry             FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL   (a Foundry resource's Azure OpenAI-compatible endpoint)
/// azure               AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT
/// openai-compatible   OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL   (OPENAI_COMPATIBLE_API_KEY optional; Ollama, LM Studio, vLLM, Groq, ...)
/// </code>
///
/// When the selector is unset, the first provider with credentials wins (Azure, Bitdeer, OpenAI,
/// Foundry, generic). When it names a provider whose variables are missing, nothing is selected
/// and the banner says why — it never silently spends on a host you did not choose.
/// <c>--provider &lt;name&gt;</c> on the command line sets the selector for one run.
///
/// The resolution itself lives in Core (<see cref="InferenceProviderEnvironment"/>) so the CLI and
/// any host read the same variable the same way. Two surfaces stay Azure-only and say so:
/// embeddings (<see cref="IsEmbeddingConfigured"/>) and the hosted-agent Foundry path
/// (<see cref="IsFoundryConfigured"/>, Entra login), which is not a chat provider.
/// </summary>
/// <remarks>
/// The Azure-protocol paths use an API key for simplicity. For production, use
/// <c>ManagedIdentityCredential</c> or another specific <c>TokenCredential</c>. See MAF best practice #2.
/// </remarks>
public static class AIConfig
{
    public const string BitdeerDefaultEndpoint = InferenceProviderEnvironment.BitdeerDefaultEndpoint;
    public const string BitdeerDefaultModel = InferenceProviderEnvironment.BitdeerDefaultModel;

    private static readonly Lazy<InferenceProviderSettings> s_settings = new(InferenceProviderEnvironment.Resolve, isThreadSafe: true);

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>The resolved settings for this process.</summary>
    public static InferenceProviderSettings Settings => s_settings.Value;

    /// <summary>The provider in use for this process.</summary>
    public static InferenceProvider Provider => Settings.Provider;

    /// <summary>True when some provider is selected and credentialed. Samples that need a model check this first.</summary>
    public static bool IsConfigured => Settings.IsConfigured;

    /// <summary>A human-readable provider name for banners.</summary>
    public static string ProviderName => Settings.DisplayName;

    /// <summary>A short tag for agent identities (<c>model@provider</c>), because the host is part of what was measured.</summary>
    public static string ProviderTag => Settings.ProviderTag;

    /// <summary>
    /// True when the Azure OpenAI trio is set AND the endpoint is one a key may be sent to (https, or
    /// loopback http), whatever provider is selected — the Azure-only surfaces (embeddings) rely on it.
    /// </summary>
    public static bool IsAzureConfigured =>
        InferenceProviderEnvironment.HasCredentials(InferenceProvider.AzureOpenAI, Environment.GetEnvironmentVariable)
        && InferenceProviderEnvironment.TryValidateEndpoint(Env("AZURE_OPENAI_ENDPOINT"), out _, out _);

    /// <summary>True when <c>BITDEER_API_KEY</c> is set, whatever provider is selected.</summary>
    public static bool IsBitdeerConfigured => InferenceProviderEnvironment.HasCredentials(InferenceProvider.Bitdeer, Environment.GetEnvironmentVariable);

    // ── The one factory every sample uses ─────────────────────────────────────

    /// <summary>
    /// Builds the chat client for the selected provider. <paramref name="model"/> defaults to
    /// <see cref="ModelDeployment"/>; pass <see cref="SecondaryModelDeployment"/> or an explicit
    /// name for comparisons.
    /// </summary>
    public static IChatClient CreateChatClient(string? model = null)
    {
        var s = Settings;
        if (!s.IsConfigured)
            throw new InvalidOperationException(s.Diagnostic ?? "No chat provider is configured.");

        var resolved = model ?? s.Model!;
        // AGENTEVAL_SAMPLES_SHOW_RAW=1 prints every request and reply body on the wire, key scrubbed — the same
        // switch and the same logger the Jev transport uses, so provider evidence has one shape.
        var transport = new HttpClientPipelineTransport(ProviderConfig.CreateWireLoggedHttpClient(s.ApiKey!));
        if (s.UsesAzureProtocol)
        {
            // Azure OpenAI and a Foundry resource's OpenAI-compatible endpoint speak the same protocol.
            return new AzureOpenAIClient(s.Endpoint!, new AzureKeyCredential(s.ApiKey!), new AzureOpenAIClientOptions { Transport = transport })
                .GetChatClient(resolved)
                .AsIChatClient();
        }

        // Bitdeer, OpenAI and any OpenAI-compatible host: the same construction the CLI's
        // EndpointFactory.CreateOpenAICompatible uses for --endpoint --model --api-key.
        return new OpenAIClient(new ApiKeyCredential(s.ApiKey!), new OpenAIClientOptions { Endpoint = s.Endpoint, Transport = transport })
            .GetChatClient(resolved)
            .AsIChatClient();
    }

    /// <summary>
    /// The raw Azure OpenAI client, for the Azure-only surfaces (embeddings). Reads the
    /// <c>AZURE_OPENAI_*</c> trio directly, whichever provider is selected for chat.
    /// </summary>
    public static AzureOpenAIClient CreateAzureOpenAIClient() => new(AzureEndpoint, AzureKeyCredential);

    // ── Endpoint / model for the selected provider ────────────────────────────

    /// <summary>The selected provider's endpoint.</summary>
    public static Uri Endpoint => Settings.Endpoint ?? throw new InvalidOperationException(Settings.Diagnostic ?? "No chat provider is configured.");

    /// <summary>Primary model or deployment name for the selected provider.</summary>
    public static string ModelDeployment => Settings.Model ?? throw new InvalidOperationException(Settings.Diagnostic ?? "No chat provider is configured.");

    /// <summary>Secondary model for comparison samples (<c>*_MODEL_2</c> / <c>AZURE_OPENAI_DEPLOYMENT_2</c>); defaults to the primary except on Azure.</summary>
    public static string SecondaryModelDeployment => Settings.SecondaryModel ?? ModelDeployment;

    /// <summary>Tertiary model for comparison samples (<c>*_MODEL_3</c> / <c>AZURE_OPENAI_DEPLOYMENT_3</c>); defaults to the primary except on Azure.</summary>
    public static string TertiaryModelDeployment => Settings.TertiaryModel ?? ModelDeployment;

    /// <summary>The primary model with the provider in the name, for agent identities and provenance.</summary>
    public static string ModelIdentity => Settings.ModelIdentity;

    // ── Azure-only surfaces ───────────────────────────────────────────────────

    /// <summary>
    /// The Azure OpenAI resource endpoint, validated the same way as every provider endpoint (absolute
    /// https, or loopback http). Throws with the reason when it is unset, malformed, or would carry the
    /// key in cleartext — the Azure-only surfaces must not bypass the check the resolver applies.
    /// </summary>
    public static Uri AzureEndpoint =>
        InferenceProviderEnvironment.TryValidateEndpoint(Env("AZURE_OPENAI_ENDPOINT"), out var endpoint, out var why)
            ? endpoint!
            : throw new InvalidOperationException($"AZURE_OPENAI_ENDPOINT='{Env("AZURE_OPENAI_ENDPOINT")}' {why}");

    /// <summary>The Azure OpenAI API key credential. Throws when the Azure trio is not set.</summary>
    public static AzureKeyCredential AzureKeyCredential =>
        new(Env("AZURE_OPENAI_API_KEY") ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not configured"));

    /// <summary>Kept for the Azure-only call sites. Same as <see cref="AzureKeyCredential"/>.</summary>
    public static AzureKeyCredential KeyCredential => AzureKeyCredential;

    /// <summary>Embedding model deployment (Azure only). Reads <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c>, default text-embedding-ada-002.</summary>
    public static string EmbeddingDeployment =>
        Env("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-ada-002";

    /// <summary>True when the Azure trio AND an embedding deployment are set. Embeddings are Azure-only in these samples.</summary>
    public static bool IsEmbeddingConfigured =>
        IsAzureConfigured && Env("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") is not null;

    /// <summary>
    /// Azure AI Foundry PROJECT endpoint for the hosted-agent samples (H11, H12). Reads
    /// <c>AZURE_FOUNDRY_ENDPOINT</c>. Format: <c>https://&lt;hub&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>.
    /// Authenticates with Entra (<c>DefaultAzureCredential</c>; run <c>az login</c>). This is not the
    /// <c>foundry</c> CHAT provider, which is <c>FOUNDRY_ENDPOINT</c> + <c>FOUNDRY_API_KEY</c> + <c>FOUNDRY_MODEL</c>.
    /// </summary>
    public static Uri? FoundryEndpoint =>
        // The project client sends an Entra bearer token here; the same rule applies as for an API key:
        // https, or loopback http. Anything else is treated as not configured.
        InferenceProviderEnvironment.TryValidateEndpoint(Env("AZURE_FOUNDRY_ENDPOINT"), out var uri, out _) ? uri : null;

    /// <summary>True when a chat provider is configured and <c>AZURE_FOUNDRY_ENDPOINT</c> is set (H11/H12 need a judge too).</summary>
    public static bool IsFoundryConfigured => IsConfigured && FoundryEndpoint is not null;

    // ── Banners ──────────────────────────────────────────────────────────────

    /// <summary>One line for sample banners: provider, model, endpoint, and how it was chosen.</summary>
    public static string Describe() => IsConfigured
        ? $"{ProviderName} · {ModelDeployment} · {Endpoint} ({(Settings.Selection == InferenceProviderSelection.Explicit ? "AI_INFERENCE_PROVIDER" : "auto-detected")})"
        : ProviderName;

    public static void PrintMissingCredentialsWarning()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  No chat provider configured                                             ║");
        Console.WriteLine("║                                                                              ║");
        Console.WriteLine("║  AI_INFERENCE_PROVIDER = bitdeer | openai | foundry | azure | openai-compatible ║");
        Console.WriteLine("║                                                                              ║");
        Console.WriteLine("║   bitdeer            BITDEER_API_KEY                                          ║");
        Console.WriteLine("║   openai             OPENAI_API_KEY                                           ║");
        Console.WriteLine("║   foundry            FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL       ║");
        Console.WriteLine("║   azure              AZURE_OPENAI_ENDPOINT + _API_KEY + _DEPLOYMENT           ║");
        Console.WriteLine("║   openai-compatible  OPENAI_COMPATIBLE_ENDPOINT + _MODEL  (_API_KEY optional) ║");
        Console.WriteLine("║                                                                              ║");
        Console.WriteLine("║  or pass --provider <name> for one run.                                       ║");
        Console.WriteLine("║  Samples that need a model run in MOCK MODE or stop without one.              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        if (Settings.Diagnostic is { } why)
            Console.WriteLine($"   ↳ {why}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
