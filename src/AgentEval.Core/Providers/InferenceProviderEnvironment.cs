// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Providers;

/// <summary>Which inference host serves the chat model. Selected by <c>AI_INFERENCE_PROVIDER</c>.</summary>
public enum InferenceProvider
{
    /// <summary>No provider is selected or credentialed.</summary>
    None = 0,

    /// <summary>Azure OpenAI — <c>AZURE_OPENAI_ENDPOINT</c> + <c>AZURE_OPENAI_API_KEY</c> + <c>AZURE_OPENAI_DEPLOYMENT</c>. Azure SDK.</summary>
    AzureOpenAI,

    /// <summary>Bitdeer AI Model Studio — <c>BITDEER_API_KEY</c>; endpoint and model have defaults. OpenAI-compatible.</summary>
    Bitdeer,

    /// <summary>OpenAI's own API — <c>OPENAI_API_KEY</c>; <c>OPENAI_BASE_URL</c> and <c>OPENAI_MODEL</c> have defaults.</summary>
    OpenAI,

    /// <summary>
    /// Azure AI Foundry — the Azure OpenAI-compatible endpoint of a Foundry resource, with an API key:
    /// <c>FOUNDRY_ENDPOINT</c> + <c>FOUNDRY_API_KEY</c> + <c>FOUNDRY_MODEL</c>. Azure SDK. Distinct from the
    /// hosted-agent path (<c>AZURE_FOUNDRY_ENDPOINT</c> + Entra login), which is not a chat provider.
    /// </summary>
    Foundry,

    /// <summary>Any OpenAI-compatible endpoint — <c>OPENAI_COMPATIBLE_ENDPOINT</c> + <c>OPENAI_COMPATIBLE_MODEL</c>; <c>OPENAI_COMPATIBLE_API_KEY</c> optional (Ollama, LM Studio and vLLM take none).</summary>
    OpenAICompatible,
}

/// <summary>How the provider was chosen.</summary>
public enum InferenceProviderSelection
{
    /// <summary>Nothing selected and nothing detected.</summary>
    None = 0,

    /// <summary><c>AI_INFERENCE_PROVIDER</c> named it.</summary>
    Explicit,

    /// <summary><c>AI_INFERENCE_PROVIDER</c> was unset; the first provider with credentials won.</summary>
    AutoDetected,
}

/// <summary>
/// Everything a caller needs to build a chat client for the selected provider, read from the
/// environment once. Carries no SDK types, so it can live in Core and be shared by the samples,
/// the CLI and any host.
/// </summary>
/// <param name="Provider">The selected provider, or <see cref="InferenceProvider.None"/>.</param>
/// <param name="ProviderTag">Short tag for identities and provenance: <c>azure</c>, <c>bitdeer</c>, <c>openai</c>, <c>foundry</c>, <c>openai-compatible</c>, <c>none</c>.</param>
/// <param name="DisplayName">Human-readable provider name for banners.</param>
/// <param name="Endpoint">The base URL for OpenAI-style hosts, or the resource endpoint for the Azure SDK hosts.</param>
/// <param name="ApiKey">The API key. Never log it.</param>
/// <param name="Model">The primary model or deployment name.</param>
/// <param name="SecondaryModel">A second model for comparison runs; defaults to the primary where the provider defines no other default.</param>
/// <param name="TertiaryModel">A third model for comparison runs; same rule.</param>
/// <param name="Selection">Whether the choice was explicit or auto-detected.</param>
/// <param name="Diagnostic">Why nothing is configured, when nothing is — e.g. an explicit provider whose variables are missing, or an unknown selector value.</param>
public sealed record InferenceProviderSettings(
    InferenceProvider Provider,
    string ProviderTag,
    string DisplayName,
    Uri? Endpoint,
    string? ApiKey,
    string? Model,
    string? SecondaryModel,
    string? TertiaryModel,
    InferenceProviderSelection Selection,
    string? Diagnostic)
{
    /// <summary>True when a provider with credentials is selected.</summary>
    public bool IsConfigured => Provider != InferenceProvider.None;

    /// <summary>True for the hosts that speak the Azure OpenAI protocol (<see cref="InferenceProvider.AzureOpenAI"/>, <see cref="InferenceProvider.Foundry"/>); false for the OpenAI-protocol hosts.</summary>
    public bool UsesAzureProtocol => Provider is InferenceProvider.AzureOpenAI or InferenceProvider.Foundry;

    /// <summary><c>model@provider</c> — the identity a benchmark should carry, because the host is part of what was measured.</summary>
    public string ModelIdentity => $"{Model ?? "?"}@{ProviderTag}";

    /// <summary>The empty settings, with a reason.</summary>
    public static InferenceProviderSettings NotConfigured(string? diagnostic) => new(
        InferenceProvider.None, "none", "no provider configured", null, null, null, null, null,
        InferenceProviderSelection.None, diagnostic);

    /// <summary>
    /// Never prints the key. A positional record's generated <c>ToString()</c> would, and this object
    /// is the kind that ends up in a log line ("resolved settings: {settings}").
    /// </summary>
    public override string ToString() =>
        $"{nameof(InferenceProviderSettings)} {{ Provider = {Provider}, ProviderTag = {ProviderTag}, Endpoint = {Endpoint}, " +
        $"ApiKey = {(ApiKey is null ? "null" : "[redacted]")}, Model = {Model}, SecondaryModel = {SecondaryModel}, " +
        $"TertiaryModel = {TertiaryModel}, Selection = {Selection}, Diagnostic = {Diagnostic} }}";
}

