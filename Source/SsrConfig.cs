using BepInEx;
using BepInEx.Configuration;

namespace SSRSix.Source
{
    internal sealed class ConfigurationManagerAttributes { public bool? IsAdvanced; }

    // What the debug views show. Normals / Smoothness / SpecColor are the G-buffer probe — the FIRST thing
    // to check when reflections misbehave: black/garbage means the tracer has no surface data at this hook.
    internal enum ESsrDebugView
    {
        Off,
        Normals,        // G-buffer world normals (should be a smooth colour-by-orientation image)
        Smoothness,     // G-buffer smoothness (white = glossy; water/glass/wet should light up)
        SpecColor,      // G-buffer specular colour (F0)
        Depth,          // linear01 depth (sanity: matches the scene, per eye)
        ReflectionMask, // where SSR lands and how strongly (white = full reflection)
        ReflectionOnly, // the traced reflection colour on black
        HitDist,        // traced hit distance (sqrt-scaled against Max Distance; black = miss)
        Coherence,      // normal coherence — the foliage detector (white = flat/coherent, dark = chaotic)
        FoliageMask,    // what the foliage gate decides: red = F0/grass, green = normals/trees, black = keep
        TraceEnd,       // why each ray ended: green=hit yellow=rejected red=budget blue=miss cyan=sky magenta=selfz
        ProbeRefl,      // the game's baked-probe specular buffer (_CameraReflectionsTexture)
        SceneMip,       // the sanitized scene copy at mip 3 — what the cone fetch / firefly reference read
    }

    internal static class SsrConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> SuppressGameSsr;
        public static ConfigEntry<bool> Debug;
        public static ConfigEntry<ESsrDebugView> DebugView;

        // Look
        public static ConfigEntry<float> Intensity;
        public static ConfigEntry<float> SmoothnessCutoff;
        public static ConfigEntry<float> ReflSoftness;
        public static ConfigEntry<float> Foliage;
        public static ConfigEntry<float> RainFlow;
        public static ConfigEntry<float> EdgeFade;
        public static ConfigEntry<bool> Glossy;
        public static ConfigEntry<float> GlossyScale;
        public static ConfigEntry<float> GlossyAniso;
        public static ConfigEntry<bool> Stochastic;
        public static ConfigEntry<float> SpecularAA;
        public static ConfigEntry<float> Firefly;
        public static ConfigEntry<float> FoliageF0;
        public static ConfigEntry<float> SelfExclusion;
        public static ConfigEntry<float> SkimRejection;
        public static ConfigEntry<bool> SkyRefl;

        // Quality / performance
        public static ConfigEntry<float> TemporalSmoothing;
        public static ConfigEntry<int> Steps;
        public static ConfigEntry<float> MaxDistance;
        public static ConfigEntry<float> MarchStride;
        public static ConfigEntry<float> StrideGrowth;
        public static ConfigEntry<float> Thickness;
        public static ConfigEntry<bool> HalfRes;

        private static ConfigDescription Adv(string desc, AcceptableValueBase range = null)
        {
            return new ConfigDescription(desc, range, new ConfigurationManagerAttributes { IsAdvanced = true });
        }

