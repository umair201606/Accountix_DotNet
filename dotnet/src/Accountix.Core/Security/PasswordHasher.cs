using System.Security.Cryptography;
using System.Text;

namespace Accountix.Core.Security;

/// <summary>
/// Password hashes in Werkzeug's format (<c>method$salt$hexdigest</c>), the
/// format every row of <c>users.password_hash</c> already uses (the accounts
/// came from the Python app). Keep it: it lets every existing password keep
/// working with no rehash. This writes exactly what
/// <c>werkzeug.security.generate_password_hash</c> wrote (scrypt, N=32768,
/// r=8, p=1, 16-character salt) and reads both scrypt and pbkdf2 hashes.
/// </summary>
public static class PasswordHasher
{
    private const string SaltChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int DefaultPbkdf2Iterations = 1_000_000;   // Werkzeug 3.1's default

    public static string Hash(string password)
    {
        const int n = 32768, r = 8, p = 1;
        string salt = RandomNumberGenerator.GetString(SaltChars, 16);
        byte[] dk = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), n, r, p, 64);
        return $"scrypt:{n}:{r}:{p}${salt}${Convert.ToHexStringLower(dk)}";
    }

    public static bool Verify(string? storedHash, string? password)
    {
        if (string.IsNullOrEmpty(storedHash) || password is null)
            return false;
        string[] parts = storedHash.Split('$', 3);
        if (parts.Length != 3)
            return false;
        string? computed = Compute(parts[0], parts[1], password);
        if (computed is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(parts[2]));
    }

    private static string? Compute(string method, string salt, string password)
    {
        string[] args = method.Split(':');
        byte[] pw = Encoding.UTF8.GetBytes(password);
        byte[] saltBytes = Encoding.UTF8.GetBytes(salt);
        try
        {
            switch (args[0])
            {
                case "scrypt":
                {
                    int n = 32768, r = 8, p = 1;
                    if (args.Length == 4)
                    {
                        n = int.Parse(args[1]);
                        r = int.Parse(args[2]);
                        p = int.Parse(args[3]);
                    }
                    else if (args.Length != 1)
                        return null;
                    // A stored hash asking for more than 1 GB of memory is not
                    // one this app wrote; refuse instead of trying.
                    if ((long)n * r * 128 > 1L << 30 || p > 16)
                        return null;
                    return Convert.ToHexStringLower(Scrypt.DeriveKey(pw, saltBytes, n, r, p, 64));
                }
                case "pbkdf2":
                {
                    string hashName = args.Length >= 2 ? args[1] : "sha256";
                    int iterations = args.Length == 3 ? int.Parse(args[2]) : DefaultPbkdf2Iterations;
                    if (args.Length > 3 || iterations < 1)
                        return null;
                    (HashAlgorithmName alg, int size) = hashName switch
                    {
                        "sha256" => (HashAlgorithmName.SHA256, 32),
                        "sha512" => (HashAlgorithmName.SHA512, 64),
                        "sha384" => (HashAlgorithmName.SHA384, 48),
                        "sha1" => (HashAlgorithmName.SHA1, 20),
                        _ => (default, 0),
                    };
                    if (size == 0)
                        return null;
                    return Convert.ToHexStringLower(Rfc2898DeriveBytes.Pbkdf2(pw, saltBytes, iterations, alg, size));
                }
                default:
                    return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static string? _dummyHash;

    /// <summary>
    /// Spend the same time as a real check when no account matches, so the
    /// reply time does not reveal which logins exist.
    /// </summary>
    public static void SpendEqualTime(string? password)
    {
        _dummyHash ??= Hash("no account has this password");
        Verify(_dummyHash, password ?? "");
    }
}

/// <summary>Rules for a NEW password (existing logins keep working whatever their length).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "password1", "password123", "12345678", "123456789",
        "1234567890", "11111111", "00000000", "qwerty123", "qwertyui",
        "iloveyou", "admin123", "admin1234", "welcome1", "letmein1",
        "abc12345", "abcd1234", "pakistan", "pakistan1", "pakistan123",
    };

    public static string? Problem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return $"Password must be at least {MinLength} characters.";
        if (password.Length > 128)
            return "Password must be at most 128 characters.";
        if (password.Trim() != password)
            return "Password cannot start or end with a space.";
        if (Common.Contains(password.ToLowerInvariant()))
            return "That password is too common — choose another.";
        return null;
    }
}

/// <summary>
/// A short keyed digest of the stored password hash. It rides in the session
/// next to the user id, so a new password signs out every other session of
/// that login (HMAC-SHA256 under SECRET_KEY, first 16 hex characters).
/// </summary>
public static class SessionFingerprint
{
    public static string For(string? passwordHash, string secretKey)
    {
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey),
                                         Encoding.UTF8.GetBytes(passwordHash ?? ""));
        return Convert.ToHexStringLower(mac)[..16];
    }

    public static bool Matches(string mark, string? passwordHash, string secretKey) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(mark), Encoding.ASCII.GetBytes(For(passwordHash, secretKey)));
}
