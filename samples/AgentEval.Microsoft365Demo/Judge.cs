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
    /// <summary>Whether the criteria are graded.</summary>
    public bool IsAvailable => Client is not null;

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
            return new JudgeChoice(
                client,
                $"{settings.DisplayName}, model {model} ({(settings.Selection == InferenceProviderSelection.Explicit ? InferenceProviderEnvironment.SelectorVariable : "auto-detected")}); judge calls cost money on that provider",
                "",
                Owned: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            var why = $"the judge client could not be built ({ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)})";
            return new JudgeChoice(null, "none (" + why + ")", "no judge: " + why, Owned: false);
        }
    }
}
