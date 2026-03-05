using AgentEval.Memory.Models;

namespace AgentEval.Memory.Temporal;

/// <summary>
/// Scenarios for testing temporal memory capabilities - understanding what was known when.
/// Tests agent ability to reason about information in time context.
/// </summary>
public static class TemporalMemoryScenarios
{
    /// <summary>
    /// Creates a scenario where facts are established at different times,
    /// then queries test temporal understanding ("what did you know at time T?").
    /// </summary>
    /// <param name="timedFacts">Facts with their establishment timestamps</param>
    /// <param name="queryTime">Time point to query about</param>
    /// <returns>Temporal memory scenario</returns>
    public static MemoryTestScenario CreateTimeTravel(
        IReadOnlyList<(MemoryFact fact, DateTimeOffset timestamp)> timedFacts,
        DateTimeOffset queryTime)
    {
        var steps = new List<MemoryStep>();
        
        // Sort facts by timestamp to establish them in chronological order
        var sortedFacts = timedFacts.OrderBy(tf => tf.timestamp).ToArray();
        
        foreach (var (fact, timestamp) in sortedFacts)
        {
            steps.Add(MemoryStep.Temporal(
                $"[{timestamp:yyyy-MM-dd HH:mm}] {fact.Content}",
                timestamp
            ));
        }
        
        // Determine which facts should be known at query time
        var factsKnownAtQueryTime = sortedFacts
            .Where(tf => tf.timestamp <= queryTime)
            .Select(tf => tf.fact)
            .ToArray();
            
        var factsUnknownAtQueryTime = sortedFacts
            .Where(tf => tf.timestamp > queryTime)
            .Select(tf => tf.fact)
            .ToArray();
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.CreateTemporal(
                $"What did you know as of {queryTime:yyyy-MM-dd HH:mm}?",
                queryTime,
                factsKnownAtQueryTime
            )
        };
        
        // Add query that should exclude future facts
        if (factsUnknownAtQueryTime.Length > 0)
        {
            queries.Add(new MemoryQuery
            {
                Question = $"As of {queryTime:yyyy-MM-dd HH:mm}, did you know anything about future events?",
                ExpectedFacts = Array.Empty<MemoryFact>(),
                ForbiddenFacts = factsUnknownAtQueryTime,
                QueryTime = queryTime
            });
        }
        
