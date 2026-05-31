// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.MAF.Tracing;
using AgentEval.Tracing;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Samples;

/// <summary>
/// Glass Box — Real vs Framework (single travel agent). Runs a REAL MAF <see cref="ChatClientAgent"/> against
/// Azure OpenAI ONCE, captured two ways from the same run: the framework's own account (MAF
/// <c>AgentResponse</c>) and the Glass Box chat/tool-boundary trace. Renders the honest MAF-vs-Glass Box diff
/// table — proving the framework does not lie about totals, but HIDES the per-turn detail an auditor needs.
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials (AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT). One run ≈ a few
/// thousand tokens. With no credentials the sample skips cleanly — it never substitutes scripted data, because
/// a comparison built on a fake run would be exactly the hollowness this sample exists to replace.
/// </remarks>
public static class RealVsFrameworkTravelAgent
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Glass Box — Real vs Framework: Travel Agent ===\n");
        Console.WriteLine("  Runs ONE real MAF agent and shows what the framework reports vs what actually happened.\n");

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("  This sample runs a REAL agent — it will not fake a run. Set the variables above and re-run.\n");
            return;
        }

        // ── Build the SAME agent the framework would, but instrument it: the recorder wraps the raw model
        //    (the MAF runtime adds its tool loop on top → recorder sits INNER of the loop, one entry per
        //    round-trip), and every tool is wrapped to capture its execution.
        var chatTrace = new AgentTrace { TraceName = "travel-agent" };
        var rawModel = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();
        var recordingClient = rawModel.AsBuilder()
            .UseTraceRecording("TravelAgent", chatTrace, SamplePreset.AuditGrade)
            .Build();

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(TravelToolbox.GetDestinationInfo).WithEvaluation(chatTrace),
            AIFunctionFactory.Create(TravelToolbox.SearchFlights).WithEvaluation(chatTrace),
            AIFunctionFactory.Create(TravelToolbox.BookFlight).WithEvaluation(chatTrace),
            AIFunctionFactory.Create(TravelToolbox.SearchHotel).WithEvaluation(chatTrace),
            AIFunctionFactory.Create(TravelToolbox.BookHotel).WithEvaluation(chatTrace),
        };

        var agent = new ChatClientAgent(recordingClient, new ChatClientAgentOptions
        {
            Name = "TravelAgent",
            Description = "A travel agent that researches a destination and books flights and a hotel.",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    "You are a travel agent. Handle the whole trip in one turn without asking the user anything: " +
                    "(1) call GetDestinationInfo for the destination, (2) SearchFlights then BookFlight for the " +
                    "outbound AND the return leg, (3) SearchHotel then BookHotel for the stay, (4) finish with a " +
                    "short itinerary listing every confirmation code and a cost total. Invent sensible defaults; " +
                    "never ask questions.",
                Tools = tools,
            },
        });

        Console.WriteLine($"  Model    : {AIConfig.ModelDeployment}");
        Console.WriteLine($"  Request  : \"{TravelToolbox.CanonicalRequest}\"\n");
        Console.WriteLine("  ⏳ Running the real agent (this calls Azure; ~20–40s)...\n");

        AgentResponse response;
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            var session = await agent.CreateSessionAsync();
            using (new ToolCorrelationScope("travel-agent-run"))
            {
                response = await agent.RunAsync(new[] { new ChatMessage(ChatRole.User, TravelToolbox.CanonicalRequest) }, session);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Agent run failed ({ex.GetType().Name}): {ex.Message}");
            Console.WriteLine("  (Azure throttling/timeout or a tool error — the comparison needs a completed run.)");
            Console.ResetColor();
            return;
        }
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        // ── The framework's own account (the "MAF" lens) ──
        var mafToolCalls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count();
        var maf = new GlassBoxComparison.MafLens(
            (int?)response.Usage?.InputTokenCount,
            (int?)response.Usage?.OutputTokenCount,
            mafToolCalls,
            response.FinishReason?.ToString());

        // ── Reconcile the framework's account against the chat boundary (honest fidelity score) ──
        var agentBoundary = AgentBoundaryTraceBuilder.FromAgentResponse(
            response.Messages, response.Usage, response.FinishReason?.ToString(), "TravelAgent", (long)elapsed.TotalMilliseconds);
        var fidelity = new TraceFidelityRunner().Reconcile(agentBoundary, chatTrace);

        // ── Render ──
        var rows = GlassBoxComparison.BuildRows(chatTrace, maf);
        GlassBoxComparison.RenderConsole("Travel Agent", rows, fidelity, chatTrace);

        Console.WriteLine();
        Console.WriteLine(GlassBoxComparison.RenderMarkdown("Travel Agent", rows, fidelity));
        Console.WriteLine("  Same run, two lenses. MAF's totals are honest; Glass Box recovers the per-turn ledger it discards.");
    }
}
