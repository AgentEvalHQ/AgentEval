// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.Samples;

/// <summary>Which chat provider the samples talk to. Resolved once per process from the environment.</summary>
public enum SampleProvider
{
    /// <summary>No provider has credentials; samples that need a model print a warning and stop.</summary>
    None,

    /// <summary>Azure OpenAI — <c>AZURE_OPENAI_ENDPOINT</c> + <c>AZURE_OPENAI_API_KEY</c> + <c>AZURE_OPENAI_DEPLOYMENT</c>.</summary>
    AzureOpenAI,

    /// <summary>Bitdeer AI Model Studio — <c>BITDEER_API_KEY</c>; endpoint and model have defaults.</summary>
    Bitdeer,

    /// <summary>Any OpenAI-compatible endpoint — <c>OPENAI_COMPATIBLE_ENDPOINT</c> + <c>OPENAI_COMPATIBLE_API_KEY</c> + <c>OPENAI_COMPATIBLE_MODEL</c>.</summary>
    OpenAICompatible,
}

/// <summary>
/// Provider configuration for the samples. Every sample obtains its model through
/// <see cref="CreateChatClient"/>, so switching the whole catalogue from Azure OpenAI to Bitdeer or
/// to any OpenAI-compatible endpoint is a matter of environment variables — no sample knows which
/// provider it is running on.
///
/// Selection order when several providers have credentials: <c>AGENTEVAL_SAMPLES_PROVIDER</c>
/// (or <c>--provider &lt;name&gt;</c> on the command line) wins; otherwise Azure OpenAI, then Bitdeer,
/// then the generic endpoint.
///
/// <code>
/// # Azure OpenAI (the historical default)
/// $env:AZURE_OPENAI_ENDPOINT   = "https://your-resource.openai.azure.com/"
/// $env:AZURE_OPENAI_API_KEY    = "..."
/// $env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"
///
/// # Bitdeer — one variable is enough
/// $env:BITDEER_API_KEY = "..."                       # BITDEER_MODEL defaults to zai-org/GLM-5.3-Flash
/// dotnet run -- 1 --provider bitdeer                  # or $env:AGENTEVAL_SAMPLES_PROVIDER = "bitdeer"
///
/// # Anything OpenAI-compatible (Ollama, Groq, vLLM, Together, LM Studio, ...)
/// $env:OPENAI_COMPATIBLE_ENDPOINT = "http://localhost:11434/v1"
/// $env:OPENAI_COMPATIBLE_API_KEY  = "no-key-needed"
/// $env:OPENAI_COMPATIBLE_MODEL    = "llama3.1"
/// </code>
///
/// Azure-only features stay Azure-only and say so: embeddings (<see cref="IsEmbeddingConfigured"/>)
/// and Azure AI Foundry (<see cref="IsFoundryConfigured"/>).
/// </summary>
/// <remarks>
/// The Azure path uses <c>AzureKeyCredential</c> (API key) for simplicity. For production, use
/// <c>ManagedIdentityCredential</c> or another specific <c>TokenCredential</c>. See MAF best practice #2.
/// </remarks>
public static class AIConfig
{
    public const string BitdeerDefaultEndpoint = "https://api-inference.bitdeer.ai/v1";
    public const string BitdeerDefaultModel = "zai-org/GLM-5.3-Flash";

    private static readonly Lazy<SampleProvider> s_provider = new(Resolve, isThreadSafe: true);

    // ── Provider selection ────────────────────────────────────────────────────

    /// <summary>The provider in use for this process.</summary>
    public static SampleProvider Provider => s_provider.Value;

    /// <summary>True when some provider has credentials. Samples that need a model check this first.</summary>
    public static bool IsConfigured => Provider != SampleProvider.None;

    /// <summary>A human-readable provider name for banners.</summary>
    public static string ProviderName => Provider switch
    {
        SampleProvider.AzureOpenAI => "Azure OpenAI",
        SampleProvider.Bitdeer => "Bitdeer AI Model Studio",
        SampleProvider.OpenAICompatible => "OpenAI-compatible endpoint",
        _ => "no provider configured",
    };

    /// <summary>A short tag for agent identities (<c>model@provider</c>), because the host is part of what was measured.</summary>
    public static string ProviderTag => Provider switch
    {
        SampleProvider.AzureOpenAI => "azure",
        SampleProvider.Bitdeer => "bitdeer",
        SampleProvider.OpenAICompatible => "openai-compatible",
        _ => "none",
    };

    /// <summary>True when the Azure OpenAI trio is set, whatever provider is selected.</summary>
    public static bool IsAzureConfigured =>
        Env("AZURE_OPENAI_ENDPOINT") is not null && Env("AZURE_OPENAI_API_KEY") is not null && Env("AZURE_OPENAI_DEPLOYMENT") is not null;

    /// <summary>True when <c>BITDEER_API_KEY</c> is set.</summary>
    public static bool IsBitdeerConfigured => Env("BITDEER_API_KEY") is not null;

