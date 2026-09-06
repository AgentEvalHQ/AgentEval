// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// What the CHAT lane spent, accumulated from the provider's own usage blocks and from nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and what was actually broken.</b> <c>DiscoveryModelCall.RunAsync</c> called
/// <c>AIAgent.RunAsync</c> and returned <c>response.Text</c>, discarding
/// <c>Microsoft.Agents.AI.AgentResponse.Usage</c> — the property the provider fills in on exactly
/// that call. The usage was never missing from the response and was never un-asked-for; it reached
/// this process and our code dropped it. The proof is a same-provider comparison inside this
/// repository: <c>MAFAgentAdapter</c> makes the identical <c>RunAsync</c> call and reads
/// <c>response.Usage</c> (<c>src/AgentEval.MAF/MAF/MAFAgentAdapter.cs</c>), and the evals that run
/// through it report <c>measured usage N · token-estimated 0 · unaccounted 0</c> against the same
/// deployment. So <c>agent -- 2</c> made three live model calls and printed no token count, and
/// Eval 08's workflow arm fell back to the harness's text-length estimate over text that is
/// REPLAYED from workflow state — an estimate of the wrong string, of the wrong tokenizer.
/// </para>
/// <para>
/// <b>The one rule this type is built around: an ABSENCE is not a ZERO.</b> A call whose response
/// carried no usage block contributes nothing to the token totals and increments
/// <see cref="CallsWithoutUsage"/> instead, and <see cref="Describe"/> renders the two cases in
/// different words. A run of three calls that all reported <c>0</c> tokens and a run of three calls
/// that reported nothing at all must never print the same line — the first is a measurement, the
/// second is an unknown.
/// </para>
/// <para>
/// <b>It never estimates, and it never sees our own text.</b> There is no path in this type from a
/// string to a token count. That is deliberate: a meter that counts our own tokens is measuring our
/// tokenizer, not the provider's bill. If a later change wants a fallback, it belongs somewhere
/// that is not called a measurement.
/// </para>
/// <para>
/// <b>No currency here, on purpose.</b> This project has no AgentEval dependency (see the csproj)
/// and therefore no rate table, so it cannot turn tokens into money without inventing a rate.
/// Consumers that DO have a declared rate source apply it themselves and name it; the one that does
/// not says the money is UNKNOWN. Tokens are the measurement either way.
/// </para>
/// </remarks>
public sealed class ChatSpend
{
    private readonly object _gate = new();

    /// <summary>Calls whose response carried a usage block that was read.</summary>
    public int CallsWithUsage { get; private set; }

    /// <summary>
    /// Calls that were made and whose usage is UNKNOWN — no usage block on the response, or no
    /// response at all (a timeout, a transport failure). Those calls may well have been billed.
    /// </summary>
    public int CallsWithoutUsage { get; private set; }

    /// <summary>Every chat call this meter was told about.</summary>
    public int Calls => CallsWithUsage + CallsWithoutUsage;

    /// <summary>Prompt tokens the PROVIDER reported, over <see cref="CallsWithUsage"/> only.</summary>
    public long PromptTokens { get; private set; }

    /// <summary>Completion tokens the PROVIDER reported, over <see cref="CallsWithUsage"/> only.</summary>
    public long CompletionTokens { get; private set; }

    /// <summary>The reported total.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// True when every call this meter saw reported usage, so the totals are complete rather than a
    /// lower bound. False for a lane that made no call at all — nothing is a complete measurement of
    /// nothing.
    /// </summary>
    public bool Complete => CallsWithUsage > 0 && CallsWithoutUsage == 0;

    /// <summary>
    /// Records one chat call from the provider's usage block.
    /// </summary>
    /// <param name="usage">
    /// The response's usage, or null. Null — and a block carrying neither an input nor an output
    /// count — is recorded as an ABSENCE, never as a zero.
    /// </param>
    public void Record(UsageDetails? usage)
    {
        lock (_gate)
        {
            if (usage is null || (usage.InputTokenCount is null && usage.OutputTokenCount is null))
            {
                CallsWithoutUsage++;
                return;
            }

            CallsWithUsage++;
            PromptTokens += usage.InputTokenCount ?? 0;
            CompletionTokens += usage.OutputTokenCount ?? 0;
        }
    }

