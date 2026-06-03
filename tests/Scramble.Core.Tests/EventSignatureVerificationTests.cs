using System.Text;
using NBitcoin.Secp256k1;
using Scramble.Core.Services;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace Scramble.Core.Tests;

/// <summary>
/// Proves that forged Nostr events are rejected by signature verification.
/// Tests the VerifyEventSignature method that guards all event parsing from relays.
/// </summary>
public class EventSignatureVerificationTests
{
    private readonly NostrService _nostrService = new();

    /// <summary>
    /// Creates a valid, signed Nostr event and returns all its components.
    /// </summary>
    private (string id, string pubkey, long createdAt, int kind,
        List<List<string>> tags, string content, string sig)
        CreateSignedEvent(string? privateKeyHex = null, int kind = 1,
            string content = "hello", List<List<string>>? tags = null)
    {
        if (privateKeyHex == null)
        {
            var (priv, _, _, _) = _nostrService.GenerateKeyPair();
            privateKeyHex = priv;
        }

        var privBytes = Convert.FromHexString(privateKeyHex);
        var pubBytes = NostrService.DerivePublicKey(privBytes);
        var pubkey = Convert.ToHexString(pubBytes).ToLowerInvariant();
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        tags ??= new List<List<string>>();

        var serialized = NostrService.SerializeForEventId(pubkey, createdAt, kind, tags, content);
        var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(serialized));
        var id = Convert.ToHexString(idBytes).ToLowerInvariant();
        var sigBytes = NostrService.SignSchnorr(idBytes, privBytes);
        var sig = Convert.ToHexString(sigBytes).ToLowerInvariant();

