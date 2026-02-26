// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Extension methods to convert <see cref="DatasetTestCase"/> instances loaded from
/// JSONL/JSON/YAML/CSV files into benchmark-specific test case types.
/// </summary>
/// <remarks>
/// <para>
/// These extensions bridge the dataset persistence model to the benchmark execution models,
/// enabling JSONL-based benchmark datasets (industry standard) with AgentEval's benchmark classes.
/// </para>
/// <para>See <see cref="DatasetTestCaseExtensions"/> for general test case conversions.</para>
/// </remarks>
public static class DatasetTestCaseBenchmarkExtensions
{
    /// <summary>
    /// Converts a <see cref="DatasetTestCase"/> to a <see cref="ToolAccuracyTestCase"/>
    /// for use with <see cref="AgenticBenchmark.RunToolAccuracyBenchmarkAsync"/>.
    /// </summary>
    /// <remarks>
    /// Maps:
    /// <list type="bullet">
    ///   <item><c>Id</c> → <c>Name</c></item>
    ///   <item><c>Input</c> → <c>Prompt</c></item>
    ///   <item><c>ExpectedTools</c> → <c>ExpectedTools</c> (as <see cref="ExpectedTool"/> with name only)</item>
    ///   <item><c>Metadata["required_params"]</c> → <c>ExpectedTool.RequiredParameters</c> (optional)</item>
    /// </list>
    /// </remarks>
    /// <param name="datasetCase">The dataset test case to convert.</param>
    /// <param name="allowExtraTools">Whether extra tool calls are allowed. Default: <see langword="true"/>.</param>
    /// <returns>A <see cref="ToolAccuracyTestCase"/> ready for benchmark execution.</returns>
    public static ToolAccuracyTestCase ToToolAccuracyTestCase(
        this DatasetTestCase datasetCase,
        bool allowExtraTools = true)
    {
        ArgumentNullException.ThrowIfNull(datasetCase);

        var expectedTools = new List<ExpectedTool>();

        if (datasetCase.ExpectedTools is { Count: > 0 })
        {
            // Check for required_params in metadata
            Dictionary<string, List<string>>? requiredParams = null;
            if (datasetCase.Metadata.TryGetValue("required_params", out var paramsObj)
                && paramsObj is System.Text.Json.JsonElement jsonEl
                && jsonEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                requiredParams = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in jsonEl.EnumerateObject())
                {
                    var paramList = new List<string>();
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            paramList.Add(item.GetString() ?? "");
                        }
                    }
                    requiredParams[prop.Name] = paramList;
                }
            }

            foreach (var toolName in datasetCase.ExpectedTools)
            {
                // ExpectedTool uses init-only properties, so RequiredParameters
                // must be set in the object initializer (not after construction).
                List<string>? rp = null;
                requiredParams?.TryGetValue(toolName, out rp);

                expectedTools.Add(new ExpectedTool
                {
                    Name = toolName,
                    RequiredParameters = rp ?? []
                });
            }
        }

        return new ToolAccuracyTestCase
        {
            Name = datasetCase.Id,
            Prompt = datasetCase.Input,
            ExpectedTools = expectedTools,
            AllowExtraTools = allowExtraTools
        };
    }

    /// <summary>
    /// Converts a <see cref="DatasetTestCase"/> to a <see cref="TaskCompletionTestCase"/>
    /// for use with <see cref="AgenticBenchmark.RunTaskCompletionBenchmarkAsync"/>.
    /// </summary>
    /// <remarks>
    /// Maps:
    /// <list type="bullet">
    ///   <item><c>Id</c> → <c>Name</c></item>
    ///   <item><c>Input</c> → <c>Prompt</c></item>
    ///   <item><c>EvaluationCriteria</c> → <c>CompletionCriteria</c></item>
    ///   <item><c>PassingScore</c> → <c>PassingScore</c> (defaults to <see cref="EvaluationDefaults.DefaultPassingScore"/>)</item>
    /// </list>
    /// </remarks>
    /// <param name="datasetCase">The dataset test case to convert.</param>
    /// <returns>A <see cref="TaskCompletionTestCase"/> ready for benchmark execution.</returns>
    public static TaskCompletionTestCase ToTaskCompletionTestCase(this DatasetTestCase datasetCase)
    {
        ArgumentNullException.ThrowIfNull(datasetCase);

        return new TaskCompletionTestCase
        {
            Name = datasetCase.Id,
            Prompt = datasetCase.Input,
            CompletionCriteria = datasetCase.EvaluationCriteria?.ToList() ?? [],
            PassingScore = datasetCase.PassingScore ?? EvaluationDefaults.DefaultPassingScore
        };
    }
}
