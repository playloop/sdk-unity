#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using UnityEngine;

namespace Playloop.Trace
{
    /// <summary>
    /// Hidden MonoBehaviour that pumps <see cref="TraceApi.Tick"/> from
    /// <c>Update</c> on the unscaled clock. Spawned by
    /// <c>Telemetry.AutoBatch()</c> next to the auto-instrument object and
    /// torn down with it. Never <c>FixedUpdate</c>, never <c>OnGUI</c>: one
    /// tick per rendered frame, so a long frame shows up as a large gap in
    /// the samples instead of a burst.
    /// </summary>
    internal sealed class PlayloopTraceDriver : MonoBehaviour
    {
        private TraceApi? _trace;

        internal void Attach(TraceApi trace)
        {
            _trace = trace;
        }

        private void Update()
        {
            var trace = _trace;
            if (trace == null) return;
            try { trace.Tick(Time.unscaledTimeAsDouble); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Playloop] Trace tick failed: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            _trace = null;
        }
    }
}
#endif
