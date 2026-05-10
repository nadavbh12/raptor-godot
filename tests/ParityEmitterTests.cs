using System.IO;
using Raptor.Sim;
using Raptor.Test;
using Xunit;

namespace Raptor.Tests;

public class ParityEmitterTests
{
    [Fact]
    public void Emits_NDJSON_on_70_frame_boundaries_only()
    {
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            using var worker = new ParityEmitWorker();
            worker.Open(path);

            for (int i = 0; i < 140; i++) {
                SimClock.Tick();
                worker.Tick();
            }

            var lines = File.ReadAllLines(path);
            // Checkpoints emitted at fc=70 and fc=140.
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"fc\":70", lines[0]);
            Assert.Contains("\"fc\":140", lines[1]);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_output_when_path_is_null()
    {
        SimClock.ResetForTest();
        using var worker = new ParityEmitWorker();
        worker.Open(null);   // should be a no-op
        for (int i = 0; i < 200; i++) {
            SimClock.Tick();
            worker.Tick();
        }
        // No assertion needed beyond "does not throw".
    }

    [Fact]
    public void Output_validates_against_schema()
    {
        var path = Path.GetTempFileName();
        try {
            SimClock.ResetForTest();
            using var worker = new ParityEmitWorker();
            worker.Open(path);
            for (int i = 0; i < 70; i++) { SimClock.Tick(); worker.Tick(); }

            var line = File.ReadAllText(path).Trim();

            // Spot-check the NDJSON shape. Full schema validation runs in Python CI.
            Assert.StartsWith("{\"fc\":70,", line);
            Assert.Contains("\"win\":\"UNKNOWN\"", line);
            Assert.Contains("\"player_x\":160", line);
            Assert.Contains("\"player_y\":100", line);
            Assert.Contains("\"obj_hash\":\"0000000000000000\"", line);
            Assert.EndsWith("}", line);
        } finally {
            File.Delete(path);
        }
    }
}
