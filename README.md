# SSRSix

Screen-space reflections for Single Player Tarkov. SSRSix replaces Tarkov's built-in SSR with its own
reflections on water, puddles, glass and wet ground. They stay stable in motion and are correct in VR, where
the game's own SSR doesn't work at all. Works on flatscreen and in VR.

Support my work on Ko-fi: https://ko-fi.com/matsix

## Features

- Smooth reflections on rough surfaces
- Water flows down walls when it's raining
- Compatible with CloudSix, base game SSR does not reflect my clouds
- Compatible with SPT-VR

## Requirements

- SPT 4.1.

## Install

1. Extract the release into your SPT folder. You should end up with:
   ```
   BepInEx/plugins/SSRSix/SSRSix.dll
   BepInEx/plugins/SSRSix/Assets/ssr
   ```
2. In Tarkov's graphics settings, turn **SSR on**. SSRSix stops the game's SSR from rendering, but the setting
   puts the game's materials in the state SSRSix's reflections expect. With it off, wet surfaces can show
   blinding baked reflections, especially at night.
3. Launch the game.

## Settings

Open the in-game configuration manager (F12) or edit `BepInEx/config/com.matsix.ssrsix.cfg`.
These are the main ones. The Advanced view has many more.

| Setting | Default | What it does |
|---|---|---|
| Enabled | On | Turns SSRSix's reflections on or off. |
| Disable Tarkov SSR Render | On | Stops the game's own SSR from drawing, so the two never stack. |
| Intensity | 1 | Reflection strength. 1 is physically based. |
| Smoothness Cutoff | 0.77 | Only surfaces at least this smooth reflect. Lower it for more wet-looking surfaces, at more cost. |
| Reflection Softness | 0 | Extra blur on every reflection. 0 is razor sharp. |
| Foliage Gloss Reduction | 0.9 | Keeps grass from looking glossy. |
| Rain Streaks On Walls | 0.4 | Strength of the rain running down walls. |
| Temporal Smoothing | 0.8 | Denoises the reflections, at the cost of some smudging in motion. |
| Ray Steps | 250 | How far a ray can travel across the screen. |
| Max Distance | 160 | How far a reflected ray may travel, in metres. |

The Advanced view also has glossy and stochastic options, trace tuning, a half-resolution trace and debug views.

## Performance

If you need frames back:

- Turn on **Half-Resolution Trace** in the Advanced view. It's the biggest win for a small quality cost.
- Lower **Ray Steps**. This also tends to make reflections steadier.
- Raise **Smoothness Cutoff** so fewer surfaces are traced.

## Compatibility

- **CloudSix (optional, recommended):** turn on CloudSix's **Publish Sky For Reflections** and SSRSix reflects the live sky and clouds in every direction, with no seam. A roof check keeps clouds out of indoor reflections. Without CloudSix, sky that isn't on screen falls back to the game's baked reflection probes.
- **FogSix:** reflections are drawn before the fog, so they get fogged like the rest of the scene. The path from the surface to what it reflects is fogged too. SSRSix works the same with or without FogSix.
- **SPT-VR:** supported.

## Known limitations

- Reflections are drawn before transparent objects, so they appear under glass and smoke rather than on top.
- No reflections are drawn through scopes.

## Building from source

```sh
dotnet build SSRSix.csproj -c Release
```

The project references the game's DLLs from a local `libs/` folder that isn't in the repository. Copy these
into it from your SPT install:

- From `EscapeFromTarkov_Data/Managed`: `Assembly-CSharp.dll`, `Comfort.dll`, `Comfort.Unity.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`, `UnityEngine.AssetBundleModule.dll`, `Unity.Postprocessing.Runtime.dll`
- From `BepInEx/core`: `0Harmony.dll`, `BepInEx.dll`
- From `BepInEx/plugins/spt`: `spt-reflection.dll`

The reflection shader ships compiled in the `ssr` bundle in each release. Its source isn't part of this
repository.

## Credits

- Tomasz Stachowiak, "Stochastic Screen-Space Reflections" (Frostbite, SIGGRAPH 2015).
- Morgan McGuire and Michael Mara, "Efficient GPU Screen-Space Ray Tracing" (2014).
- AMD FidelityFX SSSR.
