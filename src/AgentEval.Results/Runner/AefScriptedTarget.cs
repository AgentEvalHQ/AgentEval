// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Runner;

/// <summary>
/// One case of a scripted target's suite (spec 09 §9.2.1 "A scripted target"): the fixed answer the target gives it.
/// </summary>
/// <param name="CaseId">The case's id, written unrewritten on its result line ([PLAN-8]).</param>
/// <param name="State">Its state: a state of [RES-1] other than <c>pending</c>.</param>
/// <param name="Severity">How bad its failure is ([RES-9]): on a <c>failed</c> or <c>warn</c> case, and on no other.</param>
/// <param name="Usd">Its cost, added to the spend when it completes.</param>
/// <param name="UsdBound">Its cost bound ([PLAN-9]), not below <paramref name="Usd"/>.</param>
/// <param name="Seconds">Its duration on the job's clock, in whole seconds.</param>
/// <param name="SecondsBound">Its time bound ([PLAN-9]), not below <paramref name="Seconds"/>, in whole seconds.</param>
public sealed record AefScriptedCase(string CaseId, AefState State, AefSeverity? Severity, double Usd, double UsdBound, long Seconds, long SecondsBound);

/// <summary>A suite a scripted target has: its <c>ref</c>, <c>version</c>, <c>content</c>, and its cases in the order it runs them.</summary>
public sealed record AefScriptedSuite(string Ref, string Version, string Content, IReadOnlyList<AefScriptedCase> Cases)
{
    /// <summary>The suite's digest ([PLAN-8], §9.2.1): <c>sha256:</c> and the lower-case hex SHA-256 of its content's UTF-8 bytes.</summary>
    public string Digest => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Content))).ToLowerInvariant();
}

/// <summary>
/// A scripted target (contracts/aef/1/spec/09-conformance.md §9.2.1): a test fixture that stands for the subject and
/// answers each case of its suites with a fixed state, cost and duration, on a clock that moves only as it says. A
/// <see cref="AefScriptedRunner"/> resolves a plan's suites in it and drives it (target mode <c>scripted</c>, [RUN-7]).
/// </summary>
public sealed class AefScriptedTarget
{
    private static readonly string[] TargetMembers = ["suites", "closeSeconds", "priceTable"];
    private static readonly string[] SuiteMembers = ["ref", "version", "content", "cases"];
    private static readonly string[] CaseMembers = ["caseId", "state", "usd", "usdBound", "seconds", "secondsBound"];
    private static readonly string[] OptionalCaseMembers = ["severity"];

    private AefScriptedTarget(IReadOnlyList<AefScriptedSuite> suites, long closeSeconds, string priceTable, string fingerprint)
    {
        Suites = suites;
        CloseSeconds = closeSeconds;
        PriceTable = priceTable;
        Fingerprint = fingerprint;
    }

    /// <summary>The suites it has, in the order the fixture lists them.</summary>
    public IReadOnlyList<AefScriptedSuite> Suites { get; }

    /// <summary>The time closing and sealing one run takes, in whole seconds.</summary>
    public long CloseSeconds { get; }

    /// <summary>The name of the price table its costs come from (1 to 64 characters), as a run's <c>costPolicy.priceTable</c> gives it.</summary>
    public string PriceTable { get; }

    /// <summary>The SHA-256 (lower-case hex) of the fixture as read, written compactly: what tells two fixtures apart.</summary>
    public string Fingerprint { get; }

    /// <summary>The suite of that <c>ref</c> and <c>version</c> (compared byte for byte), or null: how a runner resolves a plan's suite.</summary>
    public AefScriptedSuite? Find(string suiteRef, string version) =>
        Suites.FirstOrDefault(s => string.Equals(s.Ref, suiteRef, StringComparison.Ordinal) && string.Equals(s.Version, version, StringComparison.Ordinal));

    /// <summary>Reads a fixture from its bytes: an I-JSON document ([ENC-1]) of §9.2.1's shape (<see cref="Read(JsonNode)"/>).</summary>
    /// <exception cref="FormatException">Not an I-JSON document, or not of that shape.</exception>
    public static AefScriptedTarget Read(ReadOnlySpan<byte> utf8)
    {
        JsonObject document;
        try
        {
            document = AefJsonReader.ParseDocument(utf8);
        }
        catch (AefReadException e)
        {
            throw new FormatException($"The scripted target is not an I-JSON document: {e.Message}", e);
        }

        return Read(document);
    }

