using System.Numerics;
using Raylib_cs;
using MouseHouse.Scenes.Activities.Retro;

namespace MouseHouse.Scenes.Activities;

/// <summary>
/// WordZap — make as many valid words as possible from a 7-letter pool. Click
/// letters to build a word, Enter / Submit to score. Score = letters used.
/// Backspace removes the last letter; Clear resets the input. Built-in
/// dictionary of common short words; you can drop your own wordlist into
/// assets/text/wordzap.txt (one word per line) to expand it.
/// </summary>
public class WordZapActivity : IActivity
{
    private const int FrameInset = 3;
    private const int PoolSize = 7;
    private const int PoolTile = 44;
    private const int Margin = 18;

    public Vector2 PanelSize => new(
        2 * FrameInset + 2 * Margin + PoolSize * (PoolTile + 6),
        2 * FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight
            + Margin + PoolTile + 30 + PoolTile + 30 + 110 + Margin
            + RetroWidgets.StatusBarHeight);

    public bool IsFinished { get; private set; }

    private readonly RetroHelp _help = new()
    {
        Title = "WordZap — How to play",
        Lines = new[]
        {
            "Make as many valid words as you can from the letter pool.",
            "Click a letter to add it to the current word.",
            "Press Enter or Submit to score it; longer words score more.",
            "Backspace removes the last letter; Clear resets the word.",
            "Built-in dictionary is small — drop a wordlist at",
            "assets/text/wordzap.txt to expand it.",
        },
    };

    private static readonly string[] Vowels = { "A", "E", "I", "O", "U" };
    private static readonly string[] Consonants = {
        "B","C","D","F","G","H","L","M","N","P","R","S","T","W","Y"
    };

    private string[] _pool = new string[PoolSize];
    private bool[] _used = new bool[PoolSize];
    private List<int> _word = new();
    private List<string> _found = new();
    private int _score;
    private string _msg = "";
    private float _msgTimer;
    private static HashSet<string>? _dict;
    private readonly Random _rng = new();

    public void Load()
    {
        EnsureDict();
        Reset();
    }

    private void Reset()
    {
        // 3 vowels + 4 consonants gives a workable pool
        var letters = new List<string>();
        for (int i = 0; i < 3; i++) letters.Add(Vowels[_rng.Next(Vowels.Length)]);
        for (int i = 0; i < 4; i++) letters.Add(Consonants[_rng.Next(Consonants.Length)]);
        for (int i = letters.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (letters[i], letters[j]) = (letters[j], letters[i]);
        }
        for (int i = 0; i < PoolSize; i++) _pool[i] = letters[i];
        for (int i = 0; i < PoolSize; i++) _used[i] = false;
        _word.Clear();
        _found.Clear();
        _score = 0;
        _msg = "";
    }

