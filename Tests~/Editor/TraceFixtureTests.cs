#nullable enable
#if PLAYLOOP_DOTNET_STANDALONE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Playloop.Samples.TraceFixture;
using Playloop.Trace;

namespace Playloop.Tests
{
    /// <summary>
    /// Drives the Trace fixture's known path through a real
    /// <see cref="PlayloopClient"/> on a fake clock and audits every byte that
    /// reaches the wire. The committed golden file is what every engine's
    /// fixture must reproduce from the same route table.
    ///
    /// <para>
    /// Two runs in one session: run 0 walks the three rooms and ends with
    /// <c>death</c>, the clock keeps ticking with no state for two seconds,
    /// then run 1 opens with <c>Begin()</c>, walks a short way in <c>hall</c>
    /// and ends with <c>quit</c> before the session end.
    /// </para>
    /// </summary>
    [TestFixture]
    public class TraceFixtureTests
    {
        private const long BaseWallMs = 1758140000000L;
        private const int StepMs = 100;
        private const string UpdateGoldenEnv = "PLAYLOOP_UPDATE_TRACE_GOLDEN";

        private static readonly string[] KnownLabels =
        {
            TraceFixtureRoute.RoomHall, TraceFixtureRoute.RoomCrypt, TraceFixtureRoute.RoomVault,
            "move", "jump", "attack", TraceFixtureRoute.EntityKey, "xy", "death", "quit",
        };

        private sealed class Run
        {
            public List<JObject> Bodies = new List<JObject>();
            public List<JObject> ChunkEvents = new List<JObject>();
            public List<JObject> Chunks = new List<JObject>();
            public List<string> RawBodies = new List<string>();
        }

        private static Run _run = null!;

        [OneTimeSetUp]
        public void DriveOnce()
        {
            _run = DriveKnownPath().GetAwaiter().GetResult();
        }

