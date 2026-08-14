// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Core;

/// <summary>
/// Sampling parameters an evaluator asks an agent to attach to its answer call.
/// </summary>
/// <remarks>
/// <para>
/// A benchmark cannot resolve differences smaller than the answer model's disagreement with itself.
/// Left at a provider default (temperature 1.0 on most deployments) that self-disagreement is the
/// noise floor of every number the benchmark produces, and it is invisible in the result: two runs
/// over an identical corpus, an identical config and an identical retrieval set can still differ,
/// and nothing in the output says why.
/// </para>
/// <para>
/// AgentEval cannot set sampling parameters on an agent it does not own — <see cref="IEvaluableAgent"/>
/// is a prompt in, text out contract with no provider surface. This interface is how an agent adapter
/// opts in to receiving them. An agent that does not implement it is recorded as not having received
/// the request at all, rather than being reported as though the run had been pinned.
/// </para>
/// </remarks>
public sealed record AnswerSamplingRequest
{
    /// <summary>Requested sampling temperature, or null to leave the provider default alone.</summary>
    public double? Temperature { get; init; }

    /// <summary>Requested sampling seed, or null to leave the provider default alone.</summary>
    public int? Seed { get; init; }

    /// <summary>True when nothing was requested, so an adapter can return early.</summary>
    public bool IsEmpty => Temperature is null && Seed is null;
}

/// <summary>
/// What an agent did with an <see cref="AnswerSamplingRequest"/>.
/// </summary>
/// <remarks>
/// "Applied" means the value was attached to the request the agent will send. It deliberately does
/// <b>not</b> mean the provider honoured it: a provider that silently ignores a seed returns a
/// perfectly normal response, and an adapter has no way to tell that from one that used it. The
/// stronger claim, where it can be made at all, comes from the provider echoing the value back —
/// see <c>AnswerSamplingDisposition.SentAndEchoed</c>.
/// </remarks>
public sealed record AnswerSamplingAcknowledgement
{
    /// <summary>Whether the requested temperature was attached to the outbound call.</summary>
    public bool TemperatureApplied { get; init; }

    /// <summary>Whether the requested seed was attached to the outbound call.</summary>
    public bool SeedApplied { get; init; }

    /// <summary>An acknowledgement that applied nothing.</summary>
    public static AnswerSamplingAcknowledgement None { get; } = new();

    /// <summary>Acknowledges every value the request actually carried.</summary>
    public static AnswerSamplingAcknowledgement AppliedFrom(AnswerSamplingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AnswerSamplingAcknowledgement
        {
            TemperatureApplied = request.Temperature.HasValue,
            SeedApplied = request.Seed.HasValue
        };
    }
}

/// <summary>
/// Capability interface for agents that accept evaluator-supplied sampling parameters for the
/// answer call, so a benchmark can pin the answer model instead of measuring it at a provider
/// default it never chose.
/// </summary>
/// <remarks>
/// <para>
/// Implement this on the adapter that owns the provider call. The evaluator invokes
/// <see cref="ConfigureAnswerSampling"/> before each question and records the returned
/// acknowledgement, so a stored run states which parameters actually reached the wire.
/// </para>
/// <para>
/// Two conventions make the record complete. First, return an honest acknowledgement: an adapter
/// that cannot express a seed should leave <see cref="AnswerSamplingAcknowledgement.SeedApplied"/>
/// false rather than acknowledge it. Second, when the provider echoes a sampling value back, surface
/// it on <see cref="AgentResponse.AdditionalProperties"/> under <c>"seed"</c> or
/// <c>"temperature"</c> — that echo is the only evidence available that a value was received rather
/// than dropped, and AgentEval reads it when, and only when, sampling was requested.
/// </para>
/// </remarks>
public interface IAnswerSamplingConfigurableAgent
{
    /// <summary>
    /// Applies what the agent can of <paramref name="request"/> to its next answer call and reports
    /// what it applied.
    /// </summary>
    /// <param name="request">The requested sampling parameters; may be empty.</param>
    /// <returns>The parameters actually attached to the outbound call.</returns>
    AnswerSamplingAcknowledgement ConfigureAnswerSampling(AnswerSamplingRequest request);
}
