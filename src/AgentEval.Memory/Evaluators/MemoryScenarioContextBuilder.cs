// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.Evaluators;

/// <summary>
/// Builds the conversation-context inputs for memory benchmark scenarios — corpus/fact interleaving,
/// session segmentation, and context-pressure text blobs. Extracted from <see cref="MemoryBenchmarkRunner"/>
/// (MNT-03/MNT-05) so the runner is left with dispatch + aggregation rather than also owning scenario
/// authoring. All members are pure static functions over their explicit inputs (no shared state).
/// </summary>
internal static class MemoryScenarioContextBuilder
{
    /// <summary>
    /// Merges corpus turns with positioned facts, interleaving facts at their specified
    /// fractional positions within the corpus. This buries facts deep in context instead
    /// of appending them at the end (which exploits LLM recency bias).
    /// When <paramref name="sessionsCount"/> is greater than 0, the corpus is divided into
    /// that many segments with session boundary markers inserted between them.
    /// Facts with a <see cref="DataLoading.FactDefinition.SessionId"/> are placed within
    /// the corresponding session segment instead of using fractional positioning.
    /// </summary>
    internal static IEnumerable<(string User, string Assistant)> BuildInterleavedHistory(
        List<(string User, string Assistant)> corpusTurns,
        List<DataLoading.FactDefinition> positionedFacts,
        List<string> noiseBetweenFacts,
        int sessionsCount = 0)
    {
        if (sessionsCount <= 0)
        {
            // Original behavior — no session boundaries
            return BuildInterleavedHistoryNoSessions(corpusTurns, positionedFacts, noiseBetweenFacts);
        }

        // --- Session-aware interleaving ---
        // Divide corpus turns into sessionsCount equal segments
        var turnsPerSession = corpusTurns.Count / sessionsCount;
        var remainder = corpusTurns.Count % sessionsCount;

        // Build session segments (distribute remainder turns across first segments)
        var segments = new List<List<(string, string)>>();
        var offset = 0;
        for (int s = 0; s < sessionsCount; s++)
        {
            var count = turnsPerSession + (s < remainder ? 1 : 0);
            segments.Add(corpusTurns.GetRange(offset, count));
            offset += count;
        }

        // Separate facts by session assignment
        var sessionFacts = positionedFacts.Where(f => f.SessionId.HasValue).ToList();
        var fractionalFacts = positionedFacts.Where(f => !f.SessionId.HasValue).ToList();

        // Insert fractional-position facts into their computed segment
        // (same logic as before but scoped to the whole corpus)
        foreach (var (fact, i) in fractionalFacts.Select((f, i) => (f, i)))
        {
            var globalIndex = Math.Clamp((int)(fact.FractionalPosition!.Value * corpusTurns.Count), 0, corpusTurns.Count);
            // Determine which segment this index falls into
            var cumulative = 0;
            for (int s = 0; s < segments.Count; s++)
            {
                if (globalIndex <= cumulative + segments[s].Count || s == segments.Count - 1)
                {
                    var localIndex = Math.Clamp(globalIndex - cumulative, 0, segments[s].Count);
                    InsertFactIntoSegment(segments[s], localIndex, fact, i, noiseBetweenFacts);
                    break;
                }
                cumulative += segments[s].Count;
            }
        }

        // Insert session-assigned facts into the middle of their segment
        var noiseIdx = fractionalFacts.Count;
        foreach (var fact in sessionFacts)
        {
            var sessionIndex = Math.Clamp(fact.SessionId!.Value - 1, 0, segments.Count - 1);
            var segment = segments[sessionIndex];
            var midpoint = segment.Count / 2;
            InsertFactIntoSegment(segment, midpoint, fact, noiseIdx, noiseBetweenFacts);
            noiseIdx++;
        }

        // Generate session boundary dates spread across the last 6 months
        var today = DateTime.Today;
        var totalDays = 180; // ~6 months
        var dayStep = sessionsCount > 1 ? totalDays / (sessionsCount - 1) : 0;

        // Assemble final result with session markers between segments
        var result = new List<(string, string)>();
        for (int s = 0; s < segments.Count; s++)
        {
            // Insert session boundary marker before each segment
            var daysAgo = sessionsCount > 1
                ? totalDays - (s * dayStep)
                : 0;
            var sessionDate = today.AddDays(-daysAgo);
            var dateStr = sessionDate.ToString("yyyy-MM-dd");
            result.Add(($"--- Session {s + 1} ({dateStr}) ---", "Starting a new conversation session."));

            result.AddRange(segments[s]);
        }

        return result;
    }

