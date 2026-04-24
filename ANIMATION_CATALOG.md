# Animation Catalog

All animations in the MouseHouse (CrabbyCS) app, organized by category. Each sprite sheet PNG has been converted to an animated GIF for Aseprite editing. Both formats live side-by-side in the assets directory.

---

## Mouse Pet Animations

The main desktop pet. Three color modes: default (2-color), 1-color (`_1c_`), and full-color (`_fc_`). Sprites are 76x76px per frame.

### Walk (8 frames @ 250ms)

| Default | 1-Color | Full-Color |
|---------|---------|------------|
| ![walk](assets/sprites/pets/mouse_walk.gif) | ![walk_1c](assets/sprites/pets/mouse_1c_walk.gif) | ![walk_fc](assets/sprites/pets/mouse_fc_walk.gif) |

`assets/sprites/pets/mouse_walk.png` / `mouse_1c_walk.png` / `mouse_fc_walk.png`

Pink nose variant: ![walk_pinknose](assets/sprites/pets/mouse_walk_pinknose.gif)
`assets/sprites/pets/mouse_walk_pinknose.png`

### Idle (8 frames @ 130ms)

| Default | 1-Color | Full-Color |
|---------|---------|------------|
| ![idle](assets/sprites/pets/mouse_idle.gif) | ![idle_1c](assets/sprites/pets/mouse_1c_idle.gif) | ![idle_fc](assets/sprites/pets/mouse_fc_idle.gif) |

`assets/sprites/pets/mouse_idle.png` / `mouse_1c_idle.png` / `mouse_fc_idle.png`

### Sleep Intro (12 frames @ 130ms)

| Default | 1-Color | Full-Color |
|---------|---------|------------|
| ![sleep](assets/sprites/pets/mouse_sleep.gif) | ![sleep_1c](assets/sprites/pets/mouse_1c_sleep.gif) | ![sleep_fc](assets/sprites/pets/mouse_fc_sleep.gif) |

`assets/sprites/pets/mouse_sleep.png` / `mouse_1c_sleep.png` / `mouse_fc_sleep.png`

### Sleep Loop (3 frames @ 300ms, frame 0 holds 10s in-game)

| Default | 1-Color | Full-Color |
|---------|---------|------------|
| ![sleep_loop](assets/sprites/pets/mouse_sleep_loop.gif) | ![sleep_loop_1c](assets/sprites/pets/mouse_1c_sleep_loop.gif) | ![sleep_loop_fc](assets/sprites/pets/mouse_fc_sleep_loop.gif) |

`assets/sprites/pets/mouse_sleep_loop.png` / `mouse_1c_sleep_loop.png` / `mouse_fc_sleep_loop.png`

### Jump (8 frames @ 130ms)

| Default | 1-Color | Full-Color |
|---------|---------|------------|
| ![jump](assets/sprites/pets/mouse_jump.gif) | ![jump_1c](assets/sprites/pets/mouse_1c_jump.gif) | ![jump_fc](assets/sprites/pets/mouse_fc_jump.gif) |

`assets/sprites/pets/mouse_jump.png` / `mouse_1c_jump.png` / `mouse_fc_jump.png`

Eyes-closed variant: ![jump_eyes_closed](assets/sprites/pets/mouse_jump_eyes_closed.gif)
`assets/sprites/pets/mouse_jump_eyes_closed.png`

### Backups

| File | Description |
|------|-------------|
| ![](assets/sprites/pets/mouse_idle%20BACKUP.gif) | `mouse_idle BACKUP.png` — idle backup |
| ![](assets/sprites/pets/mouse_walk%20BACKUP.gif) | `mouse_walk BACKUP.png` — walk backup |
| ![](assets/sprites/pets/mouse_sleep%20BACKUP.gif) | `mouse_sleep BACKUP.png` — sleep backup |
| ![](assets/sprites/pets/mouse_sleep%20BACKUP%20v2.gif) | `mouse_sleep BACKUP v2.png` — sleep backup v2 |
| ![](assets/sprites/pets/mouse_sleep_12frame_backup.gif) | `mouse_sleep_12frame_backup.png` |
| ![](assets/sprites/pets/mouse_sleep_loop_12frame_backup.gif) | `mouse_sleep_loop_12frame_backup.png` |

### Other Pets

| Pet | Preview | Path |
|-----|---------|------|
| Cat | ![cat](assets/sprites/pets/cat.gif) | `assets/sprites/pets/cat.png` |
| Crab | ![crab](assets/sprites/pets/crab.gif) | `assets/sprites/pets/crab.png` |
| Duck | ![duck](assets/sprites/pets/duck.gif) | `assets/sprites/pets/duck.png` |
| Frog | ![frog](assets/sprites/pets/frog.gif) | `assets/sprites/pets/frog.png` |
| Hamster | ![hamster](assets/sprites/pets/hamster.gif) | `assets/sprites/pets/hamster.png` |
| Penguin | ![penguin](assets/sprites/pets/penguin.gif) | `assets/sprites/pets/penguin.png` |

