using AgentEval.Results.Signatures;
using AgentEval.Results.Tests.Corpus;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>The signature vectors of the AEF corpus (<c>conformance/signature-vectors</c>) and their test keys.</summary>
internal static class SignatureCorpus
{
    /// <summary><c>conformance/signature-vectors</c>.</summary>
    public static readonly string Root = Path.Combine(AefCorpus.Conformance, "signature-vectors");

    /// <summary>The bytes of a file of a vector (or of <c>keys</c>).</summary>
    public static byte[] Read(string vector, string file) => File.ReadAllBytes(Path.Combine(Root, vector, file));

    /// <summary>A test key of <c>signature-vectors/keys</c>: <c>ecdsa-a</c>, <c>ecdsa-b</c>, <c>ed25519-a</c> or <c>rsa</c>.</summary>
    public static PublicKeyInfo Key(string name) =>
        PublicKeyInfo.FromPem(File.ReadAllText(Path.Combine(Root, "keys", name + ".pub.pem")));

    /// <summary>A vector's envelope, parsed.</summary>
    public static DsseEnvelope Envelope(string vector) =>
        DsseEnvelope.TryParse(Read(vector, "envelope.dsse.json"), out var envelope, out var why)
            ? envelope
            : throw new InvalidDataException($"{vector}: {why}");

    /// <summary>The key id of <c>ecdsa-a</c> (keys/README.md).</summary>
    public const string EcdsaA = "sha256:b5465da5f72069887e490258fc60e5319c05344df17f2c9d91ad2c779d07734e";

    /// <summary>The key id of <c>ecdsa-b</c> (keys/README.md).</summary>
    public const string EcdsaB = "sha256:73af97a27d5d09ef4bf48e650748da6cab2b7e75b9345d129ebcef17449d7e34";

    /// <summary>The key id of <c>ed25519-a</c> (keys/README.md).</summary>
    public const string Ed25519A = "sha256:2986cef07918485086a97ba70ef87ea966b8c3634fb59bcafc4e8b6a9ea5d787";

    /// <summary>The key id of <c>rsa</c> (keys/README.md).</summary>
    public const string Rsa = "sha256:53b4e942b234837e1786f28805d07b4eb68d1efca307d2eaff65f0e06f714c5f";
}
