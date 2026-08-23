using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace Scramble.Nostr.Crypto;

/// <summary>
/// secp256k1 curve operations shared by the Nostr primitives.
/// </summary>
/// <remarks>
/// Implemented on BouncyCastle so this assembly needs exactly one crypto
/// dependency, and so the x-only point handling is identical everywhere it is
/// used — an x-only key must always be lifted to its even-y point, and having
/// two implementations of that is how the two disagree.
/// </remarks>
public static class Secp256k1
{
    internal static readonly X9ECParameters Curve = ECNamedCurveTable.GetByName("secp256k1");

    internal static BigInteger P => Curve.Curve.Field.Characteristic;

    /// <summary>
    /// Lifts an x-coordinate to the curve point with even y, or null when x is
    /// not on the curve.
    /// </summary>
    internal static ECPoint? LiftX(BigInteger x)
    {
        if (x.SignValue <= 0 || x.CompareTo(P) >= 0)
            return null;

        // y^2 = x^3 + 7 (mod p)
        var y2 = x.ModPow(BigInteger.Three, P).Add(BigInteger.ValueOf(7)).Mod(P);

        // p = 3 (mod 4), so the square root is y2^((p+1)/4).
        var y = y2.ModPow(P.Add(BigInteger.One).ShiftRight(2), P);
        if (!y.ModPow(BigInteger.Two, P).Equals(y2))
            return null;

        if (y.TestBit(0))
            y = P.Subtract(y);

        return Curve.Curve.CreatePoint(x, y);
    }

    /// <summary>
    /// The x-coordinate of the ECDH shared point between a private key and an
    /// x-only public key.
    /// </summary>
    /// <remarks>
    /// This is the raw shared x, with no hashing applied — NIP-44 feeds it into
    /// HKDF itself, so applying a KDF here would double-derive.
    /// </remarks>
    /// <exception cref="ArgumentException">A key is the wrong length, out of range, or off-curve.</exception>
    public static byte[] SharedSecretX(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> xOnlyPublicKey)
    {
        if (privateKey.Length != 32)
            throw new ArgumentException("Private key must be 32 bytes.", nameof(privateKey));
        if (xOnlyPublicKey.Length != 32)
            throw new ArgumentException("Public key must be 32 bytes (x-only).", nameof(xOnlyPublicKey));

        var scalar = new BigInteger(1, privateKey.ToArray());
        if (scalar.SignValue == 0 || scalar.CompareTo(Curve.N) >= 0)
            throw new ArgumentException("Private key is out of range.", nameof(privateKey));

        var point = LiftX(new BigInteger(1, xOnlyPublicKey.ToArray()))
            ?? throw new ArgumentException("Public key is not a point on the curve.", nameof(xOnlyPublicKey));

        var shared = point.Multiply(scalar).Normalize();
        if (shared.IsInfinity)
            throw new ArgumentException("ECDH produced the point at infinity.", nameof(xOnlyPublicKey));

        // Fixed 32 bytes, left-padded: a short big-endian encoding would shift
        // every byte and silently produce a different conversation key.
        return shared.AffineXCoord.ToBigInteger().ToByteArrayUnsigned() switch
        {
            { Length: 32 } exact => exact,
            var short32 => Pad32(short32),
        };
    }

    private static byte[] Pad32(byte[] value)
    {
        if (value.Length > 32)
            throw new InvalidOperationException("Coordinate is larger than 32 bytes.");

        var padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }
}
