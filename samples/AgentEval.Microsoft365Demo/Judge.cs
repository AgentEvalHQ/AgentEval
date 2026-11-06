// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.ClientModel;
using AgentEval.Core;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentEval.Microsoft365Demo;

/// <summary>
/// The judge of the benchmark's criteria, or why there is none. Owned clients are disposed by <see cref="Dispose"/>; an
/// injected client (a host or test override) is the caller's.
/// </summary>
/// <param name="Client">The judge's chat client; null when the criteria are not graded.</param>
/// <param name="Description">What the banner and the summary say about the judge (provider and model; never a key or an endpoint).</param>
/// <param name="NotMeasuredReason">Why the criteria are NOT MEASURED when <paramref name="Client"/> is null.</param>
/// <param name="Owned">Whether <see cref="Dispose"/> disposes <paramref name="Client"/>.</param>
internal sealed record JudgeChoice(IChatClient? Client, string Description, string NotMeasuredReason, bool Owned) : IDisposable
{
    /// <summary>The judge of a run whose benchmark does not run: none, and nothing resolved.</summary>
    public static JudgeChoice NotUsed { get; } = new(null, "not used (the benchmark does not run in this invocation)", "the benchmark did not run", Owned: false);

    /// <summary>Whether the criteria are graded.</summary>
    public bool IsAvailable => Client is not null;

    /// <summary>The judge's model, when the demo resolved it; null for a host's client, whose model the demo does not know.</summary>
    public string? Model { get; init; }

    /// <summary>The judge's provider tag (<c>azure</c>, <c>openai</c>, <c>bitdeer</c>, …), when the demo resolved it.</summary>
    public string? Provider { get; init; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Owned)
        {
            Client?.Dispose();
        }
    }
}

/// <summary>
/// Picks the judge: <c>--no-judge</c> wins; then a host override; then the model <see cref="InferenceProviderEnvironment"/>
/// resolves from the environment (the same resolver the AgentEval samples and CLI use), wrapped with
/// <see cref="AgentEval.Core.ChatClientExtensions.WithMinimalReasoning(IChatClient)"/>. Nothing is sent here: building a client makes no call.
/// </summary>
internal static class JudgeResolver
{
    /// <summary>Resolves the judge.</summary>
    /// <param name="noJudge"><c>--no-judge</c>.</param>
    /// <param name="injected">A host or test judge; used as-is and never disposed here.</param>
    /// <param name="getEnvironmentVariable">Reads an environment variable.</param>
    public static JudgeChoice Resolve(bool noJudge, IChatClient? injected, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        if (noJudge)
        {
            return new JudgeChoice(null, "none (--no-judge)", "--no-judge: the criteria were not graded", Owned: false);
        }

        if (injected is not null)
        {
            return new JudgeChoice(injected, "the judge client the host supplied", "", Owned: false);
        }

        var settings = InferenceProviderEnvironment.Resolve(getEnvironmentVariable);
        if (!settings.IsConfigured)
        {
            if (!InferenceProviderEnvironment.AnyConfigurationAttempted(getEnvironmentVariable))
            {
                // Nothing configured at all: say so once, short; the help names the variables.
                return new JudgeChoice(
                    null,
                    $"none: no judge model is configured (set {InferenceProviderEnvironment.SelectorVariable} to " +
                    $"{string.Join(", ", InferenceProviderEnvironment.KnownValues)} and that provider's variables to grade the criteria)",
                    "no judge configured",
                    Owned: false);
            }

            // Something was configured but is incomplete or wrong: the resolver's diagnostic names the missing variable.
            var why = settings.Diagnostic ?? "the judge provider's variables are incomplete";
            return new JudgeChoice(null, "none: the judge provider is misconfigured — " + why, "no judge: the judge provider is misconfigured (see the banner or summary.md)", Owned: false);
        }

        try
        {
            var model = settings.Model ?? InferenceProviderEnvironment.AzureDefaultDeployment;
            var endpoint = settings.Endpoint ?? throw new InvalidOperationException("the provider has no endpoint");
            var key = settings.ApiKey ?? throw new InvalidOperationException("the provider has no API key");
            IChatClient client = settings.UsesAzureProtocol
                ? new AzureOpenAIClient(endpoint, new AzureKeyCredential(key)).GetChatClient(model).AsIChatClient().WithMinimalReasoning()
                : new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = endpoint }).GetChatClient(model).AsIChatClient().WithMinimalReasoning();
            return new JudgeChoice(client, $"{settings.DisplayName}, model {model} ({ChosenBy(settings)}); judge calls cost money on that provider", "", Owned: true)
            {
                Model = model,
                Provider = settings.ProviderTag,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            var why = $"the judge client could not be built ({ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)})";
            return new JudgeChoice(null, "none (" + why + ")", "no judge: " + why, Owned: false);
        }
    }

    /// <summary>
    /// How the provider was chosen, naming the variables: <c>AI_INFERENCE_PROVIDER=openai</c>, or
    /// <c>auto-detected from OPENAI_API_KEY; AI_INFERENCE_PROVIDER is not set</c>, so a tester sees which exported key is used.
    /// </summary>
    private static string ChosenBy(InferenceProviderSettings settings)
    {
        if (settings.Selection == InferenceProviderSelection.Explicit)
        {
            return $"{InferenceProviderEnvironment.SelectorVariable}={settings.ProviderTag}";
        }

        // Auto-detection picks the first provider whose required variables are all set; with no variable set, the
        // resolver lists exactly those required variables.
        var detectedFrom = InferenceProviderEnvironment.MissingVariablesOf(settings.Provider, _ => null);
        return $"auto-detected from {string.Join(", ", detectedFrom)}; {InferenceProviderEnvironment.SelectorVariable} is not set";
    }
}