Fishing variants also exist: `cat_fishing.png`, `crab_fishing.png`, `duck_fishing.png`, `frog_fishing.png`, `hamster_fishing.png`, `mouse_fishing.png`, `penguin_fishing.png`

---

## Events (150ms per frame)

Random events that fly/walk across the screen. Each has default, 1-color, and 2-color variants in `sprites/events/`, `sprites/events/1color/`, and `sprites/events/2color/`.

### Core Events (registered in EventManager.cs)

| Event | Frames | Preview | Path |
|-------|--------|---------|------|
| Seagull | 8 | ![](assets/sprites/events/seagull.gif) | `sprites/events/seagull.png` |
| Butterfly | 4 | ![](assets/sprites/events/butterfly.gif) | `sprites/events/butterfly.png` |
| Falling Leaf | 4 | ![](assets/sprites/events/falling_leaf.gif) | `sprites/events/falling_leaf.png` |
| Shooting Star | 3 | ![](assets/sprites/events/shooting_star.gif) | `sprites/events/shooting_star.png` |
| Firefly | 4 | ![](assets/sprites/events/firefly.gif) | `sprites/events/firefly.png` |
| Paper Airplane | 4 | ![](assets/sprites/events/paper_airplane.gif) | `sprites/events/paper_airplane.png` |
| Balloon | 2 | ![](assets/sprites/events/balloon.gif) | `sprites/events/balloon.png` |
| Rain Cloud | 6 | ![](assets/sprites/events/rain_cloud.gif) | `sprites/events/rain_cloud.png` |
| Bat | 4 | ![](assets/sprites/events/bat.gif) | `sprites/events/bat.png` |
| Ladybug | 4 | ![](assets/sprites/events/ladybug.gif) | `sprites/events/ladybug.png` |
| Dragonfly | 4 | ![](assets/sprites/events/dragonfly.gif) | `sprites/events/dragonfly.png` |
| Jellyfish | 4 | ![](assets/sprites/events/jellyfish.gif) | `sprites/events/jellyfish.png` |
| Dolphin | 6 | ![](assets/sprites/events/dolphin.gif) | `sprites/events/dolphin.png` |
| Hot Air Balloon | 2 | ![](assets/sprites/events/hot_air_balloon.gif) | `sprites/events/hot_air_balloon.png` |
| Comet | 3 | ![](assets/sprites/events/comet.gif) | `sprites/events/comet.png` |
| Dust Devil | 4 | ![](assets/sprites/events/dust_devil.gif) | `sprites/events/dust_devil.png` |
| Frog | 4 | ![](assets/sprites/events/frog.gif) | `sprites/events/frog.png` |
| Hermit Crab | 4 | ![](assets/sprites/events/hermit_crab.gif) | `sprites/events/hermit_crab.png` |
| Pelican | 4 | ![](assets/sprites/events/pelican.gif) | `sprites/events/pelican.png` |
| Crab Ghost | 4 | ![](assets/sprites/events/crab_ghost.gif) | `sprites/events/crab_ghost.png` |

### Additional Events (auto-detected)

| Event | Preview | Path |
|-------|---------|------|
| Ant Line | ![](assets/sprites/events/ant_line.gif) | `sprites/events/ant_line.png` |
| Aurora | ![](assets/sprites/events/aurora.gif) | `sprites/events/aurora.png` |
| Beach Ball | ![](assets/sprites/events/beach_ball.gif) | `sprites/events/beach_ball.png` |
| Cherry Blossoms | ![](assets/sprites/events/cherry_blossoms.gif) | `sprites/events/cherry_blossoms.png` |
| Coconut | ![](assets/sprites/events/coconut.gif) | `sprites/events/coconut.png` |
| Dandelion Stem | ![](assets/sprites/events/dandelion_stem.gif) | `sprites/events/dandelion_stem.png` |
| Meteor Shower | ![](assets/sprites/events/meteor_shower.gif) | `sprites/events/meteor_shower.png` |
| Owl | ![](assets/sprites/events/owl.gif) | `sprites/events/owl.png` |
| Pelican Dive | ![](assets/sprites/events/pelican_dive.gif) | `sprites/events/pelican_dive.png` |
| Rainbow | ![](assets/sprites/events/rainbow.gif) | `sprites/events/rainbow.png` |
| Sand Dollar | ![](assets/sprites/events/sand_dollar.gif) | `sprites/events/sand_dollar.png` |
| Sandcastle | ![](assets/sprites/events/sandcastle.gif) | `sprites/events/sandcastle.png` |
| Sea Foam | ![](assets/sprites/events/sea_foam.gif) | `sprites/events/sea_foam.png` |
| Sea Turtle | ![](assets/sprites/events/sea_turtle.gif) | `sprites/events/sea_turtle.png` |
| Sea Urchin | ![](assets/sprites/events/sea_urchin.gif) | `sprites/events/sea_urchin.png` |
| Seashell | ![](assets/sprites/events/seashell.gif) | `sprites/events/seashell.png` |
| Snail | ![](assets/sprites/events/snail.gif) | `sprites/events/snail.png` |
| Snowflakes | ![](assets/sprites/events/snowflakes.gif) | `sprites/events/snowflakes.png` |
| Soap Bubbles | ![](assets/sprites/events/soap_bubbles.gif) | `sprites/events/soap_bubbles.png` |
| Starfish | ![](assets/sprites/events/starfish.gif) | `sprites/events/starfish.png` |
| Treasure Coin | ![](assets/sprites/events/treasure_coin.gif) | `sprites/events/treasure_coin.png` |
| Tumbleweed | ![](assets/sprites/events/tumbleweed.gif) | `sprites/events/tumbleweed.png` |
| UFO | ![](assets/sprites/events/ufo.gif) | `sprites/events/ufo.png` |
| Wave | ![](assets/sprites/events/wave.gif) | `sprites/events/wave.png` |

