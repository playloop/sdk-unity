#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Playloop;
using Playloop.Sessions;
using UnityEngine;

namespace PlayloopSamples.BasicUsage
{
    /// <summary>
    /// Minimal MonoBehaviour that ingests a session file, prints the resulting
    /// insights, and streams a few telemetry events on a background batch task.
    ///
    /// Drop this script on any GameObject, fill in the inspector fields, hit Play.
    /// </summary>
    public sealed class PlayloopBasicUsage : MonoBehaviour
    {
        [SerializeField] private string _apiKey = "pl_ik_replace_me";
        [SerializeField] private string _game = "my-game";

        [Tooltip("Path under Application.persistentDataPath. Editor: drop a test file there.")]
        [SerializeField] private string _sessionFileName = "session.mp4";

        private PlayloopClient? _client;

        private async void Start()
        {
            _client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = _apiKey,
            });

            // Ingest a session
            try
            {
                var path = Path.Combine(Application.persistentDataPath, _sessionFileName);
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"Drop a test recording at {path} to exercise IngestAsync.");
                }
                else
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    Session session = await _client.Sessions.IngestAsync(bytes, _game, new IngestOptions
                    {
                        Title = $"Sample session: {System.DateTime.Now:O}",
                        FileName = _sessionFileName,
                    });
                    Debug.Log($"[Playloop] Got session {session.Id} with {session.Insights.Count} insights.");
                }
            }
            catch (PlayloopException ex)
            {
                Debug.LogError($"[Playloop] Ingest failed ({ex.Status}): {ex.Message}");
            }

            // Start the auto-batch loop and fire a couple of events
            _client.Telemetry.AutoBatch();

            _client.Telemetry.Track("scene_loaded", new Dictionary<string, object>
            {
                { "scene", SceneManagerName() },
                { "build", Application.version },
            });

            _client.Telemetry.Track("device", new Dictionary<string, object>
            {
                { "model", SystemInfo.deviceModel },
                { "platform", Application.platform.ToString() },
            });
        }

        private async void OnDestroy()
        {
            if (_client == null) return;
            await _client.Telemetry.StopAutoBatchAsync();
            // Make sure the tail events go out before tearing the client down.
            try { await _client.Telemetry.FlushAsync(); } catch { /* swallow on shutdown */ }
            _client.Dispose();
            _client = null;
        }

        private static string SceneManagerName()
        {
            return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        }
    }
}
