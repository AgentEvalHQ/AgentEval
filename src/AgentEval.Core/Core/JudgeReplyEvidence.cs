// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// The not-measured results of a judge that gave no reading: its call failed, or its reply could not be read. Both used
/// to be a Fail at score 0, which reads as a measured failure, and on a planted bad case as a correct detection.
/// </summary>
/// <remarks>
/// The proof of an unreadable reply (its length, SHA-256, first <see cref="ExcerptLength"/> characters and the model)
/// goes in <see cref="MetricResult.Details"/> only. The <see cref="MetricResult.Explanation"/> reaches every report
/// export (JUnit, TRX, JSON, CSV, Markdown), and a judge's reply often quotes the case's content.
/// </remarks>
internal static class JudgeReplyEvidence
{
    /// <summary>How much of an unreadable reply is kept, in UTF-16 characters.</summary>
    internal const int ExcerptLength = 2000;

    /// <summary>
    /// Asks the judge. A failed call comes back as not measured (<see cref="CallFailed"/>), never thrown; the caller's
    /// own cancellation still propagates.
    /// </summary>
    internal static async Task<(ChatResponse? Response, MetricResult? NotMeasured)> AskAsync(
        IChatClient judge, string prompt, string metricName, string label, CancellationToken cancellationToken)
    {
        try
        {
            return (await judge.GetResponseAsync(prompt, cancellationToken: cancellationToken), null);
        }
        catch (Exception ex) when (IsCallFailure(ex, cancellationToken))
        {
            return (null, CallFailed(metricName, label, ex));
        }
    }

    /// <summary>
    /// <see cref="MetricInstrument.Model"/> for a measured result. A not-measured one (a failed call, an unreadable
    /// reply) claims no instrument, as one with a missing input does: no grade was given.
    /// </summary>
    internal static MetricResult Model(MetricResult result, string template) =>
        result.Measured ? MetricInstrument.Model(result, template) : result;

    /// <summary>
    /// True for an exception that is a failed call, not the caller cancelling. A provider-side timeout (an
    /// <see cref="OperationCanceledException"/> while the caller's token is not cancelled) is a failed call.
    /// </summary>
    internal static bool IsCallFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    /// <summary>The judge's reply could not be read: not measured, with the proof in the details.</summary>
    /// <param name="metricName">The metric's name.</param>
    /// <param name="label">The metric's name in words, for the explanation ("Faithfulness").</param>
    /// <param name="response">The judge's response.</param>
    /// <param name="failure">Why the reply could not be read.</param>
    /// <param name="legacyDetails">Details the metric reported before (e.g. <c>evaluationStatus</c>), kept for readers of them.</param>
    internal static MetricResult NotMeasured(
        string metricName, string label, ChatResponse response, JudgeReplyFailure failure,
        IDictionary<string, object>? legacyDetails = null)
    {
        var reply = response.Text ?? "";
        var sha256 = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(reply))).ToLowerInvariant();
        var details = new Dictionary<string, object>(legacyDetails ?? new Dictionary<string, object>())
        {
            ["judgeReplyFailure"] = Code(failure),
            ["judgeReplyLength"] = reply.Length,
            ["judgeReplySha256"] = sha256,
            ["judgeReplyExcerpt"] = Excerpt(reply),
        };
        var model = "";
        if (!string.IsNullOrWhiteSpace(response.ModelId))
        {
            details["judgeModel"] = response.ModelId;
            model = $"; model {response.ModelId}";
        }

        // A fixed sentence: no reply text, which may quote the case.
        return MetricResult.NotMeasured(metricName, NotMeasuredCause.JudgeReplyUnreadable,
            $"{label} not measured: the judge's reply could not be parsed ({Describe(failure)}); reply " +
            $"{reply.Length.ToString("N0", CultureInfo.InvariantCulture)} chars, {sha256}{model}.",
            details);
    }

    /// <summary>A call that measures (the judge's, an evaluator's, an embedding model's) failed: not measured, with the error.</summary>
    /// <param name="metricName">The metric's name.</param>
    /// <param name="label">The metric's name in words, for the explanation.</param>
    /// <param name="ex">The failure.</param>
    /// <param name="call">What failed, in words: "the judge call", "the embedding call".</param>
    /// <param name="legacyDetails">Details the metric reported before (e.g. <c>error</c>), kept for readers of them.</param>
    internal static MetricResult CallFailed(
        string metricName, string label, Exception ex, string call = "the judge call",
        IDictionary<string, object>? legacyDetails = null)
    {
        var details = new Dictionary<string, object>(legacyDetails ?? new Dictionary<string, object>())
        {
            ["errorType"] = ex.GetType().FullName ?? ex.GetType().Name,
            ["errorMessage"] = ex.Message,
        };
        return new MetricResult
        {
            MetricName = metricName, Score = 0, Passed = false, Measured = false,
            NotMeasuredCause = NotMeasuredCause.JudgeCallFailed, CallFailure = ex,
            Explanation = $"{label} not measured: {call} failed ({ex.GetType().Name}: {ex.Message}).",
            Details = details,
        };
    }

    /// <summary>The <c>judgeReplyFailure</c> detail for <paramref name="failure"/>, in snake_case.</summary>
    internal static string Code(JudgeReplyFailure failure) => failure switch
    {
        JudgeReplyFailure.Empty => "empty",
        JudgeReplyFailure.NoJson => "no_json",
        JudgeReplyFailure.InvalidJson => "invalid_json",
        JudgeReplyFailure.NotAnObject => "not_an_object",
        JudgeReplyFailure.NoScore => "no_score",
        JudgeReplyFailure.ScoreNotANumber => "score_not_a_number",
        JudgeReplyFailure.ScoreOutOfRange => "score_out_of_range",
        JudgeReplyFailure.Ambiguous => "ambiguous",
        _ => "none",
    };

    private static string Describe(JudgeReplyFailure failure) => failure switch
    {
        JudgeReplyFailure.Empty => "the reply was empty",
        JudgeReplyFailure.NoJson => "no JSON object found",
        JudgeReplyFailure.InvalidJson => "the JSON is not valid",
        JudgeReplyFailure.NotAnObject => "the JSON is not an object",
        JudgeReplyFailure.NoScore => "no score field",
        JudgeReplyFailure.ScoreNotANumber => "the score is not a number",
        JudgeReplyFailure.ScoreOutOfRange => "the score is off the 0-100 scale",
        JudgeReplyFailure.Ambiguous => "several objects with different scores",
        _ => "no reason given",
    };

    // The first ExcerptLength UTF-16 characters, never splitting a surrogate pair.
    private static string Excerpt(string reply)
    {
        if (reply.Length <= ExcerptLength)
            return reply;
        return reply[..(char.IsHighSurrogate(reply[ExcerptLength - 1]) ? ExcerptLength - 1 : ExcerptLength)];
    }
}