### Single-Frame Events

These are static or single-image events:

| Event | Preview | Path |
|-------|---------|------|
| Bottle Message | ![](assets/sprites/events/bottle_message.gif) | `sprites/events/bottle_message.png` |
| Campfire | ![](assets/sprites/events/campfire.gif) | `sprites/events/campfire.png` |
| Crab Hole | ![](assets/sprites/events/crab_hole.gif) | `sprites/events/crab_hole.png` |
| Dandelion Seeds | ![](assets/sprites/events/dandelion_seeds.gif) | `sprites/events/dandelion_seeds.png` |
| Fish | ![](assets/sprites/events/fish.gif) | `sprites/events/fish.png` |
| Fog | ![](assets/sprites/events/fog.gif) | `sprites/events/fog.png` |
| Lightning | ![](assets/sprites/events/lightning.gif) | `sprites/events/lightning.png` |
| Manta Ray | ![](assets/sprites/events/manta_ray.gif) | `sprites/events/manta_ray.png` |
| Message in Bottle | ![](assets/sprites/events/message_in_bottle.gif) | `sprites/events/message_in_bottle.png` |
| Octopus | ![](assets/sprites/events/octopus.gif) | `sprites/events/octopus.png` |
| Palm Frond | ![](assets/sprites/events/palm_frond.gif) | `sprites/events/palm_frond.png` |
| Pizza Slice | ![](assets/sprites/events/pizza_slice.gif) | `sprites/events/pizza_slice.png` |
| Puddle | ![](assets/sprites/events/puddle.gif) | `sprites/events/puddle.png` |
| Seahorse | ![](assets/sprites/events/seahorse.gif) | `sprites/events/seahorse.png` |
| Seaweed | ![](assets/sprites/events/seaweed.gif) | `sprites/events/seaweed.png` |
| Sunbeam | ![](assets/sprites/events/sunbeam.gif) | `sprites/events/sunbeam.png` |
| Tide Pool | ![](assets/sprites/events/tide_pool.gif) | `sprites/events/tide_pool.png` |
| Whale Spout | ![](assets/sprites/events/whale_spout.gif) | `sprites/events/whale_spout.png` |

---

## Ambience Sprites

Background atmosphere sprites in `assets/sprites/ambience/`:

| Name | Preview | Path |
|------|---------|------|
| Boat | ![](assets/sprites/ambience/boat.gif) | `sprites/ambience/boat.png` |
| Butterfly | ![](assets/sprites/ambience/butterfly.gif) | `sprites/ambience/butterfly.png` |
| Cloud Shadow | ![](assets/sprites/ambience/cloud_shadow.gif) | `sprites/ambience/cloud_shadow.png` |
| Firefly | ![](assets/sprites/ambience/firefly.gif) | `sprites/ambience/firefly.png` |
| Ghost Crab | ![](assets/sprites/ambience/ghost_crab.gif) | `sprites/ambience/ghost_crab.png` |
| Hermit Crab | ![](assets/sprites/ambience/hermit_crab.gif) | `sprites/ambience/hermit_crab.png` |
| Jellyfish | ![](assets/sprites/ambience/jellyfish.gif) | `sprites/ambience/jellyfish.png` |
| Man of War | ![](assets/sprites/ambience/man_of_war.gif) | `sprites/ambience/man_of_war.png` |
| Night Sparkle | ![](assets/sprites/ambience/night_sparkle.gif) | `sprites/ambience/night_sparkle.png` |
| Owl | ![](assets/sprites/ambience/owl.gif) | `sprites/ambience/owl.png` |
| Pirate Ship | ![](assets/sprites/ambience/pirate_ship.gif) | `sprites/ambience/pirate_ship.png` |
| Sandpiper | ![](assets/sprites/ambience/sandpiper.gif) | `sprites/ambience/sandpiper.png` |
| Seagull | ![](assets/sprites/ambience/seagull.gif) | `sprites/ambience/seagull.png` |
| Shore Foam | ![](assets/sprites/ambience/shore_foam.gif) | `sprites/ambience/shore_foam.png` |
| Sparkle | ![](assets/sprites/ambience/sparkle.gif) | `sprites/ambience/sparkle.png` |
| St. Elmo's Fire | ![](assets/sprites/ambience/st_elmos_fire.gif) | `sprites/ambience/st_elmos_fire.png` |
| Starfish | ![](assets/sprites/ambience/starfish.gif) | `sprites/ambience/starfish.png` |
| Waves | ![](assets/sprites/ambience/waves.gif) | `sprites/ambience/waves.png` |

