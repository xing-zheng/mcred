using System.Security.Cryptography;
using System.Text;

// The root key derived from a password: PBKDF2-HMAC-SHA256 over the password's UTF-8 bytes (Unicode NFC) with a
// fixed salt, so the same password always gives the same 32-byte root key, on any Mac.
internal static class RootKeyDerivation
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("mcred-root-key/root-v1");
    private const int Iterations = 2_000_000;
    public const int MinimumLength = 12;
    // A password chosen now, by 'mcred init --new'. 'mcred init' and 'seat join' keep MinimumLength: they can be given a
    // password already in use, and a higher minimum there could lock out a person whose password is shorter.
    public const int MinimumNewLength = 16;

    public static void RequireLength(string password, int minimum)
    {
        if (password.Length < minimum)
            throw new ArgumentException($"Password must be at least {minimum} characters.");
    }

    public static string Derive(string password)
    {
        RequireLength(password, MinimumLength);
        byte[] encoded = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        byte[] root = Array.Empty<byte>();
        try
        {
            root = Rfc2898DeriveBytes.Pbkdf2(encoded, Salt, Iterations, HashAlgorithmName.SHA256, 32);
            return Convert.ToBase64String(root);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            CryptographicOperations.ZeroMemory(root);
        }
    }

    // A terminal is prompted twice without echo; redirected input is read to its end, one trailing newline dropped.
    public static string ReadPassword()
    {
        if (Console.IsInputRedirected)
            return Console.In.ReadToEnd().TrimEnd('\r', '\n');
        string first = Prompt("Password: ");
        string second = Prompt("Repeat password: ");
        if (first != second)
            throw new ArgumentException("The two passwords differ.");
        return first;
    }

    internal static string Prompt(string label)
    {
        Console.Error.Write(label);
        var password = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0) password.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
        Console.Error.WriteLine();
        return password.ToString();
    }
}
