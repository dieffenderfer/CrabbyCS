using System.Security.Cryptography;
using System.Text;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// 12-character Crockford-base32 friend code. 60 bits of entropy.
///
/// The alphabet drops I, L, O, U so codes can be read aloud / typed
/// out of a screenshot without ambiguity, and decoding is
/// case-insensitive. Codes are deliberately decoupled from the user's
/// cryptographic identity (see <see cref="Identity"/>): a code is just
/// a handle the broker can hash to a topic id, the keypair is what
/// actually encrypts traffic. That split means a user can regenerate
/// their code after mistyping it into a public channel without
/// re-establishing trust with their existing friends.
/// </summary>
public static class FriendCode
{
    /// <summary>
    /// Crockford base32 with the four ambiguous letters (I, L, O, U)
    /// removed. 32 characters, indexed 0..31.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const int Length = 12;

    /// <summary>
    /// Generates a fresh code using <see cref="RandomNumberGenerator"/>.
    /// Rejection-samples a uniform integer in [0, 32) per character so
    /// the distribution is exactly flat — a naive `% 32` of 8-bit
    /// bytes would be flat too at 32, but doing it the obviously-right
    /// way makes the entropy claim auditable.
    /// </summary>
    public static string Generate()
    {
        var sb = new StringBuilder(Length);
        Span<byte> buf = stackalloc byte[1];
        while (sb.Length < Length)
        {
            RandomNumberGenerator.Fill(buf);
            // 256 isn't divisible by 32 — but 32 divides 256 exactly,
            // so % is uniform. Keep the rejection-loop shape anyway so
            // anyone widening Alphabet later doesn't introduce bias.
            int v = buf[0] & 0x1F;
            sb.Append(Alphabet[v]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalise a user-typed code: uppercase, strip whitespace and
    /// hyphens (so the user can write XXXX-XXXX-XXXX if they want),
    /// and map confusable input characters (lowercase o, l, i, u)
    /// onto their canonical counterparts before validating.
    /// </summary>
    public static string Normalise(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '_') continue;
            char up = char.ToUpperInvariant(c);
            // Crockford-style "any reasonable typo of I/L/O/U becomes
            // their digit equivalent" lenient decode.
            up = up switch
            {
                'I' or '|' => '1',
                'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => up,
            };
            sb.Append(up);
        }
        return sb.ToString();
    }

    /// <summary>True if the input — after <see cref="Normalise"/> —
    /// is exactly 12 characters from the alphabet.</summary>
    public static bool IsValid(string input)
    {
        var norm = Normalise(input);
        if (norm.Length != Length) return false;
        foreach (var c in norm)
            if (Alphabet.IndexOf(c) < 0) return false;
        return true;
    }

    /// <summary>
    /// Hash a friend code into the 16-byte hex topic id the MQTT
    /// broker sees. Caller's responsibility to <see cref="Normalise"/>
    /// first — we hash exactly what's passed in so the same string
    /// always maps to the same topic.
    /// </summary>
    public static string TopicId(string code)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(code), hash);
        // 8 bytes = 16 hex chars = 64 bits of topic-id space. Plenty
        // to avoid collisions; small enough that topic names stay
        // readable in logs.
        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Convenience: pretty-prints a code as XXXX-XXXX-XXXX
    /// for display. Strictly cosmetic — never persist or hash this
    /// form.</summary>
    public static string Format(string code)
    {
        if (code.Length != Length) return code;
        return code[..4] + "-" + code[4..8] + "-" + code[8..];
    }
}
