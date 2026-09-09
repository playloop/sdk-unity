#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Playloop.Telemetry
{
    /// <summary>
    /// First-class crash reports.
    ///
    /// Hooks <c>Application.logMessageReceived</c> with
    /// <c>LogType.Exception</c> so every uncaught exception in user
    /// game code (or in the Unity runtime itself) gets persisted to
    /// disk. The dying process can't ship the report live, so we
    /// persist and let the NEXT session start flush the queue as
    /// <c>crash_reported</c> events.
    ///
    /// Storage: <c>Application.persistentDataPath/playloop-pending-crashes.json</c>.
    /// Capped at 64 KB total. Oldest crashes trim first so a
    /// crash-loop doesn't fill the user's storage.
    ///
    /// Settings: pulled from the per-game event-config endpoint on
    /// session start. When <c>crashesEnabled = false</c>,
    /// <see cref="Install"/> is a no-op AND queued crashes are dropped
    /// on next flush. The <c>crashRecentEventsCount</c> knob caps the
    /// in-memory ring; the <c>crashSampleRate</c> knob gates whether a
    /// given crash gets persisted.
    /// </summary>
    public static class CrashHandler
    {
        private const int MaxPersistedBytes = 64 * 1024;
        private const int MaxStackBytes = 12 * 1024;
        private const int MaxRecentEvents = 50;
        private const string StorageFileName = "playloop-pending-crashes.json";

        public sealed class Settings
        {
            public bool Enabled { get; set; } = true;
            public int RecentEventsCount { get; set; } = 5;
            public double SampleRate { get; set; } = 1.0;

            public static Settings Default => new Settings();
        }

        public sealed class PersistedCrash
        {
            [JsonProperty("crashedSessionId")] public string? CrashedSessionId { get; set; }
            [JsonProperty("stackTrace")] public string StackTrace { get; set; } = "";
            [JsonProperty("message")] public string? Message { get; set; }
            [JsonProperty("buildVersion")] public string? BuildVersion { get; set; }
            [JsonProperty("sdkVersion")] public string SdkVersion { get; set; } = "";
            [JsonProperty("platform")] public string Platform { get; set; } = "Unity";
            [JsonProperty("recentEvents")]
            public List<Dictionary<string, object?>> RecentEvents { get; set; } = new List<Dictionary<string, object?>>();
            [JsonProperty("crashedAtMs")] public long CrashedAtMs { get; set; }
        }

        public sealed class InstallContext
        {
            public string SdkVersion = "";
            public Func<string?> GetCurrentSessionId = () => null;
            public Func<IReadOnlyList<Dictionary<string, object?>>> GetRecentEvents
                = () => Array.Empty<Dictionary<string, object?>>();
            public Func<string?> GetBuildVersion = () => null;
            public Func<Settings> GetSettings = () => Settings.Default;
            public string? StorageDirectoryOverride;
        }

        public sealed class Handle
        {
            internal Action Uninstaller = () => { };
            public void Uninstall() => Uninstaller();
        }

        /// <summary>
        /// Install the crash trap. Returns a <see cref="Handle"/> whose
        /// <c>Uninstall()</c> removes the listener. Repeat calls are
        /// fine. Each returns its own handle and they all chain
        /// through to whatever was previously hooked.
        ///
        /// When <c>GetSettings().Enabled</c> is false at install time,
        /// the trap is NOT installed. Settings are also re-read on
        /// every fire so a flag flipped between launches takes effect.
        /// </summary>
        public static Handle Install(InstallContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var settings = ctx.GetSettings();
            if (!settings.Enabled)
            {
                return new Handle();
            }

            var storagePath = ResolveStoragePath(ctx.StorageDirectoryOverride);

#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            UnityEngine.Application.LogCallback handler = (logString, stackTrace, type) =>
            {
                if (type != UnityEngine.LogType.Exception && type != UnityEngine.LogType.Assert)
                    return;
                HandleCrash(ctx, storagePath, logString, stackTrace);
            };
            UnityEngine.Application.logMessageReceived += handler;
            var handle = new Handle();
            handle.Uninstaller = () =>
            {
                try { UnityEngine.Application.logMessageReceived -= handler; }
                catch { /* best-effort */ }
            };
            return handle;
#else
            // Non-Unity build (e.g. .NET standalone for tests). Hook
            // AppDomain.UnhandledException so the same flow runs.
            UnhandledExceptionEventHandler handler = (sender, args) =>
            {
                var exc = args.ExceptionObject as Exception;
                if (exc == null) return;
                HandleCrash(ctx, storagePath, exc.Message ?? "", exc.StackTrace ?? "");
            };
            AppDomain.CurrentDomain.UnhandledException += handler;
            var handle = new Handle();
            handle.Uninstaller = () =>
            {
                try { AppDomain.CurrentDomain.UnhandledException -= handler; }
                catch { /* best-effort */ }
            };
            return handle;
#endif
        }

        private static void HandleCrash(
            InstallContext ctx,
            string storagePath,
            string message,
            string stackTrace)
        {
            try
            {
                var live = ctx.GetSettings();
                if (!live.Enabled) return;
                if (live.SampleRate < 1.0)
                {
                    var rand = new Random();
                    if (rand.NextDouble() >= live.SampleRate) return;
                }

                if (string.IsNullOrEmpty(stackTrace) && string.IsNullOrEmpty(message))
                {
                    return;
                }

                var stack = stackTrace ?? "";
                if (stack.Length > MaxStackBytes) stack = stack.Substring(0, MaxStackBytes);
                var msg = message ?? "";
                if (msg.Length > 1000) msg = msg.Substring(0, 1000);

                var recentCount = Math.Max(0, Math.Min(MaxRecentEvents, live.RecentEventsCount));
                var recent = new List<Dictionary<string, object?>>();
                if (recentCount > 0)
                {
                    var all = ctx.GetRecentEvents();
                    var start = Math.Max(0, all.Count - recentCount);
                    for (var i = start; i < all.Count; i++) recent.Add(all[i]);
                }

                var crash = new PersistedCrash
                {
                    CrashedSessionId = ctx.GetCurrentSessionId(),
                    StackTrace = stack,
                    Message = string.IsNullOrEmpty(msg) ? null : msg,
                    BuildVersion = ctx.GetBuildVersion(),
                    SdkVersion = ctx.SdkVersion,
                    Platform = ResolvePlatformLabel(),
                    RecentEvents = recent,
                    CrashedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };

                var queue = ReadInternal(storagePath);
                queue.Add(crash);
                WriteInternal(storagePath, queue);
            }
            catch
            {
                // Never throw from a crash handler. The runtime is
                // already in trouble and a second-fault helps no one.
            }
        }

        /// <summary>
        /// Read + clear the persisted queue. Returns whatever was
        /// pending so the caller can convert each entry to a
        /// <c>crash_reported</c> event and push it into the telemetry
        /// buffer on session start.
        /// </summary>
        public static IReadOnlyList<PersistedCrash> Drain(string? storageDirectoryOverride = null)
        {
            var path = ResolveStoragePath(storageDirectoryOverride);
            var pending = ReadInternal(path);
            if (pending.Count == 0) return pending;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best-effort */ }
            return pending;
        }

        private static List<PersistedCrash> ReadInternal(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<PersistedCrash>();
                var raw = File.ReadAllText(path);
                var parsed = JsonConvert.DeserializeObject<List<PersistedCrash>>(raw);
                return parsed ?? new List<PersistedCrash>();
            }
            catch
            {
                return new List<PersistedCrash>();
            }
        }

        private static void WriteInternal(string path, List<PersistedCrash> crashes)
        {
            try
            {
                var serialized = JsonConvert.SerializeObject(crashes);
                // Trim oldest until under the quota so a crash-loop
                // can't fill the user's storage.
                while (crashes.Count > 0 && System.Text.Encoding.UTF8.GetByteCount(serialized) > MaxPersistedBytes)
                {
                    crashes.RemoveAt(0);
                    serialized = JsonConvert.SerializeObject(crashes);
                }
                File.WriteAllText(path, serialized);
            }
            catch { /* best-effort */ }
        }

        private static string ResolveStoragePath(string? overrideDir)
        {
            string baseDir;
            if (!string.IsNullOrEmpty(overrideDir))
            {
                baseDir = overrideDir!;
            }
            else
            {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
                baseDir = UnityEngine.Application.persistentDataPath;
#else
                baseDir = Path.Combine(Path.GetTempPath(), "playloop");
#endif
            }
            try
            {
                if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            }
            catch { /* best-effort */ }
            return Path.Combine(baseDir, StorageFileName);
        }

        private static string ResolvePlatformLabel()
        {
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
            return UnityEngine.Application.platform.ToString();
#else
            return $".NET/{Environment.OSVersion.Platform}";
#endif
        }
    }
}
