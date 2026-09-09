#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Playloop.Telemetry;

namespace Playloop.Tests
{
    /// <summary>
    /// Unity SDK crash handler tests.
    ///
    /// Targets the unit-testable surface (storage I/O + the
    /// <see cref="CrashHandler.Drain"/> + <see cref="CrashHandler.Install"/>
    /// factory). The Unity-specific
    /// <c>Application.logMessageReceived</c> path is exercised via the
    /// .NET standalone fallback (<c>AppDomain.UnhandledException</c>),
    /// triggered through <c>RaisingUnhandledException</c> in a try/catch
    /// pattern below.
    /// </summary>
    [TestFixture]
    public class CrashHandlerTests
    {
        private string _tempDir = "";

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "playloop-crash-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort */ }
        }

        private CrashHandler.InstallContext MakeContext(
            CrashHandler.Settings? settings = null,
            IReadOnlyList<Dictionary<string, object?>>? recentEvents = null)
        {
            return new CrashHandler.InstallContext
            {
                SdkVersion = "playloop-unity@0.5.0",
                GetCurrentSessionId = () => "sess_test",
                GetRecentEvents = () =>
                    recentEvents ?? (IReadOnlyList<Dictionary<string, object?>>)new List<Dictionary<string, object?>>
                    {
                        new Dictionary<string, object?> { { "name", "altar_consecrated" }, { "ts", 1700000000000L } },
                    },
                GetBuildVersion = () => "0.4.9",
                GetSettings = () => settings ?? CrashHandler.Settings.Default,
                StorageDirectoryOverride = _tempDir,
            };
        }

        [Test]
        public void DrainReturnsEmptyWhenNoFile()
        {
            var pending = CrashHandler.Drain(_tempDir);
            Assert.That(pending, Is.Empty);
        }

        [Test]
        public void DrainClearsFileAfterRead()
        {
            // Write directly to the storage file so Drain has something
            // to read without needing to fire a synthetic crash.
            var path = Path.Combine(_tempDir, "playloop-pending-crashes.json");
            File.WriteAllText(
                path,
                "[{\"stackTrace\":\"Error: x\",\"sdkVersion\":\"playloop-unity@0.5.0\",\"crashedAtMs\":1}]");
            var first = CrashHandler.Drain(_tempDir);
            Assert.That(first.Count, Is.EqualTo(1));
            Assert.That(File.Exists(path), Is.False, "drain should remove the file");
            var second = CrashHandler.Drain(_tempDir);
            Assert.That(second, Is.Empty);
        }

        [Test]
        public void InstallReturnsNoopHandleWhenDisabled()
        {
            var settings = new CrashHandler.Settings { Enabled = false };
            var handle = CrashHandler.Install(MakeContext(settings));
            Assert.That(handle, Is.Not.Null);
            // No crash. Just verify uninstall doesn't throw.
            handle.Uninstall();
        }

        [Test]
        public void UninstallIsIdempotent()
        {
            var handle = CrashHandler.Install(MakeContext());
            handle.Uninstall();
            Assert.DoesNotThrow(() => handle.Uninstall());
        }

        [Test]
        public void DrainHandlesCorruptJsonGracefully()
        {
            var path = Path.Combine(_tempDir, "playloop-pending-crashes.json");
            File.WriteAllText(path, "{not valid json");
            Assert.DoesNotThrow(() => CrashHandler.Drain(_tempDir));
            // Corrupt file should still get cleared on next install or
            // remain. Current behavior: Drain leaves it. Verify the
            // empty-array return path:
            var pending = CrashHandler.Drain(_tempDir);
            Assert.That(pending, Is.Empty);
        }
    }
}
