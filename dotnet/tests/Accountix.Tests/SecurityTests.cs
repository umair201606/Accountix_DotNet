using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Accountix.Core.Security;

namespace Accountix.Tests;

public class ScryptTests
{
    // RFC 7914 §12 test vectors.
    [Theory]
    [InlineData("", "", 16, 1, 1,
        "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906")]
    [InlineData("password", "NaCl", 1024, 8, 16,
        "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640")]
    public void Matches_the_rfc_vectors(string password, string salt, int n, int r, int p, string expected)
    {
        byte[] dk = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), n, r, p, 64);
        Assert.Equal(expected, Convert.ToHexStringLower(dk));
    }

    [Fact]
    public void Refuses_parameters_that_are_not_a_power_of_two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Scrypt.DeriveKey([1], [1], 1000, 8, 1, 64));
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Hash_is_in_werkzeug_scrypt_format_and_verifies()
    {
        string hash = PasswordHasher.Hash("correct horse battery");
        string[] parts = hash.Split('$');
        Assert.Equal("scrypt:32768:8:1", parts[0]);
        Assert.Equal(16, parts[1].Length);
        Assert.Equal(128, parts[2].Length);          // 64 bytes as hex
        Assert.True(PasswordHasher.Verify(hash, "correct horse battery"));
        Assert.False(PasswordHasher.Verify(hash, "correct horse batterY"));
    }

    [Fact]
    public void Verifies_pbkdf2_hashes_written_by_older_werkzeug()
    {
        // Built the way werkzeug does: pbkdf2_hmac(name, password, salt, iterations).hex()
        string salt = "abcDEF123xyz7890";
        byte[] dk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("s3cret-pass"), Encoding.UTF8.GetBytes(salt),
                                              1000, HashAlgorithmName.SHA256, 32);
        string stored = $"pbkdf2:sha256:1000${salt}${Convert.ToHexStringLower(dk)}";
        Assert.True(PasswordHasher.Verify(stored, "s3cret-pass"));
        Assert.False(PasswordHasher.Verify(stored, "s3cret-pasS"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("md5$salt$abc")]
    [InlineData("scrypt:abc:8:1$salt$00")]
    [InlineData("scrypt:1048576:64:1$salt$00")]   // asks for 8 GB: refused, not attempted
    public void Unusable_hashes_never_verify(string? stored)
    {
        Assert.False(PasswordHasher.Verify(stored, "anything"));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("Password123", false)]   // lower-cased it is on the common list, as in Python
    [InlineData("Tr1cky-Pass", true)]
    [InlineData(" leading-space", false)]
    [InlineData("a-good-long-passphrase", true)]
    public void Policy_matches_the_python_rules(string password, bool ok)
    {
        Assert.Equal(ok, PasswordPolicy.Problem(password) is null);
    }

    [Fact]
    public void Session_fingerprint_is_hmac_sha256_of_the_hash_first_16_hex()
    {
        string expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("key-0123456789abcdef"), Encoding.UTF8.GetBytes("scrypt:1$x$y")))[..16];
        Assert.Equal(expected, SessionFingerprint.For("scrypt:1$x$y", "key-0123456789abcdef"));
        Assert.True(SessionFingerprint.Matches(expected, "scrypt:1$x$y", "key-0123456789abcdef"));
        Assert.False(SessionFingerprint.Matches(expected, "scrypt:1$x$z", "key-0123456789abcdef"));
    }
}

/// <summary>
/// Every stored password is a Werkzeug-format hash (method$salt$hex). They keep
/// verifying, and new ones are written the same way, so nobody is rehashed.
/// The strings below were made by Werkzeug 3.1.3 (the Python app's), captured 29 Oct 2026.
/// </summary>
public class WerkzeugHashTests
{
    private const string Password = "Parity-pass-9";

    [Theory]
    [InlineData("scrypt:32768:8:1$0UBcDTFEnjfRydBD$41a002b3cb6aa0d07309511cc41ebe1a20becf6ba1f01c780ff63e80d8afd502951f4b7f97c0c1776d77f2291a91492b4cf8392e3e8ed476def22a38ff6fcdb7")]
    [InlineData("pbkdf2:sha256:1000$J5aUyQIw70G0G0KP$82d4f3e3cffb7c9ba620f46ddae404ec8819d99c1822a69b76004b0030d5172c")]
    [InlineData("pbkdf2:sha256:1000000$BpvZ0oCnNquA2mor$90c8ea86ffc6c36faa097b1db2f3ea87fc1a672346baebe17d08546de93670b4")]
    public void Hashes_made_by_werkzeug_verify(string stored)
    {
        Assert.True(PasswordHasher.Verify(stored, Password));
        Assert.False(PasswordHasher.Verify(stored, Password + "x"));
    }

