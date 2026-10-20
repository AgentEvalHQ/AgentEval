// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// What G8 (LongMemEval Benchmark) and G10 (LongMemEval Baseline Repro) share: every selected question accounted for,
/// the per-type table with what each accuracy is over, and a measurement of what the answer prompts delivered.
/// </summary>
/// <remarks>
/// Until 2026-10 both samples printed a per-type count of SELECTED questions beside an accuracy over SCORED ones, and
/// G10's summary had no agent-error count: on a provider without its model, all 50 questions errored and the summary
/// read "0/0 scored, 50 selected, 0 inconclusive".
/// </remarks>
internal static class LongMemEvalSampleSupport
{
    /// <summary>
    /// Fewer prompt tokens than one per this many characters sent means the provider did not read the whole prompt.
    /// English chat text runs about four characters per token on the common tokenizers; eight is half the haystack gone.
    /// </summary>
    public const double TruncationCharactersPerToken = 8.0;

    /// <summary>Every selected question, accounted for: a selected count is not a scored count.</summary>
    public static void PrintAccounting(ExternalBenchmarkResult result)
    {
        Console.WriteLine(
            $"   Questions: {result.SelectedQuestions} selected · {result.ScoredQuestions} scored " +
            $"({result.CorrectQuestions} correct, {result.ScoredQuestions - result.CorrectQuestions} wrong) · " +
            $"{result.InconclusiveQuestions} judge-inconclusive · {result.AgentFailureQuestions} agent error(s)");

        var notScored = result.QuestionResults
            .Where(q => q.Correct is null)
            .GroupBy(q => q.SafeFailureCode ?? q.JudgeStatus?.ToString() ?? "unknown")
            .Select(g => $"{g.Key} ×{g.Count()}")
            .ToList();
        if (notScored.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"   No verdict: {string.Join(", ", notScored)} — left out of the accuracy, never counted right or wrong");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    /// <summary>The per-type breakdown with what each accuracy is over: selected, scored, and the rest.</summary>
    public static void PrintPerTypeTable(ExternalBenchmarkResult result)
    {
        Console.WriteLine("   +----------------------------------+----------+--------+------------+----------+");
        Console.WriteLine("   | Question Type                    | Selected | Scored | Not scored | Accuracy |");
        Console.WriteLine("   +----------------------------------+----------+--------+------------+----------+");
        foreach (var (typeName, typeResult) in result.PerTypeResults.OrderBy(kv => kv.Key))
        {
            var notScored = typeResult.TotalQuestions - typeResult.ScoredQuestions;
            Console.Write($"   | {typeName,-32} | {typeResult.TotalQuestions,8} | {typeResult.ScoredQuestions,6} | {notScored,10} | ");
            PrintScore(typeResult.Accuracy);
            Console.WriteLine("   |");
        }
        Console.WriteLine("   +----------------------------------+----------+--------+------------+----------+");
        Console.WriteLine("   Accuracy is correct / scored. A type with one or two scored questions carries no per-type claim.");
    }

    /// <summary>
    /// What reached the provider on each answer call. True when a call's reported prompt tokens are too few for the
    /// characters sent, which is what a truncated haystack looks like.
    /// </summary>
    public static bool PrintHaystackDelivery(IReadOnlyList<PromptMeasurement> calls)
    {
        Console.WriteLine("\n   Haystack delivery (answer calls, measured on the client that sent them):");
        if (calls.Count == 0)
        {
            Console.WriteLine("     no answer call was made");
            return false;
        }

        var characters = calls.Select(c => c.Characters).ToList();
        Console.WriteLine($"     prompt sent:      median {Median(characters):N0} characters (min {characters.Min():N0}, max {characters.Max():N0}) on {calls.Count} call(s)");

        var reported = calls.Where(c => c.ReportedPromptTokens is > 0).ToList();
        if (reported.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("     prompt tokens:    the provider reported none, so delivery of the whole haystack is not verified");
            Console.ResetColor();
        }
        else
        {
            var tokens = reported.Select(c => c.ReportedPromptTokens!.Value).ToList();
            Console.WriteLine($"     prompt tokens:    median {Median(tokens):N0} as the provider counted them (min {tokens.Min():N0}, max {tokens.Max():N0}) on {reported.Count} call(s)");
        }

        foreach (var error in calls.Where(c => c.Error is not null).GroupBy(c => c.Error).Take(3))
            Console.WriteLine($"     failed call(s):   {error.Count()} × {error.Key}");

        var starved = reported
            .Where(c => (double)c.Characters / c.ReportedPromptTokens!.Value > TruncationCharactersPerToken)
            .ToList();
        if (starved.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"     ❌ {starved.Count} call(s) read fewer than one token per {TruncationCharactersPerToken:F0} characters sent: the provider truncated the prompt");
            Console.ResetColor();
        }
        return starved.Count > 0;
    }

