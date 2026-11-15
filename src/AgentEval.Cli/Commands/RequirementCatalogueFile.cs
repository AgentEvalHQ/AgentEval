// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using AgentEval.DataLoaders;
using AgentEval.Results.Requirements;

namespace AgentEval.Cli.Commands;

/// <summary>Local file-format boundary; the requirement contract and resolver remain dependency-free.</summary>
internal static class RequirementCatalogueFile
{
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
        try
        {
            var json = new BoundedYamlObjectReader(text).Read();
            return reader.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), bytes);
        }
        catch (InvalidDataException ex) { throw new RequirementCatalogueException(ex.Message); }
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

}
