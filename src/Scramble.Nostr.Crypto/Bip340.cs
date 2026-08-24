using System.Text;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Scramble.Nostr.Crypto;

/// <summary>
/// BIP-340 Schnorr signature verification over secp256k1.
/// </summary>
/// <remarks>
/// Implemented directly on BouncyCastle EC math rather than through
/// <c>NBitcoin.Secp256k1</c>, whose <c>SigVerifyBIP340</c> is broken on the
/// .NET Android runtime. That is not a preference — swapping this for the
/// library call reintroduces a bug that only manifests on one platform.
/// <para>
/// Generic Nostr cryptography, deliberately not in a Marmot namespace: nothing
/// here knows about MLS or the Marmot protocol, so another transport can reuse
/// it without depending on the engine.
/// </para>
/// </remarks>
public static class Bip340
{
    private static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("secp256k1");

    private static readonly ECDomainParameters Domain =
        new(Curve.Curve, Curve.G, Curve.N, Curve.H);

    /// <summary>
    /// Verifies a 64-byte Schnorr signature over a 32-byte message under a
    /// 32-byte x-only public key.
    /// </summary>
    /// <returns>True only if the signature verifies; never throws.</returns>
    public static bool Verify(
        ReadOnlySpan<byte> xOnlyPublicKey,
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (xOnlyPublicKey.Length != 32 || message.Length != 32 || signature.Length != 64)
            return false;

        try
        {
            var p = Curve.Curve.Field.Characteristic;
            var n = Curve.N;

            var rX = new BigInteger(1, signature[..32].ToArray());
            var s = new BigInteger(1, signature[32..].ToArray());
            if (rX.CompareTo(p) >= 0 || s.CompareTo(n) >= 0)
                return false;

            var pX = new BigInteger(1, xOnlyPublicKey.ToArray());
            if (pX.CompareTo(p) >= 0)
                return false;

            var publicPoint = LiftX(pX);
            if (publicPoint is null || publicPoint.IsInfinity)
                return false;

            // e = tagged_hash("BIP0340/challenge", R.x || P.x || m) mod n
            var challenge = new byte[96];
            signature[..32].CopyTo(challenge);
            xOnlyPublicKey.CopyTo(challenge.AsSpan(32));
            message.CopyTo(challenge.AsSpan(64));
            var e = new BigInteger(1, TaggedHash("BIP0340/challenge", challenge)).Mod(n);

            // R' = s*G - e*P
            var r = Domain.G.Multiply(s).Add(publicPoint.Multiply(e).Negate()).Normalize();
            if (r.IsInfinity)
                return false;

            // BIP-340 requires even y, and R'.x must match the signature's R.x.
            if (r.AffineYCoord.ToBigInteger().TestBit(0))
                return false;

            return r.AffineXCoord.ToBigInteger().Equals(rX);
        }
        catch
        {
            // Malformed input must read as "does not verify", not as a crash.
            return false;
        }
    }

    /// <summary>
    /// Signs a 32-byte message with a 32-byte secret, returning 64 bytes.
    /// </summary>
    /// <param name="auxRandom">
    /// 32 bytes of auxiliary randomness. Defaults to fresh random bytes.
    /// Pass zeros only to reproduce a published test vector.
    /// </param>
    /// <remarks>
    /// For locally generated keys only — ephemeral transport keys and the like.
    /// An account identity held by an external signer must be signed through
    /// that signer, not here.
    /// </remarks>
    public static byte[] Sign(
        ReadOnlySpan<byte> secret, ReadOnlySpan<byte> message, ReadOnlySpan<byte> auxRandom = default)
    {
        if (secret.Length != 32)
            throw new ArgumentException("Secret must be 32 bytes.", nameof(secret));
        if (message.Length != 32)
            throw new ArgumentException("Message must be 32 bytes.", nameof(message));

        byte[] aux = auxRandom.Length switch
        {
            0 => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            32 => auxRandom.ToArray(),
            _ => throw new ArgumentException("Auxiliary randomness must be 32 bytes.", nameof(auxRandom)),
        };

        var n = Curve.N;
        var d0 = new BigInteger(1, secret.ToArray());
        if (d0.SignValue == 0 || d0.CompareTo(n) >= 0)
            throw new ArgumentException("Secret is out of range.", nameof(secret));

        // BIP-340 keys are x-only with implicit even y, so a secret whose point
        // has odd y is negated before use.
        var point = Domain.G.Multiply(d0).Normalize();
        var d = point.AffineYCoord.ToBigInteger().TestBit(0) ? n.Subtract(d0) : d0;
        byte[] px = Pad32(Domain.G.Multiply(d).Normalize().AffineXCoord.ToBigInteger());

        byte[] t = Pad32(d);
        byte[] auxHash = TaggedHash("BIP0340/aux", aux);
        for (int i = 0; i < 32; i++)
            t[i] ^= auxHash[i];

        var nonceInput = new byte[96];
        t.CopyTo(nonceInput, 0);
        px.CopyTo(nonceInput, 32);
        message.CopyTo(nonceInput.AsSpan(64));
        var k0 = new BigInteger(1, TaggedHash("BIP0340/nonce", nonceInput)).Mod(n);
        if (k0.SignValue == 0)
            throw new InvalidOperationException("Derived a zero nonce; retry with different auxiliary randomness.");

        var r = Domain.G.Multiply(k0).Normalize();
        var k = r.AffineYCoord.ToBigInteger().TestBit(0) ? n.Subtract(k0) : k0;
        byte[] rx = Pad32(r.AffineXCoord.ToBigInteger());

        var challengeInput = new byte[96];
        rx.CopyTo(challengeInput, 0);
        px.CopyTo(challengeInput, 32);
        message.CopyTo(challengeInput.AsSpan(64));
        var e = new BigInteger(1, TaggedHash("BIP0340/challenge", challengeInput)).Mod(n);

        var signature = new byte[64];
        rx.CopyTo(signature, 0);
        Pad32(k.Add(e.Multiply(d)).Mod(n)).CopyTo(signature, 32);
        return signature;
    }