---

## Hats

Equippable hats in `assets/sprites/hats/` (3 frames each for walk animation overlay):

| Hat | Preview | Path |
|-----|---------|------|
| Bunny Ears | ![](assets/sprites/hats/bunny_ears.gif) | `sprites/hats/bunny_ears.png` |
| Chef Hat | ![](assets/sprites/hats/chef_hat.gif) | `sprites/hats/chef_hat.png` |
| Crown | ![](assets/sprites/hats/crown.gif) | `sprites/hats/crown.png` |
| Flower Crown | ![](assets/sprites/hats/flower_crown.gif) | `sprites/hats/flower_crown.png` |
| Party Hat | ![](assets/sprites/hats/party_hat.gif) | `sprites/hats/party_hat.png` |
| Pirate Hat | ![](assets/sprites/hats/pirate_hat.gif) | `sprites/hats/pirate_hat.png` |
| Santa Hat | ![](assets/sprites/hats/santa_hat.gif) | `sprites/hats/santa_hat.png` |
| Sunglasses | ![](assets/sprites/hats/sunglasses.gif) | `sprites/hats/sunglasses.png` |
| Top Hat | ![](assets/sprites/hats/top_hat.gif) | `sprites/hats/top_hat.png` |
| Witch Hat | ![](assets/sprites/hats/witch_hat.gif) | `sprites/hats/witch_hat.png` |
| Wizard Hat | ![](assets/sprites/hats/wizard_hat.gif) | `sprites/hats/wizard_hat.png` |
| Hat Icons | ![](assets/sprites/hats/hat_icons.gif) | `sprites/hats/hat_icons.png` |

---

## Eyes

Eye style overlays in `assets/sprites/eyes/` (3 frames each):

| Style | Preview | Path |
|-------|---------|------|
| Normal | ![](assets/sprites/eyes/normal.gif) | `sprites/eyes/normal.png` |
| Angry | ![](assets/sprites/eyes/angry.gif) | `sprites/eyes/angry.png` |
| Sleepy | ![](assets/sprites/eyes/sleepy.gif) | `sprites/eyes/sleepy.png` |
| Sparkly | ![](assets/sprites/eyes/sparkly.gif) | `sprites/eyes/sparkly.png` |
| Eye Icons | ![](assets/sprites/eyes/eye_icons.gif) | `sprites/eyes/eye_icons.png` |

---

## Fishing Overlay

Fish sprites for the fishing mini-game in `assets/sprites/fishing_overlay/` (2 frames each). Also has 1-color and 2-color variants.

