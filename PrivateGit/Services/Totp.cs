using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PrivateGit.Services;

// RFC 4226 / RFC 6238; SHA-1, six digits, 30-second steps.
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret() => Encode(RandomNumberGenerator.GetBytes(20));
    public static string Encode(byte[] bytes)
    {
        var output = new StringBuilder();
        var buffer = 0; var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value; bits += 8;
            while (bits >= 5) { bits -= 5; output.Append(Alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }
    public static byte[] Decode(string secret)
    {
        var output = new List<byte>(); var buffer = 0; var bits = 0;
        foreach (var value in secret)
        {
            var digit = Alphabet.IndexOf(value);
            if (digit < 0) throw new FormatException("Invalid TOTP secret.");
            buffer = (buffer << 5) | digit; bits += 5;
            if (bits >= 8) { bits -= 8; output.Add((byte)(buffer >> bits)); }
        }
        return output.ToArray();
    }
    public static string Code(string secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var digest = HMACSHA1.HashData(Decode(secret), counter);
        var offset = digest[^1] & 15;
        return ((BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(offset, 4)) & 0x7fffffff) % 1_000_000).ToString("D6");
    }
    public static long? Verify(string secret, string input, DateTimeOffset now, long lastStep)
    {
        if (input.Length != 6 || input.Any(c => c < '0' || c > '9')) return null;
        var current = now.ToUnixTimeSeconds() / 30;
        foreach (var step in new[] { current, current - 1, current + 1 })
            if (step > lastStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(input)))
                return step;
        return null;
    }
}
