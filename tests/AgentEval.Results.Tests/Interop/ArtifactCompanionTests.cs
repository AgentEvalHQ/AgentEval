using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Interop;
using AgentEval.Results.Json;

namespace AgentEval.Results.Tests.Interop;

public class ArtifactCompanionTests
{
    private static ArtifactCompanion Example(string name = "agent.tar") =>
        ArtifactCompanion.Create(name, new string('a', 64), new string('b', 64), new string('c', 64));

    [Fact]
    public void Parse_WhitespaceAndExtensions_PreservesDetachedExactBytes()
    {
        var root = JsonNode.Parse(Example().ToBytes())!.AsObject();
        root["future"] = new JsonObject { ["data"] = "retained" };
        var bytes = Encoding.UTF8.GetBytes(" \n" + root.ToJsonString() + "\n");
        var saved = bytes.ToArray();
        var parsed = ArtifactCompanion.Parse(bytes);
        bytes[0] = 0;
        var exported = parsed.ToBytes(); exported[0] = 0;
        Assert.Equal(saved, parsed.ToBytes());
        Assert.Equal("agent.tar", parsed.Name);
        Assert.Equal(new string('a', 64), parsed.ArtifactSha256);
        Assert.Equal(new string('b', 64), parsed.SealSha256);
        Assert.Equal(new string('c', 64), parsed.RunHash);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("predicate")]
    [InlineData("version")]
    [InlineData("binding")]
    [InlineData("subjects")]
    [InlineData("algorithm")]
    [InlineData("hash")]
    [InlineData("missing")]
    [InlineData("name-type")]
    public void Parse_UnsupportedBinding_Refuses(string change)
    {
        var root = JsonNode.Parse(Example().ToBytes())!.AsObject();
        switch (change)
        {
            case "type": root["_type"] = "other"; break;
            case "predicate": root["predicateType"] = "other"; break;
            case "version": root["predicate"]!["schemaVersion"] = "2.0"; break;
            case "binding": root["predicate"]!["binding"] = "executed"; break;
            case "subjects": root["subject"]!.AsArray().Add(root["subject"]![0]!.DeepClone()); break;
            case "algorithm": root["subject"]![0]!["digest"]!["sha512"] = "abc"; break;
            case "hash": root["predicate"]!["evidence"]!["runHash"] = new string('C', 64); break;
            case "missing": root["predicate"]!["evidence"] = null; break;
            case "name-type": root["subject"]![0]!["name"] = 1; break;
        }
        Assert.Throws<AefInputException>(() => ArtifactCompanion.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void Parse_LaterMinorWithKnownFields_AcceptsWithoutLosingDeclaration()
    {
        var root = JsonNode.Parse(Example().ToBytes())!.AsObject();
        root["predicate"]!["schemaVersion"] = "1.12";
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        Assert.Equal(bytes, ArtifactCompanion.Parse(bytes).ToBytes());
        root["predicate"]!["schemaVersion"] = "1.01";
        Assert.Throws<AefInputException>(() => ArtifactCompanion.Parse(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void Parse_DuplicateMember_RefusesBeforeMapping()
    {
        var json = Encoding.UTF8.GetString(Example().ToBytes());
        json = json.Insert(1, "\"_type\":\"other\",");
        Assert.Throws<AefEncodingException>(() => ArtifactCompanion.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Parse_DocumentByteBoundary_AcceptsExactlyLimitAndRefusesNextByte()
    {
        var bytes = Example().ToBytes();
        var padded = new byte[ArtifactCompanion.MaxDocumentBytes];
        Array.Fill(padded, (byte)' ');
        bytes.CopyTo(padded, 0);
        Assert.Equal(padded, ArtifactCompanion.Parse(padded).ToBytes());
        Assert.Throws<AefLimitException>(() => ArtifactCompanion.Parse(new byte[padded.Length + 1]));
    }

    [Fact]
    public void Create_NameByteBoundaryAndInvalidUnicode_EnforcesUtf8Limit()
    {
        Assert.Equal(new string('é', 512), Example(new string('é', 512)).Name);
        Assert.Throws<AefInputException>(() => Example(new string('é', 513)));
        Assert.Throws<AefInputException>(() => Example("\ud800"));
        Assert.Throws<AefInputException>(() => Example(""));
    }
}
