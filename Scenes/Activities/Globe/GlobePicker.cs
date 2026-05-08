using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities.Globe;

public record Region(string Name, string DifficultyName, float LatDeg, float LonDeg);

/// <summary>
/// 2D-dithered software-rasterized "spinning globe" region picker. Drawn
/// into a Cell-by-Cell area inside any activity that wants it.
///
/// Implementation follows the early-Win9x aesthetic spec: per-pixel CPU
/// sphere rasterization, a simple lat/lon ellipse continent mask, Lambert
/// shading from a fixed light, and an 8x8 Bayer dither matrix to threshold
/// the brightness into a two-tone (or three-tone) output. Drag to spin
/// the globe; let go and it auto-rotates slowly. Click a region marker
/// (or near it) to pick. After picking, the globe animates to centre the
/// chosen region before signalling the caller.
///
/// Updates an <see cref="Image"/> + <see cref="Texture2D"/> each frame
/// rather than blitting via DrawPixel — far cheaper, gives a stable
/// flicker-free dither pattern, and keeps everything in CPU memory so we
/// can keep tweaking the bake without GPU bookkeeping.
/// </summary>
public class GlobePicker
{
    public bool Picked { get; private set; }
    public Region? PickedRegion { get; private set; }

    public static readonly Region[] Regions =
    {
        new("North America", "Beginner",      45f,  -100f),
        new("Europe",        "Intermediate",  50f,    15f),
        new("Asia",          "Advanced",      35f,   100f),
        new("South America", "Expert",       -15f,   -60f),
        new("Australia",     "Master",       -25f,   135f),
        new("Africa",        "Legendary",      5f,    20f),
    };

    private readonly int _w, _h;
    private float _yaw;        // longitude rotation around the Y axis (drag X)
    private float _pitch = 0.35f;  // tilt — slight northward tilt by default
    private float _spin = 0.18f;   // auto-spin speed (rad/sec) when not dragging
    private bool _dragging;
    private Vector2 _dragLast;
    private float _dragVel;     // last drag velocity, applied to _yaw on release for momentum
    private int _hoverIdx = -1;
    private int _selectedIdx = -1;
    private float _zoomT;       // 0..1 transition timer after a pick
    private float _zoomYawTarget;
    private float _zoomPitchTarget;

