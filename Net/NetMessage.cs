using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MouseHouse.Net;

/// <summary>
/// All multiplayer message types. Serialized as JSON over the wire.
/// </summary>
public class NetMessage
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? Text { get; set; }
    public string? Activity { get; set; }
    public string? State { get; set; }
    public string? Json { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public bool Flag { get; set; }

    // Factory methods for each message type
    public static NetMessage Position(Vector2 pos, string location)
        => new() { Type = "position", X = pos.X, Y = pos.Y, Activity = location };

    public static NetMessage SendName(string name)
        => new() { Type = "name", Name = name };

    public static NetMessage Chat(string sender, string text)
        => new() { Type = "chat", Name = sender, Text = text };

    public static NetMessage ActivityChange(string activity)
        => new() { Type = "activity_change", Activity = activity };

    public static NetMessage ActivityState(string activity, string json)
        => new() { Type = "activity_state", Activity = activity, Json = json };

    public static NetMessage DesktopPosition(Vector2 normalizedPos)
        => new() { Type = "desktop_pos", X = normalizedPos.X, Y = normalizedPos.Y };

    public static NetMessage DesktopStatus(string text)
        => new() { Type = "desktop_status", Text = text };

    public static NetMessage DesktopState(string stateName)
        => new() { Type = "desktop_state", State = stateName };

    public static NetMessage Kick()
        => new() { Type = "kick" };

    public static NetMessage Weather(bool isRaining)
        => new() { Type = "weather", Flag = isRaining };

    // Serialization
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026")]
    public byte[] Serialize()
    {
        return JsonSerializer.SerializeToUtf8Bytes(this, JsonOpts);
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026")]
    public static NetMessage? Deserialize(byte[] data)
    {
        try { return JsonSerializer.Deserialize<NetMessage>(data, JsonOpts); }
        catch { return null; }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026")]
    public static NetMessage? Deserialize(ReadOnlySpan<byte> data)
    {
        try { return JsonSerializer.Deserialize<NetMessage>(data, JsonOpts); }
        catch { return null; }
    }
}
