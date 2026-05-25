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
        byte[] header = new byte[88];
        byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 19));
        byte[] callBytes = System.Text.Encoding.ASCII.GetBytes(callsign);
        Array.Copy(callBytes, 0, header, 20, Math.Min(callBytes.Length, 11));
        BitConverter.GetBytes(idPic).CopyTo(header, 32);
        BitConverter.GetBytes(score).CopyTo(header, 36);

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