        private static async Task<Run> DriveKnownPath()
        {
            var handler = MockHttpHandler.ReturnsJson("{\"sessionId\":\"sess_trace_fixture\"}");
            var client = new PlayloopClient(new PlayloopOptions
            {
                ApiKey = "pl_ik_test",
                BaseUrl = "https://api.test",
                Http = handler,
                HeartbeatSec = 0,
                AutoShutdownOnQuit = false,
                RetryAttempts = 1,
                EnableCrashReporting = false,
                // An explicit non-default slug, as a playtest build sets: the
                // "dev" default auto-derives, and with no engine present that
                // lands on "production" and turns Auto off.
                Environment = "playtest",
                Trace = new TraceOptions { Hz = TraceFixtureRoute.Hz, Plane = TracePlane.XY },
            });

            using (client)
            {
                client.Telemetry.ClearBuffer();
                client.Telemetry.StartSession(new Dictionary<string, object> { ["gameVersion"] = "trace-fixture" });
                Assert.IsTrue(client.Trace.IsActive, "Auto mode is on outside production");
                client.Trace.DefineActions(TraceFixtureRoute.Actions);

                string room = "";
                bool keyCleared = false;
                bool firstRunEnded = false;
                bool secondRunBegun = false;
                int endMs = (int)Math.Round(TraceFixtureRoute.Run2EndSec * 1000.0);
                for (int ms = 0; ms < endMs; ms += StepMs)
                {
                    double t = ms / 1000.0;

                    // Mirror the batcher's five-second flush cadence.
                    if (ms > 0 && ms % 5000 == 0) await client.Telemetry.FlushAsync();

                    if (TraceFixtureRoute.IsBetweenRuns(t))
                    {
                        // The run ends at its end time, then the game pushes
                        // nothing while the clock keeps ticking.
                        if (!firstRunEnded)
                        {
                            firstRunEnded = true;
                            client.Trace.End(TraceEndReason.Death);
                        }
                        client.Trace.Tick(t, BaseWallMs + ms);
                        continue;
                    }
                    if (TraceFixtureRoute.IsSecondRun(t) && !secondRunBegun)
                    {
                        secondRunBegun = true;
                        client.Trace.Begin();
                    }

                    string r = TraceFixtureRoute.RoomAt(t);
                    if (r != room)
                    {
                        room = r;
                        var b = TraceFixtureRoute.BoundsFor(r)!;
                        client.Trace.SetRoom(r, new TraceBounds(b[0], b[1], b[2], b[3]));
                    }

                    var (x, y) = TraceFixtureRoute.PositionAt(t);
                    client.Trace.SetPosition((float)x, (float)y, TraceFixtureRoute.FacingAt(t));

                    var (ax, ay) = TraceFixtureRoute.AxesAt(t);
                    client.Trace.SetInput(TraceFixtureRoute.ActionBitsAt(t), (float)ax, (float)ay);

                    if (TraceFixtureRoute.EntityAt(t, out var kx, out var ky))
                    {
                        client.Trace.SetEntity(TraceFixtureRoute.EntityKey, (float)kx, (float)ky);
                    }
                    else if (!keyCleared)
                    {
                        keyCleared = true;
                        client.Trace.ClearEntity(TraceFixtureRoute.EntityKey);
                    }

                    client.Trace.Tick(t, BaseWallMs + ms);
                }

                client.Trace.End(TraceEndReason.Quit);
                Assert.AreEqual(TraceStatus.BetweenRuns, client.Trace.Status);
                await client.Telemetry.EndSessionAsync();
            }

            var run = new Run();
            foreach (var call in handler.Calls)
            {
                if (call.Method != "POST" || !call.Url.EndsWith("/api/telemetry", StringComparison.Ordinal)) continue;
                var raw = Encoding.UTF8.GetString(call.JsonBody!);
                run.RawBodies.Add(raw);
                var body = JObject.Parse(raw);
                run.Bodies.Add(body);
                foreach (var ev in (JArray)body["events"]!)
                {
                    if (ev["name"]!.Value<string>() == "trace_chunk")
                    {
                        run.ChunkEvents.Add((JObject)ev);
                        run.Chunks.Add((JObject)ev["data"]!);
                    }
                }
            }
            return run;
        }

        // ───────────────────────── the chunks ─────────────────────────

        [Test]
        public void ExactlyFourChunks_SeqCountsAcrossRuns_SegPerRun()
        {
            Assert.AreEqual(4, _run.Chunks.Count, "four trace_chunk events across the POST bodies");
            int[] segs = { 0, 0, 0, 1 };
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(i, _run.Chunks[i]["seq"]!.Value<int>(), $"seq of chunk {i}");
                Assert.AreEqual(segs[i], _run.Chunks[i]["seg"]!.Value<int>(), $"seg of chunk {i}");
            }
        }

        [Test]
        public void ChunkKeys_InWireOrder_SegAlwaysWritten()
        {
            foreach (var chunk in _run.Chunks)
            {
                var keys = chunk.Properties().Select(p => p.Name).ToList();
                Assert.AreEqual(new[] { "v", "seq", "seg", "t0", "hz", "plane" }, keys.Take(6).ToArray(), $"key order of seq {chunk["seq"]}");
            }
        }

        [Test]
        public void EveryChunk_CarriesVersionRateAndPlane()
        {
            foreach (var chunk in _run.Chunks)
            {
                Assert.AreEqual(1, chunk["v"]!.Value<int>());
                Assert.AreEqual(TraceFixtureRoute.Hz, chunk["hz"]!.Value<int>());
                Assert.AreEqual(TraceFixtureRoute.Plane, chunk["plane"]!.Value<string>());
            }
        }

