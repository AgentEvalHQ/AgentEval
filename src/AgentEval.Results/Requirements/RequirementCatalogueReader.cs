// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Requirements;

/// <summary>Bounded strict-JSON catalogue validation; uses the existing schema engine without modifying AEF schemas.</summary>
public sealed class RequirementCatalogueReader : IRequirementCatalogueReader
{
    /// <summary>Maximum source and normalized document size (1 MiB).</summary>
    public const int MaxBytes = 1024 * 1024;
    private static readonly Lazy<AefSchemaValidator> Schema = new(() =>
    {
        using var input = typeof(RequirementCatalogueReader).Assembly.GetManifestResourceStream(
            "AgentEval.Results.Requirements.catalogue.schema.json")!;
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return AefSchemaValidator.Load([new KeyValuePair<string, byte[]>("catalogue.schema.json", buffer.ToArray())]);
    });

    /// <inheritdoc />
    public RequirementCatalogue Read(ReadOnlyMemory<byte> utf8Json, ReadOnlyMemory<byte>? sourceBytes = null)
    {
        var source = sourceBytes ?? utf8Json;
        if (utf8Json.Length > MaxBytes || source.Length > MaxBytes)
            throw Error("$", "source exceeds 1 MiB", "Split the catalogue into smaller explicit inputs.");
        JsonObject root;
        try { root = AefJsonReader.ParseDocument(utf8Json.Span); }
        catch (AefReadException e) { throw Error("$", e.Message, "Use one strict UTF-8 JSON object with unique property names."); }
        if (Schema.Value.Validate("catalogue", root) is { } why)
            throw Error("$", why, "Use catalogue schema 1.0 with explicit selectors, acceptance, aggregation and freshness.");

        var definitions = new List<RequirementDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, index) in root["requirements"]!.AsArray().Select((item, index) => (item!, index)))
        {
            var at = $"$.requirements[{index}]";
            var id = (string)item["id"]!;
            if (!ids.Add(id)) throw Error(at + ".id", $"duplicate requirement '{id}'", "Give each namespace:name a unique ID.");
            var selector = item["selector"]!;
            var lane = Nonblank(selector["lane"], at + ".selector.lane");
            var metric = Nonblank(selector["metric"], at + ".selector.metric");
            var path = Nonblank(selector["path"], at + ".selector.path");
            var suite = selector["suiteRef"] is null ? null : Nonblank(selector["suiteRef"], at + ".selector.suiteRef");
            var digest = (string?)selector["suiteDigest"];
            if (digest is not null && suite is null)
                throw Error(at + ".selector.suiteDigest", "digest without suiteRef", "Bind the digest to an explicit suite reference.");
            var deployment = selector["deployment"] is null ? null : Nonblank(selector["deployment"], at + ".selector.deployment");
            var acceptance = item["acceptance"]!;
            var kind = (string)acceptance["kind"]!;
            var overrides = (bool?)item["allowEffectiveOverrides"] ?? false;
            if (overrides && kind != "state")
                throw Error(at + ".allowEffectiveOverrides", "effective overrides with a numeric rule", "Only state rules can consume state overrides; numeric evidence is unchanged.");
            var states = acceptance["states"]?.AsArray().Select(n => (string)n!).ToArray() ?? [];
            var aggregation = item["aggregation"]!;
            definitions.Add(new(id, (string?)item["description"],
                new(lane, metric, path, suite, digest, deployment),
                new(kind, (string?)acceptance["operator"], (double?)acceptance["value"], Array.AsReadOnly(states)),
                new((string)aggregation["kind"]!, (double?)aggregation["minimum"]),
                checked((long)(double)item["freshnessSeconds"]!), overrides));
        }
        return new(Convert.ToHexString(SHA256.HashData(source.Span)).ToLowerInvariant(), definitions.AsReadOnly());
    }

    private static string Nonblank(JsonNode? node, string at)
    {
        var value = (string)node!;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw Error(at, "blank or padded selector", "Use the exact nonblank identifier without surrounding whitespace.");
        return value;
    }

    private static RequirementCatalogueException Error(string at, string actual, string suggestion) =>
        new($"{at}: Expected a valid requirement catalogue. Actual: {actual}. Suggestions: {suggestion} Because requirement status must use unambiguous author-supplied rules.");
}
