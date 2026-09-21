// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.ClientModel;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// Builds the CLI's chat client for whichever provider <c>AI_INFERENCE_PROVIDER</c> selects — the same
/// variable, the same resolver (<see cref="InferenceProviderEnvironment"/>) and the same precedence the
/// samples use. One place, so a host added to the resolver reaches every <c>bench</c> command at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existing Azure setups keep working unchanged.</b> With the selector unset and the
/// <c>AZURE_OPENAI_*</c> trio set, the resolver auto-detects Azure OpenAI and returns exactly the endpoint,
/// key and deployment the CLI read directly before. The selector only decides when more than one provider
/// has credentials, and an explicit selector whose variables are missing fails closed with a reason rather
/// than falling back to a host nobody chose.
/// </para>
/// <para>
/// This is the env-var convention path. The explicit-flag path (<c>--endpoint --model --api-key</c>,
/// <c>--azure --deployment-name</c>) still goes through <see cref="EndpointFactory"/> and is unaffected.
/// </para>
/// </remarks>
internal static class ProviderChatClientFactory
{
    /// <summary>
    /// The resolved provider settings (provider, endpoint, model, how it was chosen, why not). Read from the
    /// environment on every access, never cached: a cached first resolution would make the CLI ignore a
    /// variable set later in the same process, and would make every test after the first see stale settings.
    /// It is a handful of environment reads.
    /// </summary>
    public static InferenceProviderSettings Settings => InferenceProviderEnvironment.Resolve();

    /// <summary>
    /// Builds the chat client for the selected provider.
    /// </summary>
    /// <param name="purpose">What the client is for, for the banner: <c>"judge"</c>, <c>"agent"</c>.</param>
    /// <param name="model">Model / deployment override; defaults to the provider's configured model.</param>
    /// <returns>
    /// <c>(client, model, diagnostic)</c>. When <c>client</c> is <see langword="null"/>, <c>diagnostic</c>
    /// says which variables are missing and the caller writes it and returns a non-zero exit code — this
    /// method never writes to the console, so a caller with its own stderr stays in control of its output.
    /// </returns>
    public static (IChatClient? Client, string? Model, string? Diagnostic) TryCreate(string purpose, string? model = null, bool generousTimeout = false)
    {
        var settings = Settings;
        if (!settings.IsConfigured)
            return (null, null, settings.Diagnostic ?? "No inference provider is configured.");

        var resolved = model ?? settings.Model;
        if (string.IsNullOrWhiteSpace(resolved))
            return (null, null, $"{settings.DisplayName} is selected but names no model.");

        try
        {
            var timeout = generousTimeout ? NetworkTimeout() : (TimeSpan?)null;
            IChatClient client;
            if (settings.UsesAzureProtocol)
            {
                // Azure OpenAI and a Foundry resource's OpenAI-compatible endpoint speak the same protocol.
                var options = new AzureOpenAIClientOptions();
                if (timeout is { } t) options.NetworkTimeout = t;
                client = new AzureOpenAIClient(settings.Endpoint!, new AzureKeyCredential(settings.ApiKey!), options)
                    .GetChatClient(resolved).AsIChatClient();
            }
            else
            {
                // Bitdeer, OpenAI and any OpenAI-compatible host: an OpenAI client at a different base URL.
                var options = new OpenAIClientOptions { Endpoint = settings.Endpoint };
                if (timeout is { } t) options.NetworkTimeout = t;
                client = new OpenAIClient(new ApiKeyCredential(settings.ApiKey!), options)
                    .GetChatClient(resolved).AsIChatClient();
            }

            return (CliChatClientDiagnostics.Wrap(client, purpose), resolved, null);
        }
        catch (Exception ex)
        {
            // The endpoint is validated by the resolver, so this is a malformed key or an SDK-level refusal.
            return (null, null, $"Failed to construct the {settings.DisplayName} chat client: {ex.Message}");
        }
    }

    /// <summary>
    /// The per-attempt network timeout for an agent under test. The SDK default (100 s) can fire when the
    /// subject is a slow real agent behind an adapter, and the resulting cancellation aborts a whole scan.
    /// Override with <c>AGENTEVAL_AGENT_NETWORK_TIMEOUT_S</c>.
    /// </summary>
    private static TimeSpan NetworkTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("AGENTEVAL_AGENT_NETWORK_TIMEOUT_S");
        var seconds = double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 180.0;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// One line naming what was selected and how, for the stderr banner the bench commands print.
    /// </summary>
    /// <remarks>
    /// The endpoint is printed as scheme + host + path only. A configured URL may legitimately carry
    /// user-info, a query string or a fragment, any of which can hold a token, and this line goes to stderr
    /// on every run — where a CI log would keep it.
    /// </remarks>
    public static string Describe(string purpose, string model) => Describe(Settings, purpose, model);

    /// <summary>The same line for settings already in hand, so a caller never resolves twice.</summary>
    public static string Describe(InferenceProviderSettings s, string purpose, string model)
    {
        var how = s.Selection switch
        {
            InferenceProviderSelection.Explicit => $"{InferenceProviderEnvironment.SelectorVariable}={s.ProviderTag}",
            InferenceProviderSelection.AutoDetected => "auto-detected",
            _ => "unselected",
        };
        return $"✔ {s.DisplayName} {purpose} configured — endpoint={SafeEndpoint(s.Endpoint)}, model={model} ({how}).";
    }

    /// <summary>Scheme, host, port and path — never user-info, query or fragment, which can carry a token.</summary>
    internal static string SafeEndpoint(Uri? endpoint)
    {
        if (endpoint is null) return "(none)";
        if (!endpoint.IsAbsoluteUri) return "(relative)";
        var port = endpoint.IsDefaultPort ? "" : $":{endpoint.Port}";
        return $"{endpoint.Scheme}://{endpoint.Host}{port}{endpoint.AbsolutePath.TrimEnd('/')}";
    }
}
