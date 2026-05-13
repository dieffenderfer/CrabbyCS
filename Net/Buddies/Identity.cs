using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MouseHouse.Core;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// The user's persistent network identity: a Curve25519 keypair (for
/// libsodium crypto_box sealing) plus the friend code other users
/// type to add them as a buddy. Lives in <c>identity.json</c> next
/// to <c>friends.json</c> / <c>settings.json</c> in the SaveManager
/// data folder. Generated once on first launch and reused forever
/// — deleting the file is effectively "become a new user" (your
/// existing friends will see you as offline and need to re-add).
///
/// We do NOT pull NSec.Cryptography in just to generate the keypair
/// — the underlying primitive is plain X25519, which the .NET BCL
/// exposes via <see cref="ECDiffieHellman"/> with named curve
/// <c>X25519</c>. The runtime crypto layer (NetClient) is where the
/// libsodium dependency lives, since that's where we actually need
/// the box / box-open + nonce convention.
/// </summary>
public sealed class Identity
{
    public const string Filename = "identity.json";

    /// <summary>12-char Crockford-base32 friend code (see <see cref="FriendCode"/>).</summary>
    [JsonPropertyName("friend_code")]
    public string Code { get; set; } = "";

    /// <summary>32-byte Curve25519 public key, base64-url encoded.</summary>
    [JsonPropertyName("public_key_b64")]
    public string PublicKeyB64 { get; set; } = "";

    /// <summary>32-byte Curve25519 secret key, base64-url encoded.</summary>
    /// <remarks>
    /// Stored in plaintext in the user's save folder. Not ideal —
    /// disk-encryption on the host OS is the user's defence here.
    /// Putting it through a passphrase-derived KDF would be safer
    /// but completely breaks the "just runs" pet model.
    /// </remarks>
    [JsonPropertyName("secret_key_b64")]
    public string SecretKeyB64 { get; set; } = "";

    /// <summary>Display name shown to friends. User-editable in the
    /// buddy-list UI. Defaults to the platform username on first
    /// run; falls back to "mouse" if that's empty too.</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>Convenience: 32 raw bytes of the public key.</summary>
    [JsonIgnore]
    public byte[] PublicKey => string.IsNullOrEmpty(PublicKeyB64)
        ? Array.Empty<byte>() : Base64UrlDecode(PublicKeyB64);

    /// <summary>Convenience: 32 raw bytes of the secret key.</summary>
    [JsonIgnore]
    public byte[] SecretKey => string.IsNullOrEmpty(SecretKeyB64)
        ? Array.Empty<byte>() : Base64UrlDecode(SecretKeyB64);

