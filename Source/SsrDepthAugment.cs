using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SSRSix.Source
{
    // Glass depth cap — the fix for the angle-banded reflection flip. Transparent glass writes no depth,
    // so a windshield is a HOLE in the depth buffer exposing the geometry behind it (vehicle interiors),
    // and the game culls that barely-visible interior geometry view-dependently: rotating the camera
    // through narrow angle bands pops it in and out of rendering. Invisible in colour (hidden behind
    // tinted glass) but a metre-scale flip in DEPTH — reflection rays marching across the glass region
    // land on completely different content per pose. Proven via Minimal Reference Mode: the flip survives
    // a 70-line textbook tracer with normals and smoothness substituted away, leaving only the depth
    // content as the variable.
    //
    // The cap: each frame, copy the scene depth into our own depth target and rasterize the scene's GLASS
    // renderers on top (nearest-wins) — producing an augmented depth where rays STOP at the glass surface,
    // which never flips. The tracer's march reads this instead of the raw depth texture; trace ORIGINS
    // keep the game depth (glass pixels aren't reflective origins, and puddles must stay exactly where
    // the G-buffer says they are).
    internal static class SsrDepthAugment
    {
        private struct GlassEntry
        {
            public Renderer renderer;
            public int subMeshCount;
        }

        private const int MAXDRAW = 16;   // nearest-K cap: the fps ceiling, whatever the map throws at us

        private static readonly List<GlassEntry> _glass = new List<GlassEntry>();
        private static readonly Plane[] _frustum = new Plane[6];
        private static readonly int[] _sel = new int[MAXDRAW];
        private static readonly float[] _selD = new float[MAXDRAW];
        private static RenderTexture _rt;
        private static CommandBuffer _cb;

        public static int GlassCount => _glass.Count;
        public static int DrawnCount { get; private set; }   // visible glass drawn this frame (debug log)

        // Raid-load scan (FogSix zero-poll pattern: one sweep, no per-frame FindObjectsOfType). Glass is
        // matched by material/shader NAME token — EFT vehicle/window glass materials carry "glass" (and
        // sometimes the Russian "steklo"); "window" catches building panes. Debug Log prints what was
        // caught so the token list can be tuned against real maps.
        public static void Rescan()
        {
            _glass.Clear();
            int logged = 0;
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                // Whitelist mesh renderers only — glass is always a mesh; excludes particles/trails/etc.
                // without referencing their modules.
                if (r == null || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                Material[] mats = r.sharedMaterials;
                if (mats == null) continue;
                bool isGlass = false;
                foreach (Material m in mats)
                {
                    if (m == null) continue;
                    string mn = m.name.ToLowerInvariant();
                    string sn = m.shader != null ? m.shader.name.ToLowerInvariant() : "";
                    if (mn.Contains("glass") || mn.Contains("steklo") || mn.Contains("window") ||
                        sn.Contains("glass"))
                    {
                        isGlass = true;
                        break;
                    }
                }
                if (!isGlass) continue;

                int sub = 1;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) sub = mf.sharedMesh.subMeshCount;
                _glass.Add(new GlassEntry { renderer = r, subMeshCount = sub });

                if (SsrConfig.Debug.Value && logged < 20)
                {
                    Plugin.MyLog.LogInfo($"[SSRSix] glass renderer: {r.name} ({mats.Length} mats)");
                    logged++;
                }
            }
            Plugin.MyLog.LogInfo($"[SSRSix] Glass depth cap: {_glass.Count} glass renderers found.");
        }

        public static void Clear()
        {
            _glass.Clear();
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); _rt = null; }
        }

        // Builds the augmented depth for the CURRENT eye/pose and binds it on the material. Returns false
        // when there is nothing to cap (no glass found / feature off) — the shader then reads game depth.
        // projRaster is the GL-convention projection used for the trace; rendering INTO a texture needs
        // the GPU-adjusted form (reversed-Z, y-flip) or the rasterized glass would land at the wrong depth.
        public static bool Build(Material mat, int w, int h, Matrix4x4 view, Matrix4x4 projRaster,
                                 int passBase, int passGlass)
        {
            if (_glass.Count == 0) return false;

            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.RFloat)
                { filterMode = FilterMode.Point };
                _rt.Create();
            }

            mat.SetMatrix("_SsrAugVP", GL.GetGPUProjectionMatrix(projRaster, true) * view);

            if (_cb == null) _cb = new CommandBuffer { name = "SSRSix Glass Depth" };
            _cb.Clear();
            // Base: the scene depth, colour = linear eye depth, SV_Depth = the raw value (so the glass
            // draws below ZTest against real scene depth). Blit binds the RT's own depth buffer.
            _cb.Blit(null, _rt, mat, passBase);
            _cb.SetRenderTarget(_rt);
            // Cull to ON-SCREEN, NEARBY glass ourselves: SSR is screen-space, so a ray can only ever hit
            // glass that is in the frustum — filtering is lossless for the reflections, and without it
            // every window pane on the map rasterized every frame (tens of fps). Our OWN frustum/distance
            // test, deliberately NOT renderer.isVisible: that flag is event-driven and reports false for
            // EFT's LOD'd/batched vehicle glass even when it is plainly on screen.
            // Hard bounds (the fps guard): the cap exists for NEARBY glass — vehicle windshields, the
            // window next to a puddle — not entire mall facades. In-frustum + 60m + the NEAREST 16 only,
            // so the worst case anywhere is 16 small draws.
            GeometryUtility.CalculateFrustumPlanes(projRaster * view, _frustum);
            Vector3 camPos = view.inverse.MultiplyPoint3x4(Vector3.zero);
            int n = 0;
            for (int gi = 0; gi < _glass.Count; gi++)
            {
                Renderer r = _glass[gi].renderer;
                if (r == null) continue;
                Bounds b = r.bounds;
                float d = b.SqrDistance(camPos);
                if (d > 60f * 60f) continue;                            // beyond reflection relevance
                if (!GeometryUtility.TestPlanesAABB(_frustum, b)) continue;
                if (n < MAXDRAW)
                {
                    int j = n++;
                    while (j > 0 && _selD[j - 1] > d) { _selD[j] = _selD[j - 1]; _sel[j] = _sel[j - 1]; j--; }
                    _selD[j] = d; _sel[j] = gi;
                }
                else if (d < _selD[MAXDRAW - 1])
                {
                    int j = MAXDRAW - 1;
                    while (j > 0 && _selD[j - 1] > d) { _selD[j] = _selD[j - 1]; _sel[j] = _sel[j - 1]; j--; }
                    _selD[j] = d; _sel[j] = gi;
                }
            }
            DrawnCount = n;
            for (int k = 0; k < n; k++)
            {
                GlassEntry g = _glass[_sel[k]];
                for (int si = 0; si < g.subMeshCount; si++)
                    _cb.DrawRenderer(g.renderer, mat, si, passGlass);
            }
            if (DrawnCount == 0) return false;   // nothing in view: skip the whole augmentation
            Graphics.ExecuteCommandBuffer(_cb);

            mat.SetTexture("_SsrAugDepth", _rt);
            return true;
        }
    }
}