        public static void Bind(ConfigFile config)
        {
            string currentVersion = MetadataHelper.GetMetadata(typeof(Plugin)).Version.ToString();
            var version = config.Bind("Internal", "ConfigVersion", "", "Do not modify");
            if (version == null || version.Value != currentVersion)
            {
                config.Clear();
                System.IO.File.WriteAllText(config.ConfigFilePath, "");
                config.Reload();
                version = config.Bind("Internal", "ConfigVersion", currentVersion, "Do not modify");
                version.Value = currentVersion;
                config.Save();
                Plugin.MyLog.LogInfo($"Config reset for version {currentVersion}");
            }

            // ---- General (master toggles stay visible; diagnostics are advanced) ----
            Enabled = config.Bind("General", "Enabled", true,
                "Screen-space reflections on glossy surfaces (water, puddles, glass, wet ground). Works " +
                "flatscreen and in VR (per-eye).");
            // NOTE: BepInEx forbids ' (and = " [ ] etc.) in section/KEY names — an apostrophe here threw
            // in Bind and killed the whole plugin Awake. Descriptions may contain them; keys must not.
            SuppressGameSsr = config.Bind("General", "Disable Tarkov SSR Render", true,
                "Disables Tarkov's built in SSR");
            Debug = config.Bind("General", "Debug Log", false,
                Adv("Log diagnostics (reflection-probe census, shader-load status)."));
            DebugView = config.Bind("General", "Debug View", ESsrDebugView.Off,
                Adv("Fullscreen diagnostic overlays. Normals/Smoothness/SpecColor visualize the G-buffer " +
                    "inputs — check these FIRST if reflections misbehave: black/garbage means the tracer " +
                    "has no surface data at this hook. ReflectionMask/ReflectionOnly show what the tracer " +
                    "produces; HitDist shows how far each ray flew before hitting (black = miss)."));

            // ---- Look: the everyday knobs (not advanced) ----
            Intensity = config.Bind("Look", "Intensity", 1.0f,
                new ConfigDescription("Strength of the reflections. 1 = physically-motivated (fresnel-weighted); " +
                    "lower if surfaces look like chrome mirrors, raise for a stylized wet look.",
                    new AcceptableValueRange<float>(0f, 2f)));
            SmoothnessCutoff = config.Bind("Look", "Smoothness Cutoff", 0.77f,
                new ConfigDescription("Only surfaces at least this smooth (G-buffer smoothness) get reflections. " +
                    "Lower = more surfaces reflect (wet asphalt/concrete pick up soft reflections) but more " +
                    "rays traced; raise for glossy-only and a little more performance.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ReflSoftness = config.Bind("Look", "Reflection Softness", 0.0f,
                new ConfigDescription("Baseline blur for ALL reflections (in mip levels), on top of the " +
                    "roughness-driven glossy blur. 0 = razor sharp; ~0.5 = a subtle filmic soften; 1.5+ = " +
                    "visibly soft. Needs Glossy Reflections ON.",
                    new AcceptableValueRange<float>(0f, 3f)));
            Foliage = config.Bind("Look", "Foliage Gloss Reduction", 0.9f,
                new ConfigDescription("Reduces the gloss/reflection on grass (can have an effect on other surfaces)",
                    new AcceptableValueRange<float>(0f, 1f)));
            RainFlow = config.Bind("Look", "Rain Streaks On Walls", 0.4f,
                new ConfigDescription("Adds a flowing down effect on vertical surfaces",
                    new AcceptableValueRange<float>(0f, 1f)));

            // ---- Look: advanced tuning ----
            EdgeFade = config.Bind("Look", "Edge Fade", 0.2f,
                Adv("Screen-border band over which reflections fade out (hides rays running off-screen; in VR " +
                    "also hides the per-eye disagreement at the view edges).",
                    new AcceptableValueRange<float>(0.01f, 0.5f)));
            Glossy = config.Bind("Look", "Glossy Reflections", true,
                Adv("Roughness-aware reflection blur (cone tracing against a prefiltered pyramid). Rougher " +
                    "surfaces reflect softer, and blur grows with hit distance, so contact points stay sharp " +
                    "while distant content goes soft. Also the spatial denoiser for Stochastic Reflections."));
            GlossyScale = config.Bind("Look", "Glossy Blur Scale", 0.5f,
                Adv("Multiplier on the roughness-driven blur cone. 1 = GGX-motivated width; raise for a " +
                    "softer, dreamier wet look, lower toward 0 for sharper reflections everywhere.",
                    new AcceptableValueRange<float>(0f, 3f)));
            GlossyAniso = config.Bind("Look", "Glossy Anisotropy", 0.6f,
                Adv("Grazing-angle stretch of the glossy blur — the elongated light streaks on wet " +
                    "streets at night. 0 = round blur only; 1 = full physical stretch. Head-on " +
                    "reflections are never affected. Needs Glossy Reflections ON.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Stochastic = config.Bind("Look", "Stochastic Reflections (SSSR)", true,
                Adv("Trace REAL glossy reflections: each pixel's ray importance-samples its GGX roughness " +
                    "lobe (noise-rotated every frame) and a BRDF-weighted resolve shares rays between " +
                    "neighbouring pixels — parallax-correct roughness blur and contact hardening from the " +
                    "trace itself instead of the post-blur approximation. NEEDS Temporal Smoothing above " +
                    "~0.8 to integrate the sample noise (at 0 you see raw grain). OFF = deterministic " +
                    "mirror rays + pyramid blur."));
            SpecularAA = config.Bind("Look", "Specular AA", 0.0f,
                Adv("Geometric specular antialiasing. Detailed surfaces (brick mortar, gun rails) pack " +
                    "many differently-aimed micro-mirrors into a pixel once rain raises smoothness; this " +
                    "measures that sub-pixel variance and widens the reflection lobe to match, turning " +
                    "would-be specks into soft glints (energy-preserving blur). Mostly redundant now that " +
                    "the white-speck source is handled at the material level; adds softening. 0 = off.",
                    new AcceptableValueRange<float>(0f, 1f)));
            Firefly = config.Bind("Look", "Firefly Suppression", 0.7f,
                Adv("Tames bright white speckles in reflections — reflections of the scene's own tiny hot " +
                    "pixels (streetlight sparkles, HDR specular highlights) that otherwise flicker as " +
                    "blinding dots. Clamps a reflected pixel's brightness relative to its neighbourhood, so " +
                    "real coherent lights stay bright while lone sparkles are reined in. 0 = off; 1 = strong.",
                    new AcceptableValueRange<float>(0f, 1f)));
            FoliageF0 = config.Bind("Look", "Foliage F0 Threshold", 0.065f,
                Adv("The specular-colour level at/below which a surface counts as grass (\"declares no " +
                    "specular response\"). Tune it with Debug View = FoliageMask: RED marks what this " +
                    "catches — raise until grass turns red, and stop before WATER does (real water sits " +
                    "around 0.02, glass around 0.04). Only matters when Foliage Gloss Reduction is above 0.",
                    new AcceptableValueRange<float>(0.001f, 0.08f)));
            SelfExclusion = config.Bind("Look", "Self-Reflection Exclusion", 0.0f,
                Adv("Reflection rays ignore anything closer to the camera than this (metres). In VR your " +
                    "rendered body, arms, gun and wrist UI hover over the ground and can otherwise reflect " +
                    "as black body-shaped blobs in puddles when you look down — this treats near-camera " +
                    "depth as the viewer, not the world. 0 = off (viewmodel/body reflects again). Raise it " +
                    "if body blobs appear at extreme downward angles.",
                    new AcceptableValueRange<float>(0f, 3f)));
            SkimRejection = config.Bind("Look", "Skim Rejection", 0.12f,
                Adv("Rejects grazing 'skim' hits — rays that clip the ground's own micro-bumps a metre or " +
                    "two ahead and paint dark ground where the real reflection (vehicle, building, sky) " +
                    "belongs; the ray marches on to its true target instead. This was the VR 'black bar'. " +
                    "The value is how decisively (metres) a ray must sink behind a surface to count as a " +
                    "real hit: too low = the bar returns, too high = thin/edge-on reflections disappear. " +
                    "0 = off.",
                    new AcceptableValueRange<float>(0f, 0.5f)));
            SkyRefl = config.Bind("Look", "Sky In Reflections", true,
                Adv("Reflections always show the sky where they should: rays that reach sky ON SCREEN " +
                    "fetch it exactly (clouds included), and when the sky isn't in view, CloudSix's live " +
                    "sky map fills in — gated by a roof check so interiors never reflect phantom clouds. " +
                    "Needs CloudSix's 'Publish Sky For Reflections' for the not-on-screen half."));

            // ---- Performance: the everyday knobs (not advanced) ----
            TemporalSmoothing = config.Bind("Performance", "Temporal Smoothing", 0.8f,
                new ConfigDescription("Denoises the effect at the cost of smudging",
                    new AcceptableValueRange<float>(0f, 0.95f)));
            Steps = config.Bind("Performance", "Ray Steps", 250,
                new ConfigDescription("March iteration budget per ray. Increases the reach of the ray across the screen.",
                    new AcceptableValueRange<int>(16, 512)));
            MaxDistance = config.Bind("Performance", "Max Distance", 160f,
                new ConfigDescription("How far a reflected ray may travel, in metres. Reflections of things " +
                    "farther than this from the reflecting surface fade out.",
                    new AcceptableValueRange<float>(5f, 300f)));

            // ---- Performance: advanced ----
            MarchStride = config.Bind("Performance", "March Stride", 2.5f,
                Adv("Screen pixels between march samples. Smaller = finer contact detail and thinner " +
                    "reflected features, but shorter reach for the same Ray Steps; larger = longer reach, " +
                    "may step over very thin objects.",
                    new AcceptableValueRange<float>(1f, 8f)));
            StrideGrowth = config.Bind("Performance", "Far Stride Growth", 1.01f,
                Adv("The march stride GROWS by this factor per step (capped at 3x March Stride), so " +
                    "rays sample finely near their origin — where contact detail lives — and sweep the " +
                    "far field faster. ~3x the reach for the same Ray Steps; long rays stop dying " +
                    "mid-screen. Hits are still refined to sub-pixel precision; the trade is thin " +
                    "DISTANT objects between coarse samples. 1.0 = constant stride (old behavior).",
                    new AcceptableValueRange<float>(1f, 1.1f)));
            Thickness = config.Bind("Performance", "Thickness", 2.5f,
                Adv("How far (metres) a march sample may sink behind the depth buffer and still count as a hit. " +
                    "Too small = gaps in reflections of thin objects; too big = smeary streaks under objects.",
                    new AcceptableValueRange<float>(0.05f, 3f)));
            HalfRes = config.Bind("Performance", "Half-Resolution Trace", false,
                Adv("Trace at half resolution per axis (quarter the rays) and upsample depth-aware — big win " +
                    "for small quality cost. Debug views always render full-res."));
        }
    }
}