| Fish | Preview | Path |
|------|---------|------|
| Bluefish | ![](assets/sprites/fishing_overlay/bluefish.gif) | `sprites/fishing_overlay/bluefish.png` |
| Clownfish | ![](assets/sprites/fishing_overlay/clownfish.gif) | `sprites/fishing_overlay/clownfish.png` |
| Coelacanth | ![](assets/sprites/fishing_overlay/coelacanth.gif) | `sprites/fishing_overlay/coelacanth.png` |
| Golden Fish | ![](assets/sprites/fishing_overlay/goldenfish.gif) | `sprites/fishing_overlay/goldenfish.png` |
| Goldfish | ![](assets/sprites/fishing_overlay/goldfish.gif) | `sprites/fishing_overlay/goldfish.png` |
| Green Fish | ![](assets/sprites/fishing_overlay/greenfish.gif) | `sprites/fishing_overlay/greenfish.png` |
| Pink Fish | ![](assets/sprites/fishing_overlay/pinkfish.gif) | `sprites/fishing_overlay/pinkfish.png` |
| Purple Fish | ![](assets/sprites/fishing_overlay/purplefish.gif) | `sprites/fishing_overlay/purplefish.png` |
| Red Fish | ![](assets/sprites/fishing_overlay/redfish.gif) | `sprites/fishing_overlay/redfish.png` |
| Teal Fish | ![](assets/sprites/fishing_overlay/tealfish.gif) | `sprites/fishing_overlay/tealfish.png` |
| Tropical Fish | ![](assets/sprites/fishing_overlay/tropical_fish.gif) | `sprites/fishing_overlay/tropical_fish.png` |
| Whale | ![](assets/sprites/fishing_overlay/whale.gif) | `sprites/fishing_overlay/whale.png` |
| Yellow Fish | ![](assets/sprites/fishing_overlay/yellowfish.gif) | `sprites/fishing_overlay/yellowfish.png` |
| Anglerfish | ![](assets/sprites/fishing_overlay/anglerfish.gif) | `sprites/fishing_overlay/anglerfish.png` |
| Electric Eel | ![](assets/sprites/fishing_overlay/electric_eel.gif) | `sprites/fishing_overlay/electric_eel.png` |
| Flying Fish | ![](assets/sprites/fishing_overlay/flying_fish.gif) | `sprites/fishing_overlay/flying_fish.png` |
| Jellyfish | ![](assets/sprites/fishing_overlay/jellyfish.gif) | `sprites/fishing_overlay/jellyfish.png` |
| Lobster | ![](assets/sprites/fishing_overlay/lobster.gif) | `sprites/fishing_overlay/lobster.png` |
| Manta Ray | ![](assets/sprites/fishing_overlay/manta_ray.gif) | `sprites/fishing_overlay/manta_ray.png` |
| Narwhal | ![](assets/sprites/fishing_overlay/narwhal.gif) | `sprites/fishing_overlay/narwhal.png` |
| Octopus | ![](assets/sprites/fishing_overlay/octopus.gif) | `sprites/fishing_overlay/octopus.png` |
| Pufferfish | ![](assets/sprites/fishing_overlay/pufferfish.gif) | `sprites/fishing_overlay/pufferfish.png` |
| Sea Turtle | ![](assets/sprites/fishing_overlay/sea_turtle.gif) | `sprites/fishing_overlay/sea_turtle.png` |
| Seahorse | ![](assets/sprites/fishing_overlay/seahorse.gif) | `sprites/fishing_overlay/seahorse.png` |
| Shark | ![](assets/sprites/fishing_overlay/shark.gif) | `sprites/fishing_overlay/shark.png` |
| Starfish | ![](assets/sprites/fishing_overlay/starfish.gif) | `sprites/fishing_overlay/starfish.png` |
| Swordfish | ![](assets/sprites/fishing_overlay/swordfish.gif) | `sprites/fishing_overlay/swordfish.png` |
| Bobber | ![](assets/sprites/fishing_overlay/bobber.gif) | `sprites/fishing_overlay/bobber.png` |

---

## Crab Sprites

Main crab character in `assets/sprites/`:

| Variant | Preview | Path |
|---------|---------|------|
| Default | ![](assets/sprites/crab.gif) | `sprites/crab.png` |
| Black | ![](assets/sprites/crab_black.gif) | `sprites/crab_black.png` |
| Blue | ![](assets/sprites/crab_blue.gif) | `sprites/crab_blue.png` |
| Golden | ![](assets/sprites/crab_golden.gif) | `sprites/crab_golden.png` |
| Green | ![](assets/sprites/crab_green.gif) | `sprites/crab_green.png` |
| Orange | ![](assets/sprites/crab_orange.gif) | `sprites/crab_orange.png` |
| Pink | ![](assets/sprites/crab_pink.gif) | `sprites/crab_pink.png` |
| Purple | ![](assets/sprites/crab_purple.gif) | `sprites/crab_purple.png` |
| Red | ![](assets/sprites/crab_red.gif) | `sprites/crab_red.png` |
| Click | ![](assets/sprites/crab_click.gif) | `sprites/crab_click.png` |
| Fishing | ![](assets/sprites/crab_fishing.gif) | `sprites/crab_fishing.png` |
| Sleep | ![](assets/sprites/crab_sleep.gif) | `sprites/crab_sleep.png` |
| Portrait | ![](assets/crab_portrait.gif) | `crab_portrait.png` |

---

## Activities

### Dance (`assets/dance/`)

