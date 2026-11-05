// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace AgentEval.Microsoft365.Evidence.Decoders;

/// <summary>
/// The state-free JSON reading rules every evidence decoder shares: RFC 6901 pointer building, field readers
/// that map absent / explicit null / correctly typed / wrongly typed to <see cref="FieldState"/> (a bool is never
/// an integer, a fraction is never an integer, a non-finite or negative number is never a latency or a count),
/// unknown-field collection against a declared key set, and the validation verdict of a row set. Every reader
/// returns an <see cref="Observed{T}"/> carrying the field's own source pointer; nothing substitutes a default,
/// and nothing here depends on the current culture.
/// </summary>
internal static class EvidenceJsonReader
{
    /// <summary>Appends one reference token to an RFC 6901 pointer, escaping <c>~</c> as <c>~0</c> and <c>/</c> as <c>~1</c>.</summary>
    /// <param name="parent">The parent pointer (<c>""</c> is the document root).</param>
    /// <param name="token">The property name.</param>
    public static string Pointer(string parent, string token)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(token);
        return parent + "/" + token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Appends an array index to an RFC 6901 pointer.</summary>
    /// <param name="parent">The parent pointer.</param>
    /// <param name="index">The zero-based array index.</param>
    public static string Pointer(string parent, int index)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return parent + "/" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The source pointer of a property of <paramref name="parent"/> (same capture, origin and artifact).</summary>
    public static SourceRef Child(SourceRef parent, string token) =>
        parent with { JsonPointer = Pointer(parent.JsonPointer, token) };

    /// <summary>The source pointer of an array element of <paramref name="parent"/>.</summary>
    public static SourceRef Child(SourceRef parent, int index) =>
        parent with { JsonPointer = Pointer(parent.JsonPointer, index) };

    /// <summary>A one-element source list.</summary>
    public static ImmutableArray<SourceRef> One(SourceRef source) => ImmutableArray.Create(source);

    /// <summary>Tries to get a property of an object element; false when the element is not an object or the key is absent.</summary>
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Reads a string field: absent → Missing, null → Null, string → Present, anything else → Invalid.</summary>
    public static Observed<string> ReadString(JsonElement parent, string name, SourceRef parentSource)
    {
        if (!TryGetProperty(parent, name, out var value))
        {
            return Observed<string>.Missing();
        }

        var sources = One(Child(parentSource, name));
        return value.ValueKind switch
        {
            JsonValueKind.Null => Observed<string>.Null(sources),
            JsonValueKind.String => Observed<string>.Present(value.GetString()!, sources),
            _ => Observed<string>.Invalid(sources),
        };
    }

    /// <summary>Reads an integer field: absent → Missing, null → Null, a JSON number that is a 32-bit integer → Present; a bool, a string or a fraction → Invalid.</summary>
    public static Observed<int> ReadInteger(JsonElement parent, string name, SourceRef parentSource)
    {
        if (!TryGetProperty(parent, name, out var value))
        {
            return Observed<int>.Missing();
        }

        var sources = One(Child(parentSource, name));
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Observed<int>.Null(sources);
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? Observed<int>.Present(number, sources)
            : Observed<int>.Invalid(sources);
    }

    /// <summary>Reads a count field: absent → Missing, null → Null, a JSON number that is a non-negative 64-bit integer → Present; a bool, a string, a fraction or a negative → Invalid.</summary>
    public static Observed<long> ReadNonNegativeInteger(JsonElement parent, string name, SourceRef parentSource)
    {
        if (!TryGetProperty(parent, name, out var value))
        {
            return Observed<long>.Missing();
        }

        var sources = One(Child(parentSource, name));
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Observed<long>.Null(sources);
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
            ? Observed<long>.Present(number, sources)
            : Observed<long>.Invalid(sources);
    }

    /// <summary>Reads a duration/latency field: absent → Missing, null → Null, a finite non-negative JSON number → Present; anything else → Invalid.</summary>
    public static Observed<double> ReadFiniteNonNegativeNumber(JsonElement parent, string name, SourceRef parentSource)
    {
        if (!TryGetProperty(parent, name, out var value))
        {
            return Observed<double>.Missing();
        }

        var sources = One(Child(parentSource, name));
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Observed<double>.Null(sources);
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0
            ? Observed<double>.Present(number, sources)
            : Observed<double>.Invalid(sources);
    }