        [Test]
        public void EventTimestamp_IsTheChunkT0()
        {
            for (int i = 0; i < _run.ChunkEvents.Count; i++)
            {
                long ts = _run.ChunkEvents[i]["timestamp"]!.Value<long>();
                long t0 = _run.Chunks[i]["t0"]!.Value<long>();
                Assert.AreEqual(t0, ts, $"chunk {i}: event timestamp equals t0");
            }
            Assert.AreEqual(BaseWallMs, _run.Chunks[0]["t0"]!.Value<long>());
            Assert.AreEqual(BaseWallMs + 5000, _run.Chunks[1]["t0"]!.Value<long>());
            Assert.AreEqual(BaseWallMs + 10000, _run.Chunks[2]["t0"]!.Value<long>());
            Assert.AreEqual(BaseWallMs + 15000, _run.Chunks[3]["t0"]!.Value<long>(), "run 1 re-anchors at the respawn");
            Assert.AreEqual(0L, Samples(_run.Chunks[3])[0][0]!.Value<long>(), "run 1's first sample is dt 0");
        }

        [Test]
        public void ActionsRideSeqZeroOnly()
        {
            var acts = _run.Chunks[0]["acts"]!.Values<string>().ToArray();
            CollectionAssert.AreEqual(TraceFixtureRoute.Actions, acts);
            Assert.IsNull(_run.Chunks[1]["acts"], "seq 1 does not repeat acts");
            Assert.IsNull(_run.Chunks[2]["acts"], "seq 2 does not repeat acts");
            Assert.IsNull(_run.Chunks[3]["acts"], "a new run does not repeat acts");
        }

        [Test]
        public void RoomTables_MatchEntryOrderWithDeclaredBounds()
        {
            AssertRooms(_run.Chunks[0], (TraceFixtureRoute.RoomHall, TraceFixtureRoute.HallBounds), (TraceFixtureRoute.RoomCrypt, TraceFixtureRoute.CryptBounds));
            AssertRooms(_run.Chunks[1], (TraceFixtureRoute.RoomCrypt, TraceFixtureRoute.CryptBounds), (TraceFixtureRoute.RoomVault, TraceFixtureRoute.VaultBounds));
            AssertRooms(_run.Chunks[2], (TraceFixtureRoute.RoomVault, TraceFixtureRoute.VaultBounds));
            AssertRooms(_run.Chunks[3], (TraceFixtureRoute.RoomHall, TraceFixtureRoute.HallBounds));
        }

        [Test]
        public void SampleCounts_FiftyFiftyThirtyThirty()
        {
            Assert.AreEqual(50, Samples(_run.Chunks[0]).Count);
            Assert.AreEqual(50, Samples(_run.Chunks[1]).Count);
            Assert.AreEqual(30, Samples(_run.Chunks[2]).Count, "run 0's last chunk: 10.0 s to 12.9 s");
            Assert.AreEqual(30, Samples(_run.Chunks[3]).Count, "run 1: 15.0 s to 17.9 s, nothing sampled between runs");
            foreach (var chunk in _run.Chunks)
            {
                foreach (var row in Samples(chunk))
                {
                    Assert.AreEqual(8, row.Count, "every sample row is exactly eight numbers");
                    foreach (var cell in row)
                    {
                        Assert.That(cell.Type, Is.EqualTo(JTokenType.Integer).Or.EqualTo(JTokenType.Float), "sample cells are numbers");
                    }
                }
            }
        }

        [Test]
        public void SampleAt4800_IsInCrypt()
        {
            var chunk = _run.Chunks[0];
            var row = Samples(chunk).Single(r => r[0]!.Value<long>() == 4800);
            Assert.AreEqual(TraceFixtureRoute.RoomCrypt, RoomOf(chunk, row));
        }

        [Test]
        public void AttackBit_OnlyInsideItsWindow()
        {
            foreach (var chunk in _run.Chunks)
            {
                long chunkStart = chunk["t0"]!.Value<long>() - BaseWallMs;
                foreach (var row in Samples(chunk))
                {
                    long t = chunkStart + row[0]!.Value<long>();
                    bool attack = (row[5]!.Value<int>() & TraceFixtureRoute.BitAttack) != 0;
                    bool inWindow = t >= 6000 && t <= 6500;
                    Assert.AreEqual(inWindow, attack, $"attack bit at t={t} ms");
                }
            }
        }

