using System.Text.Json.Serialization;

namespace MouseHouse.Net.Buddies;

/// <summary>
/// Wire-format envelope for messages sent to a friend's inbox topic.
/// Always sealed with <see cref="BuddyCrypto.Seal"/> before
/// publishing — the broker only ever sees the ciphertext.
///
/// Kinds:
///   request — initial friend request (sender's pubkey + name).
///   accept  — recipient's reply to a request (their pubkey + name).
///   challenge — invite to play a game (generic stub; golf uses golf_race).
///   golf_race — netplay-golf race envelope; <see cref="GolfRace"/>
///     carries the per-lifecycle-stage payload (challenge / accept /
///     decline / stroke / hole_complete / finish / disconnect).
///   chat — chat message body in <see cref="Text"/>.
/// </summary>
public sealed class InboxEnvelope
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("from_code")] public string FromCode { get; set; } = "";
    [JsonPropertyName("from_pubkey_b64")] public string FromPublicKeyB64 { get; set; } = "";
    [JsonPropertyName("from_name")] public string FromName { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("game")] public string? Game { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("sent_at")] public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Populated when <see cref="Kind"/> == "golf_race".</summary>
    [JsonPropertyName("golf_race")] public GolfRacePayload? GolfRace { get; set; }

    /// <summary>Populated when <see cref="Kind"/> == "chess_race".</summary>
    [JsonPropertyName("chess_race")] public ChessRacePayload? ChessRace { get; set; }

    /// <summary>Populated when <see cref="Kind"/> == "tetris_race".</summary>
    [JsonPropertyName("tetris_race")] public TetrisRacePayload? TetrisRace { get; set; }

    /// <summary>Populated when <see cref="Kind"/> == "hearts".</summary>
    [JsonPropertyName("hearts")] public HeartsPayload? Hearts { get; set; }
}

/// <summary>
/// Per-stage netplay-golf payload carried inside an inbox envelope.
/// Versioned via <see cref="Protocol"/> ("golf_race_v1") so future
/// protocol changes can be rejected by old clients without
/// silently misinterpreting fields.
///
/// Subkinds (single string field rather than separate envelope kinds
/// so the outer envelope schema stays small):
/// <list type="bullet">
///   <item><c>challenge</c> — sender proposes a race
///     (Seed/Region/Difficulty populated).</item>
///   <item><c>accept</c> — recipient agreed; both clients open the
///     activity simultaneously.</item>
///   <item><c>decline</c> — recipient declined (or timed out).</item>
///   <item><c>stroke</c> — every hit, after the stroke counter
///     ticks; carries Hole + Stroke + ElapsedMs.</item>
///   <item><c>hole_complete</c> — ball-in-cup; carries Hole + Score
///     (running total of strokes so far in the round).</item>
///   <item><c>finish</c> — round complete; carries Score (total
///     strokes) + ElapsedMs (total ms since match start).</item>
///   <item><c>disconnect</c> — explicit "I'm leaving" signal so
///     the peer doesn't have to wait out the broker stale window.</item>
/// </list>
/// </summary>
public sealed class GolfRacePayload
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "golf_race_v1";
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";

    // Populated on `challenge`. Recipient mirrors these into its
    // local NetplayGolfSession so the course generates identically.
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; }

    // Populated on `stroke`, `hole_complete`, `finish`.
    [JsonPropertyName("hole")] public int Hole { get; set; }
    [JsonPropertyName("stroke")] public int Stroke { get; set; }
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

/// <summary>
/// Per-stage netplay-chess payload. Same shape as
/// <see cref="GolfRacePayload"/>: versioned via <see cref="Protocol"/>,
/// one Sub string covering the full lifecycle so the outer envelope
/// schema doesn't grow per-game.
///
/// Subkinds:
/// <list type="bullet">
///   <item><c>challenge</c> — sender proposes the race; ships the
///     pre-fetched <see cref="Puzzles"/> queue + <see cref="TimeLimitSeconds"/>
///     + <see cref="StartingBand"/>. Recipient sees the full puzzle
///     list in the envelope so both sides solve from byte-identical
///     queues without a deterministic-fetch dance.</item>
///   <item><c>accept</c> — recipient agreed; activity opens on both
///     sides simultaneously.</item>
///   <item><c>decline</c> — recipient declined.</item>
///   <item><c>puzzle_solved</c> — local player completed the
///     current puzzle. Carries <see cref="PuzzleIndex"/>,
///     <see cref="Score"/> (cumulative solved), <see cref="ElapsedMs"/>.</item>
///   <item><c>puzzle_failed</c> — local player gave up (move on
///     after a wrong move; no penalty beyond the time spent).
///     Same fields populated.</item>
///   <item><c>finish</c> — local timer hit zero or queue exhausted.
///     Score = final solved count.</item>
///   <item><c>disconnect</c> — explicit quit before time-up.</item>
/// </list>
/// </summary>
public sealed class ChessRacePayload
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "chess_race_v1";
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";

    // Populated on `challenge`.
    [JsonPropertyName("time_limit_seconds")] public int TimeLimitSeconds { get; set; }
    [JsonPropertyName("starting_band")] public string StartingBand { get; set; } = "";
    [JsonPropertyName("puzzles")] public List<ChessRacePuzzle>? Puzzles { get; set; }

    // Populated on per-puzzle events + finish.
    [JsonPropertyName("puzzle_index")] public int PuzzleIndex { get; set; }
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