/// <summary>
/// Resolves <see cref="InferenceProviderSettings"/> from environment variables. The selector is
/// <c>AI_INFERENCE_PROVIDER</c>; when several providers have credentials at once, it decides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Explicit beats detected, and an explicit mistake fails closed.</b> When
/// <c>AI_INFERENCE_PROVIDER</c> names a provider whose variables are missing, or names something
/// unknown, the result is <see cref="InferenceProvider.None"/> with a <see cref="InferenceProviderSettings.Diagnostic"/>
/// — it does not silently fall back to another host the user did not choose.
/// </para>
/// <para>
/// <b>Auto-detection order</b> when the selector is unset: Azure OpenAI, Bitdeer, OpenAI, Foundry,
/// then the generic OpenAI-compatible endpoint. Set the selector whenever more than one is configured.
/// </para>
/// </remarks>
public static class InferenceProviderEnvironment
{
    /// <summary>The selector variable.</summary>
    public const string SelectorVariable = "AI_INFERENCE_PROVIDER";

    /// <summary>The accepted selector values, canonical spelling.</summary>
    public static readonly IReadOnlyList<string> KnownValues = ["azure", "bitdeer", "openai", "foundry", "openai-compatible"];

    public const string BitdeerDefaultEndpoint = "https://api-inference.bitdeer.ai/v1";
    public const string BitdeerDefaultModel = "zai-org/GLM-5.3-Flash";
    public const string OpenAIDefaultEndpoint = "https://api.openai.com/v1";
    public const string OpenAIDefaultModel = "gpt-4o-mini";
    public const string AzureDefaultDeployment = "gpt-4o";
    public const string AzureDefaultSecondaryDeployment = "gpt-4o-mini";
    public const string AzureDefaultTertiaryDeployment = "gpt-4.1";

    private static readonly InferenceProvider[] s_autoDetectOrder =
    [
        InferenceProvider.AzureOpenAI,
        InferenceProvider.Bitdeer,
        InferenceProvider.OpenAI,
        InferenceProvider.Foundry,
        InferenceProvider.OpenAICompatible,
    ];