| Name | Preview | Path |
|------|---------|------|
| Background | ![](assets/dance/dance_bg.gif) | `dance/dance_bg.png` |
| Crab Pose 0 | ![](assets/dance/dance_crab_0.gif) | `dance/dance_crab_0.png` |
| Crab Pose 1 | ![](assets/dance/dance_crab_1.gif) | `dance/dance_crab_1.png` |
| Crab Pose 2 | ![](assets/dance/dance_crab_2.gif) | `dance/dance_crab_2.png` |
| Crab Pose 3 | ![](assets/dance/dance_crab_3.gif) | `dance/dance_crab_3.png` |
| Mouse Pose 0 | ![](assets/dance/dance_mouse_0.gif) | `dance/dance_mouse_0.png` |
| Mouse Pose 1 | ![](assets/dance/dance_mouse_1.gif) | `dance/dance_mouse_1.png` |
| Mouse Pose 2 | ![](assets/dance/dance_mouse_2.gif) | `dance/dance_mouse_2.png` |
| Mouse Pose 3 | ![](assets/dance/dance_mouse_3.gif) | `dance/dance_mouse_3.png` |
| Stumble | ![](assets/dance/dance_stumble.gif) | `dance/dance_stumble.png` |
| Note (Blue) | ![](assets/dance/note_blue.gif) | `dance/note_blue.png` |
| Note (Green) | ![](assets/dance/note_green.gif) | `dance/note_green.png` |
| Note (Red) | ![](assets/dance/note_red.gif) | `dance/note_red.png` |
| Note (Yellow) | ![](assets/dance/note_yellow.gif) | `dance/note_yellow.png` |
| Pad (Blue) | ![](assets/dance/pad_blue.gif) | `dance/pad_blue.png` |
| Pad (Green) | ![](assets/dance/pad_green.gif) | `dance/pad_green.png` |
| Pad (Red) | ![](assets/dance/pad_red.gif) | `dance/pad_red.png` |
| Pad (Yellow) | ![](assets/dance/pad_yellow.gif) | `dance/pad_yellow.png` |

### Gardening (`assets/gardening/`)

Plants have growth stages (0, 1, 2) and a harvest sprite.

| Plant | Stage 0 | Stage 1 | Stage 2 | Harvest |
|-------|---------|---------|---------|---------|
| Acorn | ![](assets/gardening/acorn_0.gif) | ![](assets/gardening/acorn_1.gif) | ![](assets/gardening/acorn_2.gif) | ![](assets/gardening/acorn_harvest.gif) |
| Berry | ![](assets/gardening/berry_0.gif) | ![](assets/gardening/berry_1.gif) | ![](assets/gardening/berry_2.gif) | ![](assets/gardening/berry_harvest.gif) |
| Clover | ![](assets/gardening/clover_0.gif) | ![](assets/gardening/clover_1.gif) | ![](assets/gardening/clover_2.gif) | ![](assets/gardening/clover_harvest.gif) |
| Dandelion | ![](assets/gardening/dandelion_0.gif) | ![](assets/gardening/dandelion_1.gif) | ![](assets/gardening/dandelion_2.gif) | ![](assets/gardening/dandelion_harvest.gif) |
| Mushroom | ![](assets/gardening/mushroom_0.gif) | ![](assets/gardening/mushroom_1.gif) | ![](assets/gardening/mushroom_2.gif) | ![](assets/gardening/mushroom_harvest.gif) |
| Sunflower | ![](assets/gardening/sunflower_0.gif) | ![](assets/gardening/sunflower_1.gif) | ![](assets/gardening/sunflower_2.gif) | ![](assets/gardening/sunflower_harvest.gif) |

Other: ![bg](assets/gardening/garden_bg.gif) `garden_bg.png`, ![butterfly](assets/gardening/butterfly.gif) `butterfly.png`, ![ladybug](assets/gardening/ladybug.gif) `ladybug.png`, ![watering_can](assets/gardening/watering_can_cursor.gif) `watering_can_cursor.png`

### Stargazing (`assets/stargazing/`)

| Name | Preview | Path |
|------|---------|------|
| Background | ![](assets/stargazing/stargazing_bg.gif) | `stargazing/stargazing_bg.png` |
| Star | ![](assets/stargazing/star.gif) | `stargazing/star.png` |
| Star Bright | ![](assets/stargazing/star_bright.gif) | `stargazing/star_bright.png` |
| Shooting Star | ![](assets/stargazing/shooting_star.gif) | `stargazing/shooting_star.png` |

### Paint (`assets/paint/`)

Tool icons and UI elements for the paint activity.

| Name | Preview | Path |
|------|---------|------|
| Titlebar | ![](assets/paint/titlebar.gif) | `paint/titlebar.png` |
| Pencil | ![](assets/paint/tool_pencil.gif) | `paint/tool_pencil.png` |
| Brush | ![](assets/paint/tool_brush.gif) | `paint/tool_brush.png` |
| Eraser | ![](assets/paint/tool_eraser.gif) | `paint/tool_eraser.png` |
| Fill | ![](assets/paint/tool_fill.gif) | `paint/tool_fill.png` |
| Line | ![](assets/paint/tool_line.gif) | `paint/tool_line.png` |
| Rectangle | ![](assets/paint/tool_rect.gif) | `paint/tool_rect.png` |
| Circle | ![](assets/paint/tool_circle.gif) | `paint/tool_circle.png` |
| Eyedropper | ![](assets/paint/tool_eyedropper.gif) | `paint/tool_eyedropper.png` |
| Text | ![](assets/paint/tool_text.gif) | `paint/tool_text.png` |
| Zoom | ![](assets/paint/tool_zoom.gif) | `paint/tool_zoom.png` |

