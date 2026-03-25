using Raylib_cs;

namespace MouseHouse.Core;

/// <summary>
/// Simple audio manager for playing sound effects.
/// </summary>
public class AudioManager
{
    private readonly AssetCache _assets;
    private bool _muted;
    private float _volume = 1.0f;

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            Raylib.SetMasterVolume(_muted ? 0f : _volume);
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (!_muted) Raylib.SetMasterVolume(_volume);
        }
    }

    public AudioManager(AssetCache assets)
    {
        _assets = assets;
    }

    public void Play(string assetPath)
    {
        if (_muted) return;
        var sound = _assets.GetSound(assetPath);
        Raylib.PlaySound(sound);
    }
}