    private static void EnsureDict()
    {
        if (_dict != null) return;
        _dict = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Built-in starter list (curated common short words). Drop a larger
        // wordlist file at assets/text/wordzap.txt to expand without rebuilding.
        var seed = new[]
        {
            "ACE","ACT","ADD","AGE","AID","AIM","AIR","ALE","ALL","AND","ANT","ANY","APE","ARC","ARE","ARK",
            "ARM","ART","ASH","ASK","ATE","AWE","BAD","BAG","BAN","BAR","BAT","BAY","BED","BEE","BEG","BET",
            "BID","BIG","BIN","BIT","BOG","BOW","BOX","BOY","BUG","BUS","BUT","BUY","BYE","CAB","CAN","CAP",
            "CAR","CAT","COG","COP","COW","CRY","CUB","CUE","CUP","CUT","DAD","DAY","DEN","DEW","DID","DIG",
            "DIM","DIP","DOG","DOT","DRY","DUE","DYE","EAR","EAT","EBB","EGG","EGO","ELF","END","ERA","EVE",
            "EWE","EYE","FAN","FAR","FAT","FED","FEE","FEW","FIG","FIN","FIT","FIX","FLY","FOE","FOG","FOR",
            "FOX","FRY","FUN","FUR","GAP","GAS","GEM","GEL","GET","GIG","GIN","GOD","GOT","GUM","GUN","GUT",
            "GUY","GYM","HAD","HAM","HAS","HAT","HAY","HEN","HER","HEY","HID","HIM","HIP","HIS","HIT","HOG",
            "HOP","HOT","HOW","HUB","HUG","HUM","HUT","ICE","ICY","ILL","INK","INN","ION","IRE","IRK","ITS",
            "JAB","JAM","JAR","JAW","JAY","JET","JIG","JOB","JOG","JOT","JOY","JUG","JUT","KEG","KEY","KID",
            "KIN","KIT","LAB","LAD","LAG","LAP","LAW","LAY","LED","LEG","LET","LID","LIE","LIP","LIT","LOG",
            "LOT","LOW","MAD","MAN","MAP","MAR","MAT","MAY","MEN","MET","MID","MIX","MOB","MOM","MOP","MUD",
            "MUG","NAB","NAG","NAP","NAY","NET","NEW","NOD","NOR","NOT","NOW","NUN","NUT","OAK","OAR","OAT",
            "ODD","OFF","OIL","OLD","ONE","OPT","ORB","ORE","OUR","OUT","OWE","OWL","OWN","PAD","PAL","PAN",
            "PAT","PAW","PAY","PEA","PEG","PEN","PEP","PER","PET","PIE","PIG","PIN","PIT","POD","POP","POT",
            "POW","PRO","PRY","PUB","PUN","PUP","PUT","QUA","RAG","RAM","RAN","RAP","RAT","RAW","RAY","RED",
            "RIB","RID","RIG","RIM","RIP","ROB","ROD","ROT","ROW","RUB","RUE","RUG","RUN","RUT","RYE","SAD",
            "SAG","SAP","SAT","SAW","SAY","SEA","SEE","SET","SEW","SHE","SHY","SIN","SIP","SIR","SIT","SIX",
            "SKI","SKY","SLY","SOB","SOD","SON","SOP","SOW","SOY","SPA","SPY","SUB","SUE","SUM","SUN","TAB",
            "TAG","TAN","TAP","TAR","TAX","TEA","TEE","TEN","THE","TIE","TIP","TOE","TON","TOO","TOP","TOT",
            "TOW","TOY","TRY","TUB","TUG","TUN","TWO","UGH","ULP","UMP","URN","USE","VAN","VAT","VET","VEX",
            "VIA","VIE","VOW","WAG","WAR","WAS","WAX","WAY","WEB","WED","WEE","WET","WHO","WHY","WIG","WIN",
            "WIT","WOE","WON","WOO","WOW","YAK","YAM","YAP","YAY","YEA","YES","YET","YEW","YIN","YOU","YOW",
            "ZAG","ZAP","ZED","ZEN","ZIG","ZIP","ZIT","ZOO","ZUZ",
            // Selected 4-7s
            "ABLE","ACID","ACRE","AERO","AGED","AIDE","AIMS","AIRS","ALES","ALLY","ALSO","AMID","AMPS","ARCH",
            "AREA","AREN","ARMS","ARMY","ARTS","ATOM","AUNT","AURA","AUTO","AVID","AWED","AWFUL","AXLE","BABY",
            "BACK","BAKE","BALD","BALL","BAND","BANE","BANK","BARN","BASE","BASH","BASS","BATH","BEAD","BEAM",
            "BEAN","BEAR","BEAT","BEDS","BEEN","BEEP","BEER","BELL","BELT","BEND","BENT","BEST","BEVY","BIAS",
            "BIBS","BIDE","BIKE","BILE","BILL","BIND","BIRD","BITE","BLOB","BLOC","BLOG","BLOT","BLOW","BLUE",
            "BOAR","BOAT","BODY","BOIL","BOLD","BOLT","BOMB","BOND","BONE","BOOK","BOOM","BOON","BOOT","BORE",
            "BORN","BOSS","BOTH","BOUT","BOWL","BRAG","BRAN","BRAS","BRAY","BREW","BRIM","BUDS","BUFF","BULB",
            "BULK","BULL","BUMP","BURN","BURP","BURR","BURY","BUSH","BUSY","BUTT","BUYS","BYES","CABS","CADS",
            "CAFE","CAGE","CAKE","CALF","CALL","CALM","CAME","CAMP","CANE","CANS","CAPE","CAPS","CARD","CARE",
            "CART","CASE","CASH","CAST","CATS","CAVE","CELL","CENT","CHAP","CHAT","CHEW","CHEF","CHIN","CHIP",
            "CHOP","CHUM","CITE","CITY","CLAD","CLAM","CLAN","CLAP","CLAW","CLAY","CLIP","CLOG","CLUB","CLUE",
            "COAL","COAT","COBS","CODE","CODS","COIL","COIN","COLA","COLD","COLT","COMA","COMB","COME","CONE",
            "COOK","COOL","COOP","COPE","COPY","CORD","CORE","CORK","CORN","COST","COTS","COUP","COVE","CRAB",
            "CRAG","CRAM","CRAW","CREW","CRIB","CROP","CROW","CRUD","CUBE","CUBS","CUES","CUFF","CULL","CULT",
            "CUPS","CURB","CURD","CURE","CURL","CURT","CUTE","CYST","DALE","DAME","DAMP","DARE","DARK","DART",
            "DASH","DATA","DATE","DAWN","DAYS","DEAD","DEAF","DEAL","DEAN","DEAR","DEBT","DECK","DEED","DEEP",
            "DEER","DELI","DEMO","DENS","DENT","DESK","DIAL","DICE","DIED","DIES","DIET","DIME","DING","DINT",
            "DIPS","DIRE","DIRT","DISC","DISH","DISK","DIVE","DOCK","DODO","DOES","DOGS","DOLE","DOLL","DOME",
            "DONE","DOOM","DOOR","DOPE","DORM","DOTE","DOTS","DOVE","DOWN","DOZE","DRAB","DRAG","DRAW","DREW",
            "DRIP","DROP","DRUG","DRUM","DUAL","DUCK","DUCT","DUDE","DUEL","DUES","DUKE","DULL","DUMB","DUMP",
            "DUNE","DUOS","DUSK","DUST","DUTY","DYED","DYES","EACH","EARL","EARN","EARS","EASE","EAST","EASY",
            "EAVE","ECHO","EDGE","EDGY","EDIT","EELS","EGGS","EGOS","ELAN","EMIT","ENDS","EPIC","ERAS","ERGO",
            "EVEN","EVER","EVES","EVIL","EWES","EWER","EXAM","EXIT","EYED","EYES","FACE","FACT","FADE","FAIL",
            "FAIR","FAKE","FALL","FAME","FANG","FANS","FARE","FARM","FAST","FATE","FAUN","FAWN","FEAR","FEAT",
            "FEED","FEEL","FELL","FELT","FERN","FEST","FEUD","FIBS","FIEF","FIGS","FILE","FILL","FILM","FIND",
            "FINE","FINS","FIRE","FIRM","FISH","FIST","FITS","FIVE","FIZZ","FLAG","FLAP","FLAT","FLAW","FLEA",
            "FLED","FLEE","FLEW","FLIP","FLIT","FLOG","FLOP","FLOW","FLUB","FLUE","FOAL","FOAM","FOES","FOGY",
            "FOIL","FOLD","FOLK","FOND","FOOD","FOOL","FOOT","FORE","FORK","FORM","FORT","FOUL","FOUR","FOWL",
            "FRAY","FREE","FRET","FROG","FROM","FUEL","FULL","FUME","FUND","FUNK","FURL","FURS","FURY","FUSE",
            "FUSS","FUZZ",
            "GABS","GAIN","GAIT","GALA","GALE","GAME","GANG","GAPE","GAPS","GARB","GASP","GATE","GAVE","GAZE",
            "GEAR","GEMS","GENE","GENT","GERM","GIFT","GILD","GILL","GILT","GIRD","GIRL","GIVE","GLAD","GLEE",
            "GLEN","GLOB","GLOW","GLUE","GLUT","GNAT","GOAD","GOAL","GOAT","GOBS","GOLD","GOLF","GONE","GONG",
            "GOOD","GOOF","GOON","GORE","GORY","GOSH","GOWN","GRAB","GRAY","GRID","GRIM","GRIN","GRIP","GROG",
            "GROW","GRUB","GULF","GULL","GULP","GUMS","GURU","GUSH","GUST","GUTS","GUYS","HACK","HAIL","HAIR",
            "HALE","HALF","HALL","HALT","HAND","HANG","HARD","HARE","HARM","HARP","HASH","HAUL","HAVE","HAWK",
            "HAYS","HAZE","HEAD","HEAL","HEAP","HEAR","HEAT","HECK","HEED","HEEL","HEFT","HEIR","HELD","HELL",
            "HELM","HELP","HEMP","HERB","HERD","HERE","HERO","HERS","HEWN","HICK","HIDE","HIGH","HIKE","HILL",
            "HILT","HINT","HIRE","HISS","HITS","HIVE","HOAX","HOBO","HOES","HOLD","HOLE","HOLY","HOME","HONE",
            "HONK","HOOD","HOOF","HOOK","HOOP","HOOT","HOPE","HOPS","HORN","HOSE","HOST","HOUR","HUBS","HUES",
            "HUFF","HUGE","HUGS","HULK","HULL","HUMP","HUMS","HUNG","HUNK","HUNT","HURL","HURT","HUSH","HUSK",
            "HYMN",
        };
        foreach (var w in seed) _dict.Add(w.ToUpperInvariant());

        // Optional: load a larger wordlist if present at runtime.
        var path = Path.Combine(AppContext.BaseDirectory, "assets/text/wordzap.txt");
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                var w = line.Trim().ToUpperInvariant();
                if (w.Length >= 3 && w.Length <= 7 && w.All(char.IsLetter)) _dict.Add(w);
            }
        }
    }

    private string CurrentWord() => string.Concat(_word.Select(i => _pool[i]));

    private void Submit()
    {
        var w = CurrentWord();
        if (w.Length < 3) { _msg = "Too short"; _msgTimer = 1.5f; return; }
        if (_found.Contains(w)) { _msg = "Already found"; _msgTimer = 1.5f; ResetWord(); return; }
        if (_dict!.Contains(w))
        {
            _found.Add(w);
            _score += w.Length;
            _msg = $"+{w.Length}";
            _msgTimer = 1.5f;
        }
        else
        {
            _msg = "Not in dictionary";
            _msgTimer = 1.5f;
        }
        ResetWord();
    }

    private void ResetWord()
    {
        for (int i = 0; i < PoolSize; i++) _used[i] = false;
        _word.Clear();
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        var titleBar = new Rectangle(FrameInset, FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        if (RetroWidgets.DrawTitleBarHitTest(titleBar, local, leftPressed))
        { IsFinished = true; return; }

        var menuBar = new Rectangle(FrameInset, FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        switch (RetroWidgets.MenuBarHitTest(menuBar, new[] { "New", "Submit", "Backspace", "Clear", "Help" }, local, leftPressed))
        {
            case 0: Reset(); return;
            case 1: Submit(); return;
            case 2: Backspace(); return;
            case 3: ResetWord(); return;
            case 4: _help.Visible = !_help.Visible; return;
        }
        if (_help.HandleInput(local, leftPressed, PanelSize)) return;

        if (Raylib.IsKeyPressed(KeyboardKey.Enter)) { Submit(); return; }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace)) { Backspace(); return; }

        if (_msgTimer > 0) _msgTimer -= delta;

        if (leftPressed)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                var pos = PoolPos(i);
                if (RetroSkin.PointInRect(local, new Rectangle(pos.X, pos.Y, PoolTile, PoolTile))
                    && !_used[i])
                { _used[i] = true; _word.Add(i); return; }
            }
        }
    }

    private void Backspace()
    {
        if (_word.Count == 0) return;
        int last = _word[^1];
        _word.RemoveAt(_word.Count - 1);
        _used[last] = false;
    }

    private Vector2 PoolPos(int i)
    {
        float bx = FrameInset + Margin;
        float by = FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin + 60;
        return new Vector2(bx + i * (PoolTile + 6), by);
    }

    public void Draw(Vector2 panelOffset)
    {
        var panel = new Rectangle(panelOffset.X, panelOffset.Y, PanelSize.X, PanelSize.Y);
        RetroWidgets.DrawWindowFrame(panel);

        var titleBar = new Rectangle(panelOffset.X + FrameInset, panelOffset.Y + FrameInset,
            PanelSize.X - 2 * FrameInset, RetroWidgets.TitleBarHeight);
        RetroWidgets.DrawTitleBarVisual(titleBar, "WordZap", true);

        var menuBar = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.MenuBarHeight);
        RetroWidgets.MenuBarVisual(menuBar, new[] { "New", "Submit", "Backspace", "Clear", "Help" }, -1);

        // Word being built
        float by = panelOffset.Y + FrameInset + RetroWidgets.TitleBarHeight + RetroWidgets.MenuBarHeight + Margin;
        var wordBox = new Rectangle(panelOffset.X + FrameInset + Margin, by,
            PanelSize.X - 2 * (FrameInset + Margin), 36);
        RetroSkin.DrawSunken(wordBox, RetroSkin.SunkenBg);
        var w = CurrentWord();
        int textW = RetroSkin.MeasureText(w, 28);
        RetroSkin.DrawText(w,
            (int)(wordBox.X + (wordBox.Width - textW) / 2),
            (int)(wordBox.Y + 4),
            RetroSkin.BodyText, 28);

        // Pool tiles
        for (int i = 0; i < PoolSize; i++)
        {
            var p = PoolPos(i);
            var abs = new Vector2(panelOffset.X + p.X, panelOffset.Y + p.Y);
            var rect = new Rectangle(abs.X, abs.Y, PoolTile, PoolTile);
            if (_used[i])
            {
                RetroSkin.DrawPressed(rect);
                Raylib.DrawRectangleRec(rect, new Color(0, 0, 0, 80));
            }
            else
            {
                RetroSkin.DrawRaised(rect);
            }
            int lw = RetroSkin.MeasureText(_pool[i], 28);
            RetroSkin.DrawText(_pool[i],
                (int)(abs.X + (PoolTile - lw) / 2),
                (int)(abs.Y + 6),
                _used[i] ? RetroSkin.DisabledText : RetroSkin.BodyText, 28);
        }

        // Found list (right side, vertical column)
        float fx = panelOffset.X + FrameInset + Margin;
        float fy = by + 110;
        RetroSkin.DrawText($"Found ({_found.Count})", (int)fx, (int)fy, RetroSkin.BodyText, 16);
        int cols = (int)((PanelSize.X - 2 * Margin) / 80);
        for (int i = 0; i < _found.Count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            RetroSkin.DrawText(_found[i],
                (int)(fx + col * 80),
                (int)(fy + 22 + row * 18),
                RetroSkin.BodyText, 14);
        }

        // Floating message
        if (_msgTimer > 0)
            RetroSkin.DrawText(_msg,
                (int)(panelOffset.X + PanelSize.X - Margin - RetroSkin.MeasureText(_msg, 20)),
                (int)(by + 8),
                new Color(220, 96, 0, 255), 20);

        var status = new Rectangle(panelOffset.X + FrameInset,
            panelOffset.Y + PanelSize.Y - FrameInset - RetroWidgets.StatusBarHeight,
            PanelSize.X - 2 * FrameInset, RetroWidgets.StatusBarHeight);
        RetroWidgets.StatusBar(status,
            "Click letters → Enter to submit  |  Backspace to undo",
            $"Score: {_score}");

        _help.Draw(panelOffset, PanelSize);
    }

    public void Close() { }
}