Each tool also has a `_pressed` variant. Patterns: `pat_solid`, `pat_light`, `pat_heavy`, `pat_horz`, `pat_vert`, `pat_dots`, `pat_cross`, `pat_checker` (each with `_pressed` variant).

### Kite Flying (`assets/kite/`)

| Name | Preview | Path |
|------|---------|------|
| Background | ![](assets/kite/kite_bg.gif) | `kite/kite_bg.png` |
| Cloud 0 | ![](assets/kite/cloud_0.gif) | `kite/cloud_0.png` |
| Cloud 1 | ![](assets/kite/cloud_1.gif) | `kite/cloud_1.png` |
| Cloud 2 | ![](assets/kite/cloud_2.gif) | `kite/cloud_2.png` |
| Leaf | ![](assets/kite/leaf.gif) | `kite/leaf.png` |
| Raindrop | ![](assets/kite/raindrop.gif) | `kite/raindrop.png` |

### Farm (`assets/farm/`)

Underwater farm crops with 4 growth stages (0-3):

| Crop | Stage 0 | Stage 1 | Stage 2 | Stage 3 |
|------|---------|---------|---------|---------|
| Barnacle | ![](assets/farm/barnacle_cluster_0.gif) | ![](assets/farm/barnacle_cluster_1.gif) | ![](assets/farm/barnacle_cluster_2.gif) | ![](assets/farm/barnacle_cluster_3.gif) |
| Coral | ![](assets/farm/coral_sprout_0.gif) | ![](assets/farm/coral_sprout_1.gif) | ![](assets/farm/coral_sprout_2.gif) | ![](assets/farm/coral_sprout_3.gif) |
| Kelp | ![](assets/farm/kelp_0.gif) | ![](assets/farm/kelp_1.gif) | ![](assets/farm/kelp_2.gif) | ![](assets/farm/kelp_3.gif) |
| Pearl Oyster | ![](assets/farm/pearl_oyster_0.gif) | ![](assets/farm/pearl_oyster_1.gif) | ![](assets/farm/pearl_oyster_2.gif) | ![](assets/farm/pearl_oyster_3.gif) |
| Sand Dollar | ![](assets/farm/sand_dollar_0.gif) | ![](assets/farm/sand_dollar_1.gif) | ![](assets/farm/sand_dollar_2.gif) | ![](assets/farm/sand_dollar_3.gif) |
| Sea Cucumber | ![](assets/farm/sea_cucumber_0.gif) | ![](assets/farm/sea_cucumber_1.gif) | ![](assets/farm/sea_cucumber_2.gif) | ![](assets/farm/sea_cucumber_3.gif) |
| Sea Grapes | ![](assets/farm/sea_grapes_0.gif) | ![](assets/farm/sea_grapes_1.gif) | ![](assets/farm/sea_grapes_2.gif) | ![](assets/farm/sea_grapes_3.gif) |
| Sea Pineapple | ![](assets/farm/sea_pineapple_0.gif) | ![](assets/farm/sea_pineapple_1.gif) | ![](assets/farm/sea_pineapple_2.gif) | ![](assets/farm/sea_pineapple_3.gif) |

Other: ![bg](assets/farm/farm_bg.gif) `farm_bg.png`, ![shell](assets/farm/shell_icon.gif) `shell_icon.png`, ![sparkle](assets/farm/sparkle.gif) `sparkle.png`, ![water](assets/farm/water_drop.gif) `water_drop.png`

---

## House / Crab Den (`assets/house/`)

Tile and decoration sprites for the crab's underground home.

**Tiles:** `sand_0-3.png`, `dirt_0-3.png`, `wood_0-3.png`, `hollow_0-2.png`, `air_0-2.png`

**Decorations:**

| Name | Preview | Path |
|------|---------|------|
| Coral | ![](assets/house/deco_coral.gif) | `house/deco_coral.png` |
| Driftwood | ![](assets/house/deco_driftwood.gif) | `house/deco_driftwood.png` |
| Flag | ![](assets/house/deco_flag.gif) | `house/deco_flag.png` |
| Kelp Curtain | ![](assets/house/deco_kelp_curtain.gif) | `house/deco_kelp_curtain.png` |
| Pebble | ![](assets/house/deco_pebble.gif) | `house/deco_pebble.png` |
| Porthole | ![](assets/house/deco_porthole.gif) | `house/deco_porthole.png` |
| Seaweed | ![](assets/house/deco_seaweed.gif) | `house/deco_seaweed.png` |
| Shelf | ![](assets/house/deco_shelf.gif) | `house/deco_shelf.png` |
| Shell Sconce | ![](assets/house/deco_shell_sconce.gif) | `house/deco_shell_sconce.png` |
| Dig Particles | ![](assets/house/dig_particles.gif) | `house/dig_particles.png` |

