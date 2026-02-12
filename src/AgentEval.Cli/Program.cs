// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using AgentEval.Cli.Commands;

namespace AgentEval.Cli;

/// <summary>
/// AgentEval CLI - Run AI agent evaluations from the command line.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Ensure Unicode characters (emoji, box-drawing, etc.) render correctly on Windows
        Console.OutputEncoding = Encoding.UTF8;

        var rootCommand = new RootCommand("AgentEval - AI agent testing and evaluation toolkit")
        {
            EvalCommand.Create(),
            InitCommand.Create(),
            ListCommand.Create(),
        };

        rootCommand.AddGlobalOption(new Option<bool>(
            ["--verbose", "-v"],
            "Enable verbose output"));

        return await rootCommand.InvokeAsync(args);
    }
}
