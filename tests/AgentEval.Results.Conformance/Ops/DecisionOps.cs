// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Conformance.Ops;

/// <summary><c>decide</c>: the checkpoint decision function (spec 05 §5.4, the <c>decision</c> vectors).</summary>
internal static class DecisionOps
{
    private const string Input = "decision#/$defs/input";

    /// <summary>
    /// <c>decide FILE</c>: FILE holds a decision input, or a decision vector whose <c>input</c> is one. Writes
    /// <c>{"output": {"outcome", "lanes", "reasons"}}</c>, or <c>{"error": why}</c> when the function refuses the input
    /// ([DEC-1]: an input the reader schema refuses is not a decision input, and is refused too). Exit code 0 either
    /// way.
    /// </summary>
    public static int Decide(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 1, 1, "decide FILE");
        var file = DriverIO.Document(args[0]);

        // A decision input has subjectVersion; a vector holds one under "input".
        var input = file["input"] is JsonObject inner && !file.ContainsKey("subjectVersion") ? inner : file;
        if (AefSchemas.Reader.Validate(Input, input) is { } invalid)
        {
            return DriverIO.Print(stdout, new JsonObject { ["error"] = $"not a decision input: {invalid}" });
        }

        try
        {
            var output = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(input)));
            return DriverIO.Print(stdout, new JsonObject { ["output"] = output });
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return DriverIO.Print(stdout, new JsonObject { ["error"] = e.Message });
        }
    }
}