        // The death spot is asserted as a literal on purpose: the route table
        // is the fixture's source of truth, and this test is the contract it
        // must keep. Moving the spot in the table goes red here and in the
        // golden, which is the point.
        private const double DeathSpotX = 50.00;
        private const double DeathSpotY = 5.00;

        [Test]
        public void RouteTable_KeepsTheContractedDeathSpot()
        {
            Assert.AreEqual(DeathSpotX, TraceFixtureRoute.DeathX, 0.0001);
            Assert.AreEqual(DeathSpotY, TraceFixtureRoute.DeathY, 0.0001);
        }

        [Test]
        public void FinalSample_IsAtTheDeathSpotInVaultAndStill()
        {
            var chunk = _run.Chunks[2];
            var row = Samples(chunk).Last();
            Assert.AreEqual(DeathSpotX, row[1]!.Value<double>(), 0.01);
            Assert.AreEqual(DeathSpotY, row[2]!.Value<double>(), 0.01);
            Assert.AreEqual(TraceFixtureRoute.RoomVault, RoomOf(chunk, row));
            Assert.AreEqual(0, row[5]!.Value<int>(), "no action bits once stopped");
            Assert.AreEqual(0.0, row[6]!.Value<double>(), 0.0001);
            Assert.AreEqual(0.0, row[7]!.Value<double>(), 0.0001);
        }

        [Test]
        public void EndBlock_DeathAtTheDeathSpot()
        {
            Assert.IsNull(_run.Chunks[0]["end"]);
            Assert.IsNull(_run.Chunks[1]["end"]);
            var end = (JObject?)_run.Chunks[2]["end"];
            Assert.IsNotNull(end, "the final chunk carries the end block");
            Assert.AreEqual("death", end!["reason"]!.Value<string>());
            Assert.AreEqual(DeathSpotX, end["x"]!.Value<double>(), 0.01);
            Assert.AreEqual(DeathSpotY, end["y"]!.Value<double>(), 0.01);
            var rooms = (JArray)_run.Chunks[2]["rooms"]!;
            Assert.AreEqual(TraceFixtureRoute.RoomVault, rooms[end["r"]!.Value<int>()]!["id"]!.Value<string>());
            var lastRow = Samples(_run.Chunks[2]).Last();
            Assert.AreEqual(lastRow[0]!.Value<long>(), end["dt"]!.Value<long>(), "end.dt is the last sample's dt");
            Assert.AreEqual(2900L, end["dt"]!.Value<long>(), "ms since seq 2's t0");
        }

        [Test]
        public void SecondRun_WalksHallStopsAndQuits()
        {
            var chunk = _run.Chunks[3];
            var rows = Samples(chunk);
            Assert.AreEqual(TraceFixtureRoute.StartX, rows[0][1]!.Value<double>(), 0.001, "respawn at the start");
            foreach (var row in rows)
            {
                long t = 15000 + row[0]!.Value<long>();
                bool stopped = t >= 17250;
                Assert.AreEqual(TraceFixtureRoute.RoomHall, RoomOf(chunk, row));
                Assert.AreEqual(0, row[3]!.Value<int>(), $"facing at t={t} ms");
                Assert.AreEqual(stopped ? 0 : TraceFixtureRoute.BitMove, row[5]!.Value<int>(), $"action bits at t={t} ms");
                Assert.AreEqual(stopped ? 0.0 : 1.0, row[6]!.Value<double>(), 0.0001, $"axis x at t={t} ms");
                if (stopped) Assert.AreEqual(TraceFixtureRoute.Run2StopX, row[1]!.Value<double>(), 0.001);
            }
            Assert.AreEqual(0, ((JArray)chunk["e"]!).Count, "the key never comes back");
            Assert.AreEqual(0, ((JArray)chunk["ents"]!).Count);

            var end = (JObject?)chunk["end"];
            Assert.IsNotNull(end, "run 1's chunk carries its own end block");
            Assert.AreEqual("quit", end!["reason"]!.Value<string>());
            Assert.AreEqual(0, end["r"]!.Value<int>());
            Assert.AreEqual(TraceFixtureRoute.Run2StopX, end["x"]!.Value<double>(), 0.001);
            Assert.AreEqual(TraceFixtureRoute.LaneY, end["y"]!.Value<double>(), 0.001);
            Assert.AreEqual(2900L, end["dt"]!.Value<long>());
        }

