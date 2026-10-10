using System.Text;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>The DSSE envelope and its pre-authentication encoding ([SIG-1]).</summary>
public class DsseEnvelopeTests
{
    private const string Key = "sha256:b5465da5f72069887e490258fc60e5319c05344df17f2c9d91ad2c779d07734e";

    [Fact]
    public void Pae_IsDssesOwnTestVector()
    {
        // DSSE protocol.md, "Test Vectors".
        Assert.Equal("DSSEv1 29 http://example.com/HelloWorld 11 hello world"u8.ToArray(),
            Dsse.Pae("http://example.com/HelloWorld", "hello world"u8));
    }

    [Fact]
    public void Pae_CountsUtf8Bytes_NotCharacters()
    {
        var payload = Encoding.UTF8.GetBytes("ü");
        Assert.Equal(Encoding.UTF8.GetBytes("DSSEv1 2 é 2 ü"), Dsse.Pae("é", payload));
        Assert.Equal("DSSEv1 0  0 "u8.ToArray(), Dsse.Pae("", []));
    }

    [Fact]
    public void Pae_RefusesAPayloadTypeWithAnUnpairedSurrogate() =>
        Assert.Throws<ArgumentException>(() => Dsse.Pae("\ud800", []));

    [Fact]
    public void TryParse_ReadsACorpusEnvelope()
    {
        Assert.True(DsseEnvelope.TryParse(SignatureCorpus.Read("ecdsa-valid", "envelope.dsse.json"), out var envelope, out var why), why);
        Assert.Equal(Dsse.InTotoPayloadType, envelope.PayloadType);
        Assert.Equal(SignatureCorpus.Read("ecdsa-valid", "seal.json"), envelope.Payload.ToArray());
        var signature = Assert.Single(envelope.Signatures);
        Assert.Equal(SignatureCorpus.EcdsaA, signature.KeyId);
        Assert.Equal(Dsse.Pae(Dsse.InTotoPayloadType, envelope.Payload), envelope.PreAuthenticationEncoding());
    }

    [Theory]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":"","sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":null,"sig":""}]}""")]   // null reads as absent ([SIG-1])
    public void TryParse_AnAbsentEmptyOrNullKeyId_IsTheEmptyOne(string json)
    {
        Assert.True(DsseEnvelope.TryParse(Encoding.UTF8.GetBytes(json), out var envelope, out var why), why);
        Assert.Equal("", Assert.Single(envelope.Signatures).KeyId);
    }

    [Fact]
    public void TryParse_IgnoresMembersDsseDoesNotDefine()
    {
        var json = """{"payloadType":"t","payload":"QQ","extra":{"a":[1,2]},"signatures":[{"keyid":"k","sig":"-_8","note":true}]}""";
        Assert.True(DsseEnvelope.TryParse(Encoding.UTF8.GetBytes(json), out var envelope, out var why), why);
        Assert.Equal("A"u8.ToArray(), envelope.Payload.ToArray());
        Assert.Equal([0xFB, 0xFF], Assert.Single(envelope.Signatures).Sig.ToArray());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"sig":""}]} {}""")]                          // two values
    [InlineData("""{"payloadType":"t","payloadType":"t","payload":"","signatures":[{"sig":""}]}""")]           // a member twice
    [InlineData("""{"payloadType":"\ud800","payload":"","signatures":[{"sig":""}]}""")]                        // unpaired surrogate
    [InlineData("""{"payloadType":"t","payload":"","x":1e400,"signatures":[{"sig":""}]}""")]                   // overflowing number
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"sig":""},]}""")]                            // trailing comma
    [InlineData("""{"payloadType":"t","payload":"", /* c */ "signatures":[{"sig":""}]}""")]                    // comment
    [InlineData("""{"payload":"","signatures":[{"sig":""}]}""")]                                               // no payloadType
    [InlineData("""{"payloadType":1,"payload":"","signatures":[{"sig":""}]}""")]
    [InlineData("""{"payloadType":null,"payload":"","signatures":[{"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","signatures":[{"sig":""}]}""")]                                          // no payload
    [InlineData("""{"payloadType":"t","payload":"@@@@","signatures":[{"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"QQ== ","signatures":[{"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"+_AA","signatures":[{"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":""}""")]                                                       // no signatures
    [InlineData("""{"payloadType":"t","payload":"","signatures":[]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":{}}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":["x"]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":"k"}]}""")]                          // no sig
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"sig":"@@@@"}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"sig":"QR=="}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":7,"sig":""}]}""")]                   // a keyid neither a string nor null
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":true,"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":{},"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":[],"sig":""}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","signatures":[{"keyid":"k","sig":null}]}""")]
    [InlineData("""{"payloadType":"t","payload":"","payload":"","signatures":[{"sig":""}]}""")]               // payload twice
    [InlineData("""{"payloadType":"t","payload":"QQ=","signatures":[{"sig":""}]}""")]                          // incomplete padding
    public void TryParse_RefusesAMalformedEnvelope(string json)
    {
        Assert.False(DsseEnvelope.TryParse(Encoding.UTF8.GetBytes(json), out var envelope, out var why));
        Assert.Null(envelope);
        Assert.False(string.IsNullOrEmpty(why));
    }

    [Fact]
    public void TryParse_RefusesAByteOrderMark()
    {
        var json = Encoding.UTF8.GetBytes("""{"payloadType":"t","payload":"","signatures":[{"sig":""}]}""");
        Assert.True(DsseEnvelope.TryParse(json, out _, out _));
        Assert.False(DsseEnvelope.TryParse([0xEF, 0xBB, 0xBF, .. json], out _, out _));
    }

    [Fact]
    public void TryParse_RefusesInvalidUtf8()
    {
        var json = Encoding.UTF8.GetBytes("""{"payloadType":"tX","payload":"","signatures":[{"sig":""}]}""");
        json[Array.IndexOf(json, (byte)'X')] = 0xFF;
        Assert.False(DsseEnvelope.TryParse(json, out _, out _));
    }

    [Fact]
    public void ToJson_WritesTheStandardAlphabetWithPadding_AndReadsBack()
    {
        var envelope = new DsseEnvelope("t", [0xFB, 0xFF], [new DsseSignature(Key, [0xFB, 0xEF]), new DsseSignature("", [1])]);
        var json = envelope.ToJson();
        var text = Encoding.UTF8.GetString(json);

        Assert.Contains("\"payload\": \"+/8=\"", text, StringComparison.Ordinal);
        Assert.Contains("\"sig\": \"++8=\"", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.True(DsseEnvelope.TryParse(json, out var back, out var why), why);
        Assert.Equal("t", back.PayloadType);
        Assert.Equal(envelope.Payload.ToArray(), back.Payload.ToArray());
        Assert.Equal([Key, ""], back.Signatures.Select(s => s.KeyId));
        Assert.Equal([1], back.Signatures[1].Sig.ToArray());
    }

    [Fact]
    public void AnEnvelope_HasAtLeastOneSignature() =>
        Assert.Throws<ArgumentException>(() => new DsseEnvelope("t", [], []));
}