    /// <summary>
    /// Records a call that was made and produced no response to read usage from — a timeout, or a
    /// transport error.
    /// </summary>
    /// <remarks>
    /// This is the honest half of "nothing came back". The loop's own <c>ModelCalls</c> counter has
    /// already counted the attempt; silently leaving it out of the meter would make an abandoned —
    /// and quite possibly billed — call read as if it had never happened.
    /// </remarks>
    public void RecordNoResponse()
    {
        lock (_gate) CallsWithoutUsage++;
    }

    /// <summary>
    /// The lane's spend, in lines, ready to print. Empty when no chat call was made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three renderings, and they are deliberately not interchangeable: <b>nothing to say</b> (no
    /// call), <b>UNKNOWN</b> (calls made, no usage block anywhere), and <b>a figure</b> — which
    /// carries its own LOWER BOUND clause when some but not all of the calls reported. A measured
    /// zero lands in the third form and reads as a measurement, which is what it is.
    /// </para>
    /// <para>
    /// ⚠ <b>Invariant culture, and it was a real defect on this machine.</b> The first live run of
    /// this meter printed <c>7’202 prompt</c> — a Swiss apostrophe, because <c>N0</c> formats in the
    /// MACHINE's culture. That is the same shape as the <c>C4</c> defect Eval 08 already carries a
    /// note about (a USD figure rendered as CHF on a Swiss box): a number whose text depends on who
    /// ran it cannot be summed out of a log by the next reader, and §34.5's corrected total is what
    /// happens when a figure is re-typed instead of summed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Describe()
    {
        lock (_gate)
        {
            if (Calls == 0) return [];

            if (CallsWithUsage == 0)
            {
                return
                [
                    $"{Calls} model call(s) · usage NOT REPORTED by the provider for any of them.",
                    "The spend on this lane is UNKNOWN. That is not the same as zero, and it is not "
                        + "estimated from our own text — see ChatSpend's remarks.",
                ];
            }

            var lines = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture,
                    $"{Calls} model call(s) · {PromptTokens:N0} prompt + {CompletionTokens:N0} completion "
                  + $"= {TotalTokens:N0} token(s), read from the provider's own usage blocks."),
            };

            if (CallsWithoutUsage > 0)
            {
                lines.Add($"⚠ LOWER BOUND: {CallsWithoutUsage} of {Calls} call(s) returned no usage block. Those "
                        + "calls' tokens are UNKNOWN, not zero, and are absent from the total above.");
            }

            return lines;
        }
    }

    /// <summary>An immutable copy, for a consumer that keeps one per repetition.</summary>
    public ChatSpendSnapshot Snapshot()
    {
        lock (_gate) return new ChatSpendSnapshot(CallsWithUsage, CallsWithoutUsage, PromptTokens, CompletionTokens);
    }
}

/// <summary>One run's chat spend, frozen.</summary>
/// <param name="CallsWithUsage">Calls whose response carried a usage block.</param>
/// <param name="CallsWithoutUsage">Calls whose token cost is UNKNOWN.</param>
/// <param name="PromptTokens">Provider-reported prompt tokens.</param>
/// <param name="CompletionTokens">Provider-reported completion tokens.</param>
public readonly record struct ChatSpendSnapshot(
    int CallsWithUsage,
    int CallsWithoutUsage,
    long PromptTokens,
    long CompletionTokens)
{
    /// <summary>Every chat call in the run.</summary>
    public int Calls => CallsWithUsage + CallsWithoutUsage;

    /// <summary>The reported total.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>True when every call in this run reported usage.</summary>
    public bool Complete => CallsWithUsage > 0 && CallsWithoutUsage == 0;
}
