// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Builds the CLI's real model client from the environment, for whichever provider
/// <c>AI_INFERENCE_PROVIDER</c> selects (Azure OpenAI, Bitdeer, OpenAI, Azure AI Foundry or any
/// OpenAI-compatible endpoint; auto-detected when the selector is unset). Every path goes through
/// <see cref="ProviderChatClientFactory.TryCreate"/>, so the provider rules live in one place.
/// </summary>
/// <remarks>
/// <para>
/// The name predates provider selection, as does <c>--azure-from-env</c>, the old name of
/// <c>--from-env</c> (still accepted, see <see cref="FromEnvOption"/>): this class is not Azure-only any
/// more. With only the <c>AZURE_OPENAI_*</c> trio set, the resolver auto-detects Azure OpenAI and behaves
/// as before.
/// </para>
/// <para>
/// Two entry points. <see cref="TryBuildFromEnv(string, string?)"/> wraps the client in
/// <see cref="ChatClientAgentAdapter"/> as the agent under test when the caller passes
/// <c>--from-env</c> to a command that otherwise needs <c>--sut</c>, <c>--endpoint</c> or a supplied response
/// (for example <c>bench owasp</c>, <c>bench mitre</c>, <c>bench nist</c>, <c>bench perf</c>,
/// <c>bench eu-ai-act</c>). <see cref="TryBuildChatClientFromEnv"/> returns the raw client, for
/// <c>bench memory</c>, <c>bench longmemeval</c> and <c>bench typedmemeval</c>
/// and for the <c>log-file</c> utilities.
/// </para>
/// <para>
/// A selector naming a provider whose variables are missing fails closed with a diagnostic naming
/// them, and so does an unset selector when no provider is fully configured; neither case falls back
/// to a stand-in or to another provider. The agent built here is a plain chat model; an agent with its
/// own tools or memory is not described by environment variables and needs a program of its own.
/// </para>
/// </remarks>
internal static class AzureChatAgentFactory
{
    /// <summary>
    /// Attempts to build an <see cref="IEvaluableAgent"/> from the selected provider's env vars.
    /// </summary>
    /// <param name="subject">The subject identifier; passed as the agent's <c>Name</c>.</param>
    /// <param name="systemPrompt">
    /// Optional system prompt to seed every conversation. When null, the agent passes
    /// only the user prompt — useful for red-team scans where the target's posture is
    /// already encoded in the deployment.
    /// </param>
    /// <returns>
    /// A tuple of <c>(agent, exitCode)</c>. When successful, <c>agent</c> is non-null and
    /// <c>exitCode</c> is 0. On any failure (missing env vars, construction failure) the
    /// method writes a friendly error to <c>stderr</c>, returns a null agent, and sets
    /// <c>exitCode</c> to <see cref="ExitCodes.RuntimeError"/> (3).
    /// </returns>
    public static (IEvaluableAgent? Agent, int ExitCode) TryBuildFromEnv(
        string subject,
        string? systemPrompt = null)
    {
        var (agent, _, exitCode) = TryBuildFromEnvWithModel(subject, systemPrompt);
        return (agent, exitCode);
    }

    /// <summary>
    /// <see cref="TryBuildFromEnv(string, string?)"/>, also returning the model the provider resolved (for example
    /// the Azure deployment or the Bitdeer model id), so a caller that prices calls can look up the model it
    /// actually used.
    /// </summary>
    public static (IEvaluableAgent? Agent, string? Model, int ExitCode) TryBuildFromEnvWithModel(
        string subject,
        string? systemPrompt = null)
    {
        var (chatClient, model, diagnostic) = ProviderChatClientFactory.TryCreate("agent", generousTimeout: true);
        if (chatClient is null)
        {
            Console.Error.WriteLine(
                "✖ --from-env was passed but no inference provider is configured.\n" +
                $"  {diagnostic}\n" +
                "  Configure one and retry, or name another target (--sut, --endpoint/--model).");
            return (null, null, ExitCodes.RuntimeError);
        }

        IEvaluableAgent agent = new ChatClientAgentAdapter(
            chatClient,
            name: subject,
            systemPrompt: systemPrompt);
        Console.Error.WriteLine($"{ProviderChatClientFactory.Describe("agent", model!)} subject={subject}");
        return (agent, model, 0);
    }

    /// <summary>
    /// Attempts to build a raw <see cref="IChatClient"/> for the provider <c>AI_INFERENCE_PROVIDER</c>
    /// selects (or auto-detects). Used by <c>bench memory</c>, <c>bench longmemeval</c> and
    /// <c>bench typedmemeval</c>, which feed the one client to both the agent under test and the
    /// judge (and by <see cref="LogFileCommand"/>), rather than the pre-wrapped <see cref="IEvaluableAgent"/> from
    /// <see cref="TryBuildFromEnv(string, string?)"/>. The <c>AZURE_OPENAI_JUDGE_*</c> override is
    /// not consulted here. On failure it writes the diagnostic to <c>stderr</c> and returns
    /// <see cref="ExitCodes.RuntimeError"/> (3); the second tuple item is the resolved model or
    /// deployment name.
    /// </summary>
    public static (IChatClient? ChatClient, string? Deployment, int ExitCode) TryBuildChatClientFromEnv()
    {
        var (chatClient, model, diagnostic) = ProviderChatClientFactory.TryCreate("agent", generousTimeout: true);
        if (chatClient is null)
        {
            Console.Error.WriteLine(
                $"✖ No inference provider is configured. {diagnostic}\n" +
                "  This benchmark requires a real LLM and cannot fall back to a stub.");
            return (null, null, ExitCodes.RuntimeError);
        }

        return (chatClient, model, 0);
    }
}