        return new MemoryTestScenario
        {
            Name = $"Time Travel Query ({queryTime:MM-dd HH:mm})",
            Description = $"Tests temporal understanding - what was known at {queryTime:yyyy-MM-dd HH:mm} vs later",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["TemporalQuery"] = true,
                ["QueryTime"] = queryTime,
                ["TotalFacts"] = timedFacts.Count,
                ["FactsKnownAtQueryTime"] = factsKnownAtQueryTime.Length
            }
        };
    }

    /// <summary>
    /// Creates a scenario testing memory of information updates over time.
    /// Tests whether agent remembers both old and new versions appropriately.
    /// </summary>
    /// <param name="factEvolutions">Sequence of fact updates over time</param>
    /// <returns>Fact evolution memory scenario</returns>
    public static MemoryTestScenario CreateFactEvolution(
        IReadOnlyList<(string topic, string content, DateTimeOffset timestamp, bool supersedes)> factEvolutions)
    {
        var steps = new List<MemoryStep>();
        var groupedByTopic = factEvolutions.GroupBy(fe => fe.topic).ToArray();
        
        // Establish facts chronologically
        var sortedEvolutions = factEvolutions.OrderBy(fe => fe.timestamp).ToArray();
        
        foreach (var (topic, content, timestamp, supersedes) in sortedEvolutions)
        {
            var prefix = supersedes ? "UPDATE" : "NEW INFO";
            steps.Add(MemoryStep.Temporal(
                $"[{timestamp:yyyy-MM-dd HH:mm}] {prefix}: {content}",
                timestamp
            ));
        }
        
        var queries = new List<MemoryQuery>();
        
        // Test understanding of current vs historical information
        foreach (var topicGroup in groupedByTopic)
        {
            var topic = topicGroup.Key;
            var evolutions = topicGroup.OrderBy(e => e.timestamp).ToArray();
            var latestEvolution = evolutions.Last();
            var historicalEvolutions = evolutions.Take(evolutions.Length - 1).ToArray();
            
            queries.Add(MemoryQuery.Create(
                $"What is the current information about {topic}?",
                MemoryFact.Create(latestEvolution.content)
            ));
            
            if (historicalEvolutions.Length > 0)
            {
                queries.Add(new MemoryQuery
                {
                    Question = $"What was previously known about {topic} before the latest update?",
                    ExpectedFacts = historicalEvolutions.Select(e => MemoryFact.Create(e.content)).ToArray(),
                    ForbiddenFacts = new[] { MemoryFact.Create(latestEvolution.content) }
                });
            }
        }
        
        return new MemoryTestScenario
        {
            Name = "Fact Evolution Over Time",
            Description = $"Tests memory of information changes across {groupedByTopic.Length} topics",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["TemporalEvolution"] = true,
                ["Topics"] = groupedByTopic.Length,
                ["TotalEvolutions"] = factEvolutions.Count
            }
        };
    }

    /// <summary>
    /// Creates a scenario testing temporal reasoning about causality and sequence.
    /// Tests understanding of "what led to what" and temporal relationships.
    /// </summary>
    /// <param name="causalChain">Sequence of causally related events</param>
    /// <returns>Causal reasoning memory scenario</returns>
    public static MemoryTestScenario CreateCausalReasoning(
        IReadOnlyList<(string eventDescription, DateTimeOffset timestamp, string[] causes, string[] effects)> causalChain)
    {
        var steps = new List<MemoryStep>();
        var allEvents = causalChain.OrderBy(e => e.timestamp).ToArray();
        
        // Establish causal chain
        foreach (var (eventDesc, timestamp, causes, effects) in allEvents)
        {
            var causesText = causes.Length > 0 ? $" (because: {string.Join(", ", causes)})" : "";
            var effectsText = effects.Length > 0 ? $" (leading to: {string.Join(", ", effects)})" : "";
            
            steps.Add(MemoryStep.Temporal(
                $"[{timestamp:yyyy-MM-dd HH:mm}] {eventDesc}{causesText}{effectsText}",
                timestamp
            ));
        }
        
        var queries = new List<MemoryQuery>
        {
            MemoryQuery.Create(
                "What sequence of events occurred? Please describe the timeline.",
                allEvents.Select(e => MemoryFact.Create(e.eventDescription)).ToArray()
            ),
            MemoryQuery.Create(
                "What were the cause-and-effect relationships in these events?",
                allEvents.SelectMany(e => e.causes.Concat(e.effects).Select(MemoryFact.Create)).ToArray()
            )
        };
        
        // Add specific causality queries
        foreach (var (eventDesc, timestamp, causes, effects) in allEvents)
        {
            if (causes.Length > 0)
            {
                queries.Add(MemoryQuery.Create(
                    $"What caused '{eventDesc}' to happen?",
                    causes.Select(MemoryFact.Create).ToArray()
                ));
            }
            
            if (effects.Length > 0)
            {
                queries.Add(MemoryQuery.Create(
                    $"What were the effects of '{eventDesc}'?",
                    effects.Select(MemoryFact.Create).ToArray()
                ));
            }
        }
        
        return new MemoryTestScenario
        {
            Name = "Causal Reasoning Test",
            Description = $"Tests temporal causality understanding across {allEvents.Length} related events",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["CausalReasoning"] = true,
                ["EventCount"] = allEvents.Length,
                ["TimeSpan"] = allEvents.Last().timestamp - allEvents.First().timestamp
            }
        };
    }

    /// <summary>
    /// Creates a scenario testing memory degradation over different time intervals.
    /// Tests how memory retention changes based on time elapsed.
    /// </summary>
    /// <param name="facts">Facts to test at different time intervals</param>
    /// <param name="timeIntervals">Time intervals to test (e.g., 1 hour, 1 day, 1 week)</param>
    /// <returns>Memory degradation scenario</returns>
    public static MemoryTestScenario CreateMemoryDegradation(
        IReadOnlyList<MemoryFact> facts,
        IReadOnlyList<TimeSpan> timeIntervals)
    {
        var baseTime = DateTimeOffset.UtcNow;
        var steps = new List<MemoryStep>();
        
        // Establish all facts at base time
        steps.Add(MemoryStep.System(
            $"[{baseTime:yyyy-MM-dd HH:mm}] Initial learning session"
        ));
        
        foreach (var fact in facts)
        {
            steps.Add(MemoryStep.Temporal(fact.Content, baseTime));
        }
        
        var queries = new List<MemoryQuery>();
        
        // Test recall at each time interval
        foreach (var interval in timeIntervals.OrderBy(i => i))
        {
            var queryTime = baseTime.Add(interval);
            
            queries.Add(MemoryQuery.CreateTemporal(
                $"After {FormatTimeSpan(interval)}, what do you remember from our {baseTime:MM-dd HH:mm} session?",
                queryTime,
                facts.ToArray()
            ));
        }
        
        return new MemoryTestScenario
        {
            Name = "Memory Degradation Over Time",
            Description = $"Tests retention of {facts.Count} facts across {timeIntervals.Count} time intervals",
            Steps = steps,
            Queries = queries,
            Metadata = new Dictionary<string, object>
            {
                ["DegradationTest"] = true,
                ["BaseTime"] = baseTime,
                ["TimeIntervals"] = timeIntervals.Select(FormatTimeSpan).ToArray(),
                ["MaxInterval"] = timeIntervals.Max()
            }
        };
    }

    /// <summary>
    /// Helper method to create common temporal scenarios.
    /// </summary>
    public static class CommonScenarios
    {
        /// <summary>
        /// Simple "what did you know yesterday" scenario.
        /// </summary>
        public static MemoryTestScenario Yesterday(params MemoryFact[] facts)
        {
            var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
            var factTuples = facts.Select(f => (f, yesterday)).ToArray();
            
            return CreateTimeTravel(factTuples, yesterday);
        }
        
        /// <summary>
        /// Information update scenario - old info gets replaced.
        /// </summary>
        public static MemoryTestScenario InformationUpdate(
            string topic,
            string oldInfo, 
            string newInfo,
            TimeSpan timeBetween)
        {
            var baseTime = DateTimeOffset.UtcNow;
            var evolutions = new[]
            {
                (topic, oldInfo, baseTime, false),
                (topic, newInfo, baseTime.Add(timeBetween), true)
            };
            
            return CreateFactEvolution(evolutions);
        }
        
        /// <summary>
        /// Simple cause-and-effect scenario.
        /// </summary>
        public static MemoryTestScenario CauseAndEffect(
            string cause, 
            string effect, 
            TimeSpan timeBetween)
        {
            var baseTime = DateTimeOffset.UtcNow;
            var events = new[]
            {
                (cause, baseTime, Array.Empty<string>(), new[] { effect }),
                (effect, baseTime.Add(timeBetween), new[] { cause }, Array.Empty<string>())
            };
            
            return CreateCausalReasoning(events);
        }
    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan.TotalDays >= 1)
            return $"{timeSpan.TotalDays:F0} day(s)";
        if (timeSpan.TotalHours >= 1)
            return $"{timeSpan.TotalHours:F0} hour(s)";
        if (timeSpan.TotalMinutes >= 1)
            return $"{timeSpan.TotalMinutes:F0} minute(s)";
        return $"{timeSpan.TotalSeconds:F0} second(s)";
    }
}