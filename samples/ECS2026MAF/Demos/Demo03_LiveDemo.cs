// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo
using Azure.AI.OpenAI;
using ECS2026MAF.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Demos;

/// <summary>
/// Demo 03 — Live Demo (Scaffold)
///
/// Starting point for the live coding session at ECS 2026.
/// The four coding phases below map 1-to-1 to the completed reference in
/// <see cref="Demo03_LiveDemoComplete"/> — build them here, in order.
/// Tools: reuse from ECS2026MAF.Tools.TravelTools — no new code needed.
/// Reference: see Demo03_LiveDemoComplete for the fully-built version.
/// </summary>
public static class Demo03_LiveDemo
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        // ════════════════════════════════════════════════════════════════════
        // 1  —  Connect to Azure OpenAI
        // ════════════════════════════════════════════════════════════════════


        // ════════════════════════════════════════════════════════════════════
        // 2  —  Register travel tools
        // ════════════════════════════════════════════════════════════════════



        // ════════════════════════════════════════════════════════════════════
        // 3  —  Create the ChatClientAgent
        // ════════════════════════════════════════════════════════════════════



        // ════════════════════════════════════════════════════════════════════
        // 4  —  Interactive chat loop
        // ════════════════════════════════════════════════════════════════════



        await Task.CompletedTask;   // placeholder — replaced by real awaits as snippets are added
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 03 — Live Demo                                                        ║
║   🚧  To be built live at ECS 2026!                                          ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Demo 03 — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT
");
        Console.ResetColor();
    }
}

