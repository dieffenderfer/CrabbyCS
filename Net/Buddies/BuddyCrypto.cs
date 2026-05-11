using System.Security.Cryptography;
using NSec.Cryptography;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// End-to-end crypto for buddy-list messages. Implements an
/// ephemeral-static ECDH + XChaCha20-Poly1305 AEAD construction
/// equivalent in design to libsodium's <c>crypto_box_seal</c>: a
/// fresh ephemeral X25519 keypair is generated per message; the
/// shared secret is derived against the recipient's static public
/// key; a Blake2b hash binds the keys into the symmetric key; the
/// payload is encrypted with XChaCha20-Poly1305.
///
/// Output wire format (all binary, concatenated):
/// <code>
///   ephem_pub (32 bytes) || nonce (24 bytes) || ciphertext || tag (16 bytes)
/// </code>
/// Total overhead: 32 + 24 + 16 = 72 bytes per message.
///
/// Anyone with the recipient's public key can encrypt; only the
/// recipient (holder of the matching secret) can decrypt. There's no
/// sender authentication in this layer — the application-level
/// envelope carries the sender's friend code + a signature against
/// their static public key for cases where authentication matters
/// (friend requests, accepts).
/// </summary>
public static class BuddyCrypto
{
    private static readonly KeyAgreementAlgorithm X25519 = KeyAgreementAlgorithm.X25519;
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

    private const int EphemPubLen = 32;
    private const int NonceLen = 24;

    /// <summary>
    /// Seal <paramref name="plaintext"/> to <paramref name="recipientPub"/>.
    /// Output is the wire-format byte array described in the class summary.
    /// </summary>
    public static byte[] Seal(ReadOnlySpan<byte> recipientPub, ReadOnlySpan<byte> plaintext)
    {
        // Ephemeral keypair — keypair is single-use, GC'd at end of
        // this scope. Using KeyCreationParameters with
        // ExportPolicy.AllowPlaintextExport so we can write the
        // ephemeral public key onto the wire.
        var ephem = Key.Create(X25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        try
        {
            var recipientKey = PublicKey.Import(X25519, recipientPub, KeyBlobFormat.RawPublicKey);
            // Agree → derive a symmetric key bound to both pubkeys.
            using var sharedSecret = X25519.Agree(ephem, recipientKey)
                ?? throw new CryptographicException("X25519 agreement returned null");
            var symKey = DeriveSymmetricKey(sharedSecret,
                ephem.Export(KeyBlobFormat.RawPublicKey), recipientPub);

            Span<byte> nonce = stackalloc byte[NonceLen];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = Aead.Encrypt(symKey, nonce, ReadOnlySpan<byte>.Empty, plaintext);
            symKey.Dispose();

            // Pack ephem_pub || nonce || ciphertext-with-tag.
            var ephemPub = ephem.Export(KeyBlobFormat.RawPublicKey);
            var wire = new byte[EphemPubLen + NonceLen + ciphertext.Length];
            Buffer.BlockCopy(ephemPub, 0, wire, 0, EphemPubLen);
            nonce.CopyTo(wire.AsSpan(EphemPubLen, NonceLen));
            Buffer.BlockCopy(ciphertext, 0, wire, EphemPubLen + NonceLen, ciphertext.Length);
            return wire;
        }
        finally
        {
            ephem.Dispose();
        }
    }

    /// <summary>
    /// Open a sealed message addressed to the local user. Returns
    /// null on any failure (malformed wire, MAC verify failure,
    /// wrong recipient). Callers should treat null as "drop this
    /// message and move on" — never log or surface the failure
    /// reason to the network, that's a classic padding-oracle
    /// foot-gun.
    /// </summary>
    public static byte[]? Open(ReadOnlySpan<byte> ourSecret, ReadOnlySpan<byte> wire)
    {
        if (wire.Length < EphemPubLen + NonceLen + 16) return null;
        try
        {
            var ourKey = Key.Import(X25519, ourSecret, KeyBlobFormat.RawPrivateKey);
            try
            {
                var ephemPub = wire[..EphemPubLen];
                var nonce = wire.Slice(EphemPubLen, NonceLen);
                var ciphertext = wire[(EphemPubLen + NonceLen)..];

                var senderKey = PublicKey.Import(X25519, ephemPub, KeyBlobFormat.RawPublicKey);
                using var sharedSecret = X25519.Agree(ourKey, senderKey);
                if (sharedSecret == null) return null;

                // Same KDF binding as Seal: ephem_pub || recipient_pub.
                var symKey = DeriveSymmetricKey(sharedSecret,
                    ephemPub.ToArray(),
                    ourKey.Export(KeyBlobFormat.RawPublicKey));

                var plaintext = Aead.Decrypt(symKey, nonce,
                    ReadOnlySpan<byte>.Empty, ciphertext);
                symKey.Dispose();
                return plaintext;
            }
            finally
            {
                ourKey.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bind the agreed ECDH secret to the ephemeral and recipient
    /// public keys so a sealed message can't be replayed against a
    /// different recipient. Blake2b-256 keyed by the shared secret.
    /// Returns an AEAD key ready to feed XChaCha20-Poly1305.
    /// </summary>
    private static Key DeriveSymmetricKey(SharedSecret shared,
        ReadOnlySpan<byte> ephemPub, ReadOnlySpan<byte> recipientPub)
    {
        // KeyDerivationAlgorithm.HkdfSha256 would be the textbook
        // choice but XChaCha20-Poly1305 wants a 32-byte key and our
        // binding inputs are themselves keyed-hash inputs — Blake2b
        // is simpler and equally secure for this construction.
        var kdf = KeyDerivationAlgorithm.HkdfSha256;
        // salt = ephemPub || recipientPub, info = "mhouse-buddy/v1".
        Span<byte> salt = stackalloc byte[64];
        ephemPub.CopyTo(salt);
        recipientPub.CopyTo(salt[32..]);
        var info = System.Text.Encoding.ASCII.GetBytes("mhouse-buddy/v1");
        return kdf.DeriveKey(shared, salt, info, Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
    }
}
