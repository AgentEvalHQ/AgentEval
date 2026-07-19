// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.MAF.CopilotStudio;

namespace AgentEval.Samples;

/// <summary>
/// Copilot Studio — Hello World (the 60-second version).
///
/// The simplest possible check against a live Microsoft Copilot Studio (MCS) agent: build the connector,
/// send ONE message, ONE fluent assertion (<c>HaveRespondedWithNonEmptyMessage</c>) on the real reply. This
/// is the on-ramp — sample 01 (Live Walkthrough) builds up conversation continuity + Gatekeeper composition,
/// and sample 02 (Budget + Red Team) adds credit-budget enforcement and red-team scanning on top of that.
///
/// 🔑 Requires a NON-PROD Copilot Studio agent + Entra app registration. Set BOTH:
///     COPILOTSTUDIO_CONFIG_PATH=&lt;path to a JSON file: environmentId/schemaName/tenantId/appClientId/[cloud]/[agentName]&gt;
///     COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true
/// ⚠️ This drives a REAL agent whose connectors/flows can fire REAL production actions and cannot be sandboxed —
///    confirm the config points at a non-prod agent before setting the consent variable.
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class CopilotStudioHelloWorld
{
    public static async Task RunAsync()
    {
        PrintHeader();

        var configPath = Environment.GetEnvironmentVariable("COPILOTSTUDIO_CONFIG_PATH");
        var understood = Environment.GetEnvironmentVariable("COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS") == "true";
        if (string.IsNullOrWhiteSpace(configPath) || !understood)
        {
            PrintMissingSetupWarning();
            return;
        }

        CopilotStudioConfig config;
        try
        {
            config = CopilotStudioConfig.Load(new FileInfo(configPath));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"   ❌ Config error: {ex.Message}");
            return;
        }

        Console.WriteLine($"   Agent: {config.DisplayName}");

        var agent = CopilotStudioAgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, maxCredits: 3);

        const string userMessage = "Hello! What can you help me with?";
        Console.WriteLine($"   Prompt: \"{userMessage}\"\n");

        var response = await agent.InvokeAsync(userMessage);
        Console.WriteLine($"   Reply: \"{Trunc(response.Text)}\"");

        Check(() => response.Text.Should().HaveRespondedWithNonEmptyMessage(), "HaveRespondedWithNonEmptyMessage()");

        Console.WriteLine("\n   -> Next: sample 01 (Live Walkthrough) adds conversation continuity + Gatekeeper composition.");
        Console.WriteLine("\n=== Copilot Studio Hello World Complete ===");
    }

    private static void Check(Action assertion, string label)
    {
        try
        {
            assertion();
            Console.WriteLine($"   [PASS] {label}");
        }
        catch (AgentEvalAssertionException ex)
        {
            Console.WriteLine($"   [FAIL] {label}");
            Console.WriteLine($"          {ex.Message.Split('\n')[0]}");
        }
    }

    private static string Trunc(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 80 ? s : s[..80] + "…";

    private static void PrintMissingSetupWarning()
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("   ⚠️  Skipped — this sample drives a REAL, LIVE Copilot Studio agent. Set BOTH:");
        Console.WriteLine("     COPILOTSTUDIO_CONFIG_PATH=<path to a JSON file with environmentId/schemaName/tenantId/appClientId>");
        Console.WriteLine("     COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true");
        Console.WriteLine("   Confirm the config points at a NON-PROD agent first — connectors/flows can fire REAL");
        Console.WriteLine("   production actions and cannot be sandboxed.");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🤖 COPILOT STUDIO — HELLO WORLD                                              ║
║   Build the live connector, send ONE message, ONE assertion — the on-ramp      ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
