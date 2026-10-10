// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Schemas;

/// <summary>
/// The AEF 1.0 schemas (contracts/aef/1/schemas/), embedded in this assembly from those files and compiled on first use:
/// <see cref="Writer"/>, what a writer produces ([VER-2]: only known fields and enum values, its own version), and
/// <see cref="Reader"/>, what a reader accepts ([VER-3]: any 1.x minor, unknown fields, unknown enum values outside the
/// enums [VER-9] closes). A schema is named by its file (<c>run</c> for run.schema.json), or a subschema by a JSON
/// pointer into it (<c>decision#/$defs/input</c>).
/// </summary>
public static class AefSchemas
{
    private const string ResourcePrefix = "AgentEval.Results.Schemas.";

    private static readonly Lazy<AefSchemaValidator> WriterSet = new(() => AefSchemaValidator.Load(EmbeddedFiles("writer")));

    private static readonly Lazy<AefSchemaValidator> ReaderSet = new(() => AefSchemaValidator.Load(EmbeddedFiles("reader")));

    /// <summary>The writer schemas (strict).</summary>
    public static AefSchemaValidator Writer => WriterSet.Value;

    /// <summary>The reader schemas (tolerant), derived from the writer schemas by exactly the relaxations of [VER-3].</summary>
    public static AefSchemaValidator Reader => ReaderSet.Value;

    /// <summary>The schema names (<c>checkpoint</c>, <c>common</c>, … <c>summary</c>), in byte order.</summary>
    public static IReadOnlyList<string> Names => Writer.Names;

    /// <summary>The embedded schema files of one set (<c>writer</c> or <c>reader</c>): file name and bytes, in byte order of the name.</summary>
    internal static IReadOnlyList<KeyValuePair<string, byte[]>> EmbeddedFiles(string side)
    {
        var assembly = typeof(AefSchemas).Assembly;
        var prefix = $"{ResourcePrefix}{side}.";
        var files = new List<KeyValuePair<string, byte[]>>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            files.Add(new(resource[prefix.Length..], bytes.ToArray()));
        }

        return files.Count > 0
            ? [.. files.OrderBy(f => f.Key, AefProblemOrder.Utf8)]
            : throw new InvalidOperationException($"No {side} schema is embedded in {assembly.GetName().Name}.");
    }
}
