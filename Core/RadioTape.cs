namespace MouseHouse.Core;

/// <summary>
/// Thread-safe circular buffer of stereo s16 PCM frames. Acts as the "tape"
/// behind the OB-4-style scrub wheel: a decode thread keeps writing freshly
/// arrived audio to the head, while the playback refill reads at any
/// fractional position within the valid window (linear interpolation, supports
/// reverse). Lock is held only across pointer math + a copy — never across
/// I/O or audio API calls.
/// </summary>
public sealed class RadioTape
{
    public const int SampleRate = 44100;
    public const int Channels = 2;

    private readonly short[] _buf;          // interleaved L,R,L,R,...
    private readonly long _capacityFrames;  // ring length in frames
    private long _writeFrame;               // total frames ever written (monotonic)
    private readonly object _lock = new();

    public RadioTape(double seconds = 60.0)
    {
        _capacityFrames = (long)(SampleRate * seconds);
        _buf = new short[_capacityFrames * Channels];
    }

    public long CapacityFrames => _capacityFrames;

    public long WriteHead { get { lock (_lock) return _writeFrame; } }

    /// <summary>Oldest frame index still resident in the ring.</summary>
    public long ValidStart
    {
        get { lock (_lock) return Math.Max(0, _writeFrame - _capacityFrames); }
    }

    /// <summary>One past the newest frame index (i.e. WriteHead).</summary>
    public long ValidEnd { get { lock (_lock) return _writeFrame; } }

    /// <summary>Append a chunk of stereo s16 frames from the decode thread.</summary>
    public void WriteSamples(short[] frames, int frameCount)
    {
        if (frameCount <= 0) return;
        lock (_lock)
        {
            int sampleCount = frameCount * Channels;
            int capSamples = (int)(_capacityFrames * Channels);
            int writePos = (int)((_writeFrame * Channels) % capSamples);

            // Splits at the wrap point.
            int firstChunk = Math.Min(sampleCount, capSamples - writePos);
            Array.Copy(frames, 0, _buf, writePos, firstChunk);
            int remaining = sampleCount - firstChunk;
            if (remaining > 0)
                Array.Copy(frames, firstChunk, _buf, 0, remaining);

            _writeFrame += frameCount;
        }
    }

    /// <summary>
    /// Linear-interpolated stereo sample at the given fractional frame index.
    /// Outputs silence for indices outside the resident window.
    /// </summary>
    public void ReadFrame(double frame, out short left, out short right)
    {
        lock (_lock)
        {
            long start = Math.Max(0, _writeFrame - _capacityFrames);
            // Need both floor and ceil to interpolate; clamp to valid window.
            if (frame < start || frame >= _writeFrame - 1)
            {
                left = 0; right = 0; return;
            }
            long f0 = (long)Math.Floor(frame);
            long f1 = f0 + 1;
            double t = frame - f0;
            int capSamples = (int)(_capacityFrames * Channels);
            int p0 = (int)(((f0 * Channels) % capSamples + capSamples) % capSamples);
            int p1 = (int)(((f1 * Channels) % capSamples + capSamples) % capSamples);
            short l0 = _buf[p0],     r0 = _buf[p0 + 1];
            short l1 = _buf[p1],     r1 = _buf[p1 + 1];
            left  = (short)(l0 + (l1 - l0) * t);
            right = (short)(r0 + (r1 - r0) * t);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _writeFrame = 0;
            Array.Clear(_buf);
        }
    }
}
