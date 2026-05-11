using MouseHouse.Scenes.Activities;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// One in-progress competitive Tetris match. Same registration /
/// inbound-routing / RecordAndUnregister model as the golf and
/// chess sessions.
///
/// Garbage attack table (from spec, applied in <see cref="OnLocalLinesCleared"/>):
/// <list type="bullet">
///   <item>Single (1 line) → 0 garbage</item>
///   <item>Double (2 lines) → 1 garbage</item>
///   <item>Triple (3 lines) → 2 garbage</item>
///   <item>Tetris (4 lines) → 4 garbage</item>
///   <item>T-spin Single → 2 garbage</item>
///   <item>T-spin Double → 4 garbage</item>
///   <item>T-spin Triple → 6 garbage</item>
///   <item>Back-to-back chain → +1 garbage per consecutive "hard"
///     clear (Tetris or T-spin)</item>
///   <item>Combo bonus → +1 garbage per chained clear
///     (combo length ≥ 1)</item>
///   <item>Perfect clear → +10 garbage</item>
/// </list>
/// Counter mechanic: pending garbage on us is cancelled 1-for-1
/// against any outgoing attack first; only the remainder ships.
/// </summary>
public sealed class NetplayTetrisSession : INetplayTetrisSink
{
    public const string Protocol = "tetris_race_v1";

