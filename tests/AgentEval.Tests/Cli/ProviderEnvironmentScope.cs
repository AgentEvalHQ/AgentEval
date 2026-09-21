// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Tests.Cli;

/// <summary>
/// Clears every variable that can select or configure an inference provider, and restores them on dispose.
/// </summary>
/// <remarks>
/// <para>
/// A test that means "no provider is configured" has to clear <b>all</b> of them, not the three it happens
/// to know about. Before the CLI read <c>AI_INFERENCE_PROVIDER</c> it was enough to scrub <c>AZURE_OPENAI_*</c>;
/// now a developer machine or CI runner with <c>OPENAI_API_KEY</c> or <c>BITDEER_API_KEY</c> set would let the
/// resolver auto-detect a provider and the assertion would fail on the environment rather than on the code.
/// That is exactly what happened when this seam was introduced, on a machine with an ambient
/// <c>OPENAI_API_KEY</c>.
/// </para>
/// <para>
/// The list is deliberately explicit rather than a prefix match: a variable the resolver starts reading must be
/// added here on purpose, and a test that silently stopped controlling its environment is easier to spot.
/// </para>
/// </remarks>
internal sealed class ProviderEnvironmentScope : IDisposable
{
    /// <summary>Every variable <c>InferenceProviderEnvironment</c> reads, plus the CLI's judge and agent knobs.</summary>
    public static readonly string[] Variables =
    [
        "AI_INFERENCE_PROVIDER",
        "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_DEPLOYMENT",
        "AZURE_OPENAI_DEPLOYMENT_2", "AZURE_OPENAI_DEPLOYMENT_3",
        "AZURE_OPENAI_JUDGE_ENDPOINT", "AZURE_OPENAI_JUDGE_API_KEY", "AZURE_OPENAI_JUDGE_DEPLOYMENT",
        "BITDEER_API_KEY", "BITDEER_ENDPOINT", "BITDEER_MODEL", "BITDEER_MODEL_2", "BITDEER_MODEL_3",
        "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_MODEL", "OPENAI_MODEL_2", "OPENAI_MODEL_3",
        "FOUNDRY_ENDPOINT", "FOUNDRY_API_KEY", "FOUNDRY_MODEL", "FOUNDRY_MODEL_2", "FOUNDRY_MODEL_3",
        "OPENAI_COMPATIBLE_ENDPOINT", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_MODEL",
        "OPENAI_COMPATIBLE_MODEL_2", "OPENAI_COMPATIBLE_MODEL_3",
        "AGENTEVAL_ALLOW_STUB_JUDGE", "AGENTEVAL_AGENT_NETWORK_TIMEOUT_S",
    ];

    private readonly Dictionary<string, string?> _saved = [];

    /// <summary>Snapshots and clears every provider variable, then applies <paramref name="set"/>.</summary>
    /// <param name="set">Variables this test wants set, applied after the scrub.</param>
    public ProviderEnvironmentScope(params (string Name, string? Value)[] set)
    {
        foreach (var name in Variables)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var (name, value) in set ?? [])
        {
            if (!_saved.ContainsKey(name))
                _saved[name] = Environment.GetEnvironmentVariable(name);   // a variable outside the list: still restored
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
    }
}
