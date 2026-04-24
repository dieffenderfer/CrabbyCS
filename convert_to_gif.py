#!/usr/bin/env python3
"""Convert horizontal sprite sheet PNGs to animated GIFs for Aseprite editing."""

import os
import sys
from PIL import Image
from pathlib import Path

ASSETS = Path("/Users/david/CrabbyCS/assets")

# Known sprite sheets: (relative path from assets, frame_count, frame_duration_ms)
# Duration: GIF uses centiseconds (minimum 20ms = 2cs for most viewers)
SPRITE_SHEETS = {
    # Mouse pet animations — 3 color modes
    # Walk: 8 frames @ 250ms
    "sprites/pets/mouse_walk.png":           (8, 250),
    "sprites/pets/mouse_1c_walk.png":        (8, 250),
    "sprites/pets/mouse_fc_walk.png":        (8, 250),
    "sprites/pets/mouse_walk_pinknose.png":  (8, 250),
    # Idle: 8 frames @ 130ms
    "sprites/pets/mouse_idle.png":           (8, 130),
    "sprites/pets/mouse_1c_idle.png":        (8, 130),
    "sprites/pets/mouse_fc_idle.png":        (8, 130),
    # Sleep intro: 12 frames @ 130ms
    "sprites/pets/mouse_sleep.png":          (12, 130),
    "sprites/pets/mouse_1c_sleep.png":       (12, 130),
    "sprites/pets/mouse_fc_sleep.png":       (12, 130),
    # Sleep loop: 3 frames @ 300ms (frame 0 holds longer in-game but 300ms for GIF preview)
    "sprites/pets/mouse_sleep_loop.png":     (3, 300),
    "sprites/pets/mouse_1c_sleep_loop.png":  (3, 300),
    "sprites/pets/mouse_fc_sleep_loop.png":  (3, 300),
    # Jump: 8 frames @ 130ms
    "sprites/pets/mouse_jump.png":           (8, 130),
    "sprites/pets/mouse_1c_jump.png":        (8, 130),
    "sprites/pets/mouse_fc_jump.png":        (8, 130),
    "sprites/pets/mouse_jump_eyes_closed.png": (8, 130),
    # Backup files that are also sprite sheets
    "sprites/pets/mouse_idle BACKUP.png":    (8, 130),
    "sprites/pets/mouse_walk BACKUP.png":    (8, 250),
    "sprites/pets/mouse_sleep BACKUP.png":   (12, 130),
    "sprites/pets/mouse_sleep BACKUP v2.png": (12, 130),
    "sprites/pets/mouse_sleep_12frame_backup.png": (12, 130),
    "sprites/pets/mouse_sleep_loop_12frame_backup.png": (3, 300),

    # Event sprites — 150ms per frame (AnimSpeed = 0.15f)
    "sprites/events/seagull.png":        (8, 150),
    "sprites/events/butterfly.png":      (4, 150),
    "sprites/events/falling_leaf.png":   (4, 150),
    "sprites/events/shooting_star.png":  (3, 150),
    "sprites/events/firefly.png":        (4, 150),
    "sprites/events/paper_airplane.png": (4, 150),
    "sprites/events/balloon.png":        (2, 150),
    "sprites/events/rain_cloud.png":     (6, 150),
    "sprites/events/bat.png":            (4, 150),
    "sprites/events/ladybug.png":        (4, 150),
    "sprites/events/dragonfly.png":      (4, 150),
    "sprites/events/jellyfish.png":      (4, 150),
    "sprites/events/dolphin.png":        (6, 150),
    "sprites/events/hot_air_balloon.png":(2, 150),
    "sprites/events/comet.png":          (3, 150),
    "sprites/events/dust_devil.png":     (4, 150),
    "sprites/events/frog.png":           (4, 150),
    "sprites/events/hermit_crab.png":    (4, 150),
    "sprites/events/pelican.png":        (4, 150),
    "sprites/events/crab_ghost.png":     (4, 150),
}

# Auto-detect color variants for events
EVENT_NAMES_WITH_KNOWN_FRAMES = {
    "seagull": 8, "butterfly": 4, "falling_leaf": 4, "shooting_star": 3,
    "firefly": 4, "paper_airplane": 4, "balloon": 2, "rain_cloud": 6,
    "bat": 4, "ladybug": 4, "dragonfly": 4, "jellyfish": 4,
    "dolphin": 6, "hot_air_balloon": 2, "comet": 3, "dust_devil": 4,
    "frog": 4, "hermit_crab": 4, "pelican": 4, "crab_ghost": 4,
}

