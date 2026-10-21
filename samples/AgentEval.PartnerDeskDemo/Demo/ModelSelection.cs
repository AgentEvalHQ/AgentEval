// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.ClientModel;
using AgentEval.Providers;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using AgentEval.Core;

namespace AgentEval.PartnerDeskDemo.Demo;

/// <summary>
/// Which model a run talks to: a live chat model on the provider <c>AI_INFERENCE_PROVIDER</c> selects, or the
/// scripted offline provider. The demo and its evaluation both decide through this one type.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is AgentEval Core's <see cref="InferenceProviderEnvironment"/> — the same reader the samples and the
/// CLI use — so the demo runs on Azure OpenAI, Bitdeer, OpenAI, a Foundry resource or any OpenAI-compatible
/// endpoint without knowing which. An explicit selector whose variables are missing fails closed: nothing live is
/// chosen and it never falls back to a host nobody chose. Because the selector asked for a live run, the demo and
/// the evaluation then stop with exit 2 (<see cref="ExplicitSelectionUnresolved"/>) instead of running the scripted
/// model in its place; <c>--offline</c> is how to ask for the scripted model.
/// </para>
/// <para>
/// <c>--model NAME</c> replaces the model of the provider the environment resolved. <c>--deployment NAME</c> is the
/// same option under the name it had when the demo was Azure-only, and keeps that version's contract: with
/// <c>AZURE_OPENAI_ENDPOINT</c> and <c>AZURE_OPENAI_API_KEY</c> set and no <c>AZURE_OPENAI_DEPLOYMENT</c>, the
/// option supplies the deployment. It is offered to the resolver only when nothing resolves without it, so it never
/// changes which provider a working environment picks, never satisfies a selector that names another provider, and
/// never stands in for a key or an endpoint.
/// </para>
/// </remarks>
public sealed class ModelSelection
{
    /// <summary>The option that names the model (or Azure deployment) for the selected provider.</summary>
    public const string ModelOption = "--model";

    /// <summary>The Azure-era name of <see cref="ModelOption"/>, still accepted.</summary>
    public const string DeploymentAlias = "--deployment";

    /// <summary>How to configure a live provider, in one line, for banners and errors.</summary>
    public const string SetupHint =
        "set AI_INFERENCE_PROVIDER (azure | bitdeer | openai | foundry | openai-compatible) and that provider's " +
        "variables — bitdeer needs only BITDEER_API_KEY; azure needs AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT";

    private const string AzureDeploymentVariable = "AZURE_OPENAI_DEPLOYMENT";

    private ModelSelection(
        InferenceProviderSettings settings,
        string? model,
        bool isLive,
        bool offlineRequested,
        bool configurationAttempted,
        bool providerExplicitlySelected)
    {
        Settings = settings;
        Model = model;
        IsLive = isLive;
        OfflineRequested = offlineRequested;
        ConfigurationAttempted = configurationAttempted;
        ProviderExplicitlySelected = providerExplicitlySelected;
    }

    /// <summary>
    /// The provider settings the environment resolved — with the <c>--deployment</c> value as
    /// <c>AZURE_OPENAI_DEPLOYMENT</c> when that completed the Azure setup. Carries the API key: never print it.
    /// </summary>
    public InferenceProviderSettings Settings { get; }

    /// <summary>The model the live client is built for: the command-line override, else the provider's model.</summary>
    public string? Model { get; }

    /// <summary>True when runs go to the live provider; false when they go to the scripted offline model.</summary>
    public bool IsLive { get; }

    /// <summary>True when <c>--offline</c> (or an offline-only mode) forced the scripted model.</summary>
    public bool OfflineRequested { get; }

    /// <summary>True when the environment names any provider variable, configured correctly or not.</summary>
    public bool ConfigurationAttempted { get; }

    /// <summary>True when <c>AI_INFERENCE_PROVIDER</c> is set, whether or not it resolved.</summary>
    public bool ProviderExplicitlySelected { get; }

    /// <summary>
    /// True when <c>AI_INFERENCE_PROVIDER</c> asked for a live provider that did not resolve and <c>--offline</c> was
    /// not passed. The run must stop: the scripted model is not what was asked for, and in its place every oracle
    /// would hold and <c>--all</c> would exit 0 on a run that measured no live model.
    /// </summary>
    public bool ExplicitSelectionUnresolved => ProviderExplicitlySelected && !IsLive && !OfflineRequested;

    /// <summary>
    /// The error for <see cref="ExplicitSelectionUnresolved"/>: the resolver's reason, which names the missing
    /// variables (never their values), and the two ways forward.
    /// </summary>
    public string UnresolvedSelectionError =>
        $"{InferenceProviderEnvironment.SelectorVariable} asks for a live provider, but none resolved. " +
        $"{Settings.Diagnostic ?? "The selected provider is not configured."} " +
        "Fix that provider's variables, or pass --offline to run the scripted model on purpose.";

    /// <summary>Resolves the selection from the process environment.</summary>
    public static ModelSelection Resolve(IReadOnlyList<string> args, bool forceOffline) =>
        Resolve(args, forceOffline, Environment.GetEnvironmentVariable);

