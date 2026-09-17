#nullable enable
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Playloop.Sessions
{
    /// <summary>One insight surfaced by Playloop from a playtest session.</summary>
    public sealed class Insight
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("sessionId")] public string SessionId { get; set; } = "";
        [JsonProperty("type")] public string Type { get; set; } = "";
        [JsonProperty("sentiment")] public string Sentiment { get; set; } = "neutral";
        [JsonProperty("confidence")] public double Confidence { get; set; }
        [JsonProperty("summary")] public string Summary { get; set; } = "";
        [JsonProperty("quote")] public string? Quote { get; set; }
        [JsonProperty("startSec")] public double? StartSec { get; set; }
        [JsonProperty("endSec")] public double? EndSec { get; set; }
        [JsonProperty("tags")] public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>One ingested playtest session, plus its insights.</summary>
    public sealed class Session
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("gameId")] public string GameId { get; set; } = "";
        [JsonProperty("userId")] public string UserId { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("testerHandle")] public string? TesterHandle { get; set; }
        [JsonProperty("source")] public string Source { get; set; } = "manual";
        [JsonProperty("status")] public string Status { get; set; } = "pending";
        [JsonProperty("recordedAt")] public long RecordedAt { get; set; }
        [JsonProperty("createdAt")] public long CreatedAt { get; set; }
        [JsonProperty("durationSec")] public int? DurationSec { get; set; }
        [JsonProperty("insights")] public List<Insight> Insights { get; set; } = new List<Insight>();
    }

    /// <summary>Options for <see cref="SessionsApi.IngestAsync(byte[], string, IngestOptions, System.Threading.CancellationToken)"/>.</summary>
    public sealed class IngestOptions
    {
        public string Game { get; set; } = "";
        public string? Title { get; set; }
        public string? TesterHandle { get; set; }
        public string? Source { get; set; }
        public string? FileName { get; set; }
    }

    /// <summary>Options for <see cref="SessionsApi.ListAsync"/>.</summary>
    public sealed class ListSessionsOptions
    {
        public int? Limit { get; set; }
        public int? Offset { get; set; }
        public string? Game { get; set; }
    }
}
