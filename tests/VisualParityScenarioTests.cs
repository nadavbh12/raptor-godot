using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Raptor.Tests;

public class VisualParityScenarioTests
{
    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string PathInRepo(params string[] parts) =>
        Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());

    [Fact]
    public void Coverage_matrix_records_initial_c_source_modules()
    {
        var path = PathInRepo("tests", "parity", "scenarios", "coverage.md");
        var text = File.ReadAllText(path);

        foreach (var module in new[]
        {
            "SHOTS.C",
            "ESHOT.C",
            "ENEMY.C",
            "TILE.C",
            "BONUS.C",
            "OBJECTS.C",
            "RAP.C",
            "WINDOWS.C",
            "INTRO.C"
        })
        {
            Assert.Contains(module, text);
        }
    }

    [Fact]
    public void Scenario_manifest_is_valid_and_has_matching_coverage_rows()
    {
        var manifestPath = PathInRepo("tests", "parity", "scenarios", "scenarios.json");
        var coveragePath = PathInRepo("tests", "parity", "scenarios", "coverage.md");
        var manifest = JsonSerializer.Deserialize<ScenarioManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest!.Scenarios);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var coverage = File.ReadAllText(coveragePath);
        foreach (var scenario in manifest.Scenarios)
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Id));
            Assert.True(ids.Add(scenario.Id), $"duplicate scenario id: {scenario.Id}");
            Assert.Contains($"`{scenario.Id}`", coverage);
            Assert.NotEmpty(scenario.SourceRefs);
            Assert.NotEmpty(scenario.RequiredDumps);
            Assert.NotEmpty(scenario.VisualRegions);
            Assert.NotEmpty(scenario.Detectors);
            Assert.NotEmpty(scenario.Acceptance);

            // Playthrough scripts for parity scenarios live in the companion
            // dosraptor repo, not here, and are not published. An outside
            // contributor legitimately cannot obtain them, so only assert the
            // script exists when that sibling checkout is actually present --
            // otherwise `dotnet test` fails on something unobtainable. When the
            // reference repo IS present, a missing script is a real problem and
            // still fails.
            var scriptPath = PathInRepo(scenario.Playthrough);
            if (!File.Exists(scriptPath))
            {
                var referenceRepo = Path.GetFullPath(Path.Combine(RepoRoot(), "..", "dosraptor"));
                if (Directory.Exists(referenceRepo))
                {
                    scriptPath = Path.Combine(referenceRepo, scenario.Playthrough);
                    Assert.True(File.Exists(scriptPath),
                        $"missing playthrough for {scenario.Id}: {scenario.Playthrough} "
                        + $"(not in this repo, and absent from {referenceRepo})");
                }
            }

            foreach (var dump in scenario.RequiredDumps)
                Assert.Contains(dump, KnownDumpCategories);
        }
    }

    [Fact]
    public void Visual_audit_script_generates_findings_crops_and_html_for_paired_frames()
    {
        using var temp = new TempDir();
        var pairs = Path.Combine(temp.Path, "pairs");
        Directory.CreateDirectory(Path.Combine(pairs, "c"));
        Directory.CreateDirectory(Path.Combine(pairs, "g"));
        WritePpm(Path.Combine(pairs, "c", "0000.png"), redSquareX: 20);
        WritePpm(Path.Combine(pairs, "g", "0000.png"), redSquareX: 28);

        var output = Path.Combine(temp.Path, "report");
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(PathInRepo("tests", "visual_audit.py"));
        psi.ArgumentList.Add("--pairs-dir");
        psi.ArgumentList.Add(pairs);
        psi.ArgumentList.Add("--out-dir");
        psi.ArgumentList.Add(output);
        psi.ArgumentList.Add("--scenario-id");
        psi.ArgumentList.Add("unit_probe");

        using var process = Process.Start(psi)!;
        Assert.True(process.WaitForExit(10_000));
        var stderr = process.StandardError.ReadToEnd();
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "findings.json")), stderr);
        Assert.True(File.Exists(Path.Combine(output, "index.html")), stderr);
        Assert.True(Directory.GetFiles(Path.Combine(output, "crops"), "*.png").Length > 0, stderr);

        var findingsJson = File.ReadAllText(Path.Combine(output, "findings.json"));
        Assert.Contains("\"scenario_id\": \"unit_probe\"", findingsJson);
        Assert.Contains("\"classification\": \"visual-diff\"", findingsJson);
    }

    private static readonly HashSet<string> KnownDumpCategories = new(StringComparer.Ordinal)
    {
        "parity",
        "position",
        "bullet",
        "frames",
        "labels"
    };

    private static void WritePpm(string path, int redSquareX)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("P3");
        writer.WriteLine("32 24");
        writer.WriteLine("255");
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var red = x >= redSquareX && x < redSquareX + 4 && y >= 8 && y < 12;
                writer.Write(red ? "255 0 0 " : "0 0 0 ");
            }
            writer.WriteLine();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "raptor_visual_audit_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ScenarioManifest
    {
        public List<Scenario> Scenarios { get; set; } = [];
    }

    private sealed class Scenario
    {
        public string Id { get; set; } = "";

        [JsonPropertyName("playthrough")]
        public string Playthrough { get; set; } = "";

        [JsonPropertyName("source_refs")]
        public List<string> SourceRefs { get; set; } = [];

        [JsonPropertyName("required_dumps")]
        public List<string> RequiredDumps { get; set; } = [];

        [JsonPropertyName("visual_regions")]
        public List<string> VisualRegions { get; set; } = [];

        [JsonPropertyName("detectors")]
        public List<string> Detectors { get; set; } = [];

        [JsonPropertyName("acceptance")]
        public List<string> Acceptance { get; set; } = [];
    }
}