        return (id, pubkey, createdAt, kind, tags, content, sig);
    }

    #region Valid events pass

    [Fact]
    public void ValidEvent_PassesVerification()
    {
        var (id, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void ValidEvent_WithTags_PassesVerification()
    {
        var tags = new List<List<string>>
        {
            new() { "p", "abc123" },
            new() { "e", "def456" },
            new() { "h", "groupid" }
        };
        var (id, pubkey, createdAt, kind, _, content, sig) =
            CreateSignedEvent(kind: 445, content: "encrypted-mls", tags: tags);

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void ValidEvent_WithUnicodeContent_PassesVerification()
    {
        var (id, pubkey, createdAt, kind, tags, content, sig) =
            CreateSignedEvent(content: "Hello 日本語 émojis 🎉 and \"quotes\" and \\backslashes\\");

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void ValidEvent_Kind1059GiftWrap_PassesVerification()
    {
        // Gift wrap outer signature is verified (inner rumor is unsigned)
        var (id, pubkey, createdAt, kind, tags, content, sig) =
            CreateSignedEvent(kind: 1059, content: "encrypted-seal",
                tags: new List<List<string>> { new() { "p", "recipientpubkey" } });

        Assert.True(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, sig));
    }

    #endregion

    #region Forged events rejected

    [Fact]
    public void TamperedContent_IsRejected()
    {
        // Attacker intercepts event and changes content
        var (id, pubkey, createdAt, kind, tags, _, sig) = CreateSignedEvent(content: "original");

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, "tampered-content", sig));
    }

    [Fact]
    public void TamperedTags_IsRejected()
    {
        // Attacker adds a tag to redirect the event
        var originalTags = new List<List<string>> { new() { "p", "alice" } };
        var (id, pubkey, createdAt, kind, _, content, sig) =
            CreateSignedEvent(tags: originalTags);

        var tamperedTags = new List<List<string>>
        {
            new() { "p", "alice" },
            new() { "p", "attacker" }  // injected tag
        };

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tamperedTags, content, sig));
    }

    [Fact]
    public void ForgedSignature_IsRejected()
    {
        // Attacker creates event with wrong private key's signature
        var (_, attackerPub, _, _) = _nostrService.GenerateKeyPair();
        var (victimPriv, _, _, _) = _nostrService.GenerateKeyPair();
        var (id, pubkey, createdAt, kind, tags, content, _) = CreateSignedEvent(victimPriv);

        // Sign with a different key (attacker forging victim's event)
        var (attackerPriv2, _, _, _) = _nostrService.GenerateKeyPair();
        var fakeIdBytes = Convert.FromHexString(id);
        var fakeSigBytes = NostrService.SignSchnorr(fakeIdBytes, Convert.FromHexString(attackerPriv2));
        var fakeSig = Convert.ToHexString(fakeSigBytes).ToLowerInvariant();

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, fakeSig));
    }

    [Fact]
    public void MismatchedPubkey_IsRejected()
    {
        // Attacker claims event is from victim but signs with own key
        var (attackerPriv, _, _, _) = _nostrService.GenerateKeyPair();
        var (_, victimPub, _, _) = _nostrService.GenerateKeyPair();
        var (id, _, createdAt, kind, tags, content, sig) = CreateSignedEvent(attackerPriv);

        // Replace pubkey with victim's — ID won't match anymore
        Assert.False(NostrService.VerifyEventSignature(
            id, victimPub, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void TamperedEventId_IsRejected()
    {
        // Attacker changes the event ID
        var (_, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        var fakeId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        Assert.False(NostrService.VerifyEventSignature(
            fakeId, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void TamperedTimestamp_IsRejected()
    {
        // Attacker changes created_at to backdate the event
        var (id, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt - 3600, kind, tags, content, sig));
    }

    [Fact]
    public void TamperedKind_IsRejected()
    {
        // Attacker changes kind (e.g., from kind 1 text note to kind 445 group message)
        var (id, pubkey, createdAt, _, tags, content, sig) = CreateSignedEvent(kind: 1);

        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, 445, tags, content, sig));
    }

    #endregion

    #region Malformed input rejected

    [Fact]
    public void EmptySignature_IsRejected()
    {
        var (id, pubkey, createdAt, kind, tags, content, _) = CreateSignedEvent();
        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, ""));
    }

    [Fact]
    public void ShortEventId_IsRejected()
    {
        var (_, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        Assert.False(NostrService.VerifyEventSignature(
            "abcd", pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void NonHexEventId_IsRejected()
    {
        var (_, pubkey, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        var nonHex = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        Assert.False(NostrService.VerifyEventSignature(
            nonHex, pubkey, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void NonHexPubkey_IsRejected()
    {
        var (id, _, createdAt, kind, tags, content, sig) = CreateSignedEvent();
        var nonHex = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
        Assert.False(NostrService.VerifyEventSignature(
            id, nonHex, createdAt, kind, tags, content, sig));
    }

    [Fact]
    public void NonHexSignature_IsRejected()
    {
        var (id, pubkey, createdAt, kind, tags, content, _) = CreateSignedEvent();
        var nonHex = new string('z', 128);
        Assert.False(NostrService.VerifyEventSignature(
            id, pubkey, createdAt, kind, tags, content, nonHex));
    }

    #endregion

    #region BIP-340 official test vectors

    /// <summary>
    /// Official BIP-340 test vectors from https://github.com/bitcoin/bips/blob/master/bip-0340/test-vectors.csv
    /// Only vectors with 32-byte messages are included (vectors 0-14), matching our Nostr use case.
    /// Tests the BouncyCastle-based VerifyBip340Schnorr implementation directly.
    /// </summary>
    [Theory]
    [InlineData(0,
        "F9308A019258C31049344F85F89D5229B531C845836F99B08601F113BCE036F9",
        "0000000000000000000000000000000000000000000000000000000000000000",
        "E907831F80848D1069A5371B402410364BDF1C5F8307B0084C55F1CE2DCA821525F66A4A85EA8B71E482A74F382D2CE5EBEEE8FDB2172F477DF4900D310536C0",
        true, "")]
    [InlineData(1,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "6896BD60EEAE296DB48A229FF71DFE071BDE413E6D43F917DC8DCF8C78DE33418906D11AC976ABCCB20B091292BFF4EA897EFCB639EA871CFA95F6DE339E4B0A",
        true, "")]
    [InlineData(2,
        "DD308AFEC5777E13121FA72B9CC1B7CC0139715309B086C960E18FD969774EB8",
        "7E2D58D8B3BCDF1ABADEC7829054F90DDA9805AAB56C77333024B9D0A508B75C",
        "5831AAEED7B44BB74E5EAB94BA9D4294C49BCF2A60728D8B4C200F50DD313C1BAB745879A5AD954A72C45A91C3A51D3C7ADEA98D82F8481E0E1E03674A6F3FB7",
        true, "")]
    [InlineData(3,
        "25D1DFF95105F5253C4022F628A996AD3A0D95FBF21D468A1B33F8C160D8F517",
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
        "7EB0509757E246F19449885651611CB965ECC1A187DD51B64FDA1EDC9637D5EC97582B9CB13DB3933705B32BA982AF5AF25FD78881EBB32771FC5922EFC66EA3",
        true, "msg reduced modulo p or n")]
    [InlineData(4,
        "D69C3509BB99E412E68B0FE8544E72837DFA30746D8BE2AA65975F29D22DC7B9",
        "4DF3C3F68FCC83B27E9D42C90431A72499F17875C81A599B566C9889B9696703",
        "00000000000000000000003B78CE563F89A0ED9414F5AA28AD0D96D6795F9C6376AFB1548AF603B3EB45C9F8207DEE1060CB71C04E80F593060B07D28308D7F4",
        true, "")]
    [InlineData(5,
        "EEFDEA4CDB677750A420FEE807EACF21EB9898AE79B9768766E4FAA04A2D4A34",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "6CFF5C3BA86C69EA4B7376F31A9BCB4F74C1976089B2D9963DA2E5543E17776969E89B4C5564D00349106B8497785DD7D1D713A8AE82B32FA79D5F7FC407D39B",
        false, "public key not on the curve")]
    [InlineData(6,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "FFF97BD5755EEEA420453A14355235D382F6472F8568A18B2F057A14602975563CC27944640AC607CD107AE10923D9EF7A73C643E166BE5EBEAFA34B1AC553E2",
        false, "has_even_y(R) is false")]
    [InlineData(7,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "1FA62E331EDBC21C394792D2AB1100A7B432B013DF3F6FF4F99FCB33E0E1515F28890B3EDB6E7189B630448B515CE4F8622A954CFE545735AAEA5134FCCDB2BD",
        false, "negated message")]
    [InlineData(8,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "6CFF5C3BA86C69EA4B7376F31A9BCB4F74C1976089B2D9963DA2E5543E177769961764B3AA9B2FFCB6EF947B6887A226E8D7C93E00C5ED0C1834FF0D0C2E6DA6",
        false, "negated s value")]
    [InlineData(9,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "0000000000000000000000000000000000000000000000000000000000000000123DDA8328AF9C23A94C1FEECFD123BA4FB73476F0D594DCB65C6425BD186051",
        false, "sG - eP is infinite (inf as true, x=0)")]
    [InlineData(10,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "00000000000000000000000000000000000000000000000000000000000000017615FBAF5AE28864013C099742DEADB4DBA87F11AC6754F93780D5A1837CF197",
        false, "sG - eP is infinite (inf as true, x=1)")]
    [InlineData(11,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "4A298DACAE57395A15D0795DDBFD1DCB564DA82B0F269BC70A74F8220429BA1D69E89B4C5564D00349106B8497785DD7D1D713A8AE82B32FA79D5F7FC407D39B",
        false, "R.x not on the curve")]
    [InlineData(12,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F69E89B4C5564D00349106B8497785DD7D1D713A8AE82B32FA79D5F7FC407D39B",
        false, "R.x equals field size")]
    [InlineData(13,
        "DFF1D77F2A671C5F36183726DB2341BE58FEAE1DA2DECED843240F7B502BA659",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "6CFF5C3BA86C69EA4B7376F31A9BCB4F74C1976089B2D9963DA2E5543E177769FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141",
        false, "s equals curve order")]
    [InlineData(14,
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC30",
        "243F6A8885A308D313198A2E03707344A4093822299F31D0082EFA98EC4E6C89",
        "6CFF5C3BA86C69EA4B7376F31A9BCB4F74C1976089B2D9963DA2E5543E17776969E89B4C5564D00349106B8497785DD7D1D713A8AE82B32FA79D5F7FC407D39B",
        false, "pubkey exceeds field size")]
    public void Bip340TestVector(int index, string pubkeyHex, string msgHex, string sigHex,
        bool expectedResult, string comment)
    {
        var pubkey = Convert.FromHexString(pubkeyHex);
        var msg = Convert.FromHexString(msgHex);
        var sig = Convert.FromHexString(sigHex);

        var result = NostrService.VerifyBip340Schnorr(pubkey, msg, sig);

        Assert.Equal(expectedResult, result);
    }

    #endregion
}
