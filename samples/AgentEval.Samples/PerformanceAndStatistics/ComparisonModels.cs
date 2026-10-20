// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Providers;

namespace AgentEval.Samples;

/// <summary>
/// The models the comparison samples (D3, D4, D6) compare, and the stop when fewer than they need are configured.
/// On every provider but Azure the second and third model default to the primary, so with only the primary set a
/// comparison used to run one model against itself: D3 and D4 ranked a single model as the winner, D6 stopped without
/// recording why.
/// </summary>
internal static class ComparisonModels
{
    /// <summary>The distinct names among <paramref name="models"/>, in order; blanks dropped.</summary>
    public static IReadOnlyList<string> Distinct(params string?[] models) =>
        models.Where(m => !string.IsNullOrWhiteSpace(m))
              .Select(m => m!.Trim())
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>
    /// True when <paramref name="models"/> has at least <paramref name="needed"/> models. Otherwise prints what to set,
    /// records the sample as not measured and returns false; no model call has been made.
    /// </summary>
    public static bool Require(string sample, IReadOnlyList<string> models, int needed = 2)
    {
        if (models.Count >= needed)
            return true;

        var configured = models.Count == 0 ? "none" : string.Join(", ", models);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"   ⏭️  {sample} compares models and needs {needed} distinct ones; configured: {configured}.");
        Console.ResetColor();
        Console.WriteLine($"   Set {SecondAndThirdVariables()} to name more models on {AIConfig.ProviderName}.");
        if (AIConfig.Provider == InferenceProvider.Bitdeer)
            Console.WriteLine("   For example: BITDEER_MODEL_2=deepseek-ai/DeepSeek-V4-Flash BITDEER_MODEL_3=Qwen/Qwen3.8-27B");
        Console.WriteLine("   No model calls were made and no comparison was reported.\n");

        SampleOutcome.Skip($"{sample} needs {needed} distinct models and {models.Count} is configured ({configured}); " +
                           $"set {SecondAndThirdVariables()}");
        return false;
    }

    /// <summary>The variables that name the second and third model for the selected provider.</summary>
    public static string SecondAndThirdVariables()
    {
        var prefix = AIConfig.Provider switch
        {
            InferenceProvider.AzureOpenAI => "AZURE_OPENAI_DEPLOYMENT",
            InferenceProvider.Bitdeer => "BITDEER_MODEL",
            InferenceProvider.OpenAI => "OPENAI_MODEL",
            InferenceProvider.Foundry => "FOUNDRY_MODEL",
            InferenceProvider.OpenAICompatible => "OPENAI_COMPATIBLE_MODEL",
            _ => "<PROVIDER>_MODEL",
        };
        return $"{prefix}_2 (and {prefix}_3 for a third)";
    }
}
