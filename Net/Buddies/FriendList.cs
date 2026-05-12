using System.Text.Json;
using System.Text.Json.Serialization;
using MouseHouse.Core;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// Persistent set of accepted friends. Backing store is
/// <c>friends.json</c> in the SaveManager save dir. The pending
/// (not-yet-accepted) friend-request queue is separately held in
/// memory by <see cref="MouseHouse.Net.Buddies.NetClient"/>; only
/// accepted friends live here.
///
/// The list is keyed by friend code, normalised. Adding the same
/// code twice updates the existing entry rather than creating a
/// duplicate.
/// </summary>
public sealed class FriendList
{
    public const string Filename = "friends.json";

    [JsonPropertyName("friends")]
    public List<Friend> Friends { get; set; } = new();

    /// <summary>Fired after any mutation that ends with a successful
    /// disk write, so the UI can rebuild its row list. The event is
    /// invoked synchronously inside the mutating call — subscribers
    /// shouldn't do heavy work in the handler.</summary>
    public event Action? Changed;

    public static FriendList Load()
    {
        try
        {
            var path = Path.Combine(SaveManager.SaveDirectory, Filename);
            if (!File.Exists(path)) return new FriendList();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FriendList>(json) ?? new FriendList();
        }
        catch { return new FriendList(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveManager.SaveDirectory);
            var path = Path.Combine(SaveManager.SaveDirectory, Filename);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
        }
        catch { /* best-effort — next mutation retries */ }
        try { Changed?.Invoke(); } catch { }
    }

    public Friend? Find(string code)
    {
        var norm = FriendCode.Normalise(code);
        foreach (var f in Friends)
            if (f.Code == norm) return f;
        return null;
    }

    /// <summary>
    /// Adds a new friend or updates the existing entry's public key
    /// and nickname. Returns the live Friend object that's now in
    /// the list — caller can read/mutate it before the next Save.
    /// </summary>
    public Friend AddOrUpdate(string code, string publicKeyB64, string nickname)
    {
        var norm = FriendCode.Normalise(code);
        var existing = Find(norm);
        if (existing != null)
        {
            // Don't overwrite a populated pubkey with an empty one
            // (race: an old friend-request retry shouldn't wipe the
            // crypto we just established).
            if (!string.IsNullOrEmpty(publicKeyB64))
                existing.PublicKeyB64 = publicKeyB64;
            if (!string.IsNullOrWhiteSpace(nickname))
                existing.Nickname = nickname;
            Save();
            return existing;
        }
        var f = new Friend
        {
            Code = norm,
            PublicKeyB64 = publicKeyB64,
            Nickname = string.IsNullOrWhiteSpace(nickname) ? norm : nickname,
            AddedAtUtc = DateTime.UtcNow,
        };
        Friends.Add(f);
        Save();
        return f;
    }

    public bool Remove(string code)
    {
        var norm = FriendCode.Normalise(code);
        for (int i = 0; i < Friends.Count; i++)
        {
            if (Friends[i].Code == norm)
            {
                Friends.RemoveAt(i);
                Save();
                return true;
            }
        }
        return false;
    }
}
