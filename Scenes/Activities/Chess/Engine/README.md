# MinimalChess (vendored)

Files in this directory are vendored verbatim from
[lithander/MinimalChessEngine](https://github.com/lithander/MinimalChessEngine)
(only the `MinimalChess` library — the UCI binary and GUI are not vendored).

Copyright (c) 2021 Thomas Jahn. Licensed under the MIT License — see
`LICENSE` in this directory.

The library exposes a `MinimalChess.Board` and a `MinimalChess.IterativeSearch`
that together give us a self-contained chess engine inside the app — no UCI
subprocess, no per-platform binary, no extra download. Around 700 LOC, plays
~2440 Elo at full strength.

Training mode wraps these classes with a depth cap (and optional move-jitter)
so the engine plays at a beginner-friendly level for lessons that need it.
