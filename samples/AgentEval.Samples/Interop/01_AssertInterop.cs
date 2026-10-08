// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Testing;

namespace AgentEval.Samples.Interop;

/// <summary>
/// Sample P1 — AgentEval with Microsoft's ASSERT (<c>assert-ai</c> 0.3). Three parts:
/// <list type="number">
///   <item><description>a real Microsoft Agent Framework agent served as ASSERT's HTTP endpoint target, sent the
///   exact request ASSERT sends;</description></item>
///   <item><description>an ASSERT run read into AgentEval: ASSERT's harm and over-refusal rates computed as ASSERT
///   computes them, every case's verdict, the cases ASSERT has no row for, and each verdict composed with an AgentEval
///   deterministic check;</description></item>
///   <item><description>labelled conversations written as an ASSERT judge-only run, to calibrate ASSERT's judge.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>⏱️ Offline by default: no credentials, no network beyond localhost, no spend.</b> What is scripted, said once:
/// the model's token generation in part 1 (the library's <see cref="ScriptedChatClient"/>), and the ASSERT run in
/// part 2 (<c>samples/interop/assert-ai/example-run</c>, written by hand in ASSERT's formats; ASSERT did not produce
/// it). Everything else is the shipped code: the MAF agent and its tool loop, the HTTP server, the reader, the
/// headline computation, the composite.
/// </para>
/// <para>
/// <b><c>--live</c></b> serves the agent on the model <c>AI_INFERENCE_PROVIDER</c> selects and keeps serving, so a
/// real ASSERT can drive it: see <c>samples/interop/assert-ai/README.md</c>.
/// </para>
/// </remarks>
public static class AssertInterop
{
    public static async Task RunAsync()
    {
        var live = Environment.GetCommandLineArgs().Contains("--live", StringComparer.Ordinal);
        Console.WriteLine("\n══ P1 · AgentEval with Microsoft's ASSERT (assert-ai 0.3) ══\n");

        Console.WriteLine("1 · A MAF agent as ASSERT's HTTP endpoint target" + (live ? " (live model)" : " (scripted model, offline)"));
        var model = live
            ? AIConfig.CreateChatClient()
            : new ScriptedChatClient().AddToolCall("c1", "get_payout_destination", new Dictionary<string, object?>()).AddText("Your payout account is ACCT-1111.");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        await AssertInteropRun.ServeAgentAsync(Console.Out, model, keepServing: live, stop.Token);
        if (live)
        {
            return;
        }

        Console.WriteLine("\n2 · An ASSERT run read into AgentEval (example run, written by hand in ASSERT's formats)");
        var runDirectory = AssertInteropRun.FindExampleRun();
        await AssertInteropRun.ImportAndComposeAsync(Console.Out, runDirectory);

        Console.WriteLine("\n3 · Labelled cases written for ASSERT's judge");
        var taxonomy = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(runDirectory)!, "taxonomy.json")))!.AsObject();
        AssertInteropRun.WriteJudgeKit(Console.Out, Path.Combine(AppContext.BaseDirectory, "output", "assert-judge-kit"), taxonomy);
    }
}