        [Test]
        public void TwoEndBlocks_OnePerRun()
        {
            var ends = _run.Chunks.Where(c => c["end"] != null).Select(c => (c["seq"]!.Value<int>(), c["end"]!["reason"]!.Value<string>())).ToList();
            CollectionAssert.AreEqual(new[] { (2, "death"), (3, "quit") }, ends);
        }

        [Test]
        public void Entity_OncePerChunkThenDespawnedOnce()
        {
            var e0 = ((JArray)_run.Chunks[0]["e"]!).Cast<JArray>().ToList();
            Assert.AreEqual(1, e0.Count, "a static entity costs one row per chunk");
            Assert.AreEqual(TraceFixtureRoute.EntityKey, ((JArray)_run.Chunks[0]["ents"]!)[e0[0][1]!.Value<int>()]!.Value<string>());
            Assert.AreEqual(TraceFixtureRoute.KeyX, e0[0][2]!.Value<double>(), 0.01);
            Assert.AreEqual(TraceFixtureRoute.KeyY, e0[0][3]!.Value<double>(), 0.01);

            var e1 = ((JArray)_run.Chunks[1]["e"]!).Cast<JArray>().ToList();
            Assert.AreEqual(2, e1.Count, "seq 1: one position row, one despawn row");
            Assert.AreEqual(JTokenType.Null, e1[1][2]!.Type);
            Assert.AreEqual(JTokenType.Null, e1[1][3]!.Type);
            long despawnT = 5000 + Samples(_run.Chunks[1])[e1[1][0]!.Value<int>()][0]!.Value<long>();
            Assert.That(despawnT, Is.InRange(9750, 9900), "the key despawns at the first sample after 9.75 s");

            Assert.AreEqual(0, ((JArray)_run.Chunks[2]["e"]!).Count);
            Assert.AreEqual(0, ((JArray)_run.Chunks[2]["ents"]!).Count);
        }

        // ───────────────────────── the session ─────────────────────────

        [Test]
        public void SessionEndedFlush_CarriesTraceChunkCount()
        {
            var endBody = _run.Bodies.Single(b => b["sessionEnded"] != null && b["sessionEnded"]!.Value<bool>());
            // Top-level is the field the server reads on an append; the
            // metadata copy only counts when the session creates and ends in
            // one flush.
            Assert.AreEqual(4, endBody["traceChunks"]!.Value<int>());
            Assert.AreEqual(4, endBody["sessionMetadata"]!["traceChunks"]!.Value<int>());
        }

        [Test]
        public void SessionEnd_BetweenRuns_WritesNoFurtherChunk()
        {
            var endBody = _run.Bodies.Single(b => b["sessionEnded"] != null && b["sessionEnded"]!.Value<bool>());
            var chunkSeqs = ((JArray)endBody["events"]!)
                .Where(e => e["name"]!.Value<string>() == "trace_chunk")
                .Select(e => e["data"]!["seq"]!.Value<int>())
                .ToList();
            CollectionAssert.AreEqual(new[] { 3 }, chunkSeqs, "the end flush carries run 1's quit chunk and nothing after it");
        }