    /// <summary>
    /// werkzeug.security._hash_internal("scrypt", "FixedSalt1234567", "Made-in-dotnet-1"): the derivation
    /// Hash() writes (scrypt N=32768 r=8 p=1, 64 bytes, hex) gives Werkzeug's digest for the same salt.
    /// </summary>
    [Fact]
    public void A_hash_written_here_is_the_digest_werkzeug_computes()
    {
        const string werkzeug = "scrypt:32768:8:1$FixedSalt1234567$bd3396ff8e7e6dbdf4a17cec1d279783f58465352df97d06365e6114d6e7fb2063213099391c815853405ac5845db3565b82febf12b1753760c029c32e772656";
        string[] p = werkzeug.Split('$');
        byte[] dk = Scrypt.DeriveKey(Encoding.UTF8.GetBytes("Made-in-dotnet-1"), Encoding.UTF8.GetBytes(p[1]), 32768, 8, 1, 64);
        Assert.Equal(p[2], Convert.ToHexStringLower(dk));
        Assert.True(PasswordHasher.Verify(werkzeug, "Made-in-dotnet-1"));
        Assert.True(PasswordHasher.Verify(
            "pbkdf2:sha256:600000$FixedSalt1234567$d69b2a0d95f8d67061fb92ba647f995739d681033ff07333b34025aff7becb9c", "Made-in-dotnet-1"));

        string mine = PasswordHasher.Hash("Made-in-dotnet-1");
        Assert.Matches("^scrypt:32768:8:1\\$[A-Za-z0-9]{16}\\$[0-9a-f]{128}$", mine);
        string[] m = mine.Split('$');
        Assert.Equal(m[2], Convert.ToHexStringLower(
            Scrypt.DeriveKey(Encoding.UTF8.GetBytes("Made-in-dotnet-1"), Encoding.UTF8.GetBytes(m[1]), 32768, 8, 1, 64)));
    }
}

/// <summary>The ax_session cookie (SessionCookie): signed values, nothing else accepted.</summary>
public class SessionCookieTests
{
    private const string Secret = "test-secret-key-0123456789";

    private static JsonObject Session(string userId, int? company = null)
    {
        var s = new JsonObject { ["_sid"] = "tok", ["_user_id"] = userId };
        if (company is int c) s["company_id"] = c;
        return s;
    }

    [Fact]
    public void Reads_user_company_and_token_back()
    {
        var now = DateTimeOffset.UtcNow;
        string cookie = SessionCookie.Sign(Session("12:0123456789abcdef", 3), Secret, now);
        var s = SessionCookie.Read(cookie, Secret, now);
        Assert.NotNull(s);
        Assert.Equal((12, "0123456789abcdef"), SessionCookie.UserOf(s!));
        Assert.Equal(3, SessionCookie.CompanyOf(s!));
        Assert.Equal("tok", SessionCookie.TokenOf(s));
    }

    [Fact]
    public void A_long_session_is_compressed_and_still_reads()
    {
        var now = DateTimeOffset.UtcNow;
        var big = Session("12:0123456789abcdef", 3);
        big["note"] = new string('x', 400);
        string cookie = SessionCookie.Sign(big, Secret, now);
        Assert.StartsWith(".", cookie);
        Assert.Equal(400, SessionCookie.Read(cookie, Secret, now)!["note"]!.GetValue<string>().Length);
    }

