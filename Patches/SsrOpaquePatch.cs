using HarmonyLib;
using SSRSix.Source;
using SPT.Reflection.Patching;
using System.Reflection;
using UnityEngine;

namespace SSRSix.Patches
{
    // SSR has to composite at [ImageEffectOpaque] time: after the deferred opaques are lit (so there is a
    // scene to reflect and the G-buffer normals/smoothness belong to THIS frame's image), before transparents
    // draw and before fog is laid over the picture. TOD_Scattering.OnRenderImageNormalMode is exactly that
    // hook — the same one FogSix claims for its volumetric fog, and it fires once per eye in VR multipass.
    //
    // We deliberately do NOT take the hook over: the prefix composites the reflections INTO `source` in
    // place and returns true, so whoever renders source -> destination afterwards (FogSix's fog prefix, or
    // vanilla TOD scattering when FogSix isn't installed) picks the reflections up and fogs them correctly.
    // Priority.First orders this prefix before FogSix's (all prefixes run; only the original is skipped by
    // FogSix's `return false`) — reflections first, fog on top. If the priorities were ever ignored the
    // failure mode is benign: our writes to `source` land after FogSix already produced `destination`, so
    // reflections silently vanish — nothing corrupts.
    internal class SsrOpaquePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(TOD_Scattering), "OnRenderImageNormalMode");
        }

        [PatchPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(TOD_Scattering __instance, RenderTexture source)
        {
            if (SsrConfig.Enabled.Value)
                SsrRenderer.Render(__instance, source);
            return true; // never consume the hook — fog/vanilla still runs source -> destination
        }
    }
}
