// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>One opaque-id tool call captured for deterministic gate replay.</summary>
public sealed class GateReplayFixture
{
    /// <summary>Creates an immutable fixture snapshot. The id is emitted in reports; arguments are not.</summary>
    public GateReplayFixture(string id, GatedToolCall call)
    {
        ValidateOpaqueId(id, nameof(id));
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.FunctionName))
        {
            throw new ArgumentException(
                "Expected a non-empty function name. Actual: empty/whitespace. Suggestions: preserve the captured tool name.",
                nameof(call));
        }

        Id = id;
        Call = call with
        {
            Arguments = call.Arguments is null
                ? null
                : new ReadOnlyDictionary<string, object?>(
                    new Dictionary<string, object?>(call.Arguments, StringComparer.Ordinal)),
            Messages = call.Messages?.ToArray(),
        };
    }

    /// <summary>Opaque, corpus-local id. It should not contain prompt or argument data.</summary>
    public string Id { get; }

    /// <summary>The captured call supplied to the real gate pipeline.</summary>
    public GatedToolCall Call { get; }

    internal static void ValidateOpaqueId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Expected a non-empty opaque id of at most 256 characters with no control characters. " +
                "Actual: invalid id. Suggestions: use a UUID or local sequence id; never embed arguments.",
                parameterName);
        }
    }
}

/// <summary>A named, ordered, immutable collection of captured tool calls.</summary>
public sealed class GateReplayCorpus
{
    /// <summary>Creates a corpus and rejects duplicate call ids.</summary>
    public GateReplayCorpus(string corpusId, IEnumerable<GateReplayFixture> fixtures)
    {
        GateReplayFixture.ValidateOpaqueId(corpusId, nameof(corpusId));
        ArgumentNullException.ThrowIfNull(fixtures);

        var snapshot = fixtures.ToArray();
        if (snapshot.Any(static fixture => fixture is null))
        {
            throw new ArgumentException(
                "Expected every fixture to be non-null. Actual: null fixture. " +
                "Suggestions: remove or replace the null entry.",
                nameof(fixtures));
        }

        var duplicate = snapshot
            .GroupBy(static fixture => fixture.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Expected corpus-local call ids to be unique. Actual: duplicate id '{duplicate}'. " +
                "Suggestions: assign opaque unique ids.",
                nameof(fixtures));
        }

        CorpusId = corpusId;
        Fixtures = Array.AsReadOnly(snapshot);
    }

    /// <summary>Opaque corpus identity included in deterministic reports.</summary>
    public string CorpusId { get; }

    /// <summary>Calls in replay order.</summary>
    public IReadOnlyList<GateReplayFixture> Fixtures { get; }
}

/// <summary>Hard bounds for the JSONL corpus parser and writer.</summary>
public sealed record GateReplayCorpusLimits(
    int MaxCalls = 10_000,
    int MaxLineBytes = 256 * 1024,
    int MaxTotalBytes = 16 * 1024 * 1024,
    int MaxJsonDepth = 32,
    int MaxMessagesPerCall = 256,
    int MaxContentsPerMessage = 64,
    int MaxArgumentsPerCall = 256)
{
    internal void Validate()
    {
        if (MaxCalls < 1 || MaxLineBytes < 256 || MaxTotalBytes < MaxLineBytes || MaxJsonDepth < 2
            || MaxMessagesPerCall < 1 || MaxContentsPerMessage < 1 || MaxArgumentsPerCall < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GateReplayCorpusLimits),
                "Expected positive corpus limits, MaxLineBytes >= 256, MaxTotalBytes >= MaxLineBytes, " +
                "and MaxJsonDepth >= 2. Actual: one or more invalid limits. " +
                "Suggestions: use the defaults or raise only a bound required by a reviewed corpus.");
        }
    }
}

/// <summary>Thrown when a replay corpus cannot be represented or parsed without losing fidelity.</summary>
public sealed class GateReplayCorpusFormatException : Exception
{
    /// <summary>Creates a format exception.</summary>
    public GateReplayCorpusFormatException(string message) : base(message) { }

    /// <summary>Creates a format exception with the underlying parser/serializer failure.</summary>
    public GateReplayCorpusFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Reads and writes the strict, bounded <c>gatekeeper.replay-corpus/1</c> JSONL format. A mandatory header keeps
/// an empty corpus representable; each following line contains one call. Blank lines are ignored.
/// </summary>
public static class GateReplayCorpusSerializer
{
    /// <summary>The only schema version this implementation accepts.</summary>
    public const string SchemaVersion = "gatekeeper.replay-corpus/1";

    /// <summary>Writes a complete corpus only after every record has serialized and passed the configured bounds.</summary>
    public static Task WriteAsync(
        Stream destination,
        GateReplayCorpus corpus,
        GateReplayCorpusLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(corpus);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "Expected a writable stream. Actual: CanWrite is false. Suggestions: open the destination for writing.",
                nameof(destination));
        }

        limits ??= new GateReplayCorpusLimits();
        limits.Validate();
        return GateReplayCorpusWriter.WriteAsync(destination, corpus, limits, cancellationToken);
    }

    /// <summary>Reads a complete corpus with total-size, line, count, shape, depth, and duplicate-property bounds.</summary>
    public static Task<GateReplayCorpus> ReadAsync(
        Stream source,
        GateReplayCorpusLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException(
                "Expected a readable stream. Actual: CanRead is false. Suggestions: open the source for reading.",
                nameof(source));
        }

        limits ??= new GateReplayCorpusLimits();
        limits.Validate();
        return GateReplayCorpusReader.ReadAsync(source, limits, cancellationToken);
    }
}