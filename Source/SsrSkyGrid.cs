using UnityEngine;

namespace SSRSix.Source
{
    // Roof-occlusion grid for the sky-map reflection fallback: a small world-anchored grid of upward
    // physics rays around the player ("is there a roof over this spot?"), refreshed a few cells per
    // frame. Reflecting surfaces look up THEIR cell, so an outdoor puddle seen from a doorway keeps its
    // sky while the indoor floor next to it doesn't — the per-surface generalization of the single
    // camera-up ray, at ~8 raycasts/frame. This is the runtime stand-in for what big engines get from
    // baked local reflection probes / sky-occlusion data, which EFT doesn't give us.
    internal static class SsrSkyGrid
    {
        private const int N = 8;            // N x N cells (must match SSR_SKYGRID_N in the shader)
        private const float CELL = 8f;      // metres per cell -> 64x64m coverage centred on the player
        private const int RAYS_PER_FRAME = 8;
        private const float RAY_LEN = 80f;

        private static readonly float[] _open = new float[N * N];   // 1 = sky above, 0 = roofed
        private static readonly bool[] _known = new bool[N * N];
        private static float _originX, _originZ;                    // world xz of cell (0,0) corner
        private static bool _originValid;
        private static int _cursor;

        public static void Clear()
        {
            _originValid = false;
            _cursor = 0;
            for (int i = 0; i < N * N; i++) { _open[i] = 1f; _known[i] = false; }
        }

        public static void Update(Vector3 camPos)
        {
            // World-snapped origin so cells are STABLE as the player moves (a surface keeps its cell).
            float ox = (Mathf.Floor(camPos.x / CELL) - N / 2) * CELL;
            float oz = (Mathf.Floor(camPos.z / CELL) - N / 2) * CELL;
            if (!_originValid || ox != _originX || oz != _originZ)
            {
                // Shift: carry over the overlapping cells, mark newcomers unknown (they answer with the
                // nearest known value until their ray fires — a few frames at most).
                int dx = _originValid ? Mathf.RoundToInt((ox - _originX) / CELL) : N;
                int dz = _originValid ? Mathf.RoundToInt((oz - _originZ) / CELL) : N;
                var open = new float[N * N];
                var known = new bool[N * N];
                for (int z = 0; z < N; z++)
                for (int x = 0; x < N; x++)
                {
                    int sx = x + dx, sz = z + dz;
                    if (sx >= 0 && sx < N && sz >= 0 && sz < N)
                    { open[z * N + x] = _open[sz * N + sx]; known[z * N + x] = _known[sz * N + sx]; }
                    else { open[z * N + x] = 1f; known[z * N + x] = false; }
                }
                System.Array.Copy(open, _open, N * N);
                System.Array.Copy(known, _known, N * N);
                _originX = ox; _originZ = oz; _originValid = true;
            }

            // Round-robin refresh: unknown cells first, then steady re-verification.
            int fired = 0;
            for (int scanned = 0; scanned < N * N && fired < RAYS_PER_FRAME; scanned++)
            {
                int i = _cursor;
                _cursor = (_cursor + 1) % (N * N);
                if (_known[i] && fired > 0) continue;   // spend spare budget on re-checks, priority to unknowns
                int cx = i % N, cz = i / N;
                Vector3 p = new Vector3(_originX + (cx + 0.5f) * CELL, camPos.y + 1f,
                                        _originZ + (cz + 0.5f) * CELL);
                bool roofed = Physics.Raycast(p, Vector3.up, RAY_LEN,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                _open[i] = roofed ? 0f : 1f;
                _known[i] = true;
                fired++;
            }
        }

        public static void Apply(Material mat)
        {
            mat.SetFloatArray("_SsrSkyGrid", _open);
            mat.SetVector("_SsrSkyGridParams", new Vector4(_originX, _originZ, 1f / CELL, N));
        }
    }
}
