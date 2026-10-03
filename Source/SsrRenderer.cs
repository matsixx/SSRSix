using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SSRSix.Source
{
    // Renders the screen-space reflections: loads the shader bundle, builds the per-eye matrices, and
    // composites the traced reflections back INTO the source image (in place) at opaque time, so the fog /
    // vanilla scattering that runs after us sees a scene that already reflects. Handles BOTH flatscreen
    // (mono) and VR (per-eye) — the hook fires once per eye in multipass and Camera.current tells us which.
    //
    // v1.2.0: the tracer is VIEW-SPACE (see the shader header — the 22-round lesson). The trace consumes
    // only the projection pair and the view rotation. One lean temporal resolve remains (SSSR's denoiser
    // core — a raw single-frame trace always carries per-frame noise in motion); Hi-Z, planar refinement
    // and the diagnostic scaffolding are gone.
    internal static class SsrRenderer
    {
        private static Material _mat;
        private static bool _triedLoad;
        private static float _nextProbeLog;
        private static float _rainFlowEnv;      // live rain envelope (trails RainController.Intensity)
        private static int _rainEnvFrame = -1;  // once-per-frame guard (Render fires per eye in VR)

        // Pass indices in Assets/ScreenSpaceReflections.shader (PINNED — keep in sync with the shader).
        private const int PassTrace = 0;     // the trace, at whatever resolution it's blitted at -> hit records
        private const int PassUpsample = 1;  // depth-aware upsample of the half-res hit records
        private const int PassShade = 2;     // records -> (premultiplied colour, blend weight), full res
        private const int PassComposite = 3; // reflections over the scene (+ glossy cone fetch)
        private const int PassDebug = 4;     // debug views
        private const int PassGlossDown = 5; // glossy pyramid: blurred downsample of the shaded reflections
        private const int PassAugBase = 6;   // glass depth cap: scene depth -> augmented target
        private const int PassAugGlass = 7;  // glass depth cap: rasterize one glass renderer, nearest-wins
        private const int PassTemporal = 8;  // temporal resolve (view-space virtual-point reprojection)
        private const int PassSanitize = 9;  // scene-copy scrub (NaN kill + radiance cap) before mip build

        // Per-eye reflection history for the temporal resolve (mono, left, right are separate renders).
        // Ping-pong: `read` is last frame's resolved output, `write` this frame's target; swapped after
        // each resolve. prevView/prevProjNoJit feed the RELATIVE reprojection matrix.
        private class EyeHistory
        {
            public RenderTexture read, write;
            public Matrix4x4 prevView, prevProjNoJit;
            public bool valid;
        }
        private static readonly Dictionary<Camera.MonoOrStereoscopicEye, EyeHistory> _history =
            new Dictionary<Camera.MonoOrStereoscopicEye, EyeHistory>();

        private static EyeHistory GetHistory(Camera.MonoOrStereoscopicEye eye, int w, int h)
        {
            if (!_history.TryGetValue(eye, out EyeHistory hb))
            {
                hb = new EyeHistory();
                _history[eye] = hb;
            }
            if (hb.read == null || hb.read.width != w || hb.read.height != h)
            {
                if (hb.read != null) hb.read.Release();
                if (hb.write != null) hb.write.Release();
                hb.read = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
                hb.write = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
                hb.valid = false;   // resolution changed -> stale history, restart accumulation
            }
            return hb;
        }

        // Glossy prefilter pyramid: mip 0 = the shaded reflections, each level below a blurred half-res
        // downsample. The composite cone-traces it. Same Blit+CopyTexture machinery as ever; Trilinear so
        // fractional LODs blend smoothly between levels.
        private static RenderTexture _gloss;
        private static int _glossMips;

        private static void BuildGlossPyramid(RenderTexture refl, int w, int h)
        {
            if (_gloss == null || _gloss.width != w || _gloss.height != h)
            {
                if (_gloss != null) _gloss.Release();
                _gloss = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf)
                {
                    useMipMap = true,
                    autoGenerateMips = false,
                    filterMode = FilterMode.Trilinear,
                };
                _gloss.Create();
                // Levels down to ~8px — blur wider than that reads as murk, not gloss.
                _glossMips = 1;
                while (_glossMips < 6 && (Mathf.Min(w, h) >> _glossMips) >= 8) _glossMips++;
            }

            Graphics.CopyTexture(refl, 0, 0, _gloss, 0, 0);
            RenderTexture prev = refl;
            for (int i = 1; i < _glossMips; i++)
            {
                RenderTexture cur = RenderTexture.GetTemporary(
                    Mathf.Max(1, w >> i), Mathf.Max(1, h >> i), 0, RenderTextureFormat.ARGBHalf);
                cur.filterMode = FilterMode.Bilinear;
                Graphics.Blit(prev, cur, _mat, PassGlossDown);
                Graphics.CopyTexture(cur, 0, 0, _gloss, 0, i);
                if (prev != refl) RenderTexture.ReleaseTemporary(prev);
                prev = cur;
            }
            if (prev != refl) RenderTexture.ReleaseTemporary(prev);

            _mat.SetTexture("_SsrGlossTex", _gloss);
            _mat.SetFloat("_SsrGlossMaxMip", _glossMips - 1);
        }

        // Max absolute element difference between two matrices (the live-vs-getter probe).
        private static float MatDelta(Matrix4x4 a, Matrix4x4 b)
        {
            float d = 0f;
            for (int i = 0; i < 16; i++) d = Mathf.Max(d, Mathf.Abs(a[i] - b[i]));
            return d;
        }

        // Double-precision 4x4 inverse (glMatrix cofactor scheme). Matrix4x4.inverse is float32; its
        // cancellation error leaves inv*M measurably off identity, which a per-pixel unproject turns into
        // a constant sub-pixel error. The projection inverse is the one matrix the whole trace hangs off.
        private static Matrix4x4 InverseD(Matrix4x4 m)
        {
            double a00 = m.m00, a01 = m.m01, a02 = m.m02, a03 = m.m03;
            double a10 = m.m10, a11 = m.m11, a12 = m.m12, a13 = m.m13;
            double a20 = m.m20, a21 = m.m21, a22 = m.m22, a23 = m.m23;
            double a30 = m.m30, a31 = m.m31, a32 = m.m32, a33 = m.m33;

            double b00 = a00 * a11 - a01 * a10;
            double b01 = a00 * a12 - a02 * a10;
            double b02 = a00 * a13 - a03 * a10;
            double b03 = a01 * a12 - a02 * a11;
            double b04 = a01 * a13 - a03 * a11;
            double b05 = a02 * a13 - a03 * a12;
            double b06 = a20 * a31 - a21 * a30;
            double b07 = a20 * a32 - a22 * a30;
            double b08 = a20 * a33 - a23 * a30;
            double b09 = a21 * a32 - a22 * a31;
            double b10 = a21 * a33 - a23 * a31;
            double b11 = a22 * a33 - a23 * a32;

            double det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06;
            if (System.Math.Abs(det) < 1e-30)
                return m.inverse;   // degenerate — fall back
            double id = 1.0 / det;

            Matrix4x4 r = default;
            r.m00 = (float)((a11 * b11 - a12 * b10 + a13 * b09) * id);
            r.m01 = (float)((a02 * b10 - a01 * b11 - a03 * b09) * id);
            r.m02 = (float)((a31 * b05 - a32 * b04 + a33 * b03) * id);
            r.m03 = (float)((a22 * b04 - a21 * b05 - a23 * b03) * id);
            r.m10 = (float)((a12 * b08 - a10 * b11 - a13 * b07) * id);
            r.m11 = (float)((a00 * b11 - a02 * b08 + a03 * b07) * id);
            r.m12 = (float)((a32 * b02 - a30 * b05 - a33 * b01) * id);
            r.m13 = (float)((a20 * b05 - a22 * b02 + a23 * b01) * id);
            r.m20 = (float)((a10 * b10 - a11 * b08 + a13 * b06) * id);
            r.m21 = (float)((a01 * b08 - a00 * b10 - a03 * b06) * id);
            r.m22 = (float)((a30 * b04 - a31 * b02 + a33 * b00) * id);
            r.m23 = (float)((a21 * b02 - a20 * b04 - a23 * b00) * id);
            r.m30 = (float)((a11 * b07 - a10 * b09 - a12 * b06) * id);
            r.m31 = (float)((a00 * b09 - a01 * b07 + a02 * b06) * id);
            r.m32 = (float)((a31 * b01 - a30 * b03 - a32 * b00) * id);
            r.m33 = (float)((a20 * b03 - a21 * b01 + a22 * b00) * id);
            return r;
        }

        public static void Render(TOD_Scattering scattering, RenderTexture source)
        {
            if (_mat == null) LoadShader();
            if (_mat == null) return;                       // bundle missing -> scene stays vanilla

            Camera cam = scattering.GetComponent<Camera>();
            if (cam == null) return;

            cam.depthTextureMode |= DepthTextureMode.Depth; // the tracer walks the depth buffer

            // Per-eye view/projection (mono in flatscreen, each eye in VR). VR eye projections are
            // off-axis asymmetric, so they MUST come from the stereo getters. The LIVE projection matrix
            // is the raster's (verified: nothing in EFT rewrites camera state between raster and image
            // effects), and it carries whatever sub-pixel jitter the raster used — TAA on flatscreen,
            // VRJitterComponent under VR upscalers — so rays and depth share one lattice automatically.
            Camera.MonoOrStereoscopicEye eye = (Camera.current != null) ? Camera.current.stereoActiveEye : cam.stereoActiveEye;
            // LIVE matrices for BOTH modes (2026-07-17). This hook fires mid-eye-render, where the live
            // camera matrices ARE the raster truth for the eye being drawn — exactly what flatscreen
            // (which works) reads. The old VR path guessed the eye via Camera.current.stereoActiveEye and
            // pulled the stereo getters; any staleness/eye-mix there costs centimetre depth error that
            // only GRAZING rays notice (steep rays cross silhouettes decisively) — the VR-only grazing
            // failure fingerprint. The eye enum stays for history keying; the debug log prints the
            // live-vs-getter delta so a mismatch shows up as a number, not a vibe.
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 proj = cam.projectionMatrix;
            Matrix4x4 projNoJit = cam.nonJitteredProjectionMatrix;   // reprojection pair: AVERAGE jitter, don't chase it

            // View-space tracer uniforms (the whole matrix diet: projection pair + view rotation).
            _mat.SetMatrix("_SsrProjM", proj);
            _mat.SetMatrix("_SsrInvProjM", InverseD(proj));
            _mat.SetMatrix("_SsrViewM", view);

            _mat.SetInt("_SsrSteps", Mathf.Max(1, SsrConfig.Steps.Value));
            _mat.SetFloat("_SsrStridePx", Mathf.Max(1f, SsrConfig.MarchStride.Value));
            _mat.SetFloat("_SsrStrideGrow", Mathf.Clamp(SsrConfig.StrideGrowth.Value, 1f, 1.1f));
            _mat.SetFloat("_SsrMaxDist", SsrConfig.MaxDistance.Value);
            _mat.SetFloat("_SsrThickness", SsrConfig.Thickness.Value);
            _mat.SetFloat("_SsrIntensity", SsrConfig.Intensity.Value);
            _mat.SetFloat("_SsrSmoothCutoff", SsrConfig.SmoothnessCutoff.Value);
            _mat.SetFloat("_SsrEdgeFade", SsrConfig.EdgeFade.Value);
            _mat.SetFloat("_SsrEdgeSoft", 0f);   // Silhouette Fade removed: faded hits let sky bleed through
            _mat.SetFloat("_SsrFirefly", SsrConfig.Firefly.Value);
            _mat.SetFloat("_SsrFoliage", SsrConfig.Foliage.Value);
            _mat.SetFloat("_SsrFoliageF0", SsrConfig.FoliageF0.Value);
            _mat.SetFloat("_SsrSelfZ", SsrConfig.SelfExclusion.Value);
            _mat.SetFloat("_SsrSkimMin", SsrConfig.SkimRejection.Value);
            _mat.SetFloat("_SsrSpecAA", SsrConfig.SpecularAA.Value);
            _mat.SetFloat("_SsrStochastic", SsrConfig.Stochastic.Value ? 1f : 0f);
            // The stochastic noise sequence. Frame-keyed (not eye-keyed): both eyes draw the SAME
            // pattern, so the noise never disagrees binocularly.
            _mat.SetFloat("_SsrFrameIdx", Time.frameCount & 1023);
            _mat.SetFloat("_SsrResolveSpacing", SsrConfig.HalfRes.Value ? 2f : 1f);
            // World size of one screen pixel at 1m view depth — set BEFORE the shade pass now: the
            // cone-traced fetch needs it there, not just in the composite.
            _mat.SetFloat("_SsrPixWorld", 2f / (Mathf.Abs(proj.m11) * source.height));
            // Reflection probe = Keep (the dropdown was removed): our reflections composite OVER the
            // scene's baked-probe term, no subtraction, no SSRenabled forcing. The clean-material state
            // comes from Suppress Tarkov SSR Render instead (game SSR on + never rendered).
            _mat.SetFloat("_SsrProbeMode", 0f);
            _mat.SetFloat("_SsrFlipRayY", 0f);   // VR Trace Flip removed (was a confirmed-wrong diagnostic)

            // Sky in reflections — hybrid: the on-screen sky-exit fetch always works (gated only by the
            // config); the sky-MAP half needs CloudSix's published map AND feeds the roof grid (a small
            // world-anchored grid of upward rays — per-surface indoor suppression without triggers).
            bool skyExitOn = SsrConfig.SkyRefl.Value;
            Texture skyMapTex = Shader.GetGlobalTexture("_SsrSkyReflMap");
            bool skyMapOn = skyExitOn && skyMapTex != null;
            _mat.SetFloat("_SsrSkyOn", skyExitOn ? 1f : 0f);
            _mat.SetFloat("_SsrSkyMapOn", skyMapOn ? 1f : 0f);
            // Lobe-matched sky blur needs the map's mip chain; an old CloudSix DLL publishes mip-0 only
            // (mipmapCount 1) and this degrades to the sharp fetch.
            _mat.SetFloat("_SsrSkyMapMaxLod", skyMapOn ? Mathf.Max(0, skyMapTex.mipmapCount - 1) : 0f);
            // Rain flow on walls: strength = config x a LIVE rain envelope. RainController.Intensity is
            // the game's own static current-rain value; the envelope trails it by ~20s so rivulets keep
            // running briefly after a shower instead of snapping off (the persistent SHEEN is the game's
            // wetness system, not ours). Once per frame, not per eye.
            if (Time.frameCount != _rainEnvFrame)
            {
                _rainEnvFrame = Time.frameCount;
                float rain = 0f;
                try { rain = Mathf.Clamp01(RainController.Intensity); } catch { }
                _rainFlowEnv = Mathf.Max(rain, _rainFlowEnv - Time.deltaTime / 20f);
            }
            float rainFlow = SsrConfig.RainFlow.Value * _rainFlowEnv;
            bool rainFlowOn = rainFlow > 0.01f;
            _mat.SetFloat("_SsrRainFlow", rainFlowOn ? rainFlow : 0f);
            _mat.SetFloat("_SsrTime", Time.time);

            // The inverse view + roof grid serve the sky map AND the rain-flow gating (a roofed wall is
            // shiny, not wet). The grid defaults to "roofed" when never updated, so flow fails CLOSED.
            _mat.SetMatrix("_SsrInvViewM", InverseD(view));
            if (skyMapOn || rainFlowOn)
            {
                SsrSkyGrid.Update(view.inverse.MultiplyPoint3x4(Vector3.zero));
                SsrSkyGrid.Apply(_mat);
            }

            // Reflected-path fog (FogSix bridge): only when FogSix published its froxel volume within
            // the last few frames (stamp = frameCount; stale/absent = legacy fog mode or mod missing).
            Vector4 fogStamp = Shader.GetGlobalVector("_FogSixVolStamp");
            bool fogOn = fogStamp.x > 0f && (Time.frameCount - fogStamp.x) < 5f &&
                         Shader.GetGlobalTexture("_FogSixScatterVol") != null;   // released RT = unbound
            _mat.SetFloat("_SsrFogOn", fogOn ? 1f : 0f);
            _mat.SetFloat("_SsrFogFlipV", fogStamp.y);

            _mat.SetFloat("_SsrDepthAugOn", 0f);   // Glass Occludes Reflections removed

            if (SsrConfig.Debug.Value && Time.unscaledTime >= _nextProbeLog)
            {
                _nextProbeLog = Time.unscaledTime + 5f;
                // Buffer-registration probe (the flatscreen round-11 lesson, re-run for VR): the trace
                // assumes source, the camera viewport, and the depth texture share one pixel lattice.
                // A mismatch here = errors that GROW with ray length (contact reflections fine, distant
                // objects never found) — check these agree before chasing anything in the shader.
                RenderTexture dt = Shader.GetGlobalTexture("_CameraDepthTexture") as RenderTexture;
                Camera pc = Camera.current != null ? Camera.current : cam;
                // Live-vs-stereo-getter deltas: nonzero here = the old getter path was tracing with
                // matrices that are NOT what rastered this eye (the VR grazing-failure suspect).
                string mdelta = "";
                if (eye != Camera.MonoOrStereoscopicEye.Mono)
                {
                    Camera.StereoscopicEye sE = (eye == Camera.MonoOrStereoscopicEye.Right)
                        ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                    mdelta = $" dProj={MatDelta(proj, cam.GetStereoProjectionMatrix(sE)):E2}" +
                             $" dView={MatDelta(view, cam.GetStereoViewMatrix(sE)):E2}";
                }
                Plugin.MyLog.LogInfo($"[SSRSix] {eye} src={source.width}x{source.height} " +
                    $"px={pc.pixelWidth}x{pc.pixelHeight} rect=({pc.rect.x:F3},{pc.rect.y:F3},{pc.rect.width:F3},{pc.rect.height:F3}) " +
                    $"depth={(dt != null ? dt.width + "x" + dt.height : "null")}{mdelta} " +
                    $"sky={(skyMapOn ? "MAP(seamless)" : (skyExitOn ? "SCREEN-ONLY(no CloudSix map: seams at the ray-budget edge)" : "off"))} " +
                    $"rainFlow={rainFlow:F2}");
            }

            // The composite reads the scene while writing over it, so we work from a copy: trace from the
            // copy, write the composite back into `source` in place (downstream fog then runs source->dest).
            // MIPPED (2026-07-18): the shade pass cone-traces its colour fetches from this chain — the
            // pre-filtered hit neighbourhood is what kills the re-reflected probe-sparkle specks, and the
            // firefly clamp reads its coarse-mip reference from it too.
            RenderTextureDescriptor scDesc = new RenderTextureDescriptor(
                source.width, source.height, source.format, 0)
            {
                useMipMap = true,
                autoGenerateMips = false,
            };
            RenderTexture sceneCopy = RenderTexture.GetTemporary(scDesc);
            sceneCopy.filterMode = FilterMode.Trilinear;
            // Copy THROUGH the sanitize pass (NaN kill + 2000 radiance cap) so the mip chain is built
            // from a scrubbed image — GenerateMips averages, so one raw hot pixel would otherwise
            // contaminate every mip texel covering it (the "more specks than before" bug), and nothing
            // upstream is guaranteed to have cleaned the scene (PPv2 only NaN-kills it when its own
            // opaque chain runs, e.g. when Tarkov's SSR actually renders).
            Graphics.Blit(source, sceneCopy, _mat, PassSanitize);
            sceneCopy.GenerateMips();
            _mat.SetFloat("_SsrSceneMaxMip", Mathf.Max(0, sceneCopy.mipmapCount - 1));

            ESsrDebugView dbg = SsrConfig.DebugView.Value;
            _mat.SetFloat("_SsrDebug", (int)dbg);
            if (dbg != ESsrDebugView.Off)
            {
                // Debug views: full-res raw single-frame trace — shows the tracer's true output.
                _mat.SetFloat("_SsrTraceScale", 1f);
                Graphics.Blit(sceneCopy, source, _mat, PassDebug);
                RenderTexture.ReleaseTemporary(sceneCopy);
                return;
            }

            // 1) Trace into a full-res HIT-RECORD buffer (uv offset to the hit, hit distance, confidence).
            // ARGBFloat, not Half: a half-quantized whole-screen uv offset moves the colour fetch by most
            // of a pixel on far hits.
            RenderTexture recFull = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBFloat);
            recFull.filterMode = FilterMode.Point;
            if (SsrConfig.HalfRes.Value)
            {
                // Ceil'd half size: EFT's resolution scaling produces ODD source sizes; the pixel-ID
                // mapping in trace/upsample assumes half pixel h <-> full texels 2h/2h+1 everywhere.
                int hw = (source.width + 1) / 2, hh = (source.height + 1) / 2;
                RenderTexture recHalf = RenderTexture.GetTemporary(hw, hh, 0, RenderTextureFormat.ARGBFloat);
                recHalf.filterMode = FilterMode.Point;
                _mat.SetFloat("_SsrTraceScale", 2f);
                Graphics.Blit(sceneCopy, recHalf, _mat, PassTrace);
                _mat.SetTexture("_SsrHalfTex", recHalf);
                Graphics.Blit(sceneCopy, recFull, _mat, PassUpsample);
                RenderTexture.ReleaseTemporary(recHalf);
            }
            else
            {
                _mat.SetFloat("_SsrTraceScale", 1f);
                Graphics.Blit(sceneCopy, recFull, _mat, PassTrace);
            }

            // 2) Shade the records at full res into the reflection buffer.
            RenderTexture reflFull = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf);
            reflFull.filterMode = FilterMode.Bilinear;
            _mat.SetTexture("_SsrHitTex", recFull);   // also read by the composite (hit distance -> cone)
            Graphics.Blit(sceneCopy, reflFull, _mat, PassShade);

            // 3) Temporal resolve (the SSSR denoiser core): average this frame's reflections with the
            // reprojected history — raw per-frame trace noise in motion (marginal hits re-rolling) is the
            // one thing a single-frame trace can't fix. The view-space relative reprojection means the
            // resolve only absorbs honest noise now, not a broken world transform, so it runs light.
            RenderTexture resolved = reflFull;
            float smoothing = Mathf.Clamp(SsrConfig.TemporalSmoothing.Value, 0f, 0.95f);
            if (smoothing > 0.001f)
            {
                EyeHistory h = GetHistory(eye, source.width, source.height);
                if (h.valid)
                {
                    // At rest the previous matrices equal the current ones: hand the SAME matrix to both
                    // sides and the residual-cancelled delta is exactly zero by construction.
                    Matrix4x4 reprojPrev =
                        (h.prevView == view && h.prevProjNoJit == projNoJit)
                            ? projNoJit
                            : h.prevProjNoJit * (h.prevView * InverseD(view));
                    _mat.SetMatrix("_SsrReprojPrev", reprojPrev);
                    _mat.SetMatrix("_SsrProjNoJitM", projNoJit);
                    _mat.SetTexture("_SsrHistTex", h.read);
                    _mat.SetFloat("_SsrTemporal", smoothing);
                    Graphics.Blit(reflFull, h.write, _mat, PassTemporal);
                }
                else
                {
                    Graphics.Blit(reflFull, h.write);   // first frame: seed the history with the raw trace
                }
                h.prevView = view;
                h.prevProjNoJit = projNoJit;
                h.valid = true;
                RenderTexture t = h.read; h.read = h.write; h.write = t;
                resolved = h.read;
            }

            // 4) Composite over the scene, in place into `source` — cone-traced against the glossy
            // pyramid when enabled (roughness x hit-distance blur, contact hardening, shiny-hit softening).
            bool glossOn = SsrConfig.Glossy.Value;
            if (glossOn) BuildGlossPyramid(resolved, source.width, source.height);
            _mat.SetFloat("_SsrGlossOn", glossOn ? 1f : 0f);
            _mat.SetFloat("_SsrGlossScale", SsrConfig.GlossyScale.Value);
            _mat.SetFloat("_SsrGlossAniso", glossOn ? SsrConfig.GlossyAniso.Value : 0f);
            _mat.SetFloat("_SsrGlossMinLod", SsrConfig.ReflSoftness.Value);
            _mat.SetTexture("_SsrReflTex", resolved);
            Graphics.Blit(sceneCopy, source, _mat, PassComposite);

            RenderTexture.ReleaseTemporary(reflFull);
            RenderTexture.ReleaseTemporary(recFull);
            RenderTexture.ReleaseTemporary(sceneCopy);
        }

        public static void LoadShader()
        {
            if (_mat != null || _triedLoad) return;
            _triedLoad = true;
            try
            {
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "SSRSix", "Assets", "ssr");
                AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Plugin.MyLog.LogError("[SSRSix] Failed to load AssetBundle at " + bundlePath);
                    return;
                }
                _mat = bundle.LoadAsset<Material>("ssrMat");
                if (_mat == null)
                    Plugin.MyLog.LogError("[SSRSix] 'ssrMat' material not found in bundle.");
                else
                    Plugin.MyLog.LogInfo("[SSRSix] Material loaded.");
            }
            catch (System.Exception e)
            {
                Plugin.MyLog.LogError("[SSRSix] Shader load failed: " + e);
            }
        }
    }
}
