using UnityEngine;

// Editor test driver for Hidden/SPTVR/SSRSix. Attach to a camera, assign a Material that uses the SSR shader,
// and tune live in the inspector. Mirrors what SsrRenderer feeds the shader in-game (mono instead of
// per-eye). The camera MUST use the DEFERRED rendering path — the tracer reads _CameraGBufferTexture1/2
// (spec/smoothness + world normals); in forward there is no G-buffer and everything renders black.
// Give the test scene some glossy materials (smoothness > the cutoff) or nothing will trace.
[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
[ImageEffectAllowedInSceneView]
public class SsrTester : MonoBehaviour
{
    public Material ssrMat;          // a Material whose shader is "Hidden/SPTVR/SSRSix"

    [Header("Look")]
    [Range(0, 2)] public float intensity = 1f;
    [Range(0, 1)] public float smoothnessCutoff = 0.6f;
    [Range(0.01f, 0.5f)] public float edgeFade = 0.1f;

    [Header("Quality")]
    [Range(8, 64)] public int steps = 24;
    public float maxDistance = 50f;
    public float thickness = 0.5f;
    public bool temporalJitter = true;

    [Header("Debug (matches ESsrDebugView)")]
    [Range(0, 6)] public int debugView = 0;

    private Camera cam;

    private void OnEnable()
    {
        cam = GetComponent<Camera>();
        cam.renderingPath = RenderingPath.DeferredShading;   // the shader needs the G-buffers
        cam.depthTextureMode |= DepthTextureMode.Depth;
    }

    [ImageEffectOpaque]
    private void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (ssrMat == null) { Graphics.Blit(src, dst); return; }

        cam.depthTextureMode |= DepthTextureMode.Depth;

        // Mono equivalent of the per-eye matrices the game feeds (GL-convention projection).
        Matrix4x4 view = cam.worldToCameraMatrix;
        Matrix4x4 vp = cam.projectionMatrix * view;
        ssrMat.SetMatrix("_SsrVP", vp);
        ssrMat.SetMatrix("_SsrInvVP", vp.inverse);
        ssrMat.SetVector("_SsrCamPos", cam.transform.position);
        Vector4 r2 = view.GetRow(2);
        ssrMat.SetVector("_SsrCamFwd", new Vector4(-r2.x, -r2.y, -r2.z, -r2.w));

        ssrMat.SetInt("_SsrSteps", Mathf.Max(1, steps));
        ssrMat.SetFloat("_SsrMaxDist", maxDistance);
        ssrMat.SetFloat("_SsrThickness", thickness);
        ssrMat.SetFloat("_SsrIntensity", intensity);
        ssrMat.SetFloat("_SsrSmoothCutoff", smoothnessCutoff);
        ssrMat.SetFloat("_SsrEdgeFade", edgeFade);
        ssrMat.SetFloat("_SsrJitterPhase", temporalJitter ? Mathf.Repeat(Time.frameCount * 0.6180339887f, 1f) : 0f);
        ssrMat.SetFloat("_SsrDebug", debugView);

        Graphics.Blit(src, dst, ssrMat, 0);   // full-res pass; the half-res path is exercised in-game
    }
}
