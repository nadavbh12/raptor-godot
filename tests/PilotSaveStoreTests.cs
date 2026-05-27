using System;
using System.Collections.Generic;
using System.IO;
using Raptor.Sim;
using Xunit;

namespace Raptor.Tests;

public class PilotSaveStoreTests
{
    [Fact]
    public void LoadAll_returns_all_present_pilots_in_slot_order()
    {
        using var dir = new TempDir();
        WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        WriteFakePilot(dir.Path, slot: 2, name: "BOB", callsign: "BEAR", idPic: 1, score: 25000);
        WriteFakePilot(dir.Path, slot: 5, name: "CAROL", callsign: "CAT", idPic: 3, score: 99999);

        List<PilotSaveSummary> pilots = PilotSaveStore.LoadAll(dir.Path);

        Assert.Equal(3, pilots.Count);
        Assert.Equal(0, pilots[0].Slot);
        Assert.Equal("ALICE", pilots[0].Name);
        Assert.Equal("ACE", pilots[0].Callsign);
        Assert.Equal(0, pilots[0].IdPic);
        Assert.Equal(1000u, pilots[0].Score);
        Assert.Equal(2, pilots[1].Slot);
        Assert.Equal("BOB", pilots[1].Name);
        Assert.Equal(5, pilots[2].Slot);
        Assert.Equal("CAROL", pilots[2].Name);
    }

    [Fact]
    public void LoadAll_returns_empty_list_when_no_pilot_files()
    {
        using var dir = new TempDir();
        Assert.Empty(PilotSaveStore.LoadAll(dir.Path));
    }

    [Fact]
    public void Save_writes_pilot_summary_readable_by_LoadAll()
    {
        using var dir = new TempDir();

        int slot = PilotSaveStore.Save(dir.Path, name: "TEST", callsign: "T1", idPic: 2, score: 0);

        Assert.Equal(0, slot);
        var pilots = PilotSaveStore.LoadAll(dir.Path);
        Assert.Single(pilots);
        Assert.Equal("TEST", pilots[0].Name);
        Assert.Equal("T1", pilots[0].Callsign);
        Assert.Equal(2, pilots[0].IdPic);
        Assert.Equal(0u, pilots[0].Score);
    }

    [Fact]
    public void Save_assigns_next_available_slot()
    {
        using var dir = new TempDir();
        WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        WriteFakePilot(dir.Path, slot: 1, name: "BOB", callsign: "BEAR", idPic: 1, score: 2000);

        int slot = PilotSaveStore.Save(dir.Path, name: "CAROL", callsign: "CAT", idPic: 3, score: 99);

        Assert.Equal(2, slot);
        Assert.True(File.Exists(Path.Combine(dir.Path, "CHAR0002.FIL")));
    }

    [Fact]
    public void Save_round_trips_through_decrypt()
    {
        using var dir = new TempDir();
        PilotSaveStore.Save(dir.Path, name: "ROUNDTRIP", callsign: "RT", idPic: 1, score: 12345);
        var pilots = PilotSaveStore.LoadAll(dir.Path);
        Assert.Single(pilots);
        Assert.Equal("ROUNDTRIP", pilots[0].Name);
        Assert.Equal("RT", pilots[0].Callsign);
        Assert.Equal(1, pilots[0].IdPic);
        Assert.Equal(12345u, pilots[0].Score);
    }

    [Fact]
    public void LoadAll_parses_full_PLAYEROBJ_header()
    {
        using var dir = new TempDir();
        WriteFakePilot(dir.Path, slot: 0, name: "FULL", callsign: "F1",
            idPic: 2, score: 50000, sweapon: 7, curGame: 1,
            gameWave: new[] { 5, 10, 0 }, diff: new[] { 1, 2, 3, 0 },
            trainFlag: false, finTrain: true);

        var pilots = PilotSaveStore.LoadAll(dir.Path);

        Assert.Single(pilots);
        var p = pilots[0];
        Assert.Equal(7, p.SWeapon);
        Assert.Equal(1, p.CurGame);
        Assert.Equal(new[] { 5, 10, 0 }, p.GameWave);
        Assert.Equal(new[] { 1, 2, 3, 0 }, p.Diff);
        Assert.False(p.TrainFlag);
        Assert.True(p.FinTrain);
    }

    [Fact]
    public void LoadAll_skips_corrupt_files()
    {
        using var dir = new TempDir();
        WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
        File.WriteAllBytes(Path.Combine(dir.Path, "CHAR0001.FIL"), new byte[10]); // too short
        WriteFakePilot(dir.Path, slot: 2, name: "BOB", callsign: "BEAR", idPic: 1, score: 25000);

        var pilots = PilotSaveStore.LoadAll(dir.Path);

        Assert.Equal(2, pilots.Count);
        Assert.Equal("ALICE", pilots[0].Name);
        Assert.Equal("BOB", pilots[1].Name);
    }

    internal static void WriteFakePilot(string dir, int slot, string name, string callsign, int idPic, uint score)
    {
        WriteFakePilot(dir, slot, name, callsign, idPic, score, sweapon: 0, curGame: 0,
            gameWave: new[] { 0, 0, 0 }, diff: new[] { 0, 0, 0, 0 }, trainFlag: false, finTrain: false);
    }

    internal static void WriteFakePilot(string dir, int slot, string name, string callsign,
        int idPic, uint score, int sweapon, int curGame, int[] gameWave, int[] diff,
        bool trainFlag, bool finTrain)
    {
        byte[] header = new byte[88];
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 19));
        byte[] callBytes = System.Text.Encoding.ASCII.GetBytes(callsign);
        Array.Copy(callBytes, 0, header, 20, Math.Min(callBytes.Length, 11));
        BitConverter.GetBytes(idPic).CopyTo(header, 32);
        BitConverter.GetBytes(score).CopyTo(header, 36);
        BitConverter.GetBytes(sweapon).CopyTo(header, 40);
        BitConverter.GetBytes(curGame).CopyTo(header, 44);
        for (int i = 0; i < 3 && i < gameWave.Length; i++)
            BitConverter.GetBytes(gameWave[i]).CopyTo(header, 48 + i * 4);
        // numobjs at 60..63 left 0
        for (int i = 0; i < 4 && i < diff.Length; i++)
            BitConverter.GetBytes(diff[i]).CopyTo(header, 64 + i * 4);
        BitConverter.GetBytes(trainFlag ? 1 : 0).CopyTo(header, 80);
        BitConverter.GetBytes(finTrain ? 1 : 0).CopyTo(header, 84);

        Encrypt(header);
        File.WriteAllBytes(Path.Combine(dir, $"CHAR{slot:D4}.FIL"), header);
    }

    // Inverse of PilotSaveStore.Decrypt: E[i] = P[i] + Key[ki] + prev_E[i-1],
    // seeded with previous = SaveKey[0x19 % len] = 'A'.
    private static void Encrypt(byte[] buffer)
    {
        const string saveKey = "CASTLE";
        int keyIndex = 0x0019 % saveKey.Length;
        int previous = saveKey[keyIndex];

        for (int i = 0; i < buffer.Length; i++)
        {
            int encrypted = (buffer[i] + saveKey[keyIndex] + previous) & 0xFF;
            buffer[i] = (byte)encrypted;
            previous = encrypted;
            keyIndex++;
            if (keyIndex >= saveKey.Length) keyIndex = 0;
        }
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "raptor-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