    [Fact]
    public void The_same_values_sign_to_the_same_cookie_whatever_the_key_order()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var a = new JsonObject { ["company_id"] = 3, ["_user_id"] = "12:ab", ["_sid"] = "t" };
        var b = new JsonObject { ["_sid"] = "t", ["_user_id"] = "12:ab", ["company_id"] = 3 };
        Assert.Equal(SessionCookie.Sign(a, Secret, at), SessionCookie.Sign(b, Secret, at));
        // Format: base64url({"_sid":"t","_user_id":"12:ab","company_id":3}) . base64url(timestamp) . HMAC-SHA256.
        string[] parts = SessionCookie.Sign(a, Secret, at).Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("""{"_sid":"t","_user_id":"12:ab","company_id":3}""",
                     Encoding.UTF8.GetString(Convert.FromBase64String(parts[0].Replace('-', '+').Replace('_', '/').PadRight((parts[0].Length + 3) / 4 * 4, '='))));
        Assert.Equal(43, parts[2].Length);                    // 32 bytes
    }

    [Fact]
    public void Refuses_another_key_a_changed_payload_an_old_cookie_and_garbage()
    {
        var now = DateTimeOffset.UtcNow;
        string cookie = SessionCookie.Sign(Session("12"), Secret, now);
        Assert.Null(SessionCookie.Read(cookie, "another-secret-key-000000", now));

        string tampered = SessionCookie.Sign(Session("1"), Secret, now).Split('.')[0] + cookie[cookie.IndexOf('.')..];
        Assert.Null(SessionCookie.Read(tampered, Secret, now));

        Assert.Null(SessionCookie.Read(SessionCookie.Sign(Session("12"), Secret, now.AddHours(-14)), Secret, now));
        Assert.NotNull(SessionCookie.Read(SessionCookie.Sign(Session("12"), Secret, now.AddHours(-11)), Secret, now));
        foreach (string? junk in new[] { null, "", "abc", "a.b.c", "....", cookie + "x" })
            Assert.Null(SessionCookie.Read(junk, Secret, now));
    }

    /// <summary>The Python app's Flask cookie (itsdangerous, HMAC-SHA1, salt "cookie-session") is not a sign-in any more.</summary>
    [Fact]
    public void A_flask_session_cookie_with_the_same_key_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        byte[] payload = Encoding.UTF8.GetBytes("""{"_fresh":true,"_sid":"tok","_user_id":"12:0123456789abcdef"}""");
        string value = B64(payload) + "." + B64(BitConverter.GetBytes(now.ToUnixTimeSeconds()).Reverse().SkipWhile(x => x == 0).ToArray());
        byte[] key = HMACSHA1.HashData(Encoding.UTF8.GetBytes(Secret), "cookie-session"u8.ToArray());
        string flask = value + "." + B64(HMACSHA1.HashData(key, Encoding.ASCII.GetBytes(value)));
        Assert.Null(SessionCookie.Read(flask, Secret, now));
    }

    [Fact]
    public void A_bare_user_id_has_no_fingerprint()
    {
        var now = DateTimeOffset.UtcNow;
        var s = SessionCookie.Read(SessionCookie.Sign(new JsonObject { ["_user_id"] = "7" }, Secret, now), Secret, now)!;
        Assert.Equal((7, (string?)null), SessionCookie.UserOf(s));
        Assert.Null(SessionCookie.CompanyOf(s));
        Assert.Null(SessionCookie.TokenOf(s));
    }

    [Fact]
    public void Tokens_are_random_and_name_their_row_by_sha256()
    {
        string a = SessionCookie.NewToken(), b = SessionCookie.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(a))), SessionCookie.TokenHash(a));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public class SharedHelperTests
{
    // Values computed by the Python app (zlib.crc32(...) & 0x7FFFFFFF):
    // a .NET request and a Python request must queue on the same lock.
    [Fact]
    public void Advisory_lock_keys_match_python()
    {
        Assert.Equal(1168167679u, Accountix.Core.Shared.DbLocks.Crc32("members"u8.ToArray()) & 0x7FFFFFFF);
        Assert.Equal("(5,)", Accountix.Core.Shared.DbLocks.PythonTupleRepr([5]));
        Assert.Equal(501320820u, Accountix.Core.Shared.DbLocks.Crc32("(5,)"u8.ToArray()) & 0x7FFFFFFF);
        Assert.Equal("('a', 3)", Accountix.Core.Shared.DbLocks.PythonTupleRepr(["a", 3]));
        Assert.Equal(294790473u, Accountix.Core.Shared.DbLocks.Crc32("('a', 3)"u8.ToArray()) & 0x7FFFFFFF);
    }

    // Python's repr() of each key (printed by Python): quotes switch to "…" when the text has a ' and no ".
    [Fact]
    public void Lock_keys_with_quotes_and_escapes_match_python_repr()
    {
        Assert.Equal("(7, \"Owner's Capital\")", Accountix.Core.Shared.DbLocks.PythonTupleRepr([7, "Owner's Capital"]));
        Assert.Equal("('a\"b\\'c',)", Accountix.Core.Shared.DbLocks.PythonTupleRepr(["a\"b'c"]));
        Assert.Equal("('tab\\there\\nx',)", Accountix.Core.Shared.DbLocks.PythonTupleRepr(["tab\there\nx"]));
        Assert.Equal("(12, 'Retained Earnings — Opening')", Accountix.Core.Shared.DbLocks.PythonTupleRepr([12, "Retained Earnings — Opening"]));
        Assert.Equal("('back\\\\slash',)", Accountix.Core.Shared.DbLocks.PythonTupleRepr(["back\\slash"]));
    }

    [Theory]
    [InlineData(1234567.891, "en", "1,234,567.89")]
    [InlineData(-1234567.891, "en", "(1,234,567.89)")]
    [InlineData(1234567.891, "hi", "12,34,567.89")]
    [InlineData(-123.456, "hi", "(123.46)")]
    [InlineData(0, "en", "0.00")]
    public void Money_prints_as_the_python_app(double value, string fmt, string expected)
    {
        Assert.Equal(expected, Accountix.Core.Shared.Money.Format((decimal)value, 2, fmt));
    }
}
