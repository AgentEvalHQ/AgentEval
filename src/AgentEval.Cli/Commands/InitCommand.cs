// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The 'init' command - creates a starter configuration file.
/// </summary>
public static class InitCommand
{
    public static Command Create()
    {
        var outputOption = new Option<FileInfo>(
            ["--output", "-o"],
            () => new FileInfo("agenteval.json"),
            "Output path for the configuration file");

        var formatOption = new Option<string>(
            ["--format", "-f"],
            () => "json",
            "Configuration format: json or yaml");

        var command = new Command("init", "Create a starter evaluation configuration")
        {
            outputOption,
            formatOption
        };

        command.SetHandler(async (output, format) =>
        {
            await CreateConfigAsync(output, format);
        }, outputOption, formatOption);

        return command;
    }

    private static async Task CreateConfigAsync(FileInfo output, string format)
    {
        Console.WriteLine($"Creating AgentEval configuration: {output.FullName}");

        var config = format.ToLowerInvariant() switch
        {
            "yaml" or "yml" => GetYamlTemplate(),
            _ => GetJsonTemplate()
        };

        await File.WriteAllTextAsync(output.FullName, config);
        
        Console.WriteLine("✅ Configuration created!");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine("  1. Edit the configuration with your test cases");
        Console.WriteLine("  2. Run: agenteval eval --config " + output.Name);
    }

    private static string GetJsonTemplate() => """
        {
          "$schema": "https://raw.githubusercontent.com/joslat/AgentEval/main/schemas/agenteval-config.json",
          "name": "My Agent Evaluation",
          "description": "Evaluation suite for my AI agent",
          "agent": {
            "type": "http",
            "endpoint": "http://localhost:5000/api/agent",
            "timeout": 30000
          },
          "defaults": {
            "passThreshold": 70,
            "maxRetries": 3
          },
          "testCases": [
            {
              "name": "BasicGreeting",
              "input": "Hello, how are you?",
              "assertions": [
                { "type": "contains", "value": "hello" },
                { "type": "not-contains", "value": "error" }
              ]
            },
            {
              "name": "ToolUsage",
              "input": "What's the weather in Seattle?",
              "expectedTools": ["get_weather"],
              "assertions": [
                { "type": "tool-called", "tool": "get_weather" },
                { "type": "contains", "value": "seattle" }
              ]
            }
          ]
        }
        """;

    private static string GetYamlTemplate() => """
        # AgentEval Configuration
        name: My Agent Evaluation
        description: Evaluation suite for my AI agent
        
        agent:
          type: http
          endpoint: http://localhost:5000/api/agent
          timeout: 30000
        
        defaults:
          passThreshold: 70
          maxRetries: 3
        
        testCases:
          - name: BasicGreeting
            input: "Hello, how are you?"
            assertions:
              - type: contains
                value: hello
              - type: not-contains
                value: error
        
          - name: ToolUsage
            input: "What's the weather in Seattle?"
            expectedTools:
              - get_weather
            assertions:
              - type: tool-called
                tool: get_weather
              - type: contains
                value: seattle
        """;
}
