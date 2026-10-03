using SSRSix.Source;
using SPT.Reflection.Patching;
using HarmonyLib;
using System.Reflection;
using UnityEngine.Rendering.PostProcessing;

namespace SSRSix.Patches
{
    // Run WITH Tarkov's SSR enabled in settings, but never let it actually render.
    //
    // Why: the SSR menu toggle is what flips the game's materials out of baking the reflection-probe
    // specular into the scene (CameraClass.SetSSR — the "SSRenabled" global plus whatever else keys off
    // the setting). With it OFF, wet surfaces carry blinding baked-probe radiance (day-baked probes at
    // night = white patches/sparkle) that our reflections fetch and re-amplify; forcing the global alone
    // did NOT reproduce the clean state, and the user's A/B proved enabling the real setting does. So:
    // keep the setting ON — the whole game-side state is then exactly what our tracer wants to read —
    // and cut the render here.
    //
    // Hook: PPv2 schedules the effect via ScreenSpaceReflections.IsEnabledAndSupported (PostProcessLayer.
    // BuildCommandBuffers, the opaque-effects chain). Returning false is the one clean skip: the chain
    // recounts its src/dst ping-pong as if the effect didn't exist (no dangling blit), the optic camera's
    // SSR skips through the same gate, and CameraClass.GetSSREnabled still reports ON to the rest of the
    // game (it reads the profile's enabled flag, not this).
    internal class SuppressGameSsrPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(ScreenSpaceReflections),
                nameof(ScreenSpaceReflections.IsEnabledAndSupported));
        }

        [PatchPrefix]
        private static bool Prefix(ref bool __result)
        {
            if (SsrConfig.Enabled.Value && SsrConfig.SuppressGameSsr.Value)
            {
                __result = false;
                return false;
            }
            return true;    // our SSR (or suppression) off: vanilla behavior untouched
        }
    }
}