**Icons:** `icon_back.png`, `icon_decorate.png`, `icon_fill.png`, `icon_offline.png`, `icon_online.png`, `icon_shovel.png`

---

## Zone Props (`assets/sprites/zones/props/`)

Furniture and scenery for different zones:

| Prop | Preview | Path |
|------|---------|------|
| Beach Chair | ![](assets/sprites/zones/props/beach_chair.gif) | `sprites/zones/props/beach_chair.png` |
| Beach Towel | ![](assets/sprites/zones/props/beach_towel.gif) | `sprites/zones/props/beach_towel.png` |
| Beach Umbrella | ![](assets/sprites/zones/props/beach_umbrella.gif) | `sprites/zones/props/beach_umbrella.png` |
| Bed | ![](assets/sprites/zones/props/bed.gif) | `sprites/zones/props/bed.png` |
| Bookshelf | ![](assets/sprites/zones/props/bookshelf.gif) | `sprites/zones/props/bookshelf.png` |
| Bucket & Shovel | ![](assets/sprites/zones/props/bucket_shovel.gif) | `sprites/zones/props/bucket_shovel.png` |
| Campfire | ![](assets/sprites/zones/props/campfire.gif) | `sprites/zones/props/campfire.png` |
| Coffee Table | ![](assets/sprites/zones/props/coffee_table.gif) | `sprites/zones/props/coffee_table.png` |
| Couch | ![](assets/sprites/zones/props/couch.gif) | `sprites/zones/props/couch.png` |
| Floor Lamp | ![](assets/sprites/zones/props/floor_lamp.gif) | `sprites/zones/props/floor_lamp.png` |
| Lantern | ![](assets/sprites/zones/props/lantern.gif) | `sprites/zones/props/lantern.png` |
| Log | ![](assets/sprites/zones/props/log.gif) | `sprites/zones/props/log.png` |
| Ocean Waves | ![](assets/sprites/zones/props/ocean_waves.gif) | `sprites/zones/props/ocean_waves.png` |
| Tent | ![](assets/sprites/zones/props/tent.gif) | `sprites/zones/props/tent.png` |
| Tree | ![](assets/sprites/zones/props/tree.gif) | `sprites/zones/props/tree.png` |

---

## UI Elements (`assets/ui/`)

Menu and HUD icons:

| Name | Preview | Path |
|------|---------|------|
| Clock | ![](assets/ui/clock.gif) | `ui/clock.png` |
| Cookbook | ![](assets/ui/cookbook.gif) | `ui/cookbook.png` |
| Farm Tools | ![](assets/ui/farm_tools.gif) | `ui/farm_tools.png` |
| Fishing Rod | ![](assets/ui/fishing_rod.gif) | `ui/fishing_rod.png` |
| Gear | ![](assets/ui/gear.gif) | `ui/gear.png` |
| Music Box | ![](assets/ui/music_box.gif) | `ui/music_box.png` |
| Paint Set | ![](assets/ui/paint_set.gif) | `ui/paint_set.png` |
| Playing Cards | ![](assets/ui/playing_cards.gif) | `ui/playing_cards.png` |
| Seed Packet | ![](assets/ui/seed_packet.gif) | `ui/seed_packet.png` |
| Telescope | ![](assets/ui/telescope.gif) | `ui/telescope.png` |
| Sound On | ![](assets/ui/sound_on.gif) | `ui/sound_on.png` |
| Sound Off | ![](assets/ui/sound_off.gif) | `ui/sound_off.png` |
| Pixel Font | ![](assets/ui/pixel_font.gif) | `ui/pixel_font.png` |

---

## Backgrounds & Scenes

| Name | Preview | Path |
|------|---------|------|
| Beach BG | ![](assets/sprites/beach_bg.gif) | `sprites/beach_bg.png` |
| Forest BG | ![](assets/sprites/forest_bg.gif) | `sprites/forest_bg.png` |
| Meadow BG | ![](assets/sprites/meadow_bg.gif) | `sprites/meadow_bg.png` |
| Background | ![](assets/sprites/background.gif) | `sprites/background.png` |

---

## Notes

- All paths are relative to `assets/` unless otherwise noted
- Event sprites in `1color/` and `2color/` subdirectories are color-reduced variants of the default sprites
- Fishing overlay sprites also have `1color/` and `2color/` variants
- The original PNGs (horizontal sprite sheets) are preserved alongside the GIFs
- GIFs were converted using magenta (255,0,255) color key for transparency with disposal mode 2 (restore to background)
- To edit: open any `.gif` in Aseprite, edit frames, then export back as both `.gif` and horizontal sprite sheet `.png`
