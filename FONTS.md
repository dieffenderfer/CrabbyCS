# Font Candidates for Mouse House

To preview all fonts visually in-app: **Right-click pet → Appearance → Preview Fonts**

The preview panel shows each font at 12px, 16px, and 20px with full alphabet + sample text. Scroll to compare.

---

## Currently Loaded Fonts

### Press Start 2P
- **Style:** Chunky 8-bit arcade
- **License:** OFL (Open Font License)
- **Best for:** Titles, retro game UI
- **Notes:** Very wide — takes up a lot of horizontal space. Iconic pixel gaming look. May be too heavy for small body text.

### Silkscreen (Regular + Bold)
- **Style:** Clean, minimal pixel font
- **License:** OFL
- **Best for:** Menus, UI labels, body text
- **Notes:** Excellent legibility at small sizes. Designed specifically for screen display. One of the most popular pixel fonts. Bold weight available.

### VT323
- **Style:** Classic terminal / VT320 video terminal
- **License:** OFL
- **Best for:** Console-style text, status displays, monospace needs
- **Notes:** Faithful recreation of the DEC VT320 terminal font. Tall and narrow with good readability. Has a nostalgic computer terminal feel.

### DotGothic16
- **Style:** Japanese dot-matrix gothic
- **License:** OFL
- **Best for:** Clean body text, multilingual support (includes Japanese)
- **Notes:** Square, clean pixel aesthetic derived from Japanese bitmap fonts. Supports Latin + CJK characters. Very readable at 16px.

### Pixelify Sans
- **Style:** Modern variable pixel font
- **License:** OFL
- **Best for:** Friendly UI text, titles
- **Notes:** Variable weight support (light to bold). Rounder and friendlier than most pixel fonts. Good at larger sizes; designed for pixel-art games.

### Jersey 10
- **Style:** Condensed sport/jersey numeral pixel font
- **License:** OFL
- **Best for:** Scores, numbers, compact displays
- **Notes:** Very compact and tall. Great for fitting text in tight spaces. Sports scoreboard aesthetic.

### Jersey 15
- **Style:** Wider sport/jersey pixel font
- **License:** OFL
- **Best for:** Titles, larger displays
- **Notes:** Wider variant of Jersey 10. More readable at body text sizes. Still has that sporty pixel feel.

### Share Tech Mono
- **Style:** Clean monospace tech font
- **License:** OFL
- **Best for:** Code display, technical readouts, coordinates
- **Notes:** Not strictly a pixel font but very clean at small sizes. Good for chess coordinates, status info, or any fixed-width content.

### Bungee Shade
- **Style:** Decorative block letters with shadow
- **License:** OFL
- **Best for:** Big titles, splash screens
- **Notes:** Very decorative — each character has a built-in 3D shadow effect. Only suitable for large display text, not body copy.

---

## Recommendations

| Use Case | Recommended Font |
|----------|-----------------|
| **Menu text / UI** | Silkscreen or VT323 |
| **Activity titles** | Pixelify Sans or Press Start 2P |
| **Body text / instructions** | Silkscreen, DotGothic16, or VT323 |
| **Scores / numbers** | Jersey 10 or Press Start 2P |
| **Small labels** | Silkscreen (most legible at tiny sizes) |
| **Status bubble** | Silkscreen or VT323 |
| **Decorative / splash** | Bungee Shade |

## How to Switch App Font

Once you pick a font, the change involves:
1. Loading it via `Raylib.LoadFontEx()` in the scene/activity
2. Replacing `Raylib.DrawText()` calls with `Raylib.DrawTextEx()` using the loaded font
3. Replacing `Raylib.MeasureText()` with `Raylib.MeasureTextEx()`

All font files are in `assets/fonts/` and licensed under OFL (free for any use).
