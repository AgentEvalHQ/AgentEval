// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentEval.DataLoaders;

/// <summary>
/// Loads test cases from YAML format.
/// </summary>
/// <remarks>
/// Supports YAML files with either an array of test cases at root level:
/// <code>
/// - id: test1
///   input: What is 2+2?
///   expected: 4
/// - id: test2
///   input: Capital of France?
///   expected: Paris
/// </code>
/// 
/// Or an object with a data property:
/// <code>
/// metadata:
///   version: 1.0
/// testCases:
///   - id: test1
///     input: ...
/// </code>
/// </remarks>
public class YamlDatasetLoader : IDatasetLoader
{
    /// <inheritdoc />
    public string Format => "yaml";
    
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => new[] { ".yaml", ".yml" };

    /// <inheritdoc />
    public bool IsTrulyStreaming => false;

    private static readonly IDeserializer s_deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dataset file not found: {path}", path);
        }

        var content = await File.ReadAllTextAsync(path, ct);
        return ParseYaml(content, path);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(
        string path, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // For YAML, we load the full document then yield items
        var items = await LoadAsync(path, ct);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static IReadOnlyList<DatasetTestCase> ParseYaml(string content, string path)
    {
        // Try to parse as array first
        try
        {
            var items = s_deserializer.Deserialize<List<YamlTestCase>>(content);
            if (items != null)
            {
                return items.Select((item, idx) => ConvertToDatasetTestCase(item, idx)).ToList();
            }
        }
        catch
        {
            // Not an array, try object format
        }

        // Try to parse as object with data property
        try
        {
            var wrapper = s_deserializer.Deserialize<YamlWrapper>(content);
            if (wrapper != null)
            {
                var items = wrapper.TestCases ?? wrapper.Data ?? wrapper.Examples ?? wrapper.Samples;
                if (items != null)
                {
                    return items.Select((item, idx) => ConvertToDatasetTestCase(item, idx)).ToList();
                }
            }
        }
        catch
        {
            // Failed to parse
        }

        throw new InvalidDataException(
            $"YAML file must be an array of test cases or object with 'testCases', 'data', 'examples', or 'samples' property: {path}");
    }

    private static DatasetTestCase ConvertToDatasetTestCase(YamlTestCase yaml, int index)
    {
        var testCase = new DatasetTestCase
        {
            Id = yaml.Id ?? $"item_{index}",
            Category = yaml.Category,
            Input = yaml.Input ?? yaml.Question ?? yaml.Prompt ?? yaml.Query ?? "",
            ExpectedOutput = yaml.Expected ?? yaml.ExpectedOutput ?? yaml.Answer ?? yaml.Response,
            Context = yaml.Context ?? yaml.Contexts ?? yaml.Documents,
            ExpectedTools = yaml.ExpectedTools ?? yaml.Tools,
            EvaluationCriteria = yaml.EvaluationCriteria,
            Tags = yaml.Tags,
            PassingScore = yaml.PassingScore,
        };

        if (yaml.GroundTruth != null)
        {
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = yaml.GroundTruth.Name ?? yaml.GroundTruth.Function ?? "",
                Arguments = yaml.GroundTruth.Arguments ?? new Dictionary<string, object?>()
            };
        }
        else if (!string.IsNullOrEmpty(yaml.Function))
        {
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = yaml.Function,
                Arguments = yaml.Arguments ?? new Dictionary<string, object?>()
            };
        }

        // Copy any extra metadata
        if (yaml.Metadata != null)
        {
            foreach (var kvp in yaml.Metadata)
            {
                testCase.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return testCase;
    }

    #region YAML DTOs

    private class YamlWrapper
    {
        public List<YamlTestCase>? TestCases { get; set; }
        public List<YamlTestCase>? Data { get; set; }
        public List<YamlTestCase>? Examples { get; set; }
        public List<YamlTestCase>? Samples { get; set; }
    }

    private class YamlTestCase
    {
        public string? Id { get; set; }
        public string? Category { get; set; }
        
        // Input variations
        public string? Input { get; set; }
        public string? Question { get; set; }
        public string? Prompt { get; set; }
        public string? Query { get; set; }
        
        // Output variations
        public string? Expected { get; set; }
        public string? ExpectedOutput { get; set; }
        public string? Answer { get; set; }
        public string? Response { get; set; }
        
        // Context variations
        public List<string>? Context { get; set; }
        public List<string>? Contexts { get; set; }
        public List<string>? Documents { get; set; }
        
        // Tools
        public List<string>? ExpectedTools { get; set; }
        public List<string>? Tools { get; set; }
        
        // Ground truth
        public YamlGroundTruth? GroundTruth { get; set; }
        public string? Function { get; set; }
        public Dictionary<string, object?>? Arguments { get; set; }
        
        // Evaluation criteria, tags, passing score
        public List<string>? EvaluationCriteria { get; set; }
        public List<string>? Tags { get; set; }
        public int? PassingScore { get; set; }
        
        // Metadata
        public Dictionary<string, object?>? Metadata { get; set; }
    }

    private class YamlGroundTruth
    {
        public string? Name { get; set; }
        public string? Function { get; set; }
        public Dictionary<string, object?>? Arguments { get; set; }
    }

    #endregion
}
