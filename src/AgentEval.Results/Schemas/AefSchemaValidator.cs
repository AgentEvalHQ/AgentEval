// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Results.Json;

namespace AgentEval.Results.Schemas;

/// <summary>A schema this validator does not take: a keyword, a <c>$ref</c> or a pattern outside what AEF's schemas use.</summary>
public sealed class AefSchemaException(string message) : Exception(message);

/// <summary>
/// One set of AEF 1.0 schemas (the writer set or the reader set, contracts/aef/1/schemas/), compiled together so their
/// relative <c>$ref</c>s resolve, and a JSON Schema 2020-12 validator for exactly the keywords those schemas use. A
/// schema with any other keyword is refused when the set is loaded, so a keyword a later schema adds can never be
/// silently ignored.
/// </summary>
/// <remarks>
/// <para>Where AEF pins down what JSON Schema leaves to the implementation, this validator follows AEF:</para>
/// <list type="bullet">
/// <item>Numbers are binary64 values and compare as such ([ENC-4]); <c>integer</c> is a number with no fractional part,
/// so 2.0 and 2e0 are integers.</item>
/// <item>String lengths count code points, as JSON Schema defines them (not UTF-16 units, not graphemes).</item>
/// <item>Patterns are ECMA-262 without lookaround, backreferences or possessive forms ([ENC-14]); a pattern using a
/// construct whose meaning differs between ECMA-262 and .NET (<c>\d</c>, <c>\w</c>, <c>\s</c>, <c>\b</c>, an unescaped
/// <c>.</c>, <c>(?</c> other than <c>(?:</c>, a <c>$</c> before the end) is refused. A final <c>$</c> is compiled as
/// <c>\z</c>, the end of the input ([ENC-15]): .NET's <c>$</c> also matches before a final newline. Patterns run on
/// .NET's non-backtracking engine, in time linear in the input, where it takes them (.NET 8's refuses a large counted
/// repetition such as <c>{1,256}</c>; there the backtracking engine runs them, with a time bound).</item>
/// <item><c>format</c> is an annotation, never asserted ([ENC-16]); a time is checked by its pattern, and
/// <c>common.schema.json#/$defs/timestamp</c> also refuses a date that does not exist ([ENC-8]: a pattern cannot
/// refuse <c>2026-02-31</c>; §3.9 reports it as <c>schema</c>).</item>
/// <item><c>$id</c>s are names, never fetched ([ENC-12]): a <c>$ref</c> is <c>#&lt;pointer&gt;</c> or
/// <c>&lt;file&gt;#&lt;pointer&gt;</c> for a file of the same set.</item>
/// </list>
/// </remarks>
public sealed class AefSchemaValidator
{
    /// <summary>The JSON Schema dialect every AEF schema declares.</summary>
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The definition of an AEF time, which also checks that its date exists ([ENC-8]).</summary>
    public const string TimestampDefinition = "common.schema.json#/$defs/timestamp";

    private const string SchemaSuffix = ".schema.json";

