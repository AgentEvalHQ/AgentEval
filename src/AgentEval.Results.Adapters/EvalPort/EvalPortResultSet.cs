// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Interop;
using AgentEval.Results.Json;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Adapters.EvalPort;

/// <summary>
/// A detached EvalPort ResultSet checked against the pinned rc.5 schema and
/// duplicate-observation/null-score rules. Parsing does not validate a declared
/// aggregation policy or authenticate the producer.
/// </summary>
public sealed class EvalPortResultSet
{
    private static readonly Lazy<AefSchemaValidator> Schema = new(LoadSchema);
    private readonly JsonObject _document;
    private EvalPortResultSet(JsonObject document, EvalPortRunContext context)
    { _document = document; Context = context; }

    /// <summary>Declared run identity, before any AEF identifier mapping.</summary>
    public string RunId => _document["run_id"]!.GetValue<string>();
    /// <summary>Declared suite identity.</summary>
    public string SuiteId => _document["suite_id"]!.GetValue<string>();
    /// <summary>Run-level isolation/group declarations.</summary>
    public EvalPortRunContext Context { get; }
    /// <summary>A detached document preserving source numeric tokens.</summary>
    public JsonObject ToJson() => (JsonObject)_document.DeepClone();

    /// <summary>Reads at most 4 MiB of strict JSON and validates before conversion can write files.</summary>
    public static EvalPortResultSet Parse(ReadOnlySpan<byte> bytes)
    {
        var document = AefJsonReader.ParseDocument(bytes);
        if (Schema.Value.Validate("resultset", document) is { } error) throw Refuse(error);
        var version = document["version"]!.GetValue<string>();
        if (!version.StartsWith("1.", StringComparison.Ordinal)) throw Refuse("unsupported EvalPort major version");
        var context = EvalPortRunContext.FromResultSet(bytes);
        var seen = new HashSet<(string CaseId, long Attempt)>();
        foreach (var entry in document["results"]!.AsArray())
        {
            var result = entry!.AsObject();
            var attempt = result.ContainsKey("attempt") ? Integer(result["attempt"]!, minimum: 1) : 1;
            if (!seen.Add((result["test_case_id"]!.GetValue<string>(), attempt))) throw Refuse("duplicate case/attempt observation");
            if (result.ContainsKey("duration_ms")) Integer(result["duration_ms"]!, minimum: 0);
            var graders = new HashSet<string>(StringComparer.Ordinal);
            var measured = 0;
            foreach (var graderNode in result["grader_results"]!.AsArray())
            {
                var grader = graderNode!.AsObject();
                if (!graders.Add(grader["grader_id"]!.GetValue<string>())) throw Refuse("duplicate grader within one result");
                if (grader["score"] is null)
                {
                    if (grader["passed"]!.GetValue<bool>()) throw Refuse("null-score grader cannot pass");
                }
                else measured++;
            }
            if (measured == 0 && result["passed"]!.GetValue<bool>()) throw Refuse("result without a measured grader cannot pass");
        }
        return new(document, context);
    }

    // AEF trials/durations use Int64. Parse exact decimal integrality without rounding
    // tiny fractions into integers or large values through binary64.
    internal static long Integer(JsonNode node, long minimum)
    {
        var parts = node.ToJsonString().Split(['e', 'E']);
        var mantissa = parts[0]; var dot = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        if (digits.TrimStart('-').All(c => c == '0'))
        { if (minimum == 0) return 0; throw Refuse("integer below minimum"); }
        if (digits.StartsWith('-')) throw Refuse("negative integer");
        var exponent = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            throw Refuse("integer exponent out of range");
        long scale = (long)(dot < 0 ? 0 : mantissa.Length - dot - 1) - exponent;
        if (scale > 0)
        {
            if (scale > digits.Length || digits.AsSpan(digits.Length - (int)scale).IndexOfAnyExcept('0') >= 0) throw Refuse("fractional integer field");
            digits = digits[..(digits.Length - (int)scale)];
        }
        digits = digits.TrimStart('0');
        if (scale < 0)
        {
            if (digits.Length - scale > 19) throw Refuse("integer exceeds Int64 range");
            digits += new string('0', (int)-scale);
        }
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum)
            throw Refuse("integer outside supported range");
        return value;
    }

    private static AefSchemaValidator LoadSchema()
    {
        using var stream = typeof(EvalPortResultSet).Assembly.GetManifestResourceStream("AgentEval.Results.Adapters.EvalPort.resultset.schema.json")!;
        var root = JsonNode.Parse(stream)!.AsObject();
        // The upstream SemVer pattern uses JS \d (ASCII); .NET \d also accepts
        // Unicode digits. Normalize just this pattern for the existing strict engine.
        var version = root["properties"]!["version"]!;
        version["pattern"] = version["pattern"]!.GetValue<string>().Replace("\\d", "[0-9]", StringComparison.Ordinal);
        return AefSchemaValidator.Load([new("resultset.schema.json", Encoding.UTF8.GetBytes(root.ToJsonString()))]);
    }

    private static AefInputException Refuse(string actual) => new(
        $"Expected: supported EvalPort ResultSet with unique observations. Actual: {actual}. " +
        "Suggestions: correct the source before conversion. Because: import must not invent or merge evidence.");
}
