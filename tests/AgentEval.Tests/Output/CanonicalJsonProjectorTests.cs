// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Pins plan-13 T1.7 (v1.1) canonical-JSON projection: same input semantics → same
/// canonical bytes regardless of original whitespace / property order / number format.
/// The audit-chain hash (computed over these canonical bytes) tolerates cosmetic
/// re-formatting while still catching value-level tampering.
/// </summary>
public class CanonicalJsonProjectorTests
{
    [Fact]
    public void ProjectString_PropertyReorder_ProducesIdenticalBytes()
    {
        var a = """{ "b": 1, "a": 2 }""";
        var b = """{ "a": 2, "b": 1 }""";

        var aBytes = CanonicalJsonProjector.ProjectString(a);
        var bBytes = CanonicalJsonProjector.ProjectString(b);

        Assert.Equal(aBytes, bBytes);
    }

    [Fact]
    public void ProjectString_WhitespaceOnly_ProducesIdenticalBytes()
    {
        var compact   = """{"a":1,"b":"x"}""";
        var indented  = "{\n  \"a\": 1,\n  \"b\":  \"x\"\n}";

        var compactBytes  = CanonicalJsonProjector.ProjectString(compact);
        var indentedBytes = CanonicalJsonProjector.ProjectString(indented);

        Assert.Equal(compactBytes, indentedBytes);
    }

    [Fact]
    public void ProjectString_ValueEdit_ProducesDifferentBytes()
    {
        var a = """{"verdict":"PASS"}""";
        var b = """{"verdict":"FAIL"}""";

        var aBytes = CanonicalJsonProjector.ProjectString(a);
        var bBytes = CanonicalJsonProjector.ProjectString(b);

        Assert.NotEqual(aBytes, bBytes);
    }

    [Fact]
    public void ProjectString_PropertyAdd_ProducesDifferentBytes()
    {
        var a = """{"verdict":"PASS"}""";
        var b = """{"verdict":"PASS","extra":42}""";

        var aBytes = CanonicalJsonProjector.ProjectString(a);
        var bBytes = CanonicalJsonProjector.ProjectString(b);

        Assert.NotEqual(aBytes, bBytes);
    }

    [Fact]
    public void ProjectString_PropertyRemove_ProducesDifferentBytes()
    {
        var a = """{"verdict":"PASS","score":1.0}""";
        var b = """{"verdict":"PASS"}""";

        var aBytes = CanonicalJsonProjector.ProjectString(a);
        var bBytes = CanonicalJsonProjector.ProjectString(b);

        Assert.NotEqual(aBytes, bBytes);
    }

    [Fact]
    public void ProjectString_NestedObjectReorder_ProducesIdenticalBytes()
    {
        var a = """{"outer":{"b":1,"a":2}}""";
        var b = """{"outer":{"a":2,"b":1}}""";

        Assert.Equal(
            CanonicalJsonProjector.ProjectString(a),
            CanonicalJsonProjector.ProjectString(b));
    }

    [Fact]
    public void ProjectString_ArrayOrderMatters()
    {
        // Arrays preserve order — only object keys are sorted. A reordered array is
        // a different document.
        var a = """{"items":[1,2,3]}""";
        var b = """{"items":[3,2,1]}""";

        Assert.NotEqual(
            CanonicalJsonProjector.ProjectString(a),
            CanonicalJsonProjector.ProjectString(b));
    }

    [Fact]
    public async Task ProjectFileAsync_NonJsonContent_ReturnsRawBytes()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "canonical-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            const string content = "not valid json — raw text payload";
            await File.WriteAllTextAsync(tmp, content);

            var projected = await CanonicalJsonProjector.ProjectFileAsync(tmp);

            // Falls back to raw bytes when not parseable as JSON — keeps non-JSON
            // artefacts inside the audit envelope rather than silently zeroing them.
            Assert.Equal(Encoding.UTF8.GetBytes(content), projected);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void ProjectString_IntegerCanonicalisesAcrossDecimalForms()
    {
        // System.Text.Json.Nodes.JsonNode.Parse normalises "42" and "42.0" through the
        // same Int64 path when the value fits exactly — so 42 and 42.0 project to the
        // same canonical bytes. This is desirable: numerically-equal values should hash
        // the same regardless of whether the writer chose to emit a decimal point.
        var integer  = CanonicalJsonProjector.ProjectString("""{"n":42}""");
        var asDouble = CanonicalJsonProjector.ProjectString("""{"n":42.0}""");

        Assert.Equal(integer, asDouble);
    }

    [Fact]
    public void ProjectString_NumericValueChange_IsRejected()
    {
        // 42 vs 43 must produce different canonical bytes (otherwise the hash chain
        // can't detect value tampering).
        var a = CanonicalJsonProjector.ProjectString("""{"n":42}""");
        var b = CanonicalJsonProjector.ProjectString("""{"n":43}""");

        Assert.NotEqual(a, b);
    }
}
