// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Providers;

namespace AgentEval.Samples;

/// <summary>Configured sample judge selection and the identity recorded beside a subject.</summary>
internal static class SampleJudgeModel
{
    public static string Resolve(InferenceProviderSettings settings, Func<string, string?> environment)
    {
        var models = InferenceProviderEnvironment.NamedModels(settings, environment);
        if (models.Count == 0)
            throw new InvalidOperationException("Configure a chat provider before selecting the sample judge.");
        return models.FirstOrDefault(model => !string.Equals(model, models[0], StringComparison.OrdinalIgnoreCase))
            ?? models[0];
    }

    public static string DescribeFor(string subject, string judge) =>
        $"{judge} ({(string.Equals(subject, judge, StringComparison.OrdinalIgnoreCase) ? "self-judged" : "different model identifier")})";

    public static IReadOnlyDictionary<string, string> BaselineConfiguration(string judge, string provider) =>
        new Dictionary<string, string> { ["judgeModel"] = judge, ["judgeProvider"] = provider };
}
