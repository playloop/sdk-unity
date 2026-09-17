#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Playloop;
using Playloop.Trace;
using UnityEngine;

namespace Playloop.Samples.TraceFixture
{
    /// <summary>
    /// A tiny scripted game that walks a known path so the Trace can be
    /// audited end to end. Drop it on an empty GameObject in an empty scene
    /// and press Play: it builds its own scene (three colored quads for the
    /// rooms, a capsule for the player, a small cube for the key), starts a
    /// session, walks <see cref="TraceFixtureRoute"/> on the unscaled clock,
    /// ends the Trace with <c>Death</c> at (50, 5) in <c>vault</c>, and ends
    /// the session. Open the session on the dashboard and Playback should draw
    /// that route with the end marker on that spot.
    /// </summary>
    public sealed class PlayloopTraceFixture : MonoBehaviour
    {
        [Header("Playloop")]
        [Tooltip("Your game's ingest key. Copy it from the game's Connections page.")]
        public string ApiKey = "pl_ik_...";

        [Tooltip("Editor and development-build sends are suppressed by default. Keep this on to verify the fixture in the editor.")]
        public bool SendInEditor = true;

        [Tooltip("Environment slug for the fixture session. Keep it 'dev' so the run is easy to filter out.")]
        public string Environment = "dev";

        private PlayloopClient? _client;
        private Transform? _player;
        private Transform? _key;
        private string _room = "";
        private bool _keyCleared;
        private double _startSec;
        private bool _done;

        private void Start()
        {
            BuildScene();

            _client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = ApiKey,
                SendInEditor = SendInEditor,
                Environment = Environment,
                Trace = new TraceOptions
                {
                    Mode = TraceMode.On,
                    Hz = TraceFixtureRoute.Hz,
                    Plane = TracePlane.XY,
                },
            });

            _client.Telemetry.StartSession(new Dictionary<string, object>
            {
                ["gameVersion"] = "trace-fixture",
                ["platform"] = Application.platform.ToString(),
            });
            _client.Trace.DefineActions(TraceFixtureRoute.Actions);
            _client.Trace.SetEntity(TraceFixtureRoute.EntityKey, (float)TraceFixtureRoute.KeyX, (float)TraceFixtureRoute.KeyY);
            _client.Telemetry.AutoBatch();

            _startSec = Time.unscaledTimeAsDouble;
            Apply(0.0);
        }

        private void Update()
        {
            if (_done || _client == null) return;
            double t = Time.unscaledTimeAsDouble - _startSec;
            Apply(t);

            if (t >= TraceFixtureRoute.EndSec)
            {
                _done = true;
                _client.Trace.End(TraceEndReason.Death);
                _ = EndSessionAsync();
            }
        }

        private void Apply(double t)
        {
            var client = _client;
            if (client == null) return;

            string room = TraceFixtureRoute.RoomAt(t);
            if (room != _room)
            {
                _room = room;
                var b = TraceFixtureRoute.BoundsFor(room);
                client.Trace.SetRoom(room, b == null ? (TraceBounds?)null : new TraceBounds(b[0], b[1], b[2], b[3]));
            }

            var (x, y) = TraceFixtureRoute.PositionAt(t);
            client.Trace.SetPosition((float)x, (float)y, TraceFixtureRoute.FacingAt(t));

            var (ax, ay) = TraceFixtureRoute.AxesAt(t);
            client.Trace.SetInput(TraceFixtureRoute.ActionBitsAt(t), (float)ax, (float)ay);

            if (_player != null) _player.position = new Vector3((float)x, (float)y, 0f);

            if (TraceFixtureRoute.EntityAt(t, out var kx, out var ky))
            {
                client.Trace.SetEntity(TraceFixtureRoute.EntityKey, (float)kx, (float)ky);
            }
            else if (!_keyCleared)
            {
                _keyCleared = true;
                client.Trace.ClearEntity(TraceFixtureRoute.EntityKey);
                if (_key != null) _key.gameObject.SetActive(false);
            }
        }

        private async Task EndSessionAsync()
        {
            var client = _client;
            if (client == null) return;
            try
            {
                await client.Telemetry.EndSessionAsync();
                Debug.Log("[Playloop] Trace fixture finished: the session ended at (50, 5) in vault. Open it on the dashboard to check Playback.");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Playloop] Trace fixture could not end the session: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            var client = _client;
            _client = null;
            client?.Dispose();
        }

        // ───────────────────────── scene ─────────────────────────

        private void BuildScene()
        {
            if (Camera.main == null)
            {
                var camGo = new GameObject("Trace Fixture Camera");
                camGo.tag = "MainCamera";
                var cam = camGo.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 9f;
                cam.transform.position = new Vector3(30f, 5f, -10f);
                cam.backgroundColor = new Color(0.07f, 0.08f, 0.09f);
                cam.clearFlags = CameraClearFlags.SolidColor;
            }

            Room("hall", TraceFixtureRoute.HallBounds, new Color(0.22f, 0.30f, 0.42f));
            Room("crypt", TraceFixtureRoute.CryptBounds, new Color(0.36f, 0.24f, 0.36f));
            Room("vault", TraceFixtureRoute.VaultBounds, new Color(0.24f, 0.38f, 0.30f));

            var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            player.transform.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            Paint(player, new Color(0.95f, 0.85f, 0.35f));
            _player = player.transform;

            var key = GameObject.CreatePrimitive(PrimitiveType.Cube);
            key.name = "Key";
            key.transform.position = new Vector3((float)TraceFixtureRoute.KeyX, (float)TraceFixtureRoute.KeyY, 0f);
            key.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
            Paint(key, new Color(0.95f, 0.55f, 0.25f));
            _key = key.transform;
        }

        private static void Room(string id, float[] b, Color color)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = $"Room {id}";
            float w = b[2] - b[0];
            float h = b[3] - b[1];
            quad.transform.position = new Vector3(b[0] + w / 2f, b[1] + h / 2f, 1f);
            quad.transform.localScale = new Vector3(w - 0.2f, h - 0.2f, 1f);
            Paint(quad, color);
        }

        private static void Paint(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            renderer.material.color = color;
        }
    }
}
