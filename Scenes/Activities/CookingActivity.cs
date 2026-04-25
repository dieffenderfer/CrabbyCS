using System.Numerics;
using Raylib_cs;
using MouseHouse.Core;

namespace MouseHouse.Scenes.Activities;

public class CookingActivity : IActivity
{
    public Vector2 PanelSize => new(800, 600);
    public bool IsFinished { get; private set; }

    private readonly AssetCache _assets;
    private readonly AudioManager _audio;

    // Recipes
    private static readonly (string name, string[] ingredients, string dish)[] Recipes =
    {
        ("Cheese Fondue", new[] { "cheese", "garlic", "butter", "bread" }, "dish_fondue"),
        ("Crumb Soup", new[] { "crumbs", "onion", "potato", "salt" }, "dish_soup"),
        ("Seed Bread", new[] { "flour", "seeds", "honey", "egg" }, "dish_bread"),
    };

    // All possible ingredient names for random drops
    private static readonly string[] AllIngredients =
        { "cheese", "garlic", "butter", "bread", "crumbs", "onion", "potato",
          "salt", "flour", "seeds", "honey", "egg", "carrot", "mushroom", "celery" };

    // Game state
    private int _recipeIndex;
    private int _nextIngredient; // index within current recipe
    private int _lives = 3;
    private bool _won;
    private bool _lost;
    private string _message = "";
    private float _messageTimer;
    private Color _messageColor = Color.White;

    // Falling ingredients
    private readonly List<FallingItem> _items = new();
    private float _spawnTimer;
    private Texture2D _bgTexture;

    private static readonly Random Rng = new();

    public CookingActivity(AssetCache assets, AudioManager audio)
    {
        _assets = assets;
        _audio = audio;
    }

    public void Load()
    {
        _bgTexture = _assets.GetTexture("assets/cooking/cooking_bg.png");
        _recipeIndex = Rng.Next(Recipes.Length);
        _nextIngredient = 0;
        _lives = 3;
        _won = false;
        _lost = false;
        _spawnTimer = 1f;
        _message = $"Recipe: {Recipes[_recipeIndex].name}";
        _messageColor = Color.Yellow;
        _messageTimer = 2.5f;
    }

