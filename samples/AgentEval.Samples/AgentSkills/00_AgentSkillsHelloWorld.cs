// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Agent Skills — Hello World (the 60-second version).
///
/// The simplest possible MAF Agent Skills check: a trivial in-memory skill — no SKILL.md, no directory,
/// nothing to set up (<c>AgentInlineSkill</c> is defined entirely in code) — a real Azure OpenAI agent
/// that loads it, and ONE fluent assertion (<c>HaveLoadedSkill</c>) on the real tool-call trace. This is
/// the on-ramp; the rest of group K builds up the disclosure-efficiency metric, the compliance scanner,
/// and the composite Skill Security Index on the shared expense-report fixture.
///
/// 🔑 Requires Azure OpenAI credentials (AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT).
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class AgentSkillsHelloWorld
{
    private const string SkillName = "haiku-writer";

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        var chatClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        // A trivial skill, defined entirely in code — no fixture, no directory, nothing to build. It has
        // no resources or scripts, so its instructions tell the model, explicitly, not to reach for
        // read_skill_resource/run_skill_script — otherwise a model will sometimes hallucinate a script
        // call against a skill that doesn't have one, which is honest-but-noisy for a Hello World.
        var skill = new AgentInlineSkill(
            SkillName,
            "Writes a short haiku (5-7-5 syllables) about any topic the user names. Use this skill " +
                "whenever the user asks for a haiku or a short poem.",
            "This skill has no resources or scripts — everything you need is in these instructions. " +
                "Write exactly one haiku in the classic 5-7-5 syllable structure about the requested " +
                "topic directly in your reply. Do not call read_skill_resource or run_skill_script.");

        // Auto-approve all three skill tools so a real run completes without a human-in-the-loop pause
        // — a Hello-World-scoped sample choice (documented, not a security claim). MAF requires approval
        // for all three by default; leaving read_skill_resource/run_skill_script gated would silently
        // stall the run on a ToolApprovalRequestContent the model emits after load_skill, even though
        // this trivial skill has no resources or scripts to read/run.
        var skillsOptions = new AgentSkillsProviderOptions
        {
            DisableLoadSkillApproval = true,
            DisableReadSkillResourceApproval = true,
            DisableRunSkillScriptApproval = true,
        };
        var skillsProvider = new AgentSkillsProvider([skill], skillsOptions);

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "PoetAssistant",
            AIContextProviders = [skillsProvider],
        });

        const string userMessage = "Write me a haiku about the ocean using your haiku-writer skill.";
        Console.WriteLine($"  Model: {AIConfig.ModelDeployment}");
        Console.WriteLine($"  Prompt: \"{userMessage}\"\n");

        var response = await agent.RunAsync(userMessage);
        var toolUsage = AgentSkillsSampleHelpers.ExtractToolUsage(response);
        AgentSkillsSampleHelpers.PrintTrace(toolUsage);

        AgentSkillsSampleHelpers.Check(() => toolUsage.Should().HaveLoadedSkill(SkillName), "HaveLoadedSkill(\"haiku-writer\")");

        Console.WriteLine($"\n  Agent said: {AgentSkillsSampleHelpers.Truncate(response.Text, 300)}");
        Console.WriteLine("\n  -> Next: Disclosure Efficiency scores the whole load->read->run funnel, not just one call.");
        Console.WriteLine("\n=== Agent Skills Hello World Complete ===");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🧩 AGENT SKILLS — HELLO WORLD                                                ║
║   Load a trivial skill, assert HaveLoadedSkill — the on-ramp                   ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
