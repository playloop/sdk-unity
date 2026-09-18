#nullable enable
#if UNITY_EDITOR && !PLAYLOOP_DOTNET_STANDALONE
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Playloop.Trace;

namespace Playloop.Tests
{
    /// <summary>
    /// The quit hook (Application.quitting, and ExitingPlayMode in the
    /// editor) with the SDK's default Unity transport, against a real local
    /// HTTP server. The hook blocks the main thread while the session end
    /// runs on a worker, and a UnityWebRequest can only be created on the
    /// main thread, so this is the one path the mock-transport tests cannot
    /// see.
    /// </summary>
    [TestFixture]
    public class QuitHookTests
    {
        [Test]
        public void QuitHook_DeliversTheSessionEndAndTheTraceEnd()
        {
            int port;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var received = new List<string>();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var serve = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await listener.GetContextAsync(); }
                    catch { break; }
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream)) body = reader.ReadToEnd();
                    lock (received) received.Add(ctx.Request.Url.AbsolutePath + " " + body);
                    var bytes = Encoding.UTF8.GetBytes("{\"sessionId\":\"s_quit\"}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
            });

            try
            {
                var client = new PlayloopClient(new PlayloopOptions
                {
                    ApiKey = "pl_ik_test",
                    BaseUrl = $"http://127.0.0.1:{port}",
                    SendInEditor = true,
                    Environment = "playtest",
                    HeartbeatSec = 0,
                    RetryAttempts = 1,
                    EnableCrashReporting = false,
                    AutoShutdownOnQuit = true,
                    Trace = new TraceOptions { Mode = TraceMode.On },
                });
                client.Telemetry.StartSession();
                client.Trace.SetPosition(1f, 2f);
                client.Trace.Tick(0.0);
                client.Trace.Tick(0.1);

                client.OnApplicationQuitting();
            }
            finally
            {
                listener.Stop();
                serve.Wait(2000);
            }

            string? end = null;
            lock (received)
            {
                foreach (var r in received)
                {
                    if (r.StartsWith("/api/telemetry ") && r.Contains("\"sessionEnded\":true")) end = r;
                }
            }
            Assert.IsNotNull(end, "the quit hook's final flush reaches the server with sessionEnded");
            StringAssert.Contains("\"trace_chunk\"", end);
            StringAssert.Contains("\"end\":{\"reason\":\"quit\"", end);
        }
    }
}
#endif