    /// <summary>Resolves the selection from any variable source (tests pass a dictionary).</summary>
    /// <param name="args">The command line; only <see cref="ModelOption"/> and its alias are read.</param>
    /// <param name="forceOffline">True to use the scripted model whatever the environment says.</param>
    /// <param name="getEnvironmentVariable">Returns a variable's value or <see langword="null"/>.</param>
    public static ModelSelection Resolve(
        IReadOnlyList<string> args,
        bool forceOffline,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var modelOverride = ReadModelOverride(args);
        var settings = InferenceProviderEnvironment.Resolve(getEnvironmentVariable);
        if (!settings.IsConfigured && modelOverride is not null && LacksOnlyTheAzureDeployment(getEnvironmentVariable))
        {
            // The Azure-era contract: --deployment NAME stood in for AZURE_OPENAI_DEPLOYMENT. Offer it to the resolver
            // as that one variable, and only now, when nothing resolved without it: the resolver still applies the
            // selector, so a selector naming another provider stays unresolved.
            settings = InferenceProviderEnvironment.Resolve(name =>
                string.Equals(name, AzureDeploymentVariable, StringComparison.Ordinal)
                    ? modelOverride
                    : getEnvironmentVariable(name));
        }

        var model = settings.IsConfigured ? modelOverride ?? settings.Model : null;
        return new ModelSelection(
            settings,
            model,
            isLive: !forceOffline && settings.IsConfigured && !string.IsNullOrWhiteSpace(model),
            offlineRequested: forceOffline,
            configurationAttempted: InferenceProviderEnvironment.AnyConfigurationAttempted(getEnvironmentVariable),
            providerExplicitlySelected: !string.IsNullOrWhiteSpace(
                getEnvironmentVariable(InferenceProviderEnvironment.SelectorVariable)));
    }

    /// <summary>True when the Azure endpoint and key are set and the deployment is not: the one gap the option fills.</summary>
    private static bool LacksOnlyTheAzureDeployment(Func<string, string?> getEnvironmentVariable) =>
        !string.IsNullOrWhiteSpace(getEnvironmentVariable("AZURE_OPENAI_ENDPOINT"))
        && !string.IsNullOrWhiteSpace(getEnvironmentVariable("AZURE_OPENAI_API_KEY"))
        && string.IsNullOrWhiteSpace(getEnvironmentVariable(AzureDeploymentVariable));

    /// <summary>Reads <c>--model NAME</c>, then the <c>--deployment NAME</c> alias; null when neither is set.</summary>
    public static string? ReadModelOverride(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return ReadOption(args, ModelOption) ?? ReadOption(args, DeploymentAlias);
    }

    /// <summary>The model a report carries: provider and model when live, the scripted provider otherwise.</summary>
    public string Label => IsLive ? $"{Settings.DisplayName} '{Model}'" : "scripted (offline, deterministic)";

    /// <summary>The banner sentence naming the model in use. Names the provider and model, never the key.</summary>
    public string Describe()
    {
        if (IsLive)
        {
            var how = Settings.Selection == InferenceProviderSelection.Explicit
                ? $"selected by {InferenceProviderEnvironment.SelectorVariable}"
                : $"auto-detected; set {InferenceProviderEnvironment.SelectorVariable} to choose";
            return $"Model: live {Settings.DisplayName}, model '{Model}' ({how}). " +
                   $"Override the model with {ModelOption} <name>.";
        }

        const string scripted =
            "The model's decisions are fixed, so this path verifies the gates, not the model's susceptibility.";
        if (OfflineRequested)
        {
            return $"Model: SCRIPTED offline provider (--offline). {scripted}";
        }

        // A machine with no provider variables gets the setup line; a half-done or mistyped setup gets the
        // resolver's reason, which names the missing variables (never their values).
        return ConfigurationAttempted && Settings.Diagnostic is { } why
            ? $"Model: SCRIPTED offline provider. {scripted} No live provider was used: {why}"
            : $"Model: SCRIPTED offline provider. {scripted} For the live path, {SetupHint}.";
    }

    /// <summary>
    /// Builds the live chat client: the Azure OpenAI SDK for the Azure-protocol hosts, the OpenAI SDK pointed at the
    /// provider's endpoint for every other host — the same construction <c>AgentEval.Samples</c>' AIConfig uses.
    /// </summary>
    /// <exception cref="InvalidOperationException">The selection is not live.</exception>
    public IChatClient CreateChatClient()
    {
        if (!IsLive)
        {
            throw new InvalidOperationException(
                "No live provider is selected; the scripted offline model is in use. " +
                (Settings.Diagnostic ?? string.Empty));
        }

        if (Settings.UsesAzureProtocol)
        {
            // Azure OpenAI and a Foundry resource's OpenAI-compatible endpoint speak the same protocol.
            return new AzureOpenAIClient(Settings.Endpoint!, new AzureKeyCredential(Settings.ApiKey!))
                .GetChatClient(Model!)
                .AsIChatClient().WithMinimalReasoning();
        }

        // Bitdeer, OpenAI and any OpenAI-compatible host.
        var options = new OpenAIClientOptions { Endpoint = Settings.Endpoint };
        return new OpenAIClient(new ApiKeyCredential(Settings.ApiKey!), options)
            .GetChatClient(Model!)
            .AsIChatClient().WithMinimalReasoning();
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1].Trim();
            }
        }

        return null;
    }
}