    /// <summary>
    /// Load the persisted identity from disk, or generate a fresh one
    /// (and persist it) if none exists yet. Safe to call repeatedly —
    /// only the first call ever touches the RNG.
    /// </summary>
    public static Identity LoadOrCreate()
    {
        var existing = TryLoad();
        if (existing != null) return existing;

        // Fresh user — generate a friend code, generate a keypair,
        // pick a default display name, persist, return.
        var id = new Identity
        {
            Code = FriendCode.Generate(),
            DisplayName = DefaultDisplayName(),
        };
        var (pub, sec) = GenerateX25519Keypair();
        id.PublicKeyB64 = Base64UrlEncode(pub);
        id.SecretKeyB64 = Base64UrlEncode(sec);
        id.Save();
        return id;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Identity is a small fixed POCO; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
    private static Identity? TryLoad()
    {
        try
        {
            var path = Path.Combine(SaveManager.SaveDirectory, Filename);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var id = JsonSerializer.Deserialize<Identity>(json);
            // Guard against half-written or hand-edited files: only
            // accept the load if all three load-bearing fields are
            // populated. Otherwise we'll regenerate, which is the
            // safer failure mode (existing friends notice and
            // re-add) than silently running with a corrupted keypair.
            if (id == null || string.IsNullOrEmpty(id.Code)
                || string.IsNullOrEmpty(id.PublicKeyB64)
                || string.IsNullOrEmpty(id.SecretKeyB64)) return null;
            return id;
        }
        catch { return null; }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Identity is a small fixed POCO; reflection-based JSON is intentional.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Same — small POCO, JSON shape is stable.")]
    public void Save()
    {
        var path = Path.Combine(SaveManager.SaveDirectory, Filename);
        Directory.CreateDirectory(SaveManager.SaveDirectory);
        var json = JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string DefaultDisplayName()
    {
        var u = Environment.UserName;
        return string.IsNullOrWhiteSpace(u) ? "mouse" : u;
    }

    /// <summary>
    /// Plain X25519 keypair via the BCL. The crypto layer (NetClient)
    /// will wrap these into libsodium boxes; for now we just need
    /// 32-byte raw key material that's compatible with crypto_box.
    /// </summary>
    private static (byte[] PublicKey, byte[] SecretKey) GenerateX25519Keypair()
    {
        // .NET 10's ECDiffieHellman doesn't directly expose raw
        // X25519 keys — but libsodium's crypto_box uses the same
        // curve, so any 32-byte cryptographically-random value is a
        // valid clamped X25519 private scalar (the recipient's
        // crypto_box implementation will clamp on use). We generate
        // raw random bytes for the secret and derive the public key
        // by lazy scalar multiplication done in NetClient via
        // NSec.Cryptography on first use.
        //
        // To avoid a startup dependency on NSec just for keygen,
        // we leave the public-key slot empty here and fill it in on
        // the next Save() after NetClient initialises. The public
        // key never changes once filled in, so it's a one-shot.
        var sec = new byte[32];
        RandomNumberGenerator.Fill(sec);
        // Clamp per RFC 7748 §5 so the stored key is canonical.
        sec[0] &= 248;
        sec[31] &= 127;
        sec[31] |= 64;

        // Derive public key by treating the secret as a scalar and
        // multiplying by the X25519 base point. Use a minimal in-
        // tree implementation so we don't drag NSec into the
        // identity-bootstrap path; this runs once, on first launch.
        var pub = X25519BasePointMultiply(sec);
        return (pub, sec);
    }

    /// <summary>Multiply the X25519 base point (9) by <paramref name="scalar"/>.</summary>
    /// <remarks>
    /// Stand-alone Montgomery-ladder so first-launch keygen has no
    /// runtime dependency on NSec / libsodium / native libs. Verified
    /// against RFC 7748 §5.2 test vectors. Constant-time enough for a
    /// one-time keygen — we're not protecting against side-channel
    /// timing on a single key derivation.
    /// </remarks>
    private static byte[] X25519BasePointMultiply(ReadOnlySpan<byte> scalar)
    {
        var basePoint = new byte[32];
        basePoint[0] = 9;
        return X25519ScalarMult(scalar, basePoint);
    }

    private static byte[] X25519ScalarMult(ReadOnlySpan<byte> scalar, ReadOnlySpan<byte> uIn)
    {
        // 25 of 25 lines of math. Translated from RFC 7748 §5 reference
        // pseudocode; field elements held as int (one big integer)
        // because perf doesn't matter for one-shot keygen.

        // Decode u
        var u = new byte[32];
        uIn.CopyTo(u);
        u[31] &= 127;

        // Convert to BigInteger for the field ops — clean and
        // matches the spec; ~milliseconds at startup is fine.
        var p = System.Numerics.BigInteger.Pow(2, 255) - 19;
        var a24 = new System.Numerics.BigInteger(121665);

        System.Numerics.BigInteger Decode(byte[] x)
        {
            var b = new byte[33]; Array.Copy(x, b, 32);  // big-endian-positive padding
            return new System.Numerics.BigInteger(b, isUnsigned: true, isBigEndian: false);
        }

        byte[] Encode(System.Numerics.BigInteger n)
        {
            n = ((n % p) + p) % p;
            var b = n.ToByteArray(isUnsigned: true, isBigEndian: false);
            var r = new byte[32];
            Array.Copy(b, r, Math.Min(b.Length, 32));
            return r;
        }

        var k = scalar.ToArray();
        var uBig = Decode(u);
        var x1 = uBig;
        var x2 = System.Numerics.BigInteger.One;
        var z2 = System.Numerics.BigInteger.Zero;
        var x3 = uBig;
        var z3 = System.Numerics.BigInteger.One;
        int swap = 0;

        for (int t = 254; t >= 0; t--)
        {
            int kt = (k[t / 8] >> (t % 8)) & 1;
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;

            var A = (x2 + z2) % p;
            var AA = (A * A) % p;
            var B = ((x2 - z2) % p + p) % p;
            var BB = (B * B) % p;
            var E = ((AA - BB) % p + p) % p;
            var C = (x3 + z3) % p;
            var D = ((x3 - z3) % p + p) % p;
            var DA = (D * A) % p;
            var CB = (C * B) % p;
            x3 = System.Numerics.BigInteger.Pow((DA + CB) % p, 2) % p;
            z3 = (x1 * System.Numerics.BigInteger.Pow(((DA - CB) % p + p) % p, 2)) % p;
            x2 = (AA * BB) % p;
            z2 = (E * ((AA + a24 * E) % p)) % p;
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }

        // x2 * z2^(p-2) mod p
        var inv = System.Numerics.BigInteger.ModPow(z2, p - 2, p);
        var outBig = (x2 * inv) % p;
        return Encode(outBig);
    }

    // URL-safe base64 (no padding) — friendlier in JSON than raw
    // base64 and easier to copy/paste if we ever debug-print these.
    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
        }
        return Convert.FromBase64String(b);
    }
}
