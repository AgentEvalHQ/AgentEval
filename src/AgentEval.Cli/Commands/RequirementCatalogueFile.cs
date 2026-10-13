// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Requirements;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace AgentEval.Cli.Commands;

/// <summary>Local file-format boundary; the requirement contract and resolver remain dependency-free.</summary>
internal static class RequirementCatalogueFile
{
    private const int MaxNodes = 20000;
    internal static RequirementCatalogue Read(string path, IRequirementCatalogueReader? reader = null)
    {
        var bytes = ReadBytes(path);
        reader ??= new RequirementCatalogueReader();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".json") return reader.Read(bytes);
        if (extension is not (".yaml" or ".yml"))
            throw new RequirementCatalogueException("Expected .json, .yaml or .yml catalogue. Actual: unsupported extension. Suggestions: use an explicit supported extension. Because the format is never guessed.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new RequirementCatalogueException("Expected UTF-8 catalogue. Actual: invalid encoding. Suggestions: save as UTF-8. Because source identity is byte-exact."); }
        var json = new YamlInput(text).Read();
        return reader.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), bytes);
    }

    internal static byte[] ReadBytes(string path)
    {
        // Read at most the bound plus one byte even if a file grows while being read.
        using var input = File.OpenRead(path);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (buffer.Length <= RequirementCatalogueReader.MaxBytes)
        {
            var count = input.Read(block, 0, Math.Min(block.Length, RequirementCatalogueReader.MaxBytes + 1 - (int)buffer.Length));
            if (count == 0) break;
            buffer.Write(block, 0, count);
        }
        if (buffer.Length > RequirementCatalogueReader.MaxBytes)
            throw new RequirementCatalogueException("Expected at most 1 MiB. Actual: source exceeds limit. Suggestions: split the catalogue. Because loading must be bounded.");
        return buffer.ToArray();
    }

    // Parse events rather than an alias-resolving object graph; reject anchors/tags before allocation/expansion.
    private sealed class YamlInput(string text)
    {
        private readonly Parser _parser = new(new StringReader(text));
        private int _nodes;

        public JsonObject Read()
        {
            Next(); Take<StreamStart>(); Take<DocumentStart>();
            var root = Node(0) as JsonObject ?? throw Bad("The catalogue root must be a mapping.");
            Take<DocumentEnd>();
            if (_parser.Current is not StreamEnd) throw Bad("Exactly one YAML document is supported.");
            return root;
        }

        private JsonNode? Node(int depth)
        {
            if (depth > 32 || ++_nodes > MaxNodes) throw Bad("YAML exceeds 32 levels or 20,000 nodes.");
            if (_parser.Current is AnchorAlias) throw Bad("YAML aliases are not supported.");
            if (_parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw Bad("YAML anchors and explicit tags are not supported.");
            if (_parser.Current is MappingStart)
            {
                Next();
                var map = new JsonObject();
                while (_parser.Current is not MappingEnd)
                {
                    if (_parser.Current is not Scalar key || key.Value is null || !key.Anchor.IsEmpty || !key.Tag.IsEmpty)
                        throw Bad("Mapping keys must be plain strings without anchors or tags.");
                    if (key.Value == "<<" || map.ContainsKey(key.Value)) throw Bad("Merge keys and duplicate properties are not supported.");
                    Next(); map.Add(key.Value, Node(depth + 1));
                }
                Next(); return map;
            }
            if (_parser.Current is SequenceStart)
            {
                Next();
                var items = new JsonArray();
                while (_parser.Current is not SequenceEnd) items.Add(Node(depth + 1));
                Next(); return items;
            }
            if (_parser.Current is not Scalar scalar) throw Bad("Expected a scalar, mapping or sequence.");
            Next();
            var value = scalar.Value ?? "";
            if (scalar.Style == ScalarStyle.Plain)
            {
                if (value is "null" or "~" or "") return null;
                if (value is "true" or "false") return JsonValue.Create(value == "true");
                // Only JSON numeric spelling; YAML 1.1 yes/no, octal, infinities and timestamps are not coerced.
                if (value[0] is '-' or >= '0' and <= '9')
                {
                    try
                    {
                        var number = JsonNode.Parse(value);
                        if (number is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)) return number;
                    }
                    catch (System.Text.Json.JsonException) { }
                }
            }
            return JsonValue.Create(value);
        }

        private void Take<T>() where T : ParsingEvent
        {
            if (_parser.Current is not T) throw Bad($"Expected {typeof(T).Name}.");
            Next();
        }

        private void Next()
        {
            try { _parser.MoveNext(); }
            catch (YamlException e) { throw Bad(e.Message); }
        }

        private RequirementCatalogueException Bad(string actual)
        {
            var mark = _parser.Current?.Start;
            return new($"YAML line {mark?.Line}, column {mark?.Column}: Expected bounded catalogue YAML. Actual: {actual} Suggestions: use one document without aliases, tags or duplicate keys. Because requirement definitions must be unambiguous.");
        }
    }
}
