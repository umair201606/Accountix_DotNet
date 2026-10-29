using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Accountix.Core.Security;

/// <summary>
/// scrypt key derivation (RFC 7914). .NET has no built-in scrypt, and every
/// stored password is a Werkzeug-format scrypt hash (users.password_hash), so
/// this must be exactly that function. Checked against the RFC test vectors and
/// against hashes Werkzeug made (tests/Accountix.Tests/SecurityTests.cs).
/// </summary>
public static class Scrypt
{
    public static byte[] DeriveKey(byte[] password, byte[] salt, int n, int r, int p, int dkLen)
    {
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(n), "N must be a power of two above 1.");
        if (r < 1 || p < 1 || dkLen < 1)
            throw new ArgumentOutOfRangeException(nameof(r), "r, p and the key length must be positive.");
        if ((long)n * r > 1 << 24 || (long)r * p >= 1 << 30)
            throw new ArgumentOutOfRangeException(nameof(n), "scrypt parameters are too large.");

        int blockBytes = 128 * r;
        int words = 32 * r;
        byte[] b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockBytes);

        var x = new uint[words];
        var scratch = new uint[words];
        var v = new uint[(long)words * n];
        for (int i = 0; i < p; i++)
        {
            var block = b.AsSpan(i * blockBytes, blockBytes);
            for (int k = 0; k < words; k++)
                x[k] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4 * k, 4));
            RoMix(x, v, scratch, n, r);
            for (int k = 0; k < words; k++)
                BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4 * k, 4), x[k]);
        }
        Array.Clear(v);
        return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static void RoMix(uint[] x, uint[] v, uint[] scratch, int n, int r)
    {
        int words = 32 * r;
        for (int i = 0; i < n; i++)
        {
            Array.Copy(x, 0, v, (long)i * words, words);
            BlockMix(x, scratch, r);
        }
        for (int i = 0; i < n; i++)
        {
            long j = x[(2 * r - 1) * 16] & (uint)(n - 1);
            long off = j * words;
            for (int k = 0; k < words; k++)
                x[k] ^= v[off + k];
            BlockMix(x, scratch, r);
        }
    }

    // BlockMix(B): X = B[2r-1]; for each 64-byte block i, X = Salsa20/8(X ^ B[i]);
    // even outputs go to the first half, odd outputs to the second.
    private static void BlockMix(uint[] b, uint[] y, int r)
    {
        Span<uint> x = stackalloc uint[16];
        b.AsSpan((2 * r - 1) * 16, 16).CopyTo(x);
        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 16; k++)
                x[k] ^= b[i * 16 + k];
            Salsa20_8(x);
            int dest = ((i & 1) == 0 ? i / 2 : r + i / 2) * 16;
            x.CopyTo(y.AsSpan(dest, 16));
        }
        Array.Copy(y, b, 32 * r);
    }

    private static uint R(uint a, int c) => BitOperations.RotateLeft(a, c);

    private static void Salsa20_8(Span<uint> b)
    {
        uint x0 = b[0], x1 = b[1], x2 = b[2], x3 = b[3], x4 = b[4], x5 = b[5], x6 = b[6], x7 = b[7],
             x8 = b[8], x9 = b[9], x10 = b[10], x11 = b[11], x12 = b[12], x13 = b[13], x14 = b[14], x15 = b[15];
        for (int i = 0; i < 8; i += 2)
        {
            // Columns
            x4 ^= R(x0 + x12, 7); x8 ^= R(x4 + x0, 9); x12 ^= R(x8 + x4, 13); x0 ^= R(x12 + x8, 18);
            x9 ^= R(x5 + x1, 7); x13 ^= R(x9 + x5, 9); x1 ^= R(x13 + x9, 13); x5 ^= R(x1 + x13, 18);
            x14 ^= R(x10 + x6, 7); x2 ^= R(x14 + x10, 9); x6 ^= R(x2 + x14, 13); x10 ^= R(x6 + x2, 18);
            x3 ^= R(x15 + x11, 7); x7 ^= R(x3 + x15, 9); x11 ^= R(x7 + x3, 13); x15 ^= R(x11 + x7, 18);
            // Rows
            x1 ^= R(x0 + x3, 7); x2 ^= R(x1 + x0, 9); x3 ^= R(x2 + x1, 13); x0 ^= R(x3 + x2, 18);
            x6 ^= R(x5 + x4, 7); x7 ^= R(x6 + x5, 9); x4 ^= R(x7 + x6, 13); x5 ^= R(x4 + x7, 18);
            x11 ^= R(x10 + x9, 7); x8 ^= R(x11 + x10, 9); x9 ^= R(x8 + x11, 13); x10 ^= R(x9 + x8, 18);
            x12 ^= R(x15 + x14, 7); x13 ^= R(x12 + x15, 9); x14 ^= R(x13 + x12, 13); x15 ^= R(x14 + x13, 18);
        }
        b[0] += x0; b[1] += x1; b[2] += x2; b[3] += x3; b[4] += x4; b[5] += x5; b[6] += x6; b[7] += x7;
        b[8] += x8; b[9] += x9; b[10] += x10; b[11] += x11; b[12] += x12; b[13] += x13; b[14] += x14; b[15] += x15;
    }
}
