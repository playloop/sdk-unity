namespace Playloop
{
    /// <summary>
    /// Whether `await`s on the telemetry flush / HTTP / feedback path should
    /// capture and resume on the current <see cref="System.Threading.SynchronizationContext"/>.
    ///
    /// Why this exists:
    ///   A Unity WebGL build (without webGLThreadsSupport) is a single-threaded
    ///   WebAssembly process with NO functioning ThreadPool. A
    ///   `ConfigureAwait(false)` continuation is scheduled onto the default
    ///   TaskScheduler / ThreadPool, which is never pumped on WebGL, so any await
    ///   that suspends on a real network call (the UnityWebRequest TCS) is
    ///   orphaned: the request is sent and the server responds, but the C#
    ///   continuation never runs. In practice `FlushAsync` (and feedback submit)
    ///   never return, `_flushLock` is never released, and telemetry stops after
    ///   the first POST - the server then drops the session at its online window.
    ///   The only scheduler Unity actually pumps on WebGL is the per-frame
    ///   UnitySynchronizationContext, so on WebGL we must CAPTURE it (i.e.
    ///   ConfigureAwait(true)) and let continuations resume on the main loop.
    ///
    /// Off WebGL (desktop, editor, mobile, server) we keep ConfigureAwait(false)
    /// for the usual thread-pool offload - byte-for-byte the previous behavior.
    ///
    /// Used everywhere the literal `false` used to appear in the flush/HTTP path:
    /// `.ConfigureAwait(PlAwait.Continue)`.
    /// </summary>
    internal static class PlAwait
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        internal const bool Continue = true;
#else
        internal const bool Continue = false;
#endif
    }
}
