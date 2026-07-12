// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary><c>agenteval gatekeeper list-gates</c> — discover the callable gates (table by default, or <c>--json</c>).</summary>
internal static class GatekeeperListGatesCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },   // "chat"/"tool", matching the table + verdict schema
    };

    public static Command Create()
    {
        var jsonOpt = new Option<bool>("--json") { Description = "Emit the registry as JSON instead of a table" };
        var phaseOpt = new Option<string>("--phase") { Description = "inspect | serve | all (default all)", DefaultValueFactory = _ => "all" };

        var cmd = new Command("list-gates", "List the Gatekeeper gates callable via the CLI.");
        cmd.Options.Add(jsonOpt);
        cmd.Options.Add(phaseOpt);
        cmd.SetAction((parse, ct) => Task.FromResult(Execute(parse.GetValue(jsonOpt), parse.GetValue(phaseOpt) ?? "all", Console.Out)));
        return cmd;
    }

    internal static int Execute(bool json, string phase, TextWriter outw)
    {
        var p = (phase ?? "all").Trim().ToLowerInvariant();
        if (p is not ("all" or "inspect" or "serve"))
        {
            Console.Error.WriteLine($"  Error: --phase must be inspect | serve | all (got '{phase}').");
            return ExitCodes.UsageError;
        }

        var gates = GateRegistry.All
            .Where(d => p is "all" || d.Phase == p)
            .ToList();

        if (json)
        {
            outw.WriteLine(JsonSerializer.Serialize(gates, JsonOpts));
            return ExitCodes.Success;
        }

        outw.WriteLine("  Gatekeeper gates");
        outw.WriteLine("  ─────────────────────────────────────────────────────────────────────────────");
        outw.WriteLine($"  {"ID",-32} {"KIND",-5} {"STATE",-15} {"MODEL",-6} {"SPANS",-7} PHASE");
        foreach (var d in gates)
        {
            outw.WriteLine($"  {d.Id,-32} {d.Kind.ToString().ToLowerInvariant(),-5} {d.StateClass,-15} {(d.NeedsModel ? "yes" : "no"),-6} {d.SpanPolicy,-7} {d.Phase}");
        }

        outw.WriteLine();
        outw.WriteLine($"  {gates.Count} gate(s). Deterministic gates need no credentials; judge gates need --model + calibration.");
        outw.WriteLine("  Payload: text gates {\"text\":\"…\"}; tool gates {\"tool\":\"…\",\"args\":{…},\"messages\":[…]}.");
        return ExitCodes.Success;
    }
}