    /// <summary>
    /// Inserts a fact (and optional noise) into a segment at the given local index.
    /// </summary>
    private static void InsertFactIntoSegment(
        List<(string, string)> segment, int localIndex, DataLoading.FactDefinition fact,
        int noiseIndex, List<string> noiseBetweenFacts)
    {
        var plantedText = fact.PlantedAs ?? fact.Content;
        if (fact.Timestamp != null)
            plantedText = $"[{fact.Timestamp}] {plantedText}";
        var assistantReply = fact.AssistantResponse ?? "Got it, I'll remember that.";
        var factTurn = (plantedText, assistantReply);

        if (noiseBetweenFacts.Count > 0)
        {
            var noiseMsg = noiseBetweenFacts[noiseIndex % noiseBetweenFacts.Count];
            var noiseTurn = (noiseMsg, "That's an interesting point.");
            segment.Insert(Math.Min(localIndex, segment.Count), noiseTurn);
        }

        segment.Insert(Math.Min(localIndex, segment.Count), factTurn);
    }

    /// <summary>
    /// Original interleaving logic without session boundaries.
    /// </summary>
    private static IEnumerable<(string User, string Assistant)> BuildInterleavedHistoryNoSessions(
        List<(string User, string Assistant)> corpusTurns,
        List<DataLoading.FactDefinition> positionedFacts,
        List<string> noiseBetweenFacts)
    {
        // Calculate insertion indices, sort descending so we insert from back to front
        // (prevents earlier insertions from shifting later indices)
        var insertions = positionedFacts
            .Select((fact, i) => (
                Index: Math.Clamp((int)(fact.FractionalPosition!.Value * corpusTurns.Count), 0, corpusTurns.Count),
                Fact: fact,
                NoiseIndex: i))
            .OrderByDescending(x => x.Index)
            .ToList();

        var result = new List<(string, string)>(corpusTurns);

        foreach (var ins in insertions)
        {
            var plantedText = ins.Fact.PlantedAs ?? ins.Fact.Content;
            if (ins.Fact.Timestamp != null)
                plantedText = $"[{ins.Fact.Timestamp}] {plantedText}";
            var assistantReply = ins.Fact.AssistantResponse ?? "Got it, I'll remember that.";
            var factTurn = (plantedText, assistantReply);

            // Optionally insert a noise turn after the fact (so fact doesn't sit right next to a query)
            if (noiseBetweenFacts.Count > 0)
            {
                var noiseMsg = noiseBetweenFacts[ins.NoiseIndex % noiseBetweenFacts.Count];
                var noiseTurn = (noiseMsg, "That's an interesting point.");
                result.Insert(ins.Index, noiseTurn);
            }

            result.Insert(ins.Index, factTurn);
        }

        return result;
    }

    /// <summary>
    /// Builds a text blob from corpus-loaded conversation turns to simulate context pressure.
    /// The blob is prepended to each verification query (text-blob injection, matching LongMemEval).
    /// Falls back to SyntheticHistoryGenerator if corpus files are not available.
    /// </summary>
    // internal for P0-2 (Sprint 0) regression tests that assert Diagnostic / Overflow
    // resolve to context-stress rather than context-small.
    internal static string? BuildContextPressureBlob(string presetName)
    {
        var (corpusName, turnCount) = presetName switch
        {
            "Diagnostic" => ("context-stress", 250), // Diagnostic uses stress corpus (~120K tokens)
            "Full" => ("context-stress", 200),       // Full uses stress corpus (~130K tokens)
            "Standard" => ("context-stress", 100),   // Standard uses stress corpus (~65K tokens)
            _ => ("context-small", 15)               // Quick uses all 15 small turns (~8K tokens)
        };

        IReadOnlyList<(string UserMessage, string AssistantResponse)> turns;
        try
        {
            turns = DataLoading.CorpusLoader.Load(corpusName, turnCount);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Fallback to SyntheticHistoryGenerator if corpus not available
            turns = SyntheticHistoryGenerator.Generate(turnCount);
        }

        return FormatTurnsAsTextBlob(turns);
    }

    /// <summary>
    /// Formats conversation turns as a text blob suitable for prepending to queries.
    /// </summary>
    internal static string FormatTurnsAsTextBlob(
        IEnumerable<(string UserMessage, string AssistantResponse)> turns)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Below is a conversation history between you and a user. Use it to answer the question that follows.");
        sb.AppendLine();
        sb.AppendLine("Conversation History:");

        foreach (var (user, assistant) in turns)
        {
            sb.AppendLine($"user: {user}");
            sb.AppendLine($"assistant: {assistant}");
        }

        return sb.ToString();
    }
}
