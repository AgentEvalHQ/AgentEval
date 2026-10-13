// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using AgentEval.Results;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>Records an explicit, self-attested review of a sealed run without changing its results.</summary>
internal static class DecideCommand
{
    internal static Command Create()
    {
        var directory = new Argument<string>("run-dir") { Description = "A verified, sealed AEF run folder." };
        var kind = new Option<string>("--kind") { Required = true, Description = "Decision: approve, reject, waive, override or adjudicate. Does not change sealed results or gates." };
        var identity = new Option<string>("--by") { Required = true, Description = "Your stable opaque identity; recorded as self-attested, not authenticated." };
        var reason = new Option<string>("--reason") { Required = true, Description = "Why you are recording this decision." };
        var json = new Option<bool>("--json") { Description = "Print the event and sealed batch identifiers as JSON." };
        var result = new Option<string?>("--result") { Description = "Review this result ID instead of the whole run." };
        var requirement = new Option<string?>("--requirement") { Description = "External requirement ID to waive; use instead of --result." };
        var expires = new Option<string?>("--expires") { Description = "Required for waive: future RFC 3339 UTC time ending in Z." };
        var state = new Option<string?>("--state") { Description = "Required for override/adjudicate: effective AEF state (pending is not allowed)." };
        var command = new Command("decide", "Append and seal a human decision on an AEF run.");
        command.Add(directory);
        foreach (var option in new Option[] { kind, identity, reason, json, result, requirement, expires, state }) command.Add(option);
        command.SetAction((ParseResult parsed, CancellationToken _) => Task.FromResult(Run(
            parsed.GetValue(directory)!, parsed.GetValue(kind)!, parsed.GetValue(identity)!, parsed.GetValue(reason)!,
            parsed.GetValue(json), Console.Out, Console.Error, result: parsed.GetValue(result),
            requirement: parsed.GetValue(requirement), expires: parsed.GetValue(expires), state: parsed.GetValue(state))));
        return command;
    }

    internal static int Run(string directory, string kind, string identity, string reason, bool json,
        TextWriter stdout, TextWriter stderr, TimeProvider? clock = null, string? result = null,
        string? requirement = null, string? expires = null, string? state = null)
    {
        try
        {
            var decision = kind switch
            {
                "approve" => AefOverlayKind.Approve,
                "reject" => AefOverlayKind.Reject,
                "waive" => AefOverlayKind.Waive,
                "override" => AefOverlayKind.Override,
                "adjudicate" => AefOverlayKind.Adjudicate,
                _ => throw new ArgumentException("Expected --kind approve, reject, waive, override or adjudicate. Other decision kinds are not supported."),
            };
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (result is not null) ArgumentException.ThrowIfNullOrWhiteSpace(result);
            if (requirement is not null) ArgumentException.ThrowIfNullOrWhiteSpace(requirement);
            var at = AefTime.FromDateTimeOffset((clock ?? TimeProvider.System).GetUtcNow());
            AefTime? until = null;
            if (decision == AefOverlayKind.Waive)
            {
                if ((result is null) == (requirement is null))
                    throw new ArgumentException("Expected exactly one waiver target: --result or --requirement.");
                ArgumentException.ThrowIfNullOrWhiteSpace(expires);
                until = AefTime.Parse(expires);
                if (until.Value <= at) throw new ArgumentException("Expected --expires later than the decision time.");
            }
            else if (requirement is not null || expires is not null)
                throw new ArgumentException("--requirement and --expires are only valid for --kind waive.");
            AefState? effectiveState = null;
            if (decision is AefOverlayKind.Override or AefOverlayKind.Adjudicate)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(result);
                if (!AefNames.TryParse(state, out effectiveState) || effectiveState == AefState.Pending)
                    throw new ArgumentException("Expected --state to name a non-pending AEF state for override/adjudicate.");
            }
            else if (state is not null)
                throw new ArgumentException("--state is only valid for --kind override or adjudicate.");
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The AEF run folder does not exist.");
            var verification = AefRunVerifier.Verify(directory);
            if (verification.Outcome != AefOutcome.Intact)
            {
                stderr.WriteLine("Decision refused: expected an intact, sealed AEF run. Verify or seal the run first.");
                return ExitCodes.TestFailure;
            }

            var writer = AefOverlayWriter.Open(directory);
            // Do not silently claim earlier events in this reviewer's batch.
            if (writer.UnsealedAtOpen != 0 || writer.Batches >= AefOverlayWriter.MaxBatches)
            {
                stderr.WriteLine("Decision refused: expected no unsealed overlay events and room for another batch. Inspect the overlay journal first.");
                return ExitCodes.TestFailure;
            }
            var eventId = writer.Append(new AefOverlayEvent
            {
                Kind = decision, Target = new AefOverlayTarget { Result = result, Requirement = requirement }, Reason = reason,
                By = new AefIdentity(identity, AefAssurance.SelfAttested),
                At = at, Expires = until, State = effectiveState,
            });
            var batch = writer.SealBatch();
            if (json)
                stdout.WriteLine(JsonSerializer.Serialize(new { eventId, runId = writer.RunId, resultId = result,
                    requirementId = requirement, expires = until?.ToString(), state, kind,
                    assurance = "self-attested", batch = batch.Number, sealPath = batch.SealPath }));
            else
                stdout.WriteLine($"Recorded {kind} for {result ?? requirement ?? "the run"} as {eventId}, batch {batch.Number} (self-attested). Sealed results and gates are unchanged.");
            return ExitCodes.Success;
        }
        catch (AefWriteException)
        {
            stderr.WriteLine("Decision could not be sealed. Inspect the run and overlay journal before retrying; an unsealed event may remain.");
            return ExitCodes.TestFailure;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or FormatException)
        {
            stderr.WriteLine($"Decision input or storage error: {error.Message}");
            return ExitCodes.UsageError;
        }
    }
}