    /// <summary>
    /// Records the verdict on what was measured: a truncated haystack fails the run (the accuracy is not over the
    /// benchmark's input); every answer call failing is an error; a question without a verdict is not measured,
    /// whatever the rest scored.
    /// </summary>
    public static void RecordVerdict(ExternalBenchmarkResult result, bool truncated)
    {
        if (truncated)
        {
            SampleOutcome.Fail(
                "the provider read far fewer prompt tokens than the answer prompts held: the haystack was truncated, " +
                "so the accuracy is not a LongMemEval-S score");
        }

        var unscored = result.SelectedQuestions - result.ScoredQuestions;
        var inconclusive = unscored - result.AgentFailureQuestions;
        if (result.SelectedQuestions > 0 && result.AgentFailureQuestions == result.SelectedQuestions)
        {
            SampleOutcome.Error(
                $"every answer call failed ({result.AgentFailureQuestions} agent error(s) of {result.SelectedQuestions} selected): " +
                "nothing was measured");
        }
        else if (result.ScoredQuestions == 0)
        {
            SampleOutcome.Skip(
                $"no question was scored: {result.AgentFailureQuestions} agent error(s) and {inconclusive} judge-inconclusive " +
                $"of {result.SelectedQuestions} selected");
        }
        else if (unscored > 0)
        {
            SampleOutcome.Skip(
                $"{unscored} of {result.SelectedQuestions} questions not scored ({result.AgentFailureQuestions} agent error(s), " +
                $"{inconclusive} judge-inconclusive): the accuracy is over the {result.ScoredQuestions} scored");
        }
    }

    public static void PrintScore(double? score)
    {
        if (score is not { } value)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("  n/a ");
            Console.ResetColor();
            return;
        }
        Console.ForegroundColor = value >= 70 ? ConsoleColor.Green :
                                  value >= 40 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{value,5:F1}%");
        Console.ResetColor();
    }

    /// <summary>The first line of an exception's message, bounded, for a console line.</summary>
    public static string Describe(Exception ex)
    {
        var firstLine = ex.Message.Split('\n')[0].Trim();
        return $"{ex.GetType().Name}: {(firstLine.Length > 160 ? firstLine[..160] + "…" : firstLine)}";
    }

    private static long Median(List<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}

/// <summary>One answer call: the characters sent, the prompt tokens the provider reported, and the error if it failed.</summary>
internal sealed record PromptMeasurement(long Characters, long? ReportedPromptTokens, string? Error);

/// <summary>
/// Measures each prompt on its way to the provider: the characters sent and the prompt tokens the provider says it
/// read. A provider that truncates a long prompt to its context window reports far fewer tokens than the text holds;
/// one that refuses it fails the call, and the error is kept.
/// </summary>
internal sealed class PromptSizeMeter(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly List<PromptMeasurement> _calls = [];

    public IReadOnlyList<PromptMeasurement> Calls
    {
        get { lock (_calls) return _calls.ToArray(); }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var sent = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        var characters = sent.Sum(m => (long)m.Text.Length);
        try
        {
            var response = await base.GetResponseAsync(sent, options, cancellationToken);
            Record(new PromptMeasurement(characters, response.Usage?.InputTokenCount, null));
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(new PromptMeasurement(characters, null, LongMemEvalSampleSupport.Describe(ex)));
            throw;
        }
    }

    private void Record(PromptMeasurement measurement)
    {
        lock (_calls)
            _calls.Add(measurement);
    }
}
