#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Newtonsoft.Json;
using Playloop.Sessions;

namespace Playloop.Tests
{
    [TestFixture]
    public class SessionsApiTests
    {
        private static readonly Session SampleSession = new Session
        {
            Id = "ses_1",
            GameId = "game_1",
            UserId = "user_1",
            Title = "sample",
            Source = "manual",
            Status = "analyzed",
            DurationSec = 120,
        };

        private static string SampleSessionJson => JsonConvert.SerializeObject(SampleSession);

        [Test]
        public async Task IngestAsync_Bytes_PostsMultipart()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);

            var bytes = new byte[] { 1, 2, 3, 4 };
            var session = await client.Sessions.IngestAsync(bytes, "necromancers-army", new IngestOptions
            {
                Title = "Boss-fight playtest #4",
                TesterHandle = "tester_07",
            });

            Assert.AreEqual("ses_1", session.Id);
            Assert.AreEqual(1, handler.Calls.Count);
            var call = handler.Calls[0];
            Assert.AreEqual("POST", call.Method);
            StringAssert.EndsWith("/api/sessions", call.Url);

            Assert.IsNotNull(call.FormFields);
            Assert.AreEqual("necromancers-army", call.FormFields!["game"]);
            Assert.AreEqual("Boss-fight playtest #4", call.FormFields["title"]);
            Assert.AreEqual("tester_07", call.FormFields["testerHandle"]);

            Assert.IsNotNull(call.Files);
            Assert.AreEqual(1, call.Files!.Count);
            Assert.AreEqual("session", call.Files[0].FieldName);
            CollectionAssert.AreEqual(bytes, call.Files[0].Content);
        }

        [Test]
        public void IngestAsync_RequiresGame()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await client.Sessions.IngestAsync(new byte[] { 0 }, ""));
        }

        [Test]
        public void IngestAsync_NullBytes_Throws()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await client.Sessions.IngestAsync((byte[])null!, "g"));
        }

        [Test]
        public async Task IngestAsync_Path_ReadsFromDisk()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("hello-from-disk"));
                var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
                using var client = PlayloopClientTests.NewClient(handler);
                await client.Sessions.IngestAsync(path, "g");

                Assert.AreEqual(1, handler.Calls.Count);
                CollectionAssert.AreEqual(
                    Encoding.UTF8.GetBytes("hello-from-disk"),
                    handler.Calls[0].Files![0].Content);
                Assert.AreEqual(Path.GetFileName(path), handler.Calls[0].Files![0].FileName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public async Task IngestAsync_ExplicitFilename_Wins()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Sessions.IngestAsync(new byte[] { 0 }, "g", new IngestOptions { FileName = "custom.mp4" });
            Assert.AreEqual("custom.mp4", handler.Calls[0].Files![0].FileName);
        }

        [Test]
        public async Task ListAsync_AppendsQueryParams()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Sessions.ListAsync(new ListSessionsOptions
            {
                Limit = 10,
                Offset = 20,
                Game = "g",
            });
            var url = handler.Calls[0].Url;
            StringAssert.Contains("limit=10", url);
            StringAssert.Contains("offset=20", url);
            StringAssert.Contains("game=g", url);
        }

        [Test]
        public async Task ListAsync_OmitsQueryWhenNoOptions()
        {
            var handler = MockHttpHandler.ReturnsJson("[]");
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Sessions.ListAsync();
            Assert.AreEqual("https://api.test.playloop.gg/api/sessions", handler.Calls[0].Url);
        }

        [Test]
        public void GetAsync_RequiresId()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.ThrowsAsync<ArgumentException>(async () => await client.Sessions.GetAsync(""));
        }

        [Test]
        public async Task GetAsync_UrlEncodesId()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Sessions.GetAsync("ses with space");
            Assert.AreEqual(
                "https://api.test.playloop.gg/api/sessions/ses%20with%20space",
                handler.Calls[0].Url);
        }

        [Test]
        public async Task AnalyzeAsync_PostsToCorrectUrl()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            await client.Sessions.AnalyzeAsync("ses_abc");
            Assert.AreEqual("POST", handler.Calls[0].Method);
            Assert.AreEqual(
                "https://api.test.playloop.gg/api/analyze/ses_abc",
                handler.Calls[0].Url);
        }

        [Test]
        public void AnalyzeAsync_RequiresId()
        {
            var handler = MockHttpHandler.ReturnsJson(SampleSessionJson);
            using var client = PlayloopClientTests.NewClient(handler);
            Assert.ThrowsAsync<ArgumentException>(async () => await client.Sessions.AnalyzeAsync(""));
        }
    }
}