# Add 1color and 2color variants for events
for name, frames in EVENT_NAMES_WITH_KNOWN_FRAMES.items():
    for subdir in ["1color", "2color"]:
        key = f"sprites/events/{subdir}/{name}.png"
        SPRITE_SHEETS[key] = (frames, 150)


def detect_sprite_sheet(img_path):
    """Try to detect if a PNG is a horizontal sprite sheet by checking if width > height
    and width is evenly divisible by height (square frames)."""
    try:
        img = Image.open(img_path)
        w, h = img.size
        if w > h and h > 0 and w % h == 0:
            frame_count = w // h
            if 2 <= frame_count <= 20:
                return frame_count
    except Exception:
        pass
    return None


def convert_spritesheet_to_gif(png_path, frame_count, duration_ms, gif_path):
    """Split a horizontal sprite sheet into frames and save as animated GIF."""
    img = Image.open(png_path).convert("RGBA")
    w, h = img.size
    frame_w = w // frame_count

    frames = []
    for i in range(frame_count):
        frame = img.crop((i * frame_w, 0, (i + 1) * frame_w, h))
        # Convert RGBA to palette mode for GIF, preserving transparency
        # Use a white background for compositing, then set transparency
        bg = Image.new("RGBA", frame.size, (0, 0, 0, 0))
        bg.paste(frame, (0, 0))
        # Quantize to palette, keeping transparency
        alpha = bg.split()[3]
        # Create a version with a distinct background color for transparent pixels
        rgb = Image.new("RGB", frame.size, (255, 0, 255))  # magenta key
        rgb.paste(frame, mask=alpha)
        # Quantize
        quantized = rgb.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
        # Find the magenta index for transparency
        palette = quantized.getpalette()
        trans_idx = None
        for idx in range(256):
            if palette and idx * 3 + 2 < len(palette):
                r, g, b = palette[idx*3], palette[idx*3+1], palette[idx*3+2]
                if r == 255 and g == 0 and b == 255:
                    trans_idx = idx
                    break
        # For pixels that were transparent, set them to the transparency index
        result = quantized.copy()
        frames.append((result, trans_idx))

    if not frames:
        return False

    # Save as animated GIF
    gif_frames = []
    first_trans = None
    for result, trans_idx in frames:
        gif_frames.append(result)
        if first_trans is None:
            first_trans = trans_idx

    gif_frames[0].save(
        gif_path,
        save_all=True,
        append_images=gif_frames[1:],
        duration=duration_ms,
        loop=0,
        transparency=first_trans if first_trans is not None else 0,
        disposal=2,  # restore to background (important for transparency)
    )
    return True


def convert_single_to_gif(png_path, gif_path):
    """Convert a single-frame PNG to a single-frame GIF."""
    img = Image.open(png_path).convert("RGBA")
    alpha = img.split()[3]
    rgb = Image.new("RGB", img.size, (255, 0, 255))
    rgb.paste(img, mask=alpha)
    quantized = rgb.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    palette = quantized.getpalette()
    trans_idx = None
    for idx in range(256):
        if palette and idx * 3 + 2 < len(palette):
            r, g, b = palette[idx*3], palette[idx*3+1], palette[idx*3+2]
            if r == 255 and g == 0 and b == 255:
                trans_idx = idx
                break
    quantized.save(gif_path, transparency=trans_idx if trans_idx is not None else 0)
    return True


def main():
    converted = 0
    skipped = 0
    errors = 0
    auto_detected = 0

    # Find all PNGs
    all_pngs = sorted(ASSETS.rglob("*.png"))
    print(f"Found {len(all_pngs)} PNG files")

    for png_path in all_pngs:
        rel = str(png_path.relative_to(ASSETS))
        gif_path = png_path.with_suffix(".gif")

        try:
            if rel in SPRITE_SHEETS:
                frame_count, duration = SPRITE_SHEETS[rel]
                if convert_spritesheet_to_gif(png_path, frame_count, duration, gif_path):
                    converted += 1
                    print(f"  [SHEET {frame_count}f] {rel}")
                else:
                    errors += 1
                    print(f"  [ERROR] {rel}")
            else:
                # Try auto-detection
                detected = detect_sprite_sheet(png_path)
                if detected:
                    auto_detected += 1
                    if convert_spritesheet_to_gif(png_path, detected, 150, gif_path):
                        converted += 1
                        print(f"  [AUTO {detected}f] {rel}")
                    else:
                        errors += 1
                else:
                    # Single-frame image
                    if convert_single_to_gif(png_path, gif_path):
                        converted += 1
                    else:
                        errors += 1
        except Exception as e:
            errors += 1
            print(f"  [ERROR] {rel}: {e}")

    print(f"\nDone: {converted} converted, {auto_detected} auto-detected sheets, {errors} errors")


if __name__ == "__main__":
    main()
