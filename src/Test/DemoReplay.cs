using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raptor.Test;

public sealed class DemoReplay
{
    public sealed class HeaderData
    {
        [JsonPropertyName("max_play")]
        public int MaxPlay { get; set; }

        [JsonPropertyName("demo_game")]
        public int DemoGame { get; set; }

        [JsonPropertyName("demo_wave")]
        public int DemoWave { get; set; }
    }

    public readonly record struct Frame(
        int B1,
        int B2,
        int B3,
        int B4,
        int Px,
        int Py,
        int PlayerPic);

    private sealed class ReplayJson
    {
        [JsonPropertyName("header")]
        public HeaderData Header { get; set; } = new();

        [JsonPropertyName("records")]
        public List<RecordJson> Records { get; set; } = new();
    }

    private sealed class RecordJson
    {
        [JsonPropertyName("b1")]
        public int B1 { get; set; }

        [JsonPropertyName("b2")]
        public int B2 { get; set; }

        [JsonPropertyName("b3")]
        public int B3 { get; set; }

        [JsonPropertyName("b4")]
        public int B4 { get; set; }

        [JsonPropertyName("px")]
        public int Px { get; set; }

        [JsonPropertyName("py")]
        public int Py { get; set; }

        [JsonPropertyName("playerpic")]
        public int PlayerPic { get; set; }
    }

    private readonly List<Frame> _records;

    public HeaderData Header { get; }
    public IReadOnlyList<Frame> Records => _records;

    private DemoReplay(HeaderData header, List<Frame> records)
    {
        Header = header;
        _records = records;
    }

    public static DemoReplay LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<ReplayJson>(stream)
                   ?? throw new InvalidDataException($"Invalid demo replay JSON: {path}");

        var records = new List<Frame>(data.Records.Count);
        foreach (var r in data.Records)
            records.Add(new Frame(r.B1, r.B2, r.B3, r.B4, r.Px, r.Py, r.PlayerPic));

        return new DemoReplay(data.Header, records);
    }
}
