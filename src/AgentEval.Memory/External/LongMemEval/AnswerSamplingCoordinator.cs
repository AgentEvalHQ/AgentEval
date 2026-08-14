// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Carries the caller's requested answer-sampling parameters to the agent under test and records
/// how far each one actually got.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the two halves of the question have different answers. "Did the caller ask for a
/// pinned answer model?" is read off the options. "Did the answer call carry it?" depends on an
/// agent AgentEval does not own, and on a provider AgentEval cannot see through. Collapsing the two
/// into one boolean is how a run comes to look reproducible without being reproducible.
/// </para>
/// <para>
/// Nothing here is created unless the caller requested at least one parameter, so a default run does
/// not touch the agent's capability surface or its property bag.
/// </para>
/// </remarks>
internal sealed class AnswerSamplingCoordinator
{
    private readonly AnswerSamplingRequest _request;

    private AnswerSamplingCoordinator(AnswerSamplingRequest request) => _request = request;

    /// <summary>Null when the caller requested no answer sampling, which is the default.</summary>
    internal static AnswerSamplingCoordinator? Create(ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AnswerTemperature is null && options.AnswerSeed is null)
            return null;

        return new AnswerSamplingCoordinator(new AnswerSamplingRequest
        {
            Temperature = options.AnswerTemperature,
            Seed = options.AnswerSeed
        });
    }

    /// <summary>Per-parameter state after the agent has been asked, before the call is made.</summary>
    internal readonly record struct Attachment(
        AnswerSamplingDisposition Temperature,
        AnswerSamplingDisposition Seed)
    {
        internal bool TemperatureSent => Temperature == AnswerSamplingDisposition.SentUnverified;

        internal bool SeedSent => Seed == AnswerSamplingDisposition.SentUnverified;
    }

    /// <summary>
    /// Asks the agent to apply the request and reports what it accepted. An agent that does not
    /// implement <see cref="IAnswerSamplingConfigurableAgent"/> is recorded as not having received
    /// the request, rather than being reported as pinned.
    /// </summary>
    internal Attachment Configure(IEvaluableAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent is not IAnswerSamplingConfigurableAgent configurable)
        {
            return new Attachment(
                Requested(_request.Temperature, AnswerSamplingDisposition.NotSupportedByAgent),
                Requested(_request.Seed, AnswerSamplingDisposition.NotSupportedByAgent));
        }

        var acknowledgement = configurable.ConfigureAnswerSampling(_request)
            ?? AnswerSamplingAcknowledgement.None;

        return new Attachment(
            Requested(
                _request.Temperature,
                acknowledgement.TemperatureApplied
                    ? AnswerSamplingDisposition.SentUnverified
                    : AnswerSamplingDisposition.DeclinedByAgent),
            Requested(
                _request.Seed,
                acknowledgement.SeedApplied
                    ? AnswerSamplingDisposition.SentUnverified
                    : AnswerSamplingDisposition.DeclinedByAgent));
    }

    /// <summary>
    /// Finalises the per-question record after a successful answer call, upgrading a sent parameter
    /// to echoed when the provider returned the value — the only evidence available that it was
    /// received rather than dropped.
    /// </summary>
    internal AnswerSamplingOutcome Complete(Attachment attachment, AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // The property bag is read only when a value was actually sent, so a run that requested no
        // sampling never observes the agent's bag at all.
        var echoedTemperature = attachment.TemperatureSent
            ? ProviderSamplingEcho.TemperatureFromProperties(response.AdditionalProperties)
            : null;
        var echoedSeed = attachment.SeedSent
            ? ProviderSamplingEcho.SeedFromProperties(response.AdditionalProperties)
            : null;

        return new AnswerSamplingOutcome
        {
            RequestedTemperature = _request.Temperature,
            RequestedSeed = _request.Seed,
            TemperatureDisposition = ResolveEcho(
                attachment.Temperature,
                _request.Temperature,
                echoedTemperature,
                // Loose enough to survive a provider that round-trips the value through a float,
                // tight enough that 0.2 and 0.3 are never called the same request.
                static (requested, echoed) => Math.Abs(requested - echoed) < 1e-6),
            SeedDisposition = ResolveEcho(
                attachment.Seed,
                _request.Seed,
                echoedSeed,
                static (requested, echoed) => requested == echoed),
            EchoedTemperature = echoedTemperature,
            EchoedSeed = echoedSeed
        };
    }

    /// <summary>
    /// Finalises the per-question record after the answer call threw. A rejection that names a
    /// sampling parameter is attributed to that parameter; anything else leaves the record alone,
    /// because an unrelated failure says nothing about whether the value was accepted.
    /// </summary>
    internal AnswerSamplingOutcome Fail(Attachment attachment, Exception exception)
    {
        var (namesTemperature, namesSeed) = ClassifyRejection(exception);

        return new AnswerSamplingOutcome
        {
            RequestedTemperature = _request.Temperature,
            RequestedSeed = _request.Seed,
            TemperatureDisposition = namesTemperature && attachment.TemperatureSent
                ? AnswerSamplingDisposition.RejectedByProvider
                : attachment.Temperature,
            SeedDisposition = namesSeed && attachment.SeedSent
                ? AnswerSamplingDisposition.RejectedByProvider
                : attachment.Seed
        };
    }

    /// <summary>
    /// A provider that refuses a sampling parameter surfaces an HTTP 400 invalid-request error naming
    /// it. Recognising that, and only that, keeps a genuine agent failure (network, timeout, overload)
    /// from being mislabelled as a rejected parameter.
    /// </summary>
    private static (bool NamesTemperature, bool NamesSeed) ClassifyRejection(Exception? exception)
    {
        var message = Flatten(exception);
        if (message.Length == 0)
            return (false, false);

        var looksRejected =
            message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not support", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || message.Contains("400", StringComparison.OrdinalIgnoreCase);
        if (!looksRejected)
            return (false, false);

        return (
            message.Contains("temperature", StringComparison.OrdinalIgnoreCase),
            message.Contains("seed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Providers commonly wrap the 400 in an outer exception, so the inner chain is searched too.
    /// Bounded to a few levels: this is message matching, not a diagnosis.
    /// </summary>
    private static string Flatten(Exception? exception)
    {
        var parts = new List<string>(4);
        var current = exception;
        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            if (!string.IsNullOrEmpty(current.Message))
                parts.Add(current.Message);
            current = current.InnerException;
        }
        return string.Join(" | ", parts);
    }

    private static AnswerSamplingDisposition Requested<T>(T? value, AnswerSamplingDisposition disposition)
        where T : struct
        => value.HasValue ? disposition : AnswerSamplingDisposition.NotRequested;

    private static AnswerSamplingDisposition ResolveEcho<T>(
        AnswerSamplingDisposition current,
        T? requested,
        T? echoed,
        Func<T, T, bool> equal)
        where T : struct
    {
        if (current != AnswerSamplingDisposition.SentUnverified ||
            requested is not { } requestedValue ||
            echoed is not { } echoedValue)
        {
            return current;
        }

        return equal(requestedValue, echoedValue)
            ? AnswerSamplingDisposition.SentAndEchoed
            : AnswerSamplingDisposition.EchoedDifferentValue;
    }
}
