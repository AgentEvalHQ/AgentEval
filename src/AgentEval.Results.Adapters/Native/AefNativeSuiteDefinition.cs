// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>Shared definition binding for native probe/composite writers; results supply only the observed roster.</summary>
internal static class AefNativeSuiteDefinition
{
    internal sealed record Binding(AefSuite Suite, JsonObject Extensions, SuiteDefinitionManifest? Definition)
    {
        internal void Write(AefRunWriter writer)
        {
            if (Definition is not null) writer.PutExtFile(SuiteDefinitionManifest.ExtensionFile, Definition.ToBytes());
        }
    }

    internal static Binding Bind(string kind, string reference, string legacyVersion, IEnumerable<string> observedIds,
        SuiteDefinitionManifest? definition, JsonObject? extensions, bool partialAllowed, bool generated = false,
        AefExecutionPolicy? executionPolicy = null, IEnumerable<string>? legacyRoster = null)
    {
        var observed = observedIds.Distinct(StringComparer.Ordinal).ToArray();
        var digest = AefProbeRunWriter.RosterDigest(observed);
        var legacyDigest = legacyRoster is null ? digest : AefProbeRunWriter.RosterDigest(legacyRoster);
        if (definition is not null)
        {
            if (definition.Kind != kind || definition.SuiteRef != reference)
                throw Bad("definition kind or suite reference differs");
            if (definition.Frozen)
            {
                var planned = definition.CaseIds.ToHashSet(StringComparer.Ordinal);
                if (observed.Any(id => !planned.Contains(id))) throw Bad("observed case is outside the frozen planned roster");
                if (!partialAllowed && planned.Count != observed.Length) throw Bad("completed evidence omits planned cases");
            }
        }
        var ext = (JsonObject?)extensions?.DeepClone() ?? new JsonObject();
        ext["agenteval.suiteIdentity"] = definition?.Metadata() ?? new JsonObject
        {
            ["scope"] = legacyRoster is null ? "observed-roster" : "legacy-roster", ["digest"] = legacyDigest,
        };
        ext["agenteval.observedRoster"] = new JsonObject { ["digest"] = digest, ["count"] = observed.Length };
        return new(new AefSuite
        {
            Ref = reference, Version = definition?.DefinitionVersion ?? legacyVersion,
            Digest = definition is not null ? definition.SuiteContentDigest : generated ? null : legacyDigest,
            Frozen = definition?.Frozen ?? !generated, ExecutionPolicy = executionPolicy,
        }, ext, definition);
    }

    private static ArgumentException Bad(string actual) => new(
        $"Expected: a matching declared suite definition and planned roster. Actual: {actual}. Suggestions: bind the definition captured before execution and label interrupted evidence explicitly. Because: measured outcomes must not redefine the suite.");
}