    public BuddyService Svc { get; }
    public Friend Peer { get; }
    public bool IsHost { get; }
    public int Seed { get; }
    public int StartingLevel { get; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    string INetplayTetrisSink.PeerName => Peer.Nickname;

    // ── Peer mirror ──────────────────────────────────────────────────
    /// <summary>10×20 = 200-cell coarse board, row-major top-down,
    /// 0/1..7/8 cell convention. Replaced whole-array on each
    /// inbound snapshot.</summary>
    public byte[] PeerBoard { get; private set; } = new byte[200];
    public int PeerScore { get; private set; }
    public int PeerLines { get; private set; }
    public int PeerLevel { get; private set; }
    public int PeerPendingGarbage { get; private set; }
    public bool PeerToppedOut { get; private set; }
    public bool PeerDisconnected { get; private set; }
    public DateTime LastPeerMessageUtc { get; private set; } = DateTime.UtcNow;

    public static readonly TimeSpan PeerStaleAfter = TimeSpan.FromSeconds(30);
    public bool IsPeerStale =>
        !PeerToppedOut && !PeerDisconnected
        && DateTime.UtcNow - LastPeerMessageUtc > PeerStaleAfter;

    // ── Local state we care about for matches.json ───────────────────
    public int LocalScore { get; private set; }
    public int LocalLines { get; private set; }
    public int LocalLevel { get; private set; }
    public bool LocalToppedOut { get; private set; }
    public int LocalPendingGarbage { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? Toast;

    public NetplayTetrisSession(BuddyService svc, Friend peer, bool isHost,
        int seed, int startingLevel)
    {
        Svc = svc;
        Peer = peer;
        IsHost = isHost;
        Seed = seed;
        StartingLevel = startingLevel;
        LocalLevel = startingLevel;
    }

    public int ConsumePendingGarbage()
    {
        int n = LocalPendingGarbage;
        LocalPendingGarbage = 0;
        if (n > 0) StateChanged?.Invoke();
        return n;
    }

    public void OnLocalLinesCleared(int rows, string clearKind, bool perfectClear)
    {
        // Mark the line count locally so RecordAndUnregister has it
        // even if we never got a snapshot through. (Snapshots are
        // the canonical "score+lines" channel; this is the
        // belt-and-suspenders for early-aborts.)
        LocalLines += rows;
        int attack = AttackFor(clearKind);
        if (perfectClear) attack += 10;

        // Cancel: pending garbage on us absorbs the outgoing attack
        // first. Whatever's left actually goes to the peer.
        int absorbed = Math.Min(attack, LocalPendingGarbage);
        LocalPendingGarbage -= absorbed;
        int sent = attack - absorbed;

        SendPayload(new TetrisRacePayload
        {
            Sub = "lines_cleared",
            ClearLines = rows,
            ClearKind = clearKind,
            GarbageSent = sent,
        });
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Number of garbage rows to ship for a single clear event,
    /// before perfect-clear bonus and before the cancel pass.
    /// Activity is responsible for figuring out which "kind" the
    /// clear is (combo / B2B already factored into the kind string
    /// or as a +N suffix).
    /// </summary>
    private static int AttackFor(string clearKind)
        => clearKind switch
        {
            "single"   => 0,
            "double"   => 1,
            "triple"   => 2,
            "tetris"   => 4,
            "tspin1"   => 2,
            "tspin2"   => 4,
            "tspin3"   => 6,
            // "b2b" / "combo" / "b2b_tetris" are passed by the
            // activity as the bare kind ("tetris" / "tspin2") plus a
            // separate combo/b2b bonus encoded in clear_kind. To
            // keep this table flat, the activity computes the
            // bonus and adds it to GarbageSent itself; this method
            // returns the base-line for unknown / non-attack kinds.
            _ => 0,
        };

    public void PushBoardSnapshot(byte[] board, int score, int lines, int level)
    {
        LocalScore = score;
        LocalLines = lines;
        LocalLevel = level;
        var b64 = Convert.ToBase64String(PackCells(board));
        SendPayload(new TetrisRacePayload
        {
            Sub = "board_snapshot",
            BoardB64 = b64,
            Score = score,
            Lines = lines,
            Level = level,
            PendingGarbage = LocalPendingGarbage,
        });
    }

    public void OnLocalTopOut()
    {
        if (LocalToppedOut) return;
        LocalToppedOut = true;
        SendPayload(new TetrisRacePayload
        {
            Sub = "top_out",
            ElapsedMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
        });
        Toast?.Invoke($"🏆 {Peer.Nickname} wins!");
        StateChanged?.Invoke();
    }

    public void OnLocalQuit()
        => SendPayload(new TetrisRacePayload { Sub = "disconnect" });

    /// <summary>
    /// Pack 200 cells (0..15 per cell) into 100 bytes — 2 cells per
    /// byte, low nibble first.
    /// </summary>
    private static byte[] PackCells(byte[] cells)
    {
        if (cells.Length != 200)
            throw new ArgumentException("Snapshot must be 200 cells", nameof(cells));
        var packed = new byte[100];
        for (int i = 0; i < 100; i++)
        {
            byte a = (byte)(cells[2 * i] & 0x0F);
            byte b = (byte)(cells[2 * i + 1] & 0x0F);
            packed[i] = (byte)(a | (b << 4));
        }
        return packed;
    }

    private static byte[] UnpackCells(byte[] packed)
    {
        var cells = new byte[200];
        for (int i = 0; i < 100 && i < packed.Length; i++)
        {
            cells[2 * i] = (byte)(packed[i] & 0x0F);
            cells[2 * i + 1] = (byte)((packed[i] >> 4) & 0x0F);
        }
        return cells;
    }

    // ── Inbound ──────────────────────────────────────────────────────
    public void HandleInbound(TetrisRacePayload p)
    {
        if (p.Protocol != Protocol) return;
        LastPeerMessageUtc = DateTime.UtcNow;
        switch (p.Sub)
        {
            case "lines_cleared":
                // Peer attacked us. Queue the garbage rows for the
                // activity to consume after its next piece-lock.
                if (p.GarbageSent > 0) LocalPendingGarbage += p.GarbageSent;
                StateChanged?.Invoke();
                break;
            case "board_snapshot":
                if (!string.IsNullOrEmpty(p.BoardB64))
                {
                    try
                    {
                        var packed = Convert.FromBase64String(p.BoardB64!);
                        PeerBoard = UnpackCells(packed);
                    }
                    catch { /* malformed snapshot — keep the last good one */ }
                }
                PeerScore = p.Score;
                PeerLines = p.Lines;
                PeerLevel = p.Level;
                PeerPendingGarbage = p.PendingGarbage;
                StateChanged?.Invoke();
                break;
            case "top_out":
                PeerToppedOut = true;
                Toast?.Invoke($"🏆 You win — {Peer.Nickname} topped out!");
                StateChanged?.Invoke();
                break;
            case "disconnect":
                PeerDisconnected = true;
                Toast?.Invoke($"{Peer.Nickname} disconnected.");
                StateChanged?.Invoke();
                break;
        }
    }

    private void SendPayload(TetrisRacePayload payload)
        => _ = Svc.Client.SendTetrisRace(Peer.Code, payload);

    public MatchRecord ToRecord()
    {
        // Loser tag: whoever topped out / forfeited first loses.
        // Top-out from us → "local"; peer top-out or disconnect →
        // "peer". Window closed without either → "" (no winner).
        string loser = LocalToppedOut ? "local"
            : (PeerToppedOut || PeerDisconnected) ? "peer"
            : "";
        return new MatchRecord
        {
            Kind = "tetris",
            EndedAtUtc = DateTime.UtcNow,
            PeerCode = Peer.Code,
            PeerName = Peer.Nickname,
            StartingLevel = StartingLevel,
            DurationMs = (long)(DateTime.UtcNow - StartedAtUtc).TotalMilliseconds,
            LocalScore = LocalScore,
            LocalLines = LocalLines,
            LocalFinalLevel = LocalLevel,
            PeerScore = PeerScore,
            PeerLines = PeerLines,
            PeerFinalLevel = PeerLevel,
            PeerDisconnected = PeerDisconnected,
            Loser = loser,
        };
    }

    private bool _recorded;
    public void RecordAndUnregister()
    {
        if (_recorded) return;
        _recorded = true;
        MatchHistory.Append(ToRecord());
        Svc.UnregisterTetrisSession(this);
    }
}