    /// <summary>
    /// Reads a fixture of §9.2.1's shape: an object with exactly <c>suites</c> (each with exactly <c>ref</c>,
    /// <c>version</c>, <c>content</c>, strings, and <c>cases</c>, one or more), <c>closeSeconds</c> (whole seconds, not
    /// negative) and <c>priceTable</c> (1 to 64 characters). A case has exactly <c>caseId</c> (a result line's case id:
    /// 1 to 256 characters, no control character), <c>state</c> (a state of [RES-1] other than <c>pending</c>),
    /// <c>severity</c> (a severity of [RES-9]) on a <c>failed</c> or <c>warn</c> case and on no other, <c>usd</c> (not
    /// negative) and <c>usdBound</c> (not below it), <c>seconds</c> and <c>secondsBound</c> (whole seconds, not
    /// negative, the bound not below the duration). No two suites have one <c>ref</c> and <c>version</c>, and no two
    /// cases of a suite one <c>caseId</c>. A member named nowhere here is not of the shape.
    /// </summary>
    /// <exception cref="FormatException">Anything else, naming where.</exception>
    public static AefScriptedTarget Read(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var target = Object(document, "the scripted target", TargetMembers);
        var suites = new List<AefScriptedSuite>();
        var i = 0;
        foreach (var item in Array(target["suites"], "suites"))
        {
            var where = $"suites[{i++}]";
            var suite = Object(item, where, SuiteMembers);
            var (suiteRef, version) = (Text(suite["ref"], $"{where}.ref"), Text(suite["version"], $"{where}.version"));
            if (suites.Any(s => s.Ref == suiteRef && s.Version == version))
            {
                throw new FormatException($"{where}: a second suite {suiteRef} version {version} (no two suites of a scripted target have one ref and version).");
            }

            var cases = new List<AefScriptedCase>();
            var j = 0;
            foreach (var caseItem in Array(suite["cases"], $"{where}.cases"))
            {
                var at = $"{where}.cases[{j++}]";
                var scripted = Case(Object(caseItem, at, CaseMembers, OptionalCaseMembers), at);
                if (cases.Any(c => c.CaseId == scripted.CaseId))
                {
                    throw new FormatException($"{at}: a second case {scripted.CaseId} in suite {suiteRef} (no two cases of a suite have one caseId).");
                }

                cases.Add(scripted);
            }

            if (cases.Count == 0)
            {
                throw new FormatException($"{where}.cases: a suite has one or more cases.");
            }

            suites.Add(new AefScriptedSuite(suiteRef, version, Text(suite["content"], $"{where}.content"), cases));
        }

        var priceTable = Text(target["priceTable"], "priceTable");
        if (priceTable.Length is 0 or > 64)
        {
            throw new FormatException("priceTable: a price table's name has 1 to 64 characters.");
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(AefJsonWriter.Compact(target))).ToLowerInvariant();
        return new AefScriptedTarget(suites, WholeSeconds(target["closeSeconds"], "closeSeconds"), priceTable, fingerprint);
    }

    private static AefScriptedCase Case(JsonObject json, string where)
    {
        var caseId = Text(json["caseId"], $"{where}.caseId");
        if (!AefSchemas.Writer.IsValid("result#/properties/caseId", json["caseId"]))
        {
            throw new FormatException($"{where}.caseId: a case id is 1 to 256 characters with no control character (a result line's caseId, [RES-4]).");
        }

        var stateName = Text(json["state"], $"{where}.state");
        if (!AefNames.TryParse<AefState>(stateName, out var state) || state == AefState.Pending)
        {
            throw new FormatException($"{where}.state: '{stateName}' is not a state of [RES-1] other than pending.");
        }

        AefSeverity? severity = null;
        if (json.ContainsKey("severity"))
        {
            var severityName = Text(json["severity"], $"{where}.severity");
            if (!AefNames.TryParse<AefSeverity>(severityName, out severity))
            {
                throw new FormatException($"{where}.severity: '{severityName}' is not a severity of [RES-9].");
            }
        }

        if ((state is AefState.Failed or AefState.Warn) != severity.HasValue)
        {
            throw new FormatException($"{where}: a failed or warn case has a severity, and no other case has one (§9.2.1).");
        }

        var usd = Number(json["usd"], $"{where}.usd");
        var usdBound = Number(json["usdBound"], $"{where}.usdBound");
        var seconds = WholeSeconds(json["seconds"], $"{where}.seconds");
        var secondsBound = WholeSeconds(json["secondsBound"], $"{where}.secondsBound");
        if (usdBound < usd)
        {
            throw new FormatException($"{where}: usdBound is below usd (a cost bound is not below the cost).");
        }

        if (secondsBound < seconds)
        {
            throw new FormatException($"{where}: secondsBound is below seconds (a time bound is not below the duration).");
        }

        return new AefScriptedCase(caseId, state.Value, severity, usd, usdBound, seconds, secondsBound);
    }

    // An object with these members, and optionally those: no other.
    private static JsonObject Object(JsonNode? node, string where, string[] members, string[]? optional = null)
    {
        if (node is not JsonObject obj)
        {
            throw new FormatException($"{where} is not an object.");
        }

        var known = members.Concat(optional ?? []).ToArray();
        if (obj.Select(m => m.Key).FirstOrDefault(k => !known.Contains(k, StringComparer.Ordinal)) is { } unknown)
        {
            throw new FormatException($"{where} has a member '{unknown}' a scripted target does not have (§9.2.1: {string.Join(", ", known)}).");
        }

        if (members.FirstOrDefault(m => !obj.ContainsKey(m)) is { } missing)
        {
            throw new FormatException($"{where} has no '{missing}'.");
        }

        return obj;
    }

    private static JsonArray Array(JsonNode? node, string where) =>
        node as JsonArray ?? throw new FormatException($"{where} is not a list.");

    private static string Text(JsonNode? node, string where) =>
        AefNode.String(node) ?? throw new FormatException($"{where} is not a string.");

    private static double Number(JsonNode? node, string where) =>
        AefNode.Number(node) is { } value && double.IsFinite(value) && value >= 0
            ? value
            : throw new FormatException($"{where} is not a number of US dollars, at least 0.");

    // Whole seconds: an integral number, written as an integer or not ([ENC-4]: 20.0 is 20), at least 0, at most 2^53 − 1.
    private static long WholeSeconds(JsonNode? node, string where)
    {
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && AefNode.Number(node) is { } value
            && value >= 0 && value <= 9_007_199_254_740_991 && Math.Floor(value) == value)
        {
            return (long)value;
        }

        throw new FormatException($"{where} is not a whole number of seconds, at least 0.");
    }
}
