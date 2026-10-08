using System.Numerics;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Tests.Signatures;

/// <summary>Ed25519 verification (RFC 8032 §5.1.7), the SHOULD of [SIG-2].</summary>
public class Ed25519Tests
{
    // RFC 8032 §7.1: public key, message, signature (the secret keys are not needed to verify).
    public static TheoryData<string, string, string, string> Rfc8032 => new()
    {
        {
            "TEST 1",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        {
            "TEST 2",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        {
            "TEST 3",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        {
            "TEST 1024",
            "278117fc144c72340f67d0f2316e8386ceffbf2b2428c9c51fef7c597f1d426e",
            Test1024Message,
            "0aab4c900501b3e24d7cdf4663326a3a87df5e4843b2cbdb67cbf6e460fec350aa5371b1508f9f4528ecea23c436d94b5e8fcd4f681e30a6ac00a9704a188a03"
        },
        {
            "TEST SHA(abc)",
            "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"
        },
    };

    [Theory]
    [MemberData(nameof(Rfc8032))]
    public void TheRfc8032TestVectors_Verify(string name, string publicKey, string message, string signature)
    {
        Assert.True(Ed25519.Verify(Hex(publicKey), Hex(message), Hex(signature)), name);
        Assert.True(Ed25519.IsValidPublicKey(Hex(publicKey)), name);
    }

    [Theory]
    [MemberData(nameof(Rfc8032))]
    public void AChangedMessageKeyOrSignature_DoesNotVerify(string name, string publicKey, string message, string signature)
    {
        var key = Hex(publicKey);
        var text = Hex(message);
        var sig = Hex(signature);

        Assert.False(Ed25519.Verify(key, [.. text, 0], sig), name);
        for (var i = 0; i < Ed25519.SignatureSize; i += 9)
        {
            var changed = (byte[])sig.Clone();
            changed[i] ^= 0x04;
            Assert.False(Ed25519.Verify(key, text, changed), $"{name}, signature byte {i}");
        }
        var otherKey = name == "TEST 1"
            ? Hex("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c")    // TEST 2's
            : Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");   // TEST 1's
        Assert.False(Ed25519.Verify(otherKey, text, sig), name);
    }

    [Fact]
    public void SPlusL_IsRefused()
    {
        // [S]B = [S + L]B, so only the range check of §5.1.7 step 1 refuses S + L.
        var key = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var s = new BigInteger(sig.AsSpan(32), isUnsigned: true, isBigEndian: false);
        var sPlusL = LittleEndian32(s + L);

        Assert.True(Ed25519.Verify(key, [], sig));
        Assert.False(Ed25519.Verify(key, [], [.. sig.AsSpan(0, 32), .. sPlusL]));
        Assert.False(Ed25519.Verify(key, [], [.. sig.AsSpan(0, 32), .. LittleEndian32(L)]));   // S = L itself
    }

    [Fact]
    public void ANonCanonicalY_IsRefused_ForTheKeyAndForR()
    {
        // The neutral point (0, 1): canonical encoding y = 1; non-canonical y = p + 1 (below 2^255, so it fits).
        byte[] neutral = [0x01, .. new byte[31]];
        var nonCanonical = LittleEndian32(P + 1);
        byte[] zeroS = new byte[32];

        // RFC 8032's verification refuses no small-order key, and with A and R neutral and S = 0 the equation holds for
        // any message (which is why [SIG-2] makes such a key unusable in a policy): this shows that what refuses the
        // variants below is the encoding, not the equation.
        Assert.True(Ed25519.Verify(neutral, "any message"u8, [.. neutral, .. zeroS]));

        Assert.False(Ed25519.IsValidPublicKey(nonCanonical));
        Assert.False(Ed25519.Verify(nonCanonical, "any message"u8, [.. neutral, .. zeroS]));
        Assert.False(Ed25519.Verify(neutral, "any message"u8, [.. nonCanonical, .. zeroS]));
    }

    [Theory]
    // A' = A + T, T = (0, -1) of order 2; k mod L odd, so [k mod L]T = T and the cofactorless equation fails while the
    // cofactored one holds. Made from RFC 8032 by a script independent of contracts/aef/tools.
    [InlineData("1b605fd794fabca819b0bc4bb03dfdf73c4039d572804c73e6e09b24ea9260dd248308e8778ed39906899d9096ed6987302221ed424c2faafab5638835c01504")]   // floor(H/L) odd
    [InlineData("3afbcc84cee4191716e8355c50373a40fbad48ef2dd6e974d013511c2ea11c3f3244a33be355bc3b7b705cf1106a7691a7625bd29081487f0543bf18c89ef505")]   // floor(H/L) even
    public void AMixedOrderKey_SatisfyingOnlyTheCofactoredEquation_DoesNotVerify(string signature)
    {
        // With floor(H/L) odd, the unreduced H of §5.1.7 step 2 is even, so [S]B = R + [H]A' holds: what refuses that
        // signature is verifying with k = H mod L, which [SIG-2] requires.
        var key = Hex("e6aa4990a0eb38169568806ae6450e72d6adb844345170e239ed5e5ee0b0a56d");
        var message = "AEF WP2: signed under a mixed-order key"u8;

        Assert.True(Ed25519.IsValidPublicKey(key));
        Assert.False(Ed25519.Verify(key, message, Hex(signature)));
    }

    [Fact]
    public void AMixedOrderKey_WithAnEvenK_Verifies_BecauseKIsReducedModL()
    {
        // k = H mod L even, so [k]T is neutral and [S]B = R + [k]A' holds; floor(H/L) is odd, so the unreduced H is odd
        // and [S]B = R + [H]A' does not. [SIG-2] reduces: verified (the ed25519-mixed-order-key-even-k case).
        var key = Hex("e6aa4990a0eb38169568806ae6450e72d6adb844345170e239ed5e5ee0b0a56d");
        var signature = Hex("679c4f1b7b3872c38a8f0a1e482cbdb061636c76bc8cf0227bf77d9f77f04c756f9bb9cf8fbc4936b3073f120fcecbf8832953e16ea20d011055047bb758e90d");
        Assert.True(Ed25519.Verify(key, "AEF WP2: signed under a mixed-order key"u8, signature));
    }

    [Theory]
    // The eight points of order dividing 8 (computed from RFC 8032's curve by a script independent of contracts/aef/tools).
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")]   // the neutral point
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]   // (0, -1), order 2
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]   // order 4
    [InlineData("0000000000000000000000000000000000000000000000000000000000000080")]   // order 4
    [InlineData("26e8958fc2b227b045c3f489f2ef98f0d5dfac05d3c63339b13802886d53fc05")]   // order 8
    [InlineData("26e8958fc2b227b045c3f489f2ef98f0d5dfac05d3c63339b13802886d53fc85")]
    [InlineData("c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a")]
    [InlineData("c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac03fa")]
    public void AKeyOfSmallOrder_DecodesButIsNotUsable(string key)
    {
        // [SIG-2]: eight times a small-order key is the neutral point; such a key verifies forged signatures.
        Assert.True(Ed25519.IsValidPublicKey(Hex(key)));
        Assert.False(Ed25519.IsUsablePublicKey(Hex(key)));
    }

    [Theory]
    [InlineData("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a")]   // RFC 8032 TEST 1
    [InlineData("e6aa4990a0eb38169568806ae6450e72d6adb844345170e239ed5e5ee0b0a56d")]   // mixed order: A + T, 8·A' = 8·A
    public void AKeyNotOfSmallOrder_IsUsable(string key) => Assert.True(Ed25519.IsUsablePublicKey(Hex(key)));

    [Fact]
    public void APointNotOnTheCurve_IsRefused()
    {
        // y = 2: (y^2 - 1) / (d y^2 + 1) is not a square mod p, so no x exists.
        byte[] offCurve = [0x02, .. new byte[31]];
        var key = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        Assert.False(Ed25519.IsValidPublicKey(offCurve));
        Assert.False(Ed25519.Verify(offCurve, [], sig));
        Assert.False(Ed25519.Verify(key, [], [.. offCurve, .. sig.AsSpan(32)]));
    }

    [Fact]
    public void XZeroWithTheSignBitSet_IsRefused()
    {
        // y = 1 and y = p - 1 both have x = 0, which has no negative (§5.1.3 step 4).
        Assert.True(Ed25519.IsValidPublicKey([0x01, .. new byte[31]]));
        Assert.False(Ed25519.IsValidPublicKey([0x01, .. new byte[30], 0x80]));
        Assert.True(Ed25519.IsValidPublicKey(LittleEndian32(P - 1)));
        var minusOne = LittleEndian32(P - 1);
        minusOne[31] |= 0x80;
        Assert.False(Ed25519.IsValidPublicKey(minusOne));
    }

    [Fact]
    public void WrongSizes_AreRefused()
    {
        var key = Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig = Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");

        Assert.False(Ed25519.Verify(key[..31], [], sig));
        Assert.False(Ed25519.Verify([.. key, 0], [], sig));
        Assert.False(Ed25519.Verify(key, [], sig[..63]));
        Assert.False(Ed25519.Verify(key, [], [.. sig, 0]));
        Assert.False(Ed25519.IsValidPublicKey([]));
    }

    [Fact]
    public void TheCorpusEd25519Signature_Verifies_AndTheAlteredOneDoesNot()
    {
        var key = SignatureCorpus.Key("ed25519-a");
        Assert.Equal(AefKeyAlgorithm.Ed25519, key.Algorithm);

        var valid = SignatureCorpus.Envelope("ed25519-valid");
        Assert.True(key.Verify(valid.PreAuthenticationEncoding(), valid.Signatures[0].Sig));

        var altered = SignatureCorpus.Envelope("ed25519-altered-signature");
        Assert.False(key.Verify(altered.PreAuthenticationEncoding(), altered.Signatures[0].Sig));
    }

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] LittleEndian32(BigInteger value)
    {
        var bytes = new byte[32];
        Assert.True(value.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false));
        return bytes;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    // RFC 8032 §7.1, TEST 1024: the 1023-byte message.
    private const string Test1024Message =
        "08b8b2b733424243760fe426a4b54908632110a66c2f6591eabd3345e3e4eb98fa6e264bf09efe12ee50f8f54e9f77b1e355f6c50544e23fb1433ddf73be84d8" +
        "79de7c0046dc4996d9e773f4bc9efe5738829adb26c81b37c93a1b270b20329d658675fc6ea534e0810a4432826bf58c941efb65d57a338bbd2e26640f89ffbc" +
        "1a858efcb8550ee3a5e1998bd177e93a7363c344fe6b199ee5d02e82d522c4feba15452f80288a821a579116ec6dad2b3b310da903401aa62100ab5d1a36553e" +
        "06203b33890cc9b832f79ef80560ccb9a39ce767967ed628c6ad573cb116dbefefd75499da96bd68a8a97b928a8bbc103b6621fcde2beca1231d206be6cd9ec7" +
        "aff6f6c94fcd7204ed3455c68c83f4a41da4af2b74ef5c53f1d8ac70bdcb7ed185ce81bd84359d44254d95629e9855a94a7c1958d1f8ada5d0532ed8a5aa3fb2" +
        "d17ba70eb6248e594e1a2297acbbb39d502f1a8c6eb6f1ce22b3de1a1f40cc24554119a831a9aad6079cad88425de6bde1a9187ebb6092cf67bf2b13fd65f270" +
        "88d78b7e883c8759d2c4f5c65adb7553878ad575f9fad878e80a0c9ba63bcbcc2732e69485bbc9c90bfbd62481d9089beccf80cfe2df16a2cf65bd92dd597b07" +
        "07e0917af48bbb75fed413d238f5555a7a569d80c3414a8d0859dc65a46128bab27af87a71314f318c782b23ebfe808b82b0ce26401d2e22f04d83d1255dc51a" +
        "ddd3b75a2b1ae0784504df543af8969be3ea7082ff7fc9888c144da2af58429ec96031dbcad3dad9af0dcbaaaf268cb8fcffead94f3c7ca495e056a9b47acdb7" +
        "51fb73e666c6c655ade8297297d07ad1ba5e43f1bca32301651339e22904cc8c42f58c30c04aafdb038dda0847dd988dcda6f3bfd15c4b4c4525004aa06eeff8" +
        "ca61783aacec57fb3d1f92b0fe2fd1a85f6724517b65e614ad6808d6f6ee34dff7310fdc82aebfd904b01e1dc54b2927094b2db68d6f903b68401adebf5a7e08" +
        "d78ff4ef5d63653a65040cf9bfd4aca7984a74d37145986780fc0b16ac451649de6188a7dbdf191f64b5fc5e2ab47b57f7f7276cd419c17a3ca8e1b939ae49e4" +
        "88acba6b965610b5480109c8b17b80e1b7b750dfc7598d5d5011fd2dcc5600a32ef5b52a1ecc820e308aa342721aac0943bf6686b64b2579376504ccc493d97e" +
        "6aed3fb0f9cd71a43dd497f01f17c0e2cb3797aa2a2f256656168e6c496afc5fb93246f6b1116398a346f1a641f3b041e989f7914f90cc2c7fff357876e506b5" +
        "0d334ba77c225bc307ba537152f3f1610e4eafe595f6d9d90d11faa933a15ef1369546868a7f3a45a96768d40fd9d03412c091c6315cf4fde7cb68606937380d" +
        "b2eaaa707b4c4185c32eddcdd306705e4dc1ffc872eeee475a64dfac86aba41c0618983f8741c5ef68d3a101e8a3b8cac60c905c15fc910840b94c00a0b9d0";
}
