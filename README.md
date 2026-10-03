# SSRSix

Screen-space reflections for Single Player Tarkov — **flatscreen and VR (SPT-VR) compatible**. A standalone
BepInEx plugin laid out like FogSix (`SPT-VolumetricFog`).

Tarkov's own SSR is the stock PPv2 `ScreenSpaceReflections` effect: it reconstructs from the **mono** camera
matrices and rides the post-processing layer, both of which are dead/wrong in VR multipass. SSRSix traces
its own reflections per-eye (explicit `GetStereoViewMatrix`/`GetStereoProjectionMatrix` matrices, the FogSix
reconstruction pattern) and also runs fine in mono on flatscreen.

## How it works

- **Hook:** prefix on `TOD_Scattering.OnRenderImageNormalMode` (the `[ImageEffectOpaque]` hook FogSix uses),
  at `HarmonyPriority.First` so it runs **before** FogSix's fog. The prefix composites reflections **into
  `source` in place** and returns `true` — it never consumes the hook, so FogSix (or vanilla TOD scattering)
  still renders `source -> destination` afterwards and the reflections get fogged correctly. Works with or
  without FogSix installed.
- **Inputs:** depth (`_CameraDepthTexture`) + the deferred G-buffers (`_CameraGBufferTexture2` world normals,
  `_CameraGBufferTexture1` spec colour/smoothness). EFT renders opaques deferred — its water system
  (`WaterSSR.WaterRendererv3`) even injects water meshes into the G-buffer at `CameraEvent.BeforeReflections`,
  so lakes/puddles should carry real normals+smoothness here too.
- **Tracer:** perspective-correct screen-space DDA (the McGuire/kode80 scheme) — the ray's visible screen
  segment is walked at uniform screen steps with exact 1/w-interpolated depth, + 5-step binary refine,
  backface rejection at the hit, fresnel × smoothness × edge/distance fades. Miss = keep the scene pixel
  (the baked reflection-probe specular stays as the fallback). Half-res trace + FogSix's depth-aware
  upsample by default.
- **Temporal resolve (the Frostbite/FidelityFX-SSSR denoiser core):** the reflection buffer is blended each
  frame with last frame's resolved reflections — reprojected through the previous camera matrices and
  clamped against the current neighborhood so history can't ghost. This is what keeps reflections stable
  IN MOTION: the game's TAA jitters the depth/G-buffer sub-pixel every frame, and without our own history
  the hit tests strobe (visible while moving; the game TAA only hides it once you stand still). Knob:
  `Temporal Smoothing` (0 = off). `Temporal Jitter` adds the per-pixel/per-frame ray dither the resolve
  averages out.

## Building

```sh
dotnet build -c Release        # -> bin/Release/netstandard2.1/SSRSix.dll
```

`libs/` is copied from FogSix (game Managed + BepInEx DLLs) so it builds standalone.

## Install layout

```
BepInEx/plugins/SSRSix/SSRSix.dll
BepInEx/plugins/SSRSix/Assets/ssr        <- the AssetBundle
```

## First run — the G-buffer probe (do this before judging anything)

The one unverified assumption (from `ssr-vr-project.md`) is that the G-buffer globals are readable, per eye,
at our hook in VR. The debug views settle it in seconds:

1. Set `Debug View = Normals` in the config: the world should render as smooth orientation colours
   (ground greenish, walls by facing). Check **both eyes** — they must agree apart from the parallax.
2. `Smoothness`: water/glass/wet surfaces should read bright, dirt dark.
3. If both look right, set `Debug View = ReflectionMask`, find water/a puddle — glossy areas should light up.
4. Back to `Off` and look at the actual reflections.

If Normals/Smoothness render black or garbage in one or both eyes, the G-buffers aren't live at the hook —
report which, that decides the fallback (CommandBuffer capture at an earlier CameraEvent).

Also worth one flatscreen sanity pass first (faster loop than the headset) — everything renders through the
same code path in mono.

## Known limits (v1, accepted)

- Composites at opaque time: reflections land **under** transparents — right for fog/smoke, slightly odd on
  glass itself.
- No roughness blur: below-cutoff surfaces get no SSR rather than blurry SSR (hence the high default cutoff).
- Sky is not ray-hit (sky reflections still come from the surface's own probe/cubemap shading).
- The optic/scope camera is not hooked (its second render has no TOD_Scattering image effect).
- On flatscreen, Tarkov's own SSR setting should be **Off** — the two would stack.