    // Every keyword the AEF 1.0 schemas use, and nothing else. Annotations ($schema, $id, $defs, title, description,
    // format) are accepted and take no part in validation.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$defs", "$ref", "title", "description", "format",
        "type", "enum", "const",
        "properties", "additionalProperties", "required", "minProperties",
        "items", "minItems", "maxItems", "uniqueItems",
        "minLength", "maxLength", "pattern",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "allOf", "anyOf", "oneOf", "not", "if", "then",
    };

    private static readonly Dictionary<string, Kinds> TypeNames = new(StringComparer.Ordinal)
    {
        ["null"] = Kinds.Null,
        ["boolean"] = Kinds.Boolean,
        ["object"] = Kinds.Object,
        ["array"] = Kinds.Array,
        ["number"] = Kinds.Number | Kinds.Integer,
        ["integer"] = Kinds.Integer,
        ["string"] = Kinds.String,
    };

    // Where the backtracking engine is used, a bound on one match (AEF's patterns are linear in their input).
    private static readonly TimeSpan BacktrackingTimeout = TimeSpan.FromSeconds(10);

    // Patterns repeat across the schemas and across the writer and reader sets; a Regex is safe to share.
    private static readonly ConcurrentDictionary<string, Regex> CompiledPatterns = new(StringComparer.Ordinal);

    // Compiled schemas by location: "<file>#<JSON pointer>", the root of a file being "<file>#".
    private readonly Dictionary<string, Schema> _schemas = new(StringComparer.Ordinal);

    private AefSchemaValidator()
    {
    }

    [Flags]
    private enum Kinds
    {
        None = 0,
        Null = 1,
        Boolean = 2,
        Object = 4,
        Array = 8,
        Number = 16,    // a number with a fractional part
        Integer = 32,   // a number without one
        String = 64,
    }

    /// <summary>The schema names this set holds (<c>run</c>, <c>result</c>, …), in byte order.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Compiles a set of schemas.</summary>
    /// <param name="files">Each schema's file name (<c>run.schema.json</c>) and bytes.</param>
    /// <exception cref="AefSchemaException">A file that is not a schema this validator takes.</exception>
    public static AefSchemaValidator Load(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var validator = new AefSchemaValidator();
        var names = new List<string>();
        var compiler = new Compiler(validator._schemas);
        foreach (var (file, bytes) in files)
        {
            if (!IsFileName(file))
            {
                throw new AefSchemaException($"'{file}' is not a schema file name (<name>{SchemaSuffix}).");
            }

            JsonObject root;
            try
            {
                root = AefJsonReader.ParseDocument(bytes);
            }
            catch (AefReadException e)
            {
                throw new AefSchemaException($"{file}: {e.Message}");
            }

            if ((string?)root["$schema"] != Dialect)
            {
                throw new AefSchemaException($"{file}: $schema is not {Dialect}.");
            }

            compiler.Compile(root, file, "", isRoot: true);
            names.Add(file[..^SchemaSuffix.Length]);
        }

        compiler.Link();
        if (validator._schemas.TryGetValue(TimestampDefinition, out var timestamp))
        {
            timestamp.Timestamp = true;
        }

        validator.Names = [.. names.Order(AefProblemOrder.Utf8)];
        return validator;
    }

    /// <summary>Whether <paramref name="name"/> names a schema of this set, or a subschema of one.</summary>
    public bool Has(string name) => name is not null && Location(name) is { } key && _schemas.ContainsKey(key);

    /// <summary>
    /// Whether <paramref name="instance"/> is valid against the schema <paramref name="name"/>: a schema
    /// (<c>run</c>) or a subschema of one (<c>decision#/$defs/input</c>).
    /// </summary>
    /// <exception cref="ArgumentException">No such schema in this set.</exception>
    public bool IsValid(string name, JsonNode? instance) => Validate(name, instance) is null;

    /// <summary>
    /// Null when <paramref name="instance"/> is valid against the schema <paramref name="name"/>; otherwise where the
    /// first failure is (an instance JSON pointer) and the keyword that failed, for a person to read.
    /// </summary>
    /// <exception cref="ArgumentException">No such schema in this set.</exception>
    public string? Validate(string name, JsonNode? instance)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Location(name) is not { } key || !_schemas.TryGetValue(key, out var schema))
        {
            throw new ArgumentException($"'{name}' is not a schema of this set (known: {string.Join(", ", Names)}).", nameof(name));
        }

        var failure = new Failure();
        return Evaluate(schema, instance, "", failure, 0) ? null : failure.Message ?? $"not valid against {name}";
    }

    /// <summary>
    /// A pattern as this validator compiles it ([ENC-14], [ENC-15]): refused when it uses a construct outside the
    /// portable subset, and with a final <c>$</c> matching only at the end of the input.
    /// </summary>
    /// <exception cref="AefSchemaException">A construct outside the portable subset.</exception>
    public static Regex CompilePattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return CompiledPatterns.GetOrAdd(pattern, BuildPattern);
    }

    private static Regex BuildPattern(string pattern)
    {
        var final = CheckPortable(pattern);
        var source = final ? pattern[..^1] + "\\z" : pattern;
        Regex backtracking;
        try
        {
            backtracking = new Regex(source, RegexOptions.CultureInvariant, BacktrackingTimeout);
        }
        catch (ArgumentException e)
        {
            throw new AefSchemaException($"The pattern '{pattern}' does not compile: {e.Message}");
        }

        try
        {
            return new Regex(source, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            // .NET 8's non-backtracking engine refuses a large counted repetition ({1,256}); .NET 9 takes it. The
            // portable subset has no construct the two engines read differently, so only the cost differs.
            return backtracking;
        }
    }

    private static bool IsFileName(string file) =>
        file.EndsWith(SchemaSuffix, StringComparison.Ordinal) && file.Length > SchemaSuffix.Length
        && file.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.');

    // "run" → "run.schema.json#", "decision#/$defs/input" → "decision.schema.json#/$defs/input".
    private static string? Location(string name)
    {
        var hash = name.IndexOf('#', StringComparison.Ordinal);
        var file = (hash < 0 ? name : name[..hash]) + SchemaSuffix;
        var pointer = hash < 0 ? "" : name[(hash + 1)..];
        return IsFileName(file) && (pointer.Length == 0 || pointer[0] == '/') ? $"{file}#{pointer}" : null;
    }

    // ENC-14 and ENC-15: true when the pattern ends in an unescaped $ (compiled as \z).
    private static bool CheckPortable(string pattern)
    {
        void Refuse(string why) => throw new AefSchemaException($"The pattern '{pattern}' {why} ([ENC-14]).");

        var inClass = false;
        var endAnchor = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            var next = i + 1 < pattern.Length ? pattern[i + 1] : '\0';
            if (c == '\\')
            {
                if (i + 1 == pattern.Length) Refuse("ends in a backslash");
                if ("dDwWsSbBpP".Contains(next, StringComparison.Ordinal)) Refuse($"uses \\{next}, whose meaning differs between engines");
                if (next is >= '1' and <= '9' || next == 'k') Refuse("uses a backreference");
                i++;
                continue;
            }

            if (inClass)
            {
                if (c == '-' && next == '[') Refuse("uses a class subtraction");
                if (c == ']') inClass = false;
                continue;
            }

            switch (c)
            {
                case '[':
                    if (next == ']' || (next == '^' && i + 2 < pattern.Length && pattern[i + 2] == ']')) Refuse("uses an empty class");
                    inClass = true;
                    if (next == '^') i++;
                    break;
                case '(' when next == '?':
                    if (i + 2 >= pattern.Length || pattern[i + 2] != ':') Refuse("uses a lookaround, a named group or an inline option");
                    break;
                case '.':
                    Refuse("uses '.', whose line terminators differ between engines");
                    break;
                case '*' or '+' or '?' or '}' when next == '+':
                    Refuse("uses a possessive quantifier");
                    break;
                case '$':
                    // An escaped \$ never reaches here: it is a dollar sign, not an anchor.
                    if (i != pattern.Length - 1) Refuse("uses '$' before its end");
                    endAnchor = true;
                    break;
            }
        }

        if (inClass) Refuse("leaves a class open");
        return endAnchor;
    }

    private bool Evaluate(Schema s, JsonNode? x, string at, Failure? failure, int depth)
    {
        if (depth > 256)
        {
            throw new InvalidOperationException($"{s.Location}: $ref recursion without progress.");
        }

        bool Fail(string keyword, string detail = "")
        {
            failure?.Set($"{(at.Length == 0 ? "/" : at)}: {keyword}{(detail.Length > 0 ? " " + detail : "")} ({s.Location})");
            return false;
        }

        if (s.Never)
        {
            return Fail("false");
        }

        var kind = KindOf(x, out var number, out var text);
        if (s.Types != Kinds.None && (s.Types & kind) == 0)
        {
            return Fail("type", $"is {Describe(kind)}");
        }

        if (s.HasConst && !JsonEquals(x, s.Const))
        {
            return Fail("const");
        }

        if (s.Enum is { } values && !values.Any(v => JsonEquals(x, v)))
        {
            return Fail("enum", text is not null ? $"'{text}'" : "");
        }

        if (s.Ref is { } target && !Evaluate(target, x, at, failure, depth + 1))
        {
            return false;
        }

        switch (x)
        {
            case JsonObject obj:
                if (s.MinProperties is { } minProperties && obj.Count < minProperties)
                {
                    return Fail("minProperties");
                }

                foreach (var required in s.Required)
                {
                    if (!obj.ContainsKey(required))
                    {
                        return Fail("required", required);
                    }
                }

                foreach (var (name, value) in obj)
                {
                    var where = $"{at}/{Escape(name)}";
                    if (s.Properties is not null && s.Properties.TryGetValue(name, out var property))
                    {
                        if (!Evaluate(property, value, where, failure, depth + 1)) return false;
                    }
                    else if (s.AdditionalProperties is { } additional && !Evaluate(additional, value, where, failure, depth + 1))
                    {
                        return false;
                    }
                }

                break;
            case JsonArray array:
                if (array.Count < s.MinItems) return Fail("minItems");
                if (array.Count > s.MaxItems) return Fail("maxItems");
                if (s.Items is { } items)
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (!Evaluate(items, array[i], $"{at}/{i}", failure, depth + 1)) return false;
                    }
                }

                if (s.UniqueItems)
                {
                    for (var i = 1; i < array.Count; i++)
                    {
                        for (var j = 0; j < i; j++)
                        {
                            if (JsonEquals(array[i], array[j])) return Fail("uniqueItems", $"items {j} and {i} are equal");
                        }
                    }
                }

                break;
            default:
                if (text is not null)
                {
                    if (s.MinLength > 0 || s.MaxLength < int.MaxValue)
                    {
                        var length = CodePoints(text);
                        if (length < s.MinLength) return Fail("minLength");
                        if (length > s.MaxLength) return Fail("maxLength");
                    }

                    if (s.Pattern is { } pattern && !pattern.IsMatch(text))
                    {
                        return Fail("pattern", s.PatternSource!);
                    }

                    if (s.Timestamp && !ExistingTime(text))
                    {
                        return Fail("timestamp", "is not a time that exists ([ENC-8])");
                    }
                }
                else if (kind is Kinds.Number or Kinds.Integer)
                {
                    if (number < s.Minimum) return Fail("minimum");
                    if (number > s.Maximum) return Fail("maximum");
                    if (s.ExclusiveMinimum is { } exclusiveMinimum && number <= exclusiveMinimum) return Fail("exclusiveMinimum");
                    if (s.ExclusiveMaximum is { } exclusiveMaximum && number >= exclusiveMaximum) return Fail("exclusiveMaximum");
                }

                break;
        }

        foreach (var all in s.AllOf)
        {
            if (!Evaluate(all, x, at, failure, depth + 1)) return false;
        }

        if (s.AnyOf.Length > 0 && !s.AnyOf.Any(any => Evaluate(any, x, at, null, depth + 1)))
        {
            return Fail("anyOf", "matches none");
        }

        if (s.OneOf.Length > 0)
        {
            var matched = s.OneOf.Count(one => Evaluate(one, x, at, null, depth + 1));
            if (matched != 1) return Fail("oneOf", $"matches {matched}");
        }

        if (s.Not is { } not && Evaluate(not, x, at, null, depth + 1))
        {
            return Fail("not");
        }

        if (s.If is { } condition && s.Then is { } then && Evaluate(condition, x, at, null, depth + 1)
            && !Evaluate(then, x, at, failure, depth + 1))
        {
            return false;
        }

        return true;
    }

    private static bool ExistingTime(string text)
    {
        try
        {
            AefTime.Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Kinds KindOf(JsonNode? node, out double number, out string? text)
    {
        number = 0;
        text = null;
        switch (node)
        {
            case null:
                return Kinds.Null;
            case JsonObject:
                return Kinds.Object;
            case JsonArray:
                return Kinds.Array;
        }

        var value = (JsonValue)node;
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                text = value.TryGetValue<string>(out var s) ? s : JsonDocument.Parse(value.ToJsonString()).RootElement.GetString();
                return Kinds.String;
            case JsonValueKind.Number:
                number = Number(value);
                return Math.Floor(number) == number ? Kinds.Integer : Kinds.Number;
            case JsonValueKind.True or JsonValueKind.False:
                return Kinds.Boolean;
            default:
                return Kinds.Null;
        }
    }

    // The binary64 value of a number ([ENC-4]), however the node holds it.
    private static double Number(JsonValue value) =>
        value.TryGetValue<double>(out var d) ? d : double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Describe(Kinds kind) => kind switch
    {
        Kinds.Integer or Kinds.Number => "a number",
        _ => "a" + (kind is Kinds.Object or Kinds.Array ? "n " : " ") + kind.ToString().ToLowerInvariant(),
    };

    /// <summary>JSON Schema's equality: the same type and value, numbers compared as binary64, objects regardless of member order.</summary>
    internal static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        var kindA = KindOf(a, out var numberA, out var textA);
        var kindB = KindOf(b, out var numberB, out var textB);
        if ((kindA is Kinds.Number or Kinds.Integer) && (kindB is Kinds.Number or Kinds.Integer))
        {
            return numberA == numberB;
        }

        if (kindA != kindB)
        {
            return false;
        }

        switch (kindA)
        {
            case Kinds.String:
                return string.Equals(textA, textB, StringComparison.Ordinal);
            case Kinds.Boolean:
                return a!.GetValue<bool>() == b!.GetValue<bool>();
            case Kinds.Object:
                var objectA = (JsonObject)a!;
                var objectB = (JsonObject)b!;
                return objectA.Count == objectB.Count
                       && objectA.All(m => objectB.TryGetPropertyValue(m.Key, out var other) && JsonEquals(m.Value, other));
            case Kinds.Array:
                var arrayA = (JsonArray)a!;
                var arrayB = (JsonArray)b!;
                return arrayA.Count == arrayB.Count && arrayA.Zip(arrayB).All(p => JsonEquals(p.First, p.Second));
            default:
                return true;   // null
        }
    }

    private static int CodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes()) count++;
        return count;
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    // The first failure, for a person to read.
    private sealed class Failure
    {
        public string? Message { get; private set; }

        public void Set(string message) => Message ??= message;
    }

    // One compiled schema: the keywords it carries, as this validator uses them.
    private sealed class Schema(string location)
    {
        public string Location { get; } = location;

        public bool Never { get; set; }

        public Kinds Types { get; set; }

        public bool HasConst { get; set; }

        public JsonNode? Const { get; set; }

        public JsonNode?[]? Enum { get; set; }

        public Dictionary<string, Schema>? Properties { get; set; }

        public Schema? AdditionalProperties { get; set; }

        public string[] Required { get; set; } = [];

        public int? MinProperties { get; set; }

        public Schema? Items { get; set; }

        public int MinItems { get; set; }

        public int MaxItems { get; set; } = int.MaxValue;

        public bool UniqueItems { get; set; }

        public int MinLength { get; set; }

        public int MaxLength { get; set; } = int.MaxValue;

        public Regex? Pattern { get; set; }

        public string? PatternSource { get; set; }

        public double? Minimum { get; set; }

        public double? Maximum { get; set; }

        public double? ExclusiveMinimum { get; set; }

        public double? ExclusiveMaximum { get; set; }

        public Schema[] AllOf { get; set; } = [];

        public Schema[] AnyOf { get; set; } = [];

        public Schema[] OneOf { get; set; } = [];

        public Schema? Not { get; set; }

        public Schema? If { get; set; }

        public Schema? Then { get; set; }

        public string? RefLocation { get; set; }

        public Schema? Ref { get; set; }

        public bool Timestamp { get; set; }
    }

    // Compiles schema documents into Schemas by location, then links each $ref to the Schema at its location.
    private sealed class Compiler(Dictionary<string, Schema> schemas)
    {
        private readonly List<Schema> _references = [];

        public Schema Compile(JsonNode? node, string file, string pointer, bool isRoot = false)
        {
            var location = $"{file}#{pointer}";
            var schema = new Schema(location);
            if (!schemas.TryAdd(location, schema))
            {
                throw new AefSchemaException($"{location}: compiled twice (a file listed twice?).");
            }

            if (node is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            {
                schema.Never = !boolean.GetValue<bool>();
                return schema;
            }

            if (node is not JsonObject obj)
            {
                throw new AefSchemaException($"{location}: a schema is an object or a boolean.");
            }

            foreach (var (keyword, value) in obj)
            {
                if (!Keywords.Contains(keyword))
                {
                    throw new AefSchemaException($"{location}: the keyword '{keyword}' is not one this validator supports (an AEF schema uses only {Keywords.Count} keywords).");
                }

                if (keyword is "$schema" or "$id" && !isRoot)
                {
                    throw new AefSchemaException($"{location}: '{keyword}' is allowed only at the root of a schema file.");
                }

                var at = $"{pointer}/{Escape(keyword)}";
                switch (keyword)
                {
                    case "$defs":
                        foreach (var (name, definition) in AsObject(value, location, keyword))
                        {
                            Compile(definition, file, $"{at}/{Escape(name)}");
                        }

                        break;
                    case "$ref":
                        schema.RefLocation = Reference(Text(value, location, keyword), file, location);
                        _references.Add(schema);
                        break;
                    case "type":
                        schema.Types = value is JsonArray types
                            ? types.Select(t => Type(t, location)).Aggregate(Kinds.None, (a, b) => a | b)
                            : Type(value, location);
                        break;
                    case "enum":
                        schema.Enum = [.. AsArray(value, location, keyword).Select(v => v?.DeepClone())];
                        break;
                    case "const":
                        schema.HasConst = true;
                        schema.Const = value?.DeepClone();
                        break;
                    case "properties":
                        schema.Properties = new Dictionary<string, Schema>(StringComparer.Ordinal);
                        foreach (var (name, property) in AsObject(value, location, keyword))
                        {
                            schema.Properties[name] = Compile(property, file, $"{at}/{Escape(name)}");
                        }

                        break;
                    case "additionalProperties":
                        schema.AdditionalProperties = Compile(value, file, at);
                        break;
                    case "required":
                        schema.Required = [.. AsArray(value, location, keyword).Select(r => Text(r, location, keyword))];
                        break;
                    case "minProperties":
                        schema.MinProperties = Count(value, location, keyword);
                        break;
                    case "items":
                        schema.Items = Compile(value, file, at);
                        break;
                    case "minItems":
                        schema.MinItems = Count(value, location, keyword);
                        break;
                    case "maxItems":
                        schema.MaxItems = Count(value, location, keyword);
                        break;
                    case "uniqueItems":
                        schema.UniqueItems = value is JsonValue unique && unique.GetValueKind() is JsonValueKind.True;
                        break;
                    case "minLength":
                        schema.MinLength = Count(value, location, keyword);
                        break;
                    case "maxLength":
                        schema.MaxLength = Count(value, location, keyword);
                        break;
                    case "pattern":
                        schema.PatternSource = Text(value, location, keyword);
                        schema.Pattern = CompilePattern(schema.PatternSource);
                        break;
                    case "minimum":
                        schema.Minimum = Bound(value, location, keyword);
                        break;
                    case "maximum":
                        schema.Maximum = Bound(value, location, keyword);
                        break;
                    case "exclusiveMinimum":
                        schema.ExclusiveMinimum = Bound(value, location, keyword);
                        break;
                    case "exclusiveMaximum":
                        schema.ExclusiveMaximum = Bound(value, location, keyword);
                        break;
                    case "allOf":
                        schema.AllOf = Each(value, file, at, location, keyword);
                        break;
                    case "anyOf":
                        schema.AnyOf = Each(value, file, at, location, keyword);
                        break;
                    case "oneOf":
                        schema.OneOf = Each(value, file, at, location, keyword);
                        break;
                    case "not":
                        schema.Not = Compile(value, file, at);
                        break;
                    case "if":
                        schema.If = Compile(value, file, at);
                        break;
                    case "then":
                        schema.Then = Compile(value, file, at);
                        break;
                }
            }

            return schema;
        }

        public void Link()
        {
            foreach (var schema in _references)
            {
                schema.Ref = schemas.TryGetValue(schema.RefLocation!, out var target)
                    ? target
                    : throw new AefSchemaException($"{schema.Location}: $ref '{schema.RefLocation}' names no schema of this set.");
            }
        }

        private Schema[] Each(JsonNode? value, string file, string at, string location, string keyword)
        {
            var list = AsArray(value, location, keyword);
            if (list.Count == 0) throw new AefSchemaException($"{location}: '{keyword}' is empty.");
            return [.. list.Select((item, i) => Compile(item, file, $"{at}/{i}"))];
        }

        // "#/x" in file f → "f#/x"; "other.schema.json#/x" → "other.schema.json#/x". Nothing else: no URI is fetched.
        private static string Reference(string reference, string file, string location)
        {
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            var target = hash < 0 ? reference : reference[..hash];
            var pointer = hash < 0 ? "" : Uri.UnescapeDataString(reference[(hash + 1)..]);
            if (target.Length == 0)
            {
                target = file;
            }

            if (!IsFileName(target) || (pointer.Length > 0 && pointer[0] != '/'))
            {
                throw new AefSchemaException($"{location}: $ref '{reference}' is not '#<pointer>' or '<file>{SchemaSuffix}#<pointer>' ([ENC-12]: $ids are names, never fetched).");
            }

            return $"{target}#{pointer}";
        }

        private static Kinds Type(JsonNode? value, string location) =>
            value is JsonValue v && v.TryGetValue<string>(out var name) && TypeNames.TryGetValue(name, out var kind)
                ? kind
                : throw new AefSchemaException($"{location}: 'type' {value?.ToJsonString()} is not a JSON Schema type.");

        private static string Text(JsonNode? value, string location, string keyword) =>
            value is JsonValue v && v.TryGetValue<string>(out var text)
                ? text
                : throw new AefSchemaException($"{location}: '{keyword}' takes a string.");

        private static JsonArray AsArray(JsonNode? value, string location, string keyword) =>
            value as JsonArray ?? throw new AefSchemaException($"{location}: '{keyword}' takes an array.");

        private static JsonObject AsObject(JsonNode? value, string location, string keyword) =>
            value as JsonObject ?? throw new AefSchemaException($"{location}: '{keyword}' takes an object.");

        private static double Bound(JsonNode? value, string location, string keyword) =>
            value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
                ? Number(v)
                : throw new AefSchemaException($"{location}: '{keyword}' takes a number.");

        private static int Count(JsonNode? value, string location, string keyword)
        {
            var number = Bound(value, location, keyword);
            return number >= 0 && number <= int.MaxValue && Math.Floor(number) == number
                ? (int)number
                : throw new AefSchemaException($"{location}: '{keyword}' takes a non-negative integer.");
        }
    }
}