    /// <summary>
    /// Generates a keypair suitable for BIP-340, returning the x-only public key.
    /// </summary>
    /// <remarks>
    /// The secret is normalised so it already corresponds to the even-y form of
    /// its public key, which means callers never have to think about parity.
    /// </remarks>
    public static (byte[] Secret, byte[] PublicKey) GenerateKeyPair()
    {
        var n = Curve.N;
        while (true)
        {
            byte[] candidate = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var d0 = new BigInteger(1, candidate);
            if (d0.SignValue == 0 || d0.CompareTo(n) >= 0)
                continue;

            var point = Domain.G.Multiply(d0).Normalize();
            var d = point.AffineYCoord.ToBigInteger().TestBit(0) ? n.Subtract(d0) : d0;
            var normalized = Domain.G.Multiply(d).Normalize();
            return (Pad32(d), Pad32(normalized.AffineXCoord.ToBigInteger()));
        }
    }

    private static byte[] Pad32(BigInteger value)
    {
        byte[] raw = value.ToByteArrayUnsigned();
        if (raw.Length == 32)
            return raw;
        if (raw.Length > 32)
            throw new InvalidOperationException("Value is larger than 32 bytes.");

        var padded = new byte[32];
        raw.CopyTo(padded.AsSpan(32 - raw.Length));
        return padded;
    }

    /// <summary>Whether 32 bytes are a valid x-only secp256k1 point.</summary>
    public static bool IsValidXOnlyPublicKey(ReadOnlySpan<byte> xOnlyPublicKey)
    {
        if (xOnlyPublicKey.Length != 32)
            return false;

        try
        {
            var x = new BigInteger(1, xOnlyPublicKey.ToArray());
            if (x.CompareTo(Curve.Curve.Field.Characteristic) >= 0)
                return false;

            var point = LiftX(x);
            return point is not null && !point.IsInfinity;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>BIP-340 tagged hash: SHA256(SHA256(tag) || SHA256(tag) || data).</summary>
    public static byte[] TaggedHash(string tag, ReadOnlySpan<byte> data)
    {
        byte[] tagHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        var buffer = new byte[64 + data.Length];
        tagHash.CopyTo(buffer, 0);
        tagHash.CopyTo(buffer, 32);
        data.CopyTo(buffer.AsSpan(64));
        return System.Security.Cryptography.SHA256.HashData(buffer);
    }

    /// <summary>
    /// Lifts an x-coordinate to the secp256k1 point with even y, or null when x
    /// is not on the curve.
    /// </summary>
    private static ECPoint? LiftX(BigInteger x)
    {
        var p = Curve.Curve.Field.Characteristic;
        if (x.CompareTo(p) >= 0)
            return null;

        // y^2 = x^3 + 7 (mod p)
        var y2 = x.ModPow(BigInteger.Three, p).Add(BigInteger.ValueOf(7)).Mod(p);

        // p = 3 (mod 4), so the square root is y2^((p+1)/4).
        var y = y2.ModPow(p.Add(BigInteger.One).ShiftRight(2), p);
        if (!y.ModPow(BigInteger.Two, p).Equals(y2))
            return null;

        if (y.TestBit(0))
            y = p.Subtract(y);

        return Curve.Curve.CreatePoint(x, y);
    }
}
