# Time-grounded probe

> **This is not LongMemEval and its scores are not comparable with LongMemEval's.**
> It is twelve AgentEval-authored questions in the LongMemEval file format, shipped
> inside the package. Twelve questions is a probe: large enough to show that a system
> ignores timestamps, far too small to rank two systems that do not.

## The gap it closes

In the LongMemEval corpus a session's date exists in two places: the dataset
metadata, and the text AgentEval renders — a `Session Date:` header per session and
a `Current Date:` prefix on the question. Nothing in that forces an ingesting system
to place the messages in time. A system that stamps every message with ingestion
time, or with a counter, still answers temporal questions correctly, because the
answer model reads the dates out of the prompt.

The consequence is worth stating plainly: **a good `temporal-reasoning` score is
evidence about the reader, not about the memory.** The benchmark cannot separate a
system with real bitemporal storage from one with none, and prospective memory —
something stated as *not yet* true — has no questions at all.

## Two channels, two modes

`TemporalGroundingMode` decides where dates live. It works on **any**
LongMemEval-shaped dataset, not only on the corpus below.

| Mode | Session dates in text | Timestamps through the typed channel |
|---|---|---|
| `None` (default) | yes | no |
| `TimestampsAndText` | yes | yes |
| `TimestampsOnly` | no | yes |

The typed channel is `ITimestampedHistoryInjectableAgent`:

```csharp
public interface ITimestampedHistoryInjectableAgent
{
    void InjectTimestampedConversationHistory(TimestampedConversationHistory history);
}
```

Each `TimestampedConversationTurn` carries `Timestamp` (when the exchange happened —
store it as valid time, not ingestion time) and `SessionIndex`. The history also
carries `QueryTime`: the "now" that decides already-happened from not-yet-happened,
which no amount of conversation supplies on its own.

Any mode other than `None` **requires** the interface. A run whose agent does not
implement it fails before its first provider call, because the text fallback would
answer temporal questions from the very scaffolding the mode removes:

```text
TemporalGrounding is TimestampsOnly, which delivers session dates as real timestamps,
but agent 'my-agent' does not implement ITimestampedHistoryInjectableAgent. There is
no text fallback for this mode: the dates the fallback would use are the ones the mode
exists to take away.
```

`ChatClientAgentAdapter` and `LongMemEvalOracleReader` implement it by writing each
turn's instant into their own prompt. That makes them systems which place messages
in time perfectly, by construction — useful as a ceiling, and not a substitute for
testing a real memory system.

## The measurement is the difference

One run tells you very little. Run the probe and its control over the same agent:

```csharp
var runner = LongMemEvalBenchmark.Subset(judgeClient);   // judge client only; the corpus is embedded

var control = await runner.RunTimeGroundedAsync(agent, LongMemEvalTimeGroundedCorpus.ControlOptions);
var probe   = await runner.RunTimeGroundedAsync(agent, LongMemEvalTimeGroundedCorpus.ProbeOptions);

Console.WriteLine($"control (dates in text + timestamps): {control.OverallAccuracy:F1}%");
Console.WriteLine($"probe   (timestamps only):            {probe.OverallAccuracy:F1}%");
```

- **Equal scores** — the system honours the timestamps it was given.
- **A drop under the probe** — that share of its temporal score was coming from
  dates printed in the prompt, not from stored time.

For the ceiling on the same corpus:

```csharp
var ceiling = await runner.RunTimeGroundedOracleAsync(answerClient);
```

`ExternalBenchmarkResult.TemporalGrounding` records what was delivered: mode,
questions, sessions and turns timestamped, the earliest and latest instant, whether
the in-text scaffolding was removed, and `SessionsWithDateLikeContent`.

### What `TimestampsOnly` cannot remove

Measured over the real LongMemEval oracle corpus (500 questions, 948 sessions, 6,427
turns, dates spanning 2021-06 to 2024-02), **159 of 948 sessions — 16.8% — still
contain a date-like string in the message text itself.** Those are dates the speakers
wrote, not scaffolding the harness added, and no mode can take them away.

So on the original corpus `TimestampsOnly` weakens the crutch rather than removing it,
and a system that ignores timestamps can still answer some questions from the text.
That is the reason the authored corpus below exists: it is written under a rule the
original corpus never had to follow.

## The corpus

`LongMemEvalTimeGroundedCorpus` — id `agenteval-timegrounded-v1`, 12 questions,
three types, four each:

| Type | Asks | Example shape |
|---|---|---|
| `temporal-as-of` | what was true at a past moment | *Which gym was I a member of at the start of March?* |
| `temporal-current` | what is true now | *Is my Lumen trial still running?* |
| `prospective-memory` | something stated as not yet true | *Have I started at Meridian Health yet?* |

The rule that gives it teeth: **no message content contains an absolute date or a
four-digit year.** Every temporal expression a speaker uses is relative — "eight
weeks from today", "the first Monday of next month", "a fortnight from tomorrow" —
so resolving it requires the session's own timestamp, and answering requires
comparing that against the query time. A single "in March 2026" in a conversation
would let a system that stores no time at all answer from the text, so the property
is enforced by test (`Corpus_ContainsNoAbsoluteDateInAnyMessage`) rather than by
good intentions.

Ordering alone is not enough to answer these. A system that knows only "session B
came after session A" cannot say whether a switch happened before or after the 1st
of March, cannot turn "eight weeks from today" into a date, and cannot tell whether
a thirty-day trial has expired.

The corpus is generated by `tools/gen_timegrounded_corpus.py`, which derives every
absolute date in every gold answer from the session timestamps rather than accepting
a typed one, so the arithmetic in the answers cannot drift from the arithmetic in the
conversations. Weekday names, chronological order, and gold-session labelling are
verified on generation and again in the C# test suite.

Judging reuses the existing `Temporal` judge template — these answers are dates and
intervals, so the same off-by-one-day tolerance applies. No new template was added,
which keeps the judge-prompt fingerprint, and therefore every sealed baseline,
unchanged.

## Reading a run honestly

- Twelve questions. Report the count next to the score, always.
- The probe says whether timestamps are honoured. It does not rank memory systems,
  and a system can pass it while retrieving badly.
- Dates are read as UTC; the corpus carries no time zone. Every comparison is made
  under the same assumption, so intervals are unaffected, but an absolute instant
  from here is not evidence about a real-world local time.
- A session date the harness cannot parse fails the run
  (`LongMemEvalTemporalGroundingException`) rather than being replaced with a
  placeholder — a placeholder would make questions unanswerable by construction and
  score them anyway.
- Under `RunProvenanceMode.Full` the corpus is pinned by `DatasetIdentifier` and
  `DatasetSha256` even though it has no file on disk.

See also [LongMemEval getting started](getting-started.md).