/// <summary>
/// One puzzle as it travels in the chess-race challenge envelope.
/// Mirrors the on-the-wire shape of Lichess's puzzle endpoint just
/// enough that the activity can render it the same way it renders
/// a freshly-fetched one (PGN replay → solution UCI sequence) —
/// no rating-fetch round trip needed during the race.
///
/// Themes are intentionally dropped to keep the envelope small;
/// they only affect status-bar flavor text and the race scoreboard
/// doesn't surface them. ID is kept so the activity's existing
/// "puzzle: lichess/xxxx" status line stays meaningful.
/// </summary>
public sealed class ChessRacePuzzle
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("pgn")] public string Pgn { get; set; } = "";
    [JsonPropertyName("solution")] public string[] Solution { get; set; } = Array.Empty<string>();
    [JsonPropertyName("rating")] public int Rating { get; set; }
    /// <summary>Set when this puzzle came from the bundled fallback
    /// (no PGN; <see cref="Fen"/> + <see cref="ExpectedUci"/> instead).
    /// The activity loads it via <c>LoadFen</c> rather than
    /// <c>ApplyPuzzle</c>.</summary>
    [JsonPropertyName("fen")] public string? Fen { get; set; }
    [JsonPropertyName("expected_uci")] public string? ExpectedUci { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("white_to_move")] public bool WhiteToMove { get; set; } = true;
}

/// <summary>
/// Per-stage netplay-tetris payload. Mirrors the golf / chess
/// shapes: versioned protocol, single <see cref="Sub"/> string for
/// every lifecycle stage.
///
/// Subkinds:
/// <list type="bullet">
///   <item><c>challenge</c> — sender proposes the race (Seed +
///     StartingLevel populated).</item>
///   <item><c>accept</c> — recipient agreed.</item>
///   <item><c>decline</c> — recipient declined.</item>
///   <item><c>lines_cleared</c> — local cleared N rows; carries
///     <see cref="ClearLines"/>, <see cref="ClearKind"/> (
///     "single"|"double"|"triple"|"tetris"|"tspin1"|"tspin2"|"tspin3"
///     |"perfect"), and the <see cref="GarbageSent"/> count after
///     the local cancel pass. The receiver applies the garbage
///     after their NEXT piece-lock, classic Tetris Battle style.</item>
///   <item><c>board_snapshot</c> — coarse 10×20 board state for
///     the opponent mini-board. Cells packed two-per-byte then
///     base64; <see cref="Score"/>, <see cref="Lines"/>,
///     <see cref="Level"/>, and <see cref="PendingGarbage"/>
///     ride along so the mini-board renders correctly without a
///     separate stat event.</item>
///   <item><c>top_out</c> — sender's piece can't spawn; sender
///     declares themselves the loser (peer wins).</item>
///   <item><c>disconnect</c> — explicit "I'm leaving" signal.</item>
/// </list>
///
/// We deliberately do NOT replicate every piece move on the wire —
/// the existing per-game protocol shapes (golf strokes, chess
/// solves) assume "report the meaningful state-change, not the
/// input stream", and the same here keeps bandwidth bounded
/// regardless of how fast either player drops pieces.
/// </summary>
public sealed class TetrisRacePayload
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "tetris_race_v1";
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";

    // ── challenge / accept ──
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("starting_level")] public int StartingLevel { get; set; } = 1;

    // ── lines_cleared ──
    [JsonPropertyName("clear_lines")] public int ClearLines { get; set; }
    [JsonPropertyName("clear_kind")] public string? ClearKind { get; set; }
    [JsonPropertyName("garbage_sent")] public int GarbageSent { get; set; }

    // ── board_snapshot ──
    /// <summary>200 cells (10 columns × 20 rows) packed 2 per byte
    /// (each cell is 4 bits; 0 = empty, 1..7 = piece type, 8 =
    /// garbage block). 100 bytes → base64 ≈ 136 chars per snapshot.
    /// </summary>
    [JsonPropertyName("board_b64")] public string? BoardB64 { get; set; }
    [JsonPropertyName("score")] public int Score { get; set; }
    [JsonPropertyName("lines")] public int Lines { get; set; }
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("pending_garbage")] public int PendingGarbage { get; set; }

    // ── top_out ──
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