    /// <summary>Parses a selector value; tolerant of a few spellings. <see langword="null"/> for unknown.</summary>
    public static InferenceProvider? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "azure" or "azure-openai" or "azureopenai" => InferenceProvider.AzureOpenAI,
        "bitdeer" => InferenceProvider.Bitdeer,
        "openai" => InferenceProvider.OpenAI,
        "foundry" or "azure-foundry" or "azure-ai-foundry" => InferenceProvider.Foundry,
        "openai-compatible" or "openai_compatible" or "compatible" or "openai-compat" => InferenceProvider.OpenAICompatible,
        _ => null,
    };

    /// <summary>The canonical tag for a provider.</summary>
    public static string TagOf(InferenceProvider provider) => provider switch
    {
        InferenceProvider.AzureOpenAI => "azure",
        InferenceProvider.Bitdeer => "bitdeer",
        InferenceProvider.OpenAI => "openai",
        InferenceProvider.Foundry => "foundry",
        InferenceProvider.OpenAICompatible => "openai-compatible",
        _ => "none",
    };

    /// <summary>The display name for a provider.</summary>
    public static string DisplayNameOf(InferenceProvider provider) => provider switch
    {
        InferenceProvider.AzureOpenAI => "Azure OpenAI",
        InferenceProvider.Bitdeer => "Bitdeer AI Model Studio",
        InferenceProvider.OpenAI => "OpenAI",
        InferenceProvider.Foundry => "Azure AI Foundry (OpenAI-compatible endpoint)",
        InferenceProvider.OpenAICompatible => "OpenAI-compatible endpoint",
        _ => "no provider configured",
    };

    /// <summary>The variables a provider needs, for diagnostics.</summary>
    public static string RequiredVariablesOf(InferenceProvider provider) => provider switch
    {
        InferenceProvider.AzureOpenAI => "AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY + AZURE_OPENAI_DEPLOYMENT",
        InferenceProvider.Bitdeer => "BITDEER_API_KEY (BITDEER_ENDPOINT and BITDEER_MODEL have defaults)",
        InferenceProvider.OpenAI => "OPENAI_API_KEY (OPENAI_BASE_URL and OPENAI_MODEL have defaults)",
        InferenceProvider.Foundry => "FOUNDRY_ENDPOINT + FOUNDRY_API_KEY + FOUNDRY_MODEL",
        InferenceProvider.OpenAICompatible => "OPENAI_COMPATIBLE_ENDPOINT + OPENAI_COMPATIBLE_MODEL (OPENAI_COMPATIBLE_API_KEY optional; keyless local hosts get \"no-key-needed\")",
        _ => "",
    };

    /// <summary>
    /// The variables a provider requires that are NOT set, in order. Empty when the provider is fully
    /// configured. A diagnostic built from this never names a variable the operator has already set, which is
    /// the difference between "you are missing AZURE_OPENAI_API_KEY" and a wall of every provider's options.
    /// </summary>
    public static IReadOnlyList<string> MissingVariablesOf(InferenceProvider provider, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        bool NotSet(string n) => string.IsNullOrWhiteSpace(getEnvironmentVariable(n));
        string[] required = provider switch
        {
            InferenceProvider.AzureOpenAI => ["AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_DEPLOYMENT"],
            InferenceProvider.Bitdeer => ["BITDEER_API_KEY"],
            InferenceProvider.OpenAI => ["OPENAI_API_KEY"],
            InferenceProvider.Foundry => ["FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_MODEL"],
            InferenceProvider.OpenAICompatible => ["OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_MODEL"],
            _ => [],
        };
        return [.. required.Where(NotSet)];
    }

    /// <summary>True when a provider has SOME of its variables but not all — almost always a typo or a half-done setup.</summary>
    private static bool IsPartiallyConfigured(InferenceProvider provider, Func<string, string?> env)
    {
        var missing = MissingVariablesOf(provider, env);
        if (missing.Count == 0) return false;
        var required = provider switch
        {
            InferenceProvider.AzureOpenAI or InferenceProvider.Foundry => 3,
            InferenceProvider.OpenAICompatible => 2,
            InferenceProvider.Bitdeer or InferenceProvider.OpenAI => 1,
            _ => 0,
        };
        return required > 0 && missing.Count < required;
    }

    /// <summary>
    /// True when someone tried to configure a provider: the selector is set, or any provider has at least
    /// one of its variables. The difference matters to a caller that may fall back to a stub — an
    /// unconfigured machine is a legitimate fallback, a MISCONFIGURED one is a typo that must fail closed,
    /// or the run silently produces stub-graded evidence from a mistake.
    /// </summary>
    public static bool AnyConfigurationAttempted(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        bool Set(string n) => !string.IsNullOrWhiteSpace(getEnvironmentVariable(n));
        if (Set(SelectorVariable)) return true;
        // EVERY variable a provider reads, not only its required credentials. Someone who set
        // OPENAI_BASE_URL or BITDEER_MODEL and nothing else has tried to configure a provider and made a
        // mistake; counting only credentials would let the stub rescue exactly that case, which is the hole
        // this predicate exists to close.
        foreach (var name in AllProviderVariables)
        {
            if (Set(name)) return true;
        }
        return false;
    }

    /// <summary>Resolves the settings from the process environment.</summary>
    public static InferenceProviderSettings Resolve() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>Resolves the settings from any variable source (tests pass a dictionary).</summary>
    /// <param name="getEnvironmentVariable">Returns a variable's value or <see langword="null"/>.</param>
    public static InferenceProviderSettings Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        string? Env(string name)
        {
            var v = getEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        var selector = Env(SelectorVariable);
        if (selector is not null)
        {
            var parsed = Parse(selector);
            if (parsed is null)
            {
                return InferenceProviderSettings.NotConfigured(
                    $"{SelectorVariable}='{selector}' is not one of {string.Join(" | ", KnownValues)}.");
            }
            if (!HasCredentials(parsed.Value, Env))
            {
                return InferenceProviderSettings.NotConfigured(
                    $"{SelectorVariable}={TagOf(parsed.Value)}, but it is missing: " +
                    $"{string.Join(", ", MissingVariablesOf(parsed.Value, Env))}. It needs {RequiredVariablesOf(parsed.Value)}.");
            }
            return Describe(parsed.Value, Env, InferenceProviderSelection.Explicit);
        }

        foreach (var candidate in s_autoDetectOrder)
        {
            if (HasCredentials(candidate, Env))
                return Describe(candidate, Env, InferenceProviderSelection.AutoDetected);
        }

        // A provider with SOME of its variables set is a half-done setup, not an unchosen one: name exactly what
        // it lacks. Listing every provider's requirements here would claim variables are missing that are set.
        var partial = s_autoDetectOrder.Where(p => IsPartiallyConfigured(p, Env)).ToList();
        if (partial.Count > 0)
        {
            return InferenceProviderSettings.NotConfigured(
                string.Join(" ", partial.Select(p =>
                    $"{DisplayNameOf(p)} is partially configured — missing: {string.Join(", ", MissingVariablesOf(p, Env))}.")));
        }

        return InferenceProviderSettings.NotConfigured(
            $"{SelectorVariable} is not set and no provider has credentials. Set one of: " +
            string.Join("; ", s_autoDetectOrder.Select(p => $"{TagOf(p)} → {RequiredVariablesOf(p)}")) + ".");
    }

    /// <summary>
    /// Every environment variable this resolver reads for any provider — required and optional alike.
    /// Used by <see cref="AnyConfigurationAttempted"/>; a variable added to the resolver belongs here too,
    /// and the test fixture that scrubs the environment asserts it stays in step with this list.
    /// </summary>
    public static readonly IReadOnlyList<string> AllProviderVariables =
    [
        "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_DEPLOYMENT",
        "AZURE_OPENAI_DEPLOYMENT_2", "AZURE_OPENAI_DEPLOYMENT_3",
        "BITDEER_API_KEY", "BITDEER_ENDPOINT", "BITDEER_MODEL", "BITDEER_MODEL_2", "BITDEER_MODEL_3",
        "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_MODEL", "OPENAI_MODEL_2", "OPENAI_MODEL_3",
        "FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_MODEL", "FOUNDRY_MODEL_2", "FOUNDRY_MODEL_3",
        "OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_MODEL",
        "OPENAI_COMPATIBLE_MODEL_2", "OPENAI_COMPATIBLE_MODEL_3",
    ];

    /// <summary>True when <paramref name="provider"/> has every variable it requires.</summary>
    public static bool HasCredentials(InferenceProvider provider, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        bool Set(string n) => !string.IsNullOrWhiteSpace(getEnvironmentVariable(n));
        return provider switch
        {
            InferenceProvider.AzureOpenAI => Set("AZURE_OPENAI_ENDPOINT") && Set("AZURE_OPENAI_API_KEY") && Set("AZURE_OPENAI_DEPLOYMENT"),
            InferenceProvider.Bitdeer => Set("BITDEER_API_KEY"),
            InferenceProvider.OpenAI => Set("OPENAI_API_KEY"),
            InferenceProvider.Foundry => Set("FOUNDRY_ENDPOINT") && Set("FOUNDRY_API_KEY") && Set("FOUNDRY_MODEL"),
            // Ollama, LM Studio and vLLM take no key. The OpenAI SDK still needs a non-empty credential
            // string, so an absent key becomes the same sentinel the CLI's EndpointFactory sends.
            InferenceProvider.OpenAICompatible => Set("OPENAI_COMPATIBLE_ENDPOINT") && Set("OPENAI_COMPATIBLE_MODEL"),
            _ => false,
        };
    }

    /// <summary>The credential sent to a keyless OpenAI-compatible host; the same sentinel the CLI uses.</summary>
    public const string NoKeyNeeded = "no-key-needed";

    private static InferenceProviderSettings Describe(InferenceProvider provider, Func<string, string?> env, InferenceProviderSelection selection)
    {
        string tag = TagOf(provider), name = DisplayNameOf(provider);
        var (endpointVariable, endpointValue) = provider switch
        {
            InferenceProvider.AzureOpenAI => ("AZURE_OPENAI_ENDPOINT", env("AZURE_OPENAI_ENDPOINT")),
            InferenceProvider.Bitdeer => ("BITDEER_ENDPOINT", env("BITDEER_ENDPOINT") ?? BitdeerDefaultEndpoint),
            InferenceProvider.OpenAI => ("OPENAI_BASE_URL", env("OPENAI_BASE_URL") ?? OpenAIDefaultEndpoint),
            InferenceProvider.Foundry => ("FOUNDRY_ENDPOINT", env("FOUNDRY_ENDPOINT")),
            InferenceProvider.OpenAICompatible => ("OPENAI_COMPATIBLE_ENDPOINT", env("OPENAI_COMPATIBLE_ENDPOINT")),
            _ => ("", null),
        };

        if (!TryValidateEndpoint(endpointValue, out var endpoint, out var why))
        {
            // Name the variable and the reason, never the value: an endpoint may carry user-info, a query or
            // a fragment, and this diagnostic is printed to stderr where a CI log keeps it.
            return InferenceProviderSettings.NotConfigured($"{endpointVariable} {why}");
        }

        switch (provider)
        {
            case InferenceProvider.AzureOpenAI:
            {
                var model = env("AZURE_OPENAI_DEPLOYMENT") ?? AzureDefaultDeployment;
                return new(provider, tag, name, endpoint, env("AZURE_OPENAI_API_KEY"), model,
                    env("AZURE_OPENAI_DEPLOYMENT_2") ?? AzureDefaultSecondaryDeployment,
                    env("AZURE_OPENAI_DEPLOYMENT_3") ?? AzureDefaultTertiaryDeployment,
                    selection, null);
            }
            case InferenceProvider.Bitdeer:
            {
                var model = env("BITDEER_MODEL") ?? BitdeerDefaultModel;
                return new(provider, tag, name, endpoint, env("BITDEER_API_KEY"), model,
                    env("BITDEER_MODEL_2") ?? model, env("BITDEER_MODEL_3") ?? model, selection, null);
            }
            case InferenceProvider.OpenAI:
            {
                var model = env("OPENAI_MODEL") ?? OpenAIDefaultModel;
                return new(provider, tag, name, endpoint, env("OPENAI_API_KEY"), model,
                    env("OPENAI_MODEL_2") ?? model, env("OPENAI_MODEL_3") ?? model, selection, null);
            }
            case InferenceProvider.Foundry:
            {
                var model = env("FOUNDRY_MODEL")!;
                return new(provider, tag, name, endpoint, env("FOUNDRY_API_KEY"), model,
                    env("FOUNDRY_MODEL_2") ?? model, env("FOUNDRY_MODEL_3") ?? model, selection, null);
            }
            case InferenceProvider.OpenAICompatible:
            {
                var model = env("OPENAI_COMPATIBLE_MODEL")!;
                return new(provider, tag, name, endpoint, env("OPENAI_COMPATIBLE_API_KEY") ?? NoKeyNeeded, model,
                    env("OPENAI_COMPATIBLE_MODEL_2") ?? model, env("OPENAI_COMPATIBLE_MODEL_3") ?? model, selection, null);
            }
            default:
                return InferenceProviderSettings.NotConfigured(null);
        }
    }

    /// <summary>
    /// An endpoint that will carry an API key must be an absolute <c>https</c> URL, or a loopback
    /// <c>http</c> one for local servers (Ollama, LM Studio, vLLM). Anything else is refused with a
    /// reason rather than sending a credential in cleartext or throwing from inside resolution.
    /// </summary>
    public static bool TryValidateEndpoint(string? value, out Uri? endpoint, out string? reason)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "is not set.";
            return false;
        }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            reason = "is not an absolute http(s) URL.";
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            reason = "uses plain http to a non-loopback host; an API key would travel in cleartext. Use https, or a loopback address for a local server.";
            return false;
        }
        endpoint = uri;
        reason = null;
        return true;
    }
}