    /// <summary>Reads an object-valued field as a detached element: absent → Missing, null → Null, object → Present (cloned), anything else → Invalid.</summary>
    public static Observed<JsonElement> ReadObject(JsonElement parent, string name, SourceRef parentSource)
    {
        if (!TryGetProperty(parent, name, out var value))
        {
            return Observed<JsonElement>.Missing();
        }

        var sources = One(Child(parentSource, name));
        return value.ValueKind switch
        {
            JsonValueKind.Null => Observed<JsonElement>.Null(sources),
            JsonValueKind.Object => Observed<JsonElement>.Present(value, sources),
            _ => Observed<JsonElement>.Invalid(sources),
        };
    }

    /// <summary>Adds a pointer for every property of <paramref name="element"/> whose name is not in <paramref name="known"/>; a non-object adds nothing.</summary>
    public static void CollectUnknownFields(JsonElement element, SourceRef source, IReadOnlySet<string> known, List<SourceRef> unknown)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                unknown.Add(Child(source, property.Name));
            }
        }
    }

    /// <summary><see langword="true"/> when the field was sent with the wrong type or shape.</summary>
    public static bool IsInvalid<T>(Observed<T> observed) => observed.State == FieldState.Invalid;

    /// <summary><see langword="true"/> when a nested collection could not be fully interpreted.</summary>
    public static bool IsNotValid<T>(EvidenceSet<T> set) =>
        set.Availability == EvidenceAvailability.Provided && set.Validation != EvidenceValidation.Valid;

    /// <summary>Registers a <c>field_invalid</c> issue for an invalid field (no-op otherwise).</summary>
    /// <param name="issues">The issue list of the enclosing collection.</param>
    /// <param name="code">The stable issue code of the decoder.</param>
    /// <param name="fieldName">The schema field name (never a value).</param>
    /// <param name="observed">The field.</param>
    public static void NoteInvalid<T>(List<EvidenceIssue> issues, string code, string fieldName, Observed<T> observed)
    {
        if (observed.State == FieldState.Invalid)
        {
            issues.Add(new EvidenceIssue(
                code,
                FormattableString.Invariant($"'{fieldName}' was sent with the wrong JSON type; no substitute value is exposed."),
                observed.Sources));
        }
    }

    /// <summary>
    /// The validation verdict of a row set: <see cref="EvidenceValidation.Valid"/> when every row is an object and no
    /// field is invalid (missing fields do not demote), <see cref="EvidenceValidation.Partial"/> when some rows were
    /// interpreted but a row or a field was invalid, <see cref="EvidenceValidation.Malformed"/> when rows exist and
    /// none is an object. An empty list is Valid (observed empty).
    /// </summary>
    public static EvidenceValidation RowSetValidation(int rowCount, int interpretedRowCount, bool anyInvalid)
    {
        if (rowCount == 0)
        {
            return EvidenceValidation.Valid;
        }

        if (interpretedRowCount == 0)
        {
            return EvidenceValidation.Malformed;
        }

        return anyInvalid ? EvidenceValidation.Partial : EvidenceValidation.Valid;
    }

    /// <summary>A provided collection that could not be interpreted at all (no items).</summary>
    public static EvidenceSet<T> MalformedSet<T>(SourceRef source, ImmutableArray<EvidenceIssue> issues = default) =>
        EvidenceSet<T>.Provided(ImmutableArray<T>.Empty, EvidenceValidation.Malformed, EvidenceCoverage.Unknown, EvidenceScope.Unknown, One(source), issues);

    /// <summary>Builds an issue from a code, a safe message and its source pointers.</summary>
    public static EvidenceIssue Issue(string code, string safeMessage, params SourceRef[] sources) =>
        new(code, safeMessage, sources.ToImmutableArray());

    /// <summary>A deterministic local observation id (<c>prefix-i</c> or <c>prefix-i-j-…</c>); never a provider id.</summary>
    public static string ObservationId(string prefix, params int[] ordinals)
    {
        var id = prefix;
        foreach (var ordinal in ordinals)
        {
            id += "-" + ordinal.ToString(CultureInfo.InvariantCulture);
        }

        return id;
    }
}