        [Test]
        public void NoStateEvent_WhenTheTraceIsOn()
        {
            foreach (var body in _run.Bodies)
            {
                foreach (var ev in (JArray)body["events"]!)
                {
                    Assert.AreNotEqual("trace_state", ev["name"]!.Value<string>());
                }
            }
        }

        // ───────────────────────── privacy by shape ─────────────────────────

        [Test]
        public void Bodies_CarryNoInputIdentifiers()
        {
            var forbidden = new Regex(@"\b(KeyCode|Space|Mouse|Button)\b", RegexOptions.IgnoreCase);
            foreach (var raw in _run.RawBodies)
            {
                Assert.IsFalse(forbidden.IsMatch(raw), "no key, mouse or button identifier reaches the wire");
            }
        }

        [Test]
        public void Chunks_CarryNoStringOutsideTheLabelSet()
        {
            foreach (var chunk in _run.Chunks)
            {
                foreach (var token in chunk.DescendantsAndSelf())
                {
                    if (token is JValue v && v.Type == JTokenType.String)
                    {
                        string s = v.Value<string>()!;
                        Assert.IsTrue(KnownLabels.Contains(s), $"unexpected string on the wire: \"{s}\"");
                        Assert.LessOrEqual(s.Length, 24);
                    }
                }
            }
        }

        // ───────────────────────── the golden ─────────────────────────

        [Test]
        public void KnownPath_MatchesTheCommittedGoldenBytes()
        {
            string path = GoldenPath();
            var sb = new StringBuilder();
            foreach (var chunk in _run.Chunks)
            {
                sb.Append(chunk.ToString(Formatting.None)).Append('\n');
            }
            byte[] actual = new UTF8Encoding(false).GetBytes(sb.ToString());

            // The golden is only ever written on purpose. A missing file is a
            // failure, not a reason to mint one: a test that regenerates its
            // own oracle can never go red.
            bool update = Environment.GetEnvironmentVariable(UpdateGoldenEnv) == "1";
            if (update)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, actual);
            }
            else if (!File.Exists(path))
            {
                Assert.Fail(
                    $"Trace golden is missing at {path}. It is committed on purpose; " +
                    $"to regenerate it deliberately, re-run with {UpdateGoldenEnv}=1 and commit the file.");
            }

            byte[] committed = File.ReadAllBytes(path);
            if (!committed.SequenceEqual(actual))
            {
                File.WriteAllBytes(path + ".actual", actual);
                Assert.Fail(
                    $"Trace golden differs from {path}. The actual bytes were written beside it as .actual; " +
                    $"if the route changed on purpose, re-run with {UpdateGoldenEnv}=1 and commit the file.");
            }
            Assert.AreEqual(committed.Length, actual.Length);
        }

        // ───────────────────────── helpers ─────────────────────────

        private static string GoldenPath([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(thisFile)!, "fixtures", "trace-known-path.json");

        private static List<JArray> Samples(JObject chunk)
            => ((JArray)chunk["s"]!).Cast<JArray>().ToList();

        private static string RoomOf(JObject chunk, JArray row)
        {
            int r = row[4]!.Value<int>();
            return ((JArray)chunk["rooms"]!)[r]!["id"]!.Value<string>()!;
        }

        private static void AssertRooms(JObject chunk, params (string id, float[] bounds)[] expected)
        {
            var rooms = ((JArray)chunk["rooms"]!).Cast<JObject>().ToList();
            Assert.AreEqual(expected.Length, rooms.Count, $"room table of seq {chunk["seq"]}");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].id, rooms[i]["id"]!.Value<string>());
                var b = rooms[i]["b"]!.Values<double>().ToArray();
                Assert.AreEqual(4, b.Length);
                for (int k = 0; k < 4; k++) Assert.AreEqual(expected[i].bounds[k], b[k], 0.001);
            }
        }
    }
}
#endif