    // Light direction (in view space — pre-rotation). Matches a classic
    // upper-left key light, the workstation-rendering convention.
    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(-0.5f, -0.8f, -0.6f));

    // 8x8 Bayer matrix, values 0..63. Used to threshold continuous brightness
    // into a 2-tone (or 3-tone) ordered-dither pattern — the look classic
    // 90s grayscale-on-screen renderers had.
    private static readonly int[,] Bayer8 = new int[8, 8]
    {
        {  0, 32,  8, 40,  2, 34, 10, 42 },
        { 48, 16, 56, 24, 50, 18, 58, 26 },
        { 12, 44,  4, 36, 14, 46,  6, 38 },
        { 60, 28, 52, 20, 62, 30, 54, 22 },
        {  3, 35, 11, 43,  1, 33,  9, 41 },
        { 51, 19, 59, 27, 49, 17, 57, 25 },
        { 15, 47,  7, 39, 13, 45,  5, 37 },
        { 63, 31, 55, 23, 61, 29, 53, 21 },
    };

    private Image _img;
    private Texture2D _tex;
    private bool _texAllocated;
    private byte[] _pixelBuf;

    public GlobePicker(int width, int height)
    {
        _w = width; _h = height;
        _img = Raylib.GenImageColor(_w, _h, Color.Blank);
        _tex = Raylib.LoadTextureFromImage(_img);
        _texAllocated = true;
        // RGBA8 byte buffer we mutate directly each frame, then push to GPU.
        _pixelBuf = new byte[_w * _h * 4];
    }

    public void Unload()
    {
        if (_texAllocated)
        {
            Raylib.UnloadTexture(_tex);
            Raylib.UnloadImage(_img);
            _texAllocated = false;
        }
    }

    public void Reset()
    {
        Picked = false;
        PickedRegion = null;
        _selectedIdx = -1;
        _hoverIdx = -1;
        _zoomT = 0;
        _yaw = 0;
        _pitch = 0.35f;
    }

    public void Update(float delta, Vector2 mousePos, Rectangle hostRect,
                       bool leftPressed, bool leftReleased, bool leftHeld)
    {
        if (Picked) return;

        // Centre + radius of the rendered disk inside hostRect.
        Vector2 centre = new(hostRect.X + _w / 2f, hostRect.Y + _h / 2f);
        float radius = Math.Min(_w, _h) * 0.42f;

        Vector2 toMouse = mousePos - centre;
        bool overGlobe = toMouse.LengthSquared() <= radius * radius;

        if (_zoomT > 0)
        {
            // Animate yaw / pitch toward the picked region's centre.
            _zoomT += delta;
            float k = MathF.Min(1f, _zoomT / 0.8f);
            float ease = 1f - (1f - k) * (1f - k);
            _yaw = Lerp(_yaw, _zoomYawTarget, ease - (ease - delta * 4f));
            _pitch = Lerp(_pitch, _zoomPitchTarget, ease - (ease - delta * 4f));
            if (k >= 1f)
            {
                Picked = true;
                PickedRegion = Regions[_selectedIdx];
            }
            return;
        }

        // Hover state: nearest visible region marker within 14 px of cursor.
        _hoverIdx = -1;
        float bestD = 14f;
        for (int i = 0; i < Regions.Length; i++)
        {
            if (!RegionScreen(Regions[i], centre, radius, out var sp)) continue;
            float d = Vector2.Distance(sp, mousePos);
            if (d < bestD) { bestD = d; _hoverIdx = i; }
        }

        // Click on a hovered region selects it and starts the zoom anim.
        if (leftPressed && _hoverIdx >= 0)
        {
            _selectedIdx = _hoverIdx;
            // Aim yaw/pitch so the region ends up at the centre of the
            // visible hemisphere (yaw = -lon, pitch = lat-ish).
            float lonRad = Regions[_selectedIdx].LonDeg * MathF.PI / 180f;
            float latRad = Regions[_selectedIdx].LatDeg * MathF.PI / 180f;
            _zoomYawTarget = -lonRad;
            _zoomPitchTarget = latRad;
            _zoomT = 0.001f;        // > 0 enters the anim branch next frame
            _dragging = false;
            return;
        }

        // Drag-to-spin (any press inside the globe disk).
        if (leftPressed && overGlobe && _hoverIdx < 0)
        {
            _dragging = true;
            _dragLast = mousePos;
            _dragVel = 0;
        }
        if (_dragging && leftHeld)
        {
            var dp = mousePos - _dragLast;
            _yaw -= dp.X * 0.012f;
            _pitch = Math.Clamp(_pitch + dp.Y * 0.010f, -1.2f, 1.2f);
            _dragVel = -dp.X * 0.012f / MathF.Max(delta, 0.001f);
            _dragLast = mousePos;
        }
        if (leftReleased && _dragging)
        {
            _dragging = false;
            // Slight momentum: the spin speed reflects the user's flick, but
            // is clamped so they don't end up with a runaway carousel.
            _spin = Math.Clamp(_dragVel * 0.05f, -2f, 2f);
            // Only carry momentum for a moment; settle back to slow drift below.
        }

        // Auto-spin when the user isn't actively dragging — slow westward
        // drift like an Encarta intro.
        if (!_dragging)
        {
            _yaw += _spin * delta;
            // Decay any momentum back to the gentle baseline drift.
            _spin = Lerp(_spin, 0.18f, MathF.Min(1f, delta * 1.2f));
        }
    }

    // ── Rendering ───────────────────────────────────────────────────────

    public void Draw(Rectangle hostRect)
    {
        Render();
        Raylib.UpdateTexture(_tex, _pixelBuf);
        Raylib.DrawTextureV(_tex, new Vector2(hostRect.X, hostRect.Y), Color.White);

        // Region markers + labels on top — drawn in vector colours so they
        // pop above the dithered globe instead of being part of the b/w.
        Vector2 centre = new(hostRect.X + _w / 2f, hostRect.Y + _h / 2f);
        float radius = Math.Min(_w, _h) * 0.42f;

        for (int i = 0; i < Regions.Length; i++)
        {
            if (!RegionScreen(Regions[i], centre, radius, out var sp)) continue;
            bool hover = i == _hoverIdx;
            bool selected = i == _selectedIdx;
            float r = selected ? 7f : (hover ? 6f : 4f);
            var ringCol = selected ? new Color((byte)255, (byte)200, (byte)80, (byte)255)
                       : hover    ? new Color((byte)255, (byte)240, (byte)160, (byte)255)
                       :            new Color((byte)200, (byte)180, (byte)120, (byte)200);
            Raylib.DrawCircleV(sp, r, ringCol);
            Raylib.DrawCircleV(sp, r - 2, new Color((byte)40, (byte)28, (byte)16, (byte)255));

            if (hover || selected)
            {
                string label = $"{Regions[i].Name}  ({Regions[i].DifficultyName})";
                int lw = RetroSkin.MeasureText(label, 14);
                int lx = (int)(sp.X - lw / 2);
                int ly = (int)(sp.Y - 22);
                // Shadow box
                Raylib.DrawRectangle(lx - 4, ly - 2, lw + 8, 18,
                    new Color((byte)10, (byte)10, (byte)16, (byte)200));
                RetroSkin.DrawText(label, lx, ly, new Color((byte)240, (byte)240, (byte)240, (byte)255), 14);
            }
        }

        // Picker title over the disk.
        string title = "Choose a region";
        int tw = RetroSkin.MeasureText(title, 18);
        RetroSkin.DrawText(title, (int)(centre.X - tw / 2), (int)(hostRect.Y + 8),
            new Color((byte)240, (byte)240, (byte)240, (byte)255), 18);
        string sub = "Drag to spin · click a marker to pick";
        sub = sub.Replace("·", "|");
        int sw = RetroSkin.MeasureText(sub, 12);
        RetroSkin.DrawText(sub, (int)(centre.X - sw / 2), (int)(hostRect.Y + hostRect.Height - 16),
            new Color((byte)180, (byte)180, (byte)200, (byte)220), 12);
    }

    private void Render()
    {
        // Clear to deep-blue ocean letterbox; the globe disk overwrites
        // its own pixels.
        FillBuffer(8, 12, 28, 255);

        int cx = _w / 2, cy = _h / 2;
        float radius = Math.Min(_w, _h) * 0.42f;
        float r2 = radius * radius;

        // Pre-rotation: pitch about X, then yaw about Y. We apply the inverse
        // to each visible-hemisphere normal to recover its world-space lat/lon.
        float cy_p = MathF.Cos(_pitch),  sy_p = MathF.Sin(_pitch);
        float cy_y = MathF.Cos(_yaw),    sy_y = MathF.Sin(_yaw);

        for (int sy = -((int)radius); sy <= radius; sy++)
        {
            int py = cy + sy;
            if (py < 0 || py >= _h) continue;
            float fsy = sy;
            for (int sx = -((int)radius); sx <= radius; sx++)
            {
                int px = cx + sx;
                if (px < 0 || px >= _w) continue;
                float fsx = sx;
                float d2 = fsx * fsx + fsy * fsy;
                if (d2 > r2) continue;

                // (nx, ny, nz): unit normal on the visible front hemisphere.
                // Y points up the screen so we negate.
                float nx = fsx / radius;
                float ny = -fsy / radius;
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));

                // Inverse-rotate (pitch then yaw, applied in reverse) to get
                // the corresponding world-space point on the unit sphere.
                // First undo pitch about X.
                float wx = nx;
                float wy = ny * cy_p + nz * sy_p;
                float wz = -ny * sy_p + nz * cy_p;
                // Then undo yaw about Y.
                float ux = wx * cy_y + wz * sy_y;
                float uy = wy;
                float uz = -wx * sy_y + wz * cy_y;

                // World-space lat/lon of this surface point.
                float lat = MathF.Asin(uy) * 180f / MathF.PI;
                float lon = MathF.Atan2(ux, uz) * 180f / MathF.PI;

                bool isLand = IsLand(lat, lon);

                // Lambert shading on the *view-space* normal so the lit side
                // is always upper-left regardless of rotation.
                float lambert = MathF.Max(0f, -(nx * LightDir.X + ny * LightDir.Y + nz * LightDir.Z));
                // Add a soft rim term so the silhouette doesn't go fully dark.
                float rim = 0.18f * MathF.Pow(MathF.Max(0f, 1f - nz), 1.6f);
                float baseB = isLand ? 0.55f : 0.30f;
                float bright = baseB + lambert * 0.55f + rim;
                bright = MathF.Min(1f, bright);

                int bayer = Bayer8[(py & 7), (px & 7)];
                float threshold = bayer / 64f;

                // Two-tone dither: land vs ocean palette differs slightly so
                // continents are recognisable without colour.
                int idx = (py * _w + px) * 4;
                if (bright > threshold)
                {
                    if (isLand)
                    {
                        _pixelBuf[idx + 0] = 232;
                        _pixelBuf[idx + 1] = 218;
                        _pixelBuf[idx + 2] = 180;
                    }
                    else
                    {
                        _pixelBuf[idx + 0] = 150;
                        _pixelBuf[idx + 1] = 180;
                        _pixelBuf[idx + 2] = 220;
                    }
                }
                else
                {
                    if (isLand)
                    {
                        _pixelBuf[idx + 0] = 70;
                        _pixelBuf[idx + 1] = 60;
                        _pixelBuf[idx + 2] = 40;
                    }
                    else
                    {
                        _pixelBuf[idx + 0] = 30;
                        _pixelBuf[idx + 1] = 50;
                        _pixelBuf[idx + 2] = 90;
                    }
                }
                _pixelBuf[idx + 3] = 255;
            }
        }
    }

    private void FillBuffer(byte r, byte g, byte b, byte a)
    {
        for (int i = 0; i < _pixelBuf.Length; i += 4)
        {
            _pixelBuf[i + 0] = r;
            _pixelBuf[i + 1] = g;
            _pixelBuf[i + 2] = b;
            _pixelBuf[i + 3] = a;
        }
    }

    /// <summary>
    /// Crude continent mask — sum of soft elliptical blobs in lat/lon. Gives
    /// recognisable shapes (NA, Africa, Asia, etc.) at very low cost. Coords
    /// in degrees: lat ∈ [-90, 90], lon ∈ [-180, 180].
    /// </summary>
    private static bool IsLand(float lat, float lon)
    {
        // Antarctica: most of the south polar cap.
        if (lat < -65f) return true;
        // Greenland.
        if (Blob(lat, lon, 73f, -40f, 8f, 18f)) return true;
        // North America (Canada + USA + Mexico).
        if (Blob(lat, lon, 50f, -110f, 22f, 36f)) return true;
        if (Blob(lat, lon, 30f,  -95f, 12f, 18f)) return true;
        if (Blob(lat, lon, 18f, -100f,  6f, 12f)) return true;
        // Central + South America.
        if (Blob(lat, lon, -10f, -60f, 30f, 16f)) return true;
        if (Blob(lat, lon, -45f, -70f,  8f,  4f)) return true;
        // Europe.
        if (Blob(lat, lon, 50f,  15f, 14f, 22f)) return true;
        if (Blob(lat, lon, 60f,  25f, 10f, 30f)) return true;
        // Africa.
        if (Blob(lat, lon,   5f, 20f, 30f, 18f)) return true;
        // Madagascar
        if (Blob(lat, lon, -20f, 47f,  6f,  3f)) return true;
        // Middle East / Arabia.
        if (Blob(lat, lon, 25f, 45f, 12f, 12f)) return true;
        // Asia (large).
        if (Blob(lat, lon, 50f, 90f, 25f, 50f)) return true;
        if (Blob(lat, lon, 25f, 100f, 12f, 25f)) return true;
        // India.
        if (Blob(lat, lon, 20f, 78f, 12f, 8f)) return true;
        // Indonesia / SE Asia archipelago — narrow band.
        if (Blob(lat, lon,  -2f, 115f, 5f, 25f)) return true;
        // Australia.
        if (Blob(lat, lon, -25f, 135f, 12f, 18f)) return true;
        // Japan
        if (Blob(lat, lon, 36f, 138f, 7f, 4f)) return true;
        // British Isles
        if (Blob(lat, lon, 54f, -3f, 5f, 4f)) return true;
        return false;
    }

    private static bool Blob(float lat, float lon, float lat0, float lon0, float latR, float lonR)
    {
        float dLat = lat - lat0;
        // Wrap longitude difference into [-180, 180].
        float dLon = lon - lon0;
        while (dLon > 180f) dLon -= 360f;
        while (dLon < -180f) dLon += 360f;
        float a = dLat / latR;
        float b = dLon / lonR;
        return a * a + b * b <= 1f;
    }

    /// <summary>
    /// Project a region's lat/lon to its current screen position. Returns
    /// false when the region is on the far side of the globe from the
    /// camera (back-face cull).
    /// </summary>
    private bool RegionScreen(Region r, Vector2 centre, float radius, out Vector2 screen)
    {
        float lat = r.LatDeg * MathF.PI / 180f;
        float lon = r.LonDeg * MathF.PI / 180f;

        // Forward of the inverse-rotate in Render(). World point on unit sphere:
        float ux = MathF.Cos(lat) * MathF.Sin(lon);
        float uy = MathF.Sin(lat);
        float uz = MathF.Cos(lat) * MathF.Cos(lon);

        // Apply yaw about Y.
        float cy_y = MathF.Cos(_yaw), sy_y = MathF.Sin(_yaw);
        float wx = ux * cy_y - uz * sy_y;
        float wy = uy;
        float wz = ux * sy_y + uz * cy_y;
        // Apply pitch about X.
        float cy_p = MathF.Cos(_pitch), sy_p = MathF.Sin(_pitch);
        float nx = wx;
        float ny = wy * cy_p - wz * sy_p;
        float nz = wy * sy_p + wz * cy_p;

        screen = new Vector2(centre.X + nx * radius, centre.Y - ny * radius);
        return nz > 0.05f;          // back-face cull with a small bias
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