    public void Update(float delta, Vector2 mousePos, Vector2 panelOffset,
                       bool leftPressed, bool leftReleased, bool rightPressed)
    {
        var local = mousePos - panelOffset;
        _messageTimer -= delta;

        if (_won || _lost)
        {
            if (_messageTimer <= 0)
            {
                if (_won)
                {
                    // Next recipe or restart
                    _recipeIndex = (_recipeIndex + 1) % Recipes.Length;
                    _nextIngredient = 0;
                    _lives = 3;
                    _won = false;
                    _spawnTimer = 1f;
                    _items.Clear();
                    _message = $"Recipe: {Recipes[_recipeIndex].name}";
                    _messageColor = Color.Yellow;
                    _messageTimer = 2f;
                }
                else
                {
                    // Restart
                    _nextIngredient = 0;
                    _lives = 3;
                    _lost = false;
                    _spawnTimer = 1f;
                    _items.Clear();
                    _message = "Try again!";
                    _messageColor = Color.White;
                    _messageTimer = 1.5f;
                }
            }
            return;
        }

        // Spawn ingredients
        _spawnTimer -= delta;
        if (_spawnTimer <= 0)
        {
            _spawnTimer = Rng.NextSingle() * 1f + 0.8f;
            SpawnIngredient();
        }

        // Update falling items
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            item.Time += delta;
            item.Y += item.Speed * delta;
            item.X = item.StartX + MathF.Sin(item.Time * 2f) * 30f;

            if (item.Y > 620)
            {
                _items.RemoveAt(i);
                continue;
            }

            // Click detection
            if (leftPressed && !item.Clicked)
            {
                float dx = local.X - item.X - 32;
                float dy = local.Y - item.Y - 32;
                if (dx * dx + dy * dy < 40 * 40)
                {
                    item.Clicked = true;
                    var recipe = Recipes[_recipeIndex];
                    if (_nextIngredient < recipe.ingredients.Length &&
                        item.Name == recipe.ingredients[_nextIngredient])
                    {
                        // Correct!
                        _nextIngredient++;
                        _audio.Play("assets/audio/chop.wav");
                        _items.RemoveAt(i);

                        if (_nextIngredient >= recipe.ingredients.Length)
                        {
                            _won = true;
                            _message = $"{recipe.name} complete!";
                            _messageColor = Color.Gold;
                            _messageTimer = 2.5f;
                            _audio.Play("assets/audio/recipe_complete.wav");
                            _items.Clear();
                        }
                    }
                    else
                    {
                        // Wrong!
                        _lives--;
                        _audio.Play("assets/audio/splat.wav");
                        _items.RemoveAt(i);
                        if (_lives <= 0)
                        {
                            _lost = true;
                            _message = "Out of lives!";
                            _messageColor = new Color((byte)255, (byte)150, (byte)150, (byte)255);
                            _messageTimer = 2.5f;
                            _items.Clear();
                        }
                    }
                    break; // Only one click per frame
                }
            }
        }
    }

    private void SpawnIngredient()
    {
        var recipe = Recipes[_recipeIndex];
        string name;

        // 35% chance it's the needed ingredient
        if (_nextIngredient < recipe.ingredients.Length && Rng.NextSingle() < 0.35f)
            name = recipe.ingredients[_nextIngredient];
        else
            name = AllIngredients[Rng.Next(AllIngredients.Length)];

        var tex = _assets.GetTexture($"assets/cooking/{name}.png");
        _items.Add(new FallingItem
        {
            Name = name,
            Texture = tex,
            StartX = Rng.NextSingle() * 640 + 80,
            X = 0,
            Y = -30,
            Speed = Rng.NextSingle() * 60 + 80,
        });
    }

    public void Draw(Vector2 offset)
    {
        // Background
        var src = new Rectangle(0, 0, _bgTexture.Width, _bgTexture.Height);
        var dest = new Rectangle(offset.X, offset.Y, 800, 600);
        Raylib.DrawTexturePro(_bgTexture, src, dest, Vector2.Zero, 0f, Color.White);

        // Falling items
        foreach (var item in _items)
        {
            var texSrc = new Rectangle(0, 0, item.Texture.Width, item.Texture.Height);
            var texDest = new Rectangle(offset.X + item.X, offset.Y + item.Y, 64, 64);
            Raylib.DrawTexturePro(item.Texture, texSrc, texDest, Vector2.Zero, 0f, Color.White);
        }

        // Top bar
        Raylib.DrawRectangle((int)offset.X, (int)offset.Y, 800, 56,
            new Color((byte)30, (byte)30, (byte)35, (byte)200));
        FontManager.DrawText("Cooking", (int)offset.X + 10, (int)offset.Y + 5, 20, Color.White);
        FontManager.DrawText("[ESC] Exit", (int)offset.X + 700, (int)offset.Y + 5, 16, Color.LightGray);

        // Lives
        string hearts = new string('♥', _lives) + new string('♡', 3 - _lives);
        FontManager.DrawText(hearts, (int)offset.X + 10, (int)offset.Y + 30, 20, Color.Red);

        // Recipe progress
        var recipe = Recipes[_recipeIndex];
        int progressY = (int)offset.Y + 30;
        string progress = $"{recipe.name}: ";
        for (int i = 0; i < recipe.ingredients.Length; i++)
        {
            if (i < _nextIngredient) progress += "✓ ";
            else if (i == _nextIngredient) progress += $">> {recipe.ingredients[i]} << ";
            else progress += $"{recipe.ingredients[i]} ";
        }
        FontManager.DrawText(progress, (int)offset.X + 100, progressY, 16, Color.White);

        // Message
        if (_message != "" && _messageTimer > 0)
        {
            int tw = FontManager.MeasureText(_message, 28);
            FontManager.DrawText(_message, (int)(offset.X + 400 - tw / 2), (int)offset.Y + 280, 28, _messageColor);
        }
    }

    public void Close() => IsFinished = true;

    private class FallingItem
    {
        public string Name = "";
        public Texture2D Texture;
        public float StartX, X, Y, Speed, Time;
        public bool Clicked;
    }
}