    /// <summary>True when the generic OpenAI-compatible trio is set.</summary>
    public static bool IsOpenAICompatibleConfigured =>
        Env("OPENAI_COMPATIBLE_ENDPOINT") is not null && Env("OPENAI_COMPATIBLE_API_KEY") is not null && Env("OPENAI_COMPATIBLE_MODEL") is not null;

    private static SampleProvider Resolve()
    {
        var forced = Env("AGENTEVAL_SAMPLES_PROVIDER")?.ToLowerInvariant();
        switch (forced)
        {
            case "azure":
            case "azure-openai":
            case "azureopenai":
                return IsAzureConfigured ? SampleProvider.AzureOpenAI : SampleProvider.None;
            case "bitdeer":
                return IsBitdeerConfigured ? SampleProvider.Bitdeer : SampleProvider.None;
            case "openai-compatible":
            case "openai":
            case "compatible":
                return IsOpenAICompatibleConfigured ? SampleProvider.OpenAICompatible : SampleProvider.None;
            case null:
            case "":
                break;
            default:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"   ⚠️  AGENTEVAL_SAMPLES_PROVIDER='{forced}' is not one of azure | bitdeer | openai-compatible — falling back to auto-detection.");
                Console.ResetColor();
                break;
        }

        if (IsAzureConfigured) return SampleProvider.AzureOpenAI;
        if (IsBitdeerConfigured) return SampleProvider.Bitdeer;
        if (IsOpenAICompatibleConfigured) return SampleProvider.OpenAICompatible;
        return SampleProvider.None;
    }

    // ── The one factory every sample uses ─────────────────────────────────────

    /// <summary>
    /// Builds the chat client for the selected provider. <paramref name="model"/> defaults to
    /// <see cref="ModelDeployment"/>; pass <see cref="SecondaryModelDeployment"/> or an explicit
    /// name for comparisons.
    /// </summary>
    public static IChatClient CreateChatClient(string? model = null)
    {
        var resolved = model ?? ModelDeployment;
        switch (Provider)
        {
            case SampleProvider.AzureOpenAI:
                return CreateAzureOpenAIClient().GetChatClient(resolved).AsIChatClient();

            case SampleProvider.Bitdeer:
            case SampleProvider.OpenAICompatible:
                // The same construction the CLI's EndpointFactory.CreateOpenAICompatible uses for
                // --endpoint --model --api-key: an OpenAI client pointed at a different base URL.
                return new OpenAIClient(new ApiKeyCredential(ApiKey), new OpenAIClientOptions { Endpoint = Endpoint })
                    .GetChatClient(resolved)
                    .AsIChatClient();

            default:
                throw new InvalidOperationException(
                    "No chat provider is configured. Set AZURE_OPENAI_ENDPOINT/API_KEY/DEPLOYMENT, or BITDEER_API_KEY, or OPENAI_COMPATIBLE_ENDPOINT/API_KEY/MODEL.");
        }
    }

    /// <summary>
    /// The raw Azure client, for the Azure-only surfaces (embeddings, Foundry). Throws unless the
    /// Azure trio is set — it does not care which provider is selected for chat.
    /// </summary>
    public static AzureOpenAIClient CreateAzureOpenAIClient() => new(AzureEndpoint, AzureKeyCredential);

    // ── Endpoint / key / model for the selected provider ─────────────────────

    /// <summary>The selected provider's endpoint (base URL for OpenAI-compatible hosts, resource URL for Azure).</summary>
    public static Uri Endpoint => Provider switch
    {
        SampleProvider.AzureOpenAI => AzureEndpoint,
        SampleProvider.Bitdeer => new Uri(Env("BITDEER_ENDPOINT") ?? BitdeerDefaultEndpoint),
        SampleProvider.OpenAICompatible => new Uri(Env("OPENAI_COMPATIBLE_ENDPOINT")!),
        _ => throw new InvalidOperationException("No chat provider is configured."),
    };

    private static string ApiKey => Provider switch
    {
        SampleProvider.AzureOpenAI => Env("AZURE_OPENAI_API_KEY")!,
        SampleProvider.Bitdeer => Env("BITDEER_API_KEY")!,
        SampleProvider.OpenAICompatible => Env("OPENAI_COMPATIBLE_API_KEY")!,
        _ => throw new InvalidOperationException("No chat provider is configured."),
    };

    /// <summary>Primary model: the Azure deployment, the Bitdeer model (default GLM-5.3 Flash), or the generic model.</summary>
    public static string ModelDeployment => Provider switch
    {
        SampleProvider.Bitdeer => Env("BITDEER_MODEL") ?? BitdeerDefaultModel,
        SampleProvider.OpenAICompatible => Env("OPENAI_COMPATIBLE_MODEL") ?? throw new InvalidOperationException("OPENAI_COMPATIBLE_MODEL not configured"),
        _ => Env("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o",
    };

    /// <summary>
    /// Secondary model for comparison samples. Azure: <c>AZURE_OPENAI_DEPLOYMENT_2</c> (default gpt-4o-mini).
    /// Other providers: <c>BITDEER_MODEL_2</c> / <c>OPENAI_COMPATIBLE_MODEL_2</c>, defaulting to the primary
    /// model — a comparison of a model with itself, which those samples print plainly.
    /// </summary>
    public static string SecondaryModelDeployment => Provider switch
    {
        SampleProvider.Bitdeer => Env("BITDEER_MODEL_2") ?? ModelDeployment,
        SampleProvider.OpenAICompatible => Env("OPENAI_COMPATIBLE_MODEL_2") ?? ModelDeployment,
        _ => Env("AZURE_OPENAI_DEPLOYMENT_2") ?? "gpt-4o-mini",
    };

    /// <summary>Tertiary model for comparison samples; same convention as <see cref="SecondaryModelDeployment"/> (Azure default gpt-4.1).</summary>
    public static string TertiaryModelDeployment => Provider switch
    {
        SampleProvider.Bitdeer => Env("BITDEER_MODEL_3") ?? ModelDeployment,
        SampleProvider.OpenAICompatible => Env("OPENAI_COMPATIBLE_MODEL_3") ?? ModelDeployment,
        _ => Env("AZURE_OPENAI_DEPLOYMENT_3") ?? "gpt-4.1",
    };

    /// <summary>The primary model with the provider in the name, for agent identities and provenance.</summary>
    public static string ModelIdentity => $"{ModelDeployment}@{ProviderTag}";

    // ── Azure-only surfaces ───────────────────────────────────────────────────

    /// <summary>The Azure OpenAI resource endpoint. Throws when the Azure trio is not set.</summary>
    public static Uri AzureEndpoint =>
        new(Env("AZURE_OPENAI_ENDPOINT") ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT not configured"));

    /// <summary>The Azure OpenAI API key credential. Throws when the Azure trio is not set.</summary>
    public static AzureKeyCredential AzureKeyCredential =>
        new(Env("AZURE_OPENAI_API_KEY") ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY not configured"));

    /// <summary>Kept for the Azure-only call sites (embeddings, Foundry). Same as <see cref="AzureKeyCredential"/>.</summary>
    public static AzureKeyCredential KeyCredential => AzureKeyCredential;

    /// <summary>Embedding model deployment (Azure only). Reads <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c>, default text-embedding-ada-002.</summary>
    public static string EmbeddingDeployment =>
        Env("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") ?? "text-embedding-ada-002";

    /// <summary>True when the Azure trio AND an embedding deployment are set. Embeddings are Azure-only in these samples.</summary>
    public static bool IsEmbeddingConfigured =>
        IsAzureConfigured && Env("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") is not null;

    /// <summary>
    /// Azure AI Foundry project endpoint. Reads from <c>AZURE_FOUNDRY_ENDPOINT</c>.
    /// Format: <c>https://&lt;hub&gt;.services.ai.azure.com/api/projects/&lt;project&gt;</c>
    /// (copy from Azure AI Foundry portal → your project → Settings → Endpoint).
    /// Authentication uses Azure AD (<c>DefaultAzureCredential</c>); the API key cannot
    /// authenticate <c>AIProjectClient</c>.
    /// </summary>
    public static Uri? FoundryEndpoint =>
        Env("AZURE_FOUNDRY_ENDPOINT") is { } v && Uri.TryCreate(v, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>True when the Azure trio and <c>AZURE_FOUNDRY_ENDPOINT</c> are set.</summary>
    public static bool IsFoundryConfigured => IsAzureConfigured && FoundryEndpoint is not null;

    // ── Banners ──────────────────────────────────────────────────────────────

    /// <summary>One line for sample banners: provider, model, endpoint.</summary>
    public static string Describe() =>
        IsConfigured ? $"{ProviderName} · {ModelDeployment} · {Endpoint}" : ProviderName;

    public static void PrintMissingCredentialsWarning()
    {
        var forced = Env("AGENTEVAL_SAMPLES_PROVIDER");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ⚠️  No chat provider configured                                     ║");
        if (forced is not null)
            Console.WriteLine($"║     (AGENTEVAL_SAMPLES_PROVIDER = {forced,-8} has no credentials)          ║");
        Console.WriteLine("║                                                                      ║");
        Console.WriteLine("║  Set ONE of these:                                                   ║");
        Console.WriteLine("║   Azure OpenAI   AZURE_OPENAI_ENDPOINT + _API_KEY + _DEPLOYMENT       ║");
        Console.WriteLine("║   Bitdeer        BITDEER_API_KEY   (model defaults to GLM-5.3 Flash)  ║");
        Console.WriteLine("║   Any OpenAI-    OPENAI_COMPATIBLE_ENDPOINT + _API_KEY + _MODEL        ║");
        Console.WriteLine("║   compatible                                                         ║");
        Console.WriteLine("║                                                                      ║");
        Console.WriteLine("║  Pick one explicitly with --provider azure|bitdeer|openai-compatible ║");
        Console.WriteLine("║  Samples that need a model run in MOCK MODE or stop without one.     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}
