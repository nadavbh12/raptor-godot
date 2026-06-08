using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raptor.SimLint;

/// <summary>One flagged construct in a src/Sim file.</summary>
internal readonly record struct Violation(string File, int Line, string Rule, string Message);

/// <summary>
/// Semantic linter for the src/Sim discipline (CLAUDE.md rule #1, spec §4.2): no
/// engine frame-time, no engine RNG, no wall-clock in parity-affecting sim code.
/// It PARSES C# with Roslyn instead of grepping text, so the matches are real
/// constructs — the word "delta" in a comment or an unrelated variable named
/// `delta` (e.g. a UI volume adjustment) is no longer a false positive; only a
/// `_Process` override, a use of the frame timestep, `GD.Rand*`/`Mathf.Rand*`, or
/// `Time`/`OS` wall-clock calls are flagged. A line carrying the exact uppercase
/// marker <c>LINT-OK</c> is whitelisted (case-sensitive on purpose).
/// </summary>
internal static class Program
{
    // GD.Rand* / Mathf.Rand* (Randi/Randf/RandRange/Randomize/...) — engine RNG.
    // The sim must draw from the per-wave RandomNumberGenerator instance instead.
    private static readonly HashSet<string> RngReceivers = new() { "GD", "Mathf" };
    // Time. / OS. GetTicks*/GetUnix* — wall-clock time.
    private static readonly HashSet<string> ClockReceivers = new() { "Time", "OS" };

    internal static List<Violation> Analyze(string code, string path)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        string[] lines = code.Split('\n');
        var found = new List<Violation>();

        void Add(Location loc, string rule, string message)
        {
            int line = loc.GetLineSpan().StartLinePosition.Line + 1;
            // LINT-OK escape hatch on the violation's own source line.
            if (line - 1 < lines.Length && lines[line - 1].Contains("LINT-OK")) return;
            found.Add(new Violation(path, line, rule, message));
        }

        foreach (var m in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            string name = m.Identifier.Text;
            // No _Process override — sim ticks via _PhysicsProcess only.
            if (name == "_Process")
                Add(m.Identifier.GetLocation(), "process",
                    "_Process override — sim must tick via _PhysicsProcess");

            // Never read the frame timestep: flag references to a NAMED (non-discard)
            // parameter of _Process/_PhysicsProcess inside the body. A discarded `_`
            // timestep, or a `delta` that is merely an unrelated identifier elsewhere,
            // is clean.
            if ((name == "_Process" || name == "_PhysicsProcess") && m.Body is { } body)
            {
                foreach (var p in m.ParameterList.Parameters)
                {
                    string pn = p.Identifier.Text;
                    if (pn.Length == 0 || pn == "_") continue;
                    foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                        if (id.Identifier.Text == pn)
                            Add(id.GetLocation(), "delta",
                                $"reads the frame timestep '{pn}' — sim counts SimClock.Frame, it must not measure time");
                }
            }
        }

        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (ma.Expression is not IdentifierNameSyntax recv) continue;
            string r = recv.Identifier.Text, member = ma.Name.Identifier.Text;
            if (RngReceivers.Contains(r) && member.StartsWith("Rand", StringComparison.Ordinal))
                Add(ma.GetLocation(), "rng",
                    $"{r}.{member} — engine RNG is banned; use the per-wave RandomNumberGenerator");
            if (ClockReceivers.Contains(r) &&
                (member.StartsWith("GetTicks", StringComparison.Ordinal) ||
                 member.StartsWith("GetUnix", StringComparison.Ordinal)))
                Add(ma.GetLocation(), "clock", $"{r}.{member} — wall-clock time is banned in sim");
        }

        return found;
    }

    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--self-test") return SelfTest();

        string simDir = args.Length >= 1 ? args[0] : "src/Sim";
        if (!Directory.Exists(simDir))
        {
            Console.WriteLine($"[lint_sim] {simDir} not found");
            return 0;
        }

        var all = new List<Violation>();
        foreach (string file in Directory
                     .EnumerateFiles(simDir, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
            all.AddRange(Analyze(File.ReadAllText(file), file));

        foreach (var v in all)
            Console.WriteLine($"[lint_sim] FAIL: {v.File}:{v.Line}: [{v.Rule}] {v.Message}");

        if (all.Count > 0)
        {
            Console.WriteLine($"[lint_sim] {all.Count} violation(s)");
            return 1;
        }
        Console.WriteLine("[lint_sim] OK");
        return 0;
    }

    // Embedded fixtures so the analyzer is self-verifying. scripts/lint_sim.sh runs
    // `--self-test` before the real scan, so an analyzer regression fails CI loudly.
    private static int SelfTest()
    {
        (string code, int expect, string desc)[] cases =
        {
            ("class C { void M(){ /* the per-iter delta ramps */ int delta = 3; X += delta; } }",
                0, "'delta' in a comment + an unrelated local is clean"),
            ("class C { void Adjust(int delta){ V += delta; } }",
                0, "a non-_Process parameter named 'delta' is clean (not the timestep)"),
            ("class C : N { public override void _Process(double delta){ } }",
                1, "_Process override is flagged"),
            ("class C : N { public override void _PhysicsProcess(double _){ X(); } }",
                0, "_PhysicsProcess with a discarded timestep is clean"),
            ("class C : N { public override void _PhysicsProcess(double delta){ X += delta; } }",
                1, "reading the _PhysicsProcess timestep is flagged"),
            ("class C { void M(){ var r = GD.Randi(); GD.Randomize(); } }",
                2, "GD.Rand*/Randomize are flagged"),
            ("class C { void M(){ var r = _rng.Randi(); } }",
                0, "an instance .Randi() is clean (the allowed RNG instance)"),
            ("class C { void M(){ var t = Time.GetTicksMsec(); var u = OS.GetUnixTime(); } }",
                2, "Time/OS wall-clock is flagged"),
            ("class C : N { public override void _Process(double delta){ } // LINT-OK\n}",
                0, "LINT-OK on the line suppresses the violation"),
        };
        int failures = 0;
        foreach (var (code, expect, desc) in cases)
        {
            int got = Analyze(code, "selftest.cs").Count;
            bool ok = got == expect;
            if (!ok) failures++;
            Console.WriteLine($"[self-test] {(ok ? "ok  " : "FAIL")} expect {expect} got {got} — {desc}");
        }
        Console.WriteLine(failures == 0 ? "[self-test] all passed" : $"[self-test] {failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
