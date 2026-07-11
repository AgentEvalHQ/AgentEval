// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// The <c>agenteval gatekeeper</c> verb group — invoke Gatekeeper gates from any language via a versioned verdict
/// JSON (the language-neutral interop bridge). Slice 1 ships <c>list-gates</c> and the stateless deterministic
/// <c>inspect</c> (credential-free, byte-stable — the CI core). Judge gates + <c>calibrate</c> (the honesty guard)
/// arrive on the model path; the stateful accumulator gates arrive via <c>serve</c> (deferred).
/// </summary>
internal static class GatekeeperCommand
{
    public static Command Create()
    {
        var cmd = new Command("gatekeeper", "Invoke Gatekeeper gates from the CLI — language-neutral policy verdicts.");
        cmd.Subcommands.Add(GatekeeperListGatesCommand.Create());
        cmd.Subcommands.Add(GatekeeperInspectCommand.Create());
        cmd.Subcommands.Add(CreateServeStub());
        return cmd;
    }

    // The stateful daemon (accumulator gates over held run-state) is a designed but deferred follow-up (design §14).
    private static Command CreateServeStub()
    {
        var cmd = new Command("serve", "Stateful gate daemon (stdio/http) — DEFERRED (holds run-state for accumulator gates).");
        cmd.SetAction((_, _) =>
        {
            Console.Error.WriteLine("  'gatekeeper serve' is not yet implemented — a deferred follow-up for the stateful");
            Console.Error.WriteLine("  accumulator gates (run-budget, sequence). Stateless 'inspect' covers text + flow-control gates today.");
            return Task.FromResult(ExitCodes.RuntimeError);
        });
        return cmd;
    }
}