/// <summary>
/// Presence payload on the per-user retained topic. Not encrypted —
/// anyone who knows the user's friend code (and therefore the topic
/// id) can subscribe and see status. The away message field is
/// shown verbatim to friends; the user is responsible for not
/// putting secrets in it.
/// </summary>
public sealed class PresencePayload
{
    [JsonPropertyName("status")] public BuddyStatus Status { get; set; } = BuddyStatus.Offline;
    [JsonPropertyName("away_message")] public string AwayMessage { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("last_seen")] public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-stage netplay-Hearts payload. Host-mediated: the challenger
/// is the host and owns canonical state. Sub-envelopes broken into
/// lifecycle (challenge / accept / decline / seat_update /
/// start_match) and per-event (pass_submitted / card_played /
/// trick_complete / hand_complete / match_complete / disconnect).
///
/// Host validates plays from peers + rebroadcasts canonical
/// events to every other seat. Peers SUBMIT plays (card_played)
/// but only act on plays the host echoes back (trick_complete
/// announces the canonical resolution). This avoids the "two
/// clients diverge on rule interpretation" problem at the cost
/// of one network round-trip per play.
///
/// The host computes other players' hands from the shared seed
/// and is socially expected not to peek. See Netplay/README.md
/// threat-model section.
/// </summary>
public sealed class HeartsPayload
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "hearts_v1";
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";

    // ── challenge / accept / decline / seat_update / start_match ──
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("difficulty")] public string Difficulty { get; set; } = "Standard";
    /// <summary>The proposed/current seat composition. 4 entries,
    /// one per seat (clockwise from the host at index 0). Each
    /// entry's `Kind` is "host" / "friend" / "ai"; FriendCode is
    /// populated for friends. Updated as friends accept.</summary>
    [JsonPropertyName("seats")] public List<HeartsSeat>? Seats { get; set; }

    // ── pass_submitted ──
    /// <summary>Sender's pass picks — three card keys (suit*16+rank).
    /// Always sent from peer → host; host applies them once all
    /// four pass sets are in and rebroadcasts the canonical
    /// hand-state via card_played / hand updates as play
    /// progresses.</summary>
    [JsonPropertyName("pass_keys")] public List<int>? PassKeys { get; set; }

    // ── card_played ──
    /// <summary>Which seat played. Peer→host carries the seat of
    /// the local sender (0..3 in the canonical seat order); host→
    /// all carries the canonical seat after validation.</summary>
    [JsonPropertyName("seat")] public int Seat { get; set; }
    /// <summary>Suit*16 + rank, e.g. Hearts(0)*16+12 = 12 for QH.
    /// Compact one-int encoding — full card list is small enough
    /// that human-readable JSON isn't worth the overhead.</summary>
    [JsonPropertyName("card_key")] public int CardKey { get; set; }

    // ── trick_complete ──
    [JsonPropertyName("trick_winner")] public int TrickWinner { get; set; }

    // ── hand_complete ──
    /// <summary>Final hand scores per seat after any moon-shot
    /// reversal. Ordered seat 0..3.</summary>
    [JsonPropertyName("hand_scores")] public List<int>? HandScores { get; set; }
    /// <summary>Cumulative totals after this hand. Ordered seat 0..3.</summary>
    [JsonPropertyName("total_scores")] public List<int>? TotalScores { get; set; }
    [JsonPropertyName("moon_seat")] public int MoonSeat { get; set; } = -1;

    // ── match_complete ──
    [JsonPropertyName("winner_seat")] public int WinnerSeat { get; set; } = -1;
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; set; }
}

public sealed class HeartsSeat
{
    /// <summary>"host" / "friend" / "ai" / "pending" (invited human
    /// who hasn't accepted yet) / "empty" (slot left blank by the
    /// host).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "empty";
    /// <summary>Display name shown in the seat label.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Populated for kind == "friend" / "pending". The
    /// host uses this to route per-seat sealed envelopes.</summary>
    [JsonPropertyName("friend_code")] public string FriendCode { get; set; } = "";
}
