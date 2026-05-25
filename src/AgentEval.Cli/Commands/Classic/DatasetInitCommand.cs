// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Ported from AgentEvalHQ/AgentEval.Cli v0.2.0-alpha during the v1.1 CLI consolidation.
//
// `init` conflict resolution (documented per the consolidation plan):
//   - External CLI's `init` scaffolds a sample evaluation DATASET file (agenteval.yaml / json)
//     used by the `eval` command.
//   - In-tree `init` initialises a `.agenteval/` WORKSPACE (solution.json + README + .gitignore)
//     used by the `bench` / `doctor` / `mc` commands.
//   - Decision: external semantics win the top-level `init` command name (documentation depends
//     on it, AgentEvalHQ release notes reference it, and the 14 InitCommandTests for the dataset
//     scaffolder are part of the released CI surface). The in-tree workspace initialiser remains
//     accessible as `init-workspace` (see Program.cs).
//   - This class is named DatasetInitCommand (not InitCommand) to coexist with the workspace
//     InitCommand in the same assembly without a CLR-type collision; tests reference it by class
//     name, the runtime command name stays `init`.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace AgentEval.Cli.Commands.Classic;

/// <summary>
/// The 'agenteval init' command — scaffold a starter evaluation dataset.
/// Preserves the AgentEvalHQ/AgentEval.Cli v0.2.0-alpha shape (option set, YAML/JSON templates,
/// exit codes) so downstream documentation and tests keep working unchanged.
/// </summary>
internal static class DatasetInitCommand
{
    public static Command Create()
    {
        var command = new Command("init", "Initialize a starter evaluation dataset in the current directory");

        var formatOpt = new Option<string>("--format")
            { DefaultValueFactory = _ => "yaml", Description = "Output format: yaml or json" };

        var outputOpt = new Option<string?>("-o", "--output")
            { Description = "Output file path (default: agenteval.{format})" };

        var forceFlag = new Option<bool>("--force")
            { Description = "Overwrite existing file" };

        command.Options.Add(formatOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(forceFlag);

        command.SetAction(async (parseResult, ct) =>
        {
            var format = parseResult.GetValue(formatOpt)!;
            var output = parseResult.GetValue(outputOpt);
            var force = parseResult.GetValue(forceFlag);

            return await ExecuteAsync(format, output, force);
        });

        return command;
    }

    /// <summary>
    /// Core execution logic — separated from command wiring for testability.
    /// </summary>
    internal static async Task<int> ExecuteAsync(string format, string? output, bool force)
    {
        var normalizedFormat = format.ToLowerInvariant();
        if (normalizedFormat is not ("yaml" or "json"))
        {
            Console.Error.WriteLine($"Error: Unsupported format '{format}'. Use 'yaml' or 'json'.");
            return ExitCodes.UsageError;
        }

        var fileName = output ?? $"agenteval.{normalizedFormat}";

        if (File.Exists(fileName) && !force)
        {
            Console.Error.WriteLine($"Error: {fileName} already exists. Use --force to overwrite.");
            return ExitCodes.UsageError;
        }

        var template = normalizedFormat switch
        {
            "yaml" => GetYamlTemplate(),
            "json" => GetJsonTemplate(),
            _ => throw new InvalidOperationException($"Unsupported format: {format}")
        };

        // Ensure parent directory exists
        var dir = Path.GetDirectoryName(Path.GetFullPath(fileName));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(fileName, template);
        Console.Error.WriteLine($"  Created {fileName} with sample test cases.");
        Console.Error.WriteLine($"  Edit the file and run: agenteval eval --endpoint <url> --model <model> --dataset {fileName}");
        return ExitCodes.Success;
    }

    private static string GetYamlTemplate() => """
        # AgentEval Evaluation Dataset
        # Documentation: https://agenteval.dev/docs/getting-started
        #
        # Each test case has:
        #   - id: Unique test identifier
        #   - input: The prompt sent to the agent
        #   - expected: (optional) Expected response for comparison
        #   - context: (optional) Retrieved context for RAG evaluation
        #   - groundTruth: (optional) Ground truth for faithfulness metrics
        #   - tags: (optional) Tags for filtering and grouping

        examples:
          - id: greeting_test
            input: "Hello, how are you?"
            expected: "A friendly greeting response"
            tags: [basic, greeting]

          - id: knowledge_test
            input: "What is the capital of France?"
            expected: "Paris"
            context:
              - "France is a country in Western Europe. Its capital is Paris."
            groundTruth: "The capital of France is Paris."
            tags: [knowledge, geography]

          - id: reasoning_test
            input: "If a train travels 60mph for 2 hours, how far does it go?"
            expected: "120 miles"
            tags: [reasoning, math]
        """;

    private static string GetJsonTemplate() => """
        {
          "examples": [
            {
              "id": "greeting_test",
              "input": "Hello, how are you?",
              "expected": "A friendly greeting response",
              "tags": ["basic", "greeting"]
            },
            {
              "id": "knowledge_test",
              "input": "What is the capital of France?",
              "expected": "Paris",
              "context": ["France is a country in Western Europe. Its capital is Paris."],
              "groundTruth": "The capital of France is Paris.",
              "tags": ["knowledge", "geography"]
            },
            {
              "id": "reasoning_test",
              "input": "If a train travels 60mph for 2 hours, how far does it go?",
              "expected": "120 miles",
              "tags": ["reasoning", "math"]
            }
          ]
        }
        """;
}
