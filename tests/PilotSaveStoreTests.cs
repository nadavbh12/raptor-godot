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

    [Fact]
    public void WriteFakePilot_with_objs_produces_correct_file_length()
    {
        using var dir = new TempDir();
        var objs = new (ObjType type, int num, bool inuse)[]
        {
            (ObjType.ForwardGuns, 99, true),
            (ObjType.MegaBomb,     3, false),
        };
        WriteFakePilot(dir.Path, slot: 0, name: "LEN", callsign: "L1", idPic: 0, score: 0, objs: objs);

        long fileLen = new FileInfo(Path.Combine(dir.Path, "CHAR0000.FIL")).Length;
        Assert.Equal(88 + 40 * objs.Length, fileLen);
    }

    internal static void WriteFakePilot(string dir, int slot, string name, string callsign, int idPic, uint score,
        (ObjType type, int num, bool inuse)[]? objs = null)
    {
        WriteFakePilot(dir, slot, name, callsign, idPic, score, sweapon: 0, curGame: 0,
            gameWave: new[] { 0, 0, 0 }, diff: new[] { 0, 0, 0, 0 }, trainFlag: false, finTrain: false,
            objs: objs);
    }

    internal static void WriteFakePilot(string dir, int slot, string name, string callsign,
        int idPic, uint score, int sweapon, int curGame, int[] gameWave, int[] diff,
        bool trainFlag, bool finTrain,
        (ObjType type, int num, bool inuse)[]? objs = null)
    {
        objs ??= Array.Empty<(ObjType, int, bool)>();

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
        BitConverter.GetBytes(objs.Length).CopyTo(header, 60);  // numobjs
        for (int i = 0; i < 4 && i < diff.Length; i++)
            BitConverter.GetBytes(diff[i]).CopyTo(header, 64 + i * 4);
        BitConverter.GetBytes(trainFlag ? 1 : 0).CopyTo(header, 80);
        BitConverter.GetBytes(finTrain ? 1 : 0).CopyTo(header, 84);

        Encrypt(header);

        using var fs = new FileStream(Path.Combine(dir, $"CHAR{slot:D4}.FIL"), FileMode.Create);
        fs.Write(header, 0, header.Length);

        foreach (var (type, num, inuse) in objs)
        {
            byte[] rec = new byte[40];
            // OBJ record layout: num@16, type@20, inuse@32 (int32 LE); other bytes 0.
            BitConverter.GetBytes(num).CopyTo(rec, 16);
            BitConverter.GetBytes((int)type).CopyTo(rec, 20);
            BitConverter.GetBytes(inuse ? 1 : 0).CopyTo(rec, 32);
            // Encrypt is called per-record (seed/key reset each call). Task 2.2's decrypt MUST also
            // decrypt each 40-byte record independently — NOT all OBJ bytes in one pass.
            Encrypt(rec);
            fs.Write(rec, 0, rec.Length);
        }
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

    // -----------------------------------------------------------------------
    // Task 2.2 — LoadInventory tests
    // -----------------------------------------------------------------------

    [Fact]
    public void Loaded_inventory_round_trips_objs()
    {
        using var tmp = new TempDir();
        WriteFakePilot(tmp.Path, slot: 0, name: "A", callsign: "B", idPic: 0, score: 5000u,
            sweapon: (int)ObjType.MiniGun, curGame: 0, gameWave: new[] { 0, 0, 0 }, diff: new[] { 1, 1, 1, 1 },
            trainFlag: false, finTrain: false,
            objs: new[] {
                (ObjType.ForwardGuns, 1, true),
                (ObjType.MiniGun,     1, true),
                (ObjType.MegaBomb,    3, false),
                (ObjType.Energy,     75, false),
            });
        var inv = PilotSaveStore.LoadInventory(Path.Combine(tmp.Path, "CHAR0000.FIL"));
        Assert.True(inv.IsEquip(ObjType.ForwardGuns));
        Assert.True(inv.IsEquip(ObjType.MiniGun));
        Assert.Equal(3, inv.GetAmt(ObjType.MegaBomb));
        Assert.Equal(75, inv.GetAmt(ObjType.Energy));
        Assert.Equal(ObjType.MiniGun, inv.EquippedSpecial);   // sweapon applied + valid
    }

    [Theory]
    [MemberData(nameof(InventoryGen.RandomInventories), 50, MemberType = typeof(InventoryGen))]
    public void Save_then_load_is_identity((ObjType type, int num, bool inuse)[] objs, int sweapon)
    {
        using var tmp = new TempDir();
        WriteFakePilot(tmp.Path, slot: 0, name: "X", callsign: "Y", idPic: 0, score: 0u,
            sweapon: sweapon, curGame: 0, gameWave: new[] { 0, 0, 0 }, diff: new[] { 0, 0, 0, 0 },
            trainFlag: false, finTrain: false,
            objs: objs);
        var inv = PilotSaveStore.LoadInventory(Path.Combine(tmp.Path, "CHAR0000.FIL"));
        foreach (var (t, n, inuse) in objs)
        {
            Assert.Equal(n, inv.GetAmt(t));
            Assert.Equal(inuse, inv.IsEquip(t));
        }
    }

    // -----------------------------------------------------------------------
    // Task 2.3 — Save writes OBJ inventory array (production save path)
    // -----------------------------------------------------------------------

    [Fact]
    public void Production_save_round_trips_inventory_through_load()
    {
        using var tmp = new TempDir();

        // Build an inventory via Inventory.Add/Load so we test the PRODUCTION path,
        // not WriteFakePilot.
        var inv = new Inventory();
        inv.Load(ObjType.ForwardGuns, 1, true);
        inv.Load(ObjType.MiniGun,     2, true);
        inv.Load(ObjType.MegaBomb,    3, false);
        inv.Load(ObjType.Energy,     75, false);
        inv.EquippedSpecial = ObjType.MiniGun;

        int slot = PilotSaveStore.Save(tmp.Path, name: "INV", callsign: "I1",
            idPic: 0, score: 9999u, inventory: inv);

        string path = System.IO.Path.Combine(tmp.Path, $"CHAR{slot:D4}.FIL");
        var loaded = PilotSaveStore.LoadInventory(path);

        // All four slots survive round-trip.
        Assert.Equal(1,  loaded.GetAmt(ObjType.ForwardGuns));
        Assert.True(loaded.IsEquip(ObjType.ForwardGuns));
        Assert.Equal(2,  loaded.GetAmt(ObjType.MiniGun));
        Assert.True(loaded.IsEquip(ObjType.MiniGun));
        Assert.Equal(3,  loaded.GetAmt(ObjType.MegaBomb));
        Assert.False(loaded.IsEquip(ObjType.MegaBomb));
        Assert.Equal(75, loaded.GetAmt(ObjType.Energy));
        Assert.False(loaded.IsEquip(ObjType.Energy));

        // EquippedSpecial survives.
        Assert.Equal(ObjType.MiniGun, loaded.EquippedSpecial);
    }

    [Fact]
    public void Production_save_null_inventory_writes_zero_numobjs()
    {
        // Saving without inventory should produce the same file layout as before
        // (88 bytes, numobjs == 0). Existing LoadInventory must return an empty inventory.
        using var tmp = new TempDir();
        int slot = PilotSaveStore.Save(tmp.Path, name: "EMPTY", callsign: "E1",
            idPic: 0, score: 0u, inventory: null);

        string path = System.IO.Path.Combine(tmp.Path, $"CHAR{slot:D4}.FIL");
        long fileLen = new System.IO.FileInfo(path).Length;
        Assert.Equal(88, fileLen);  // no OBJ records appended

        var inv = PilotSaveStore.LoadInventory(path);
        Assert.Equal(0, inv.GetAmt(ObjType.ForwardGuns));
        Assert.False(inv.IsEquip(ObjType.ForwardGuns));

        // Legacy byte layout: offset 40 (sweapon) stays zero-filled when no inventory
        // is provided. Pin SWeapon == 0 so the null path can't silently regress to -1.
        var summary = PilotSaveStore.LoadAll(tmp.Path)[0];
        Assert.Equal(0, summary.SWeapon);
    }

    [Fact]
    public void Production_save_null_equipped_special_encodes_as_empty()
    {
        // EquippedSpecial == null must be saved as -1 (C EMPTY) so LoadInventory
        // calls GetNext() and finds no owned special → EquippedSpecial stays null.
        using var tmp = new TempDir();
        var inv = new Inventory();
        inv.Load(ObjType.MegaBomb, 3, false);  // no InUse special
        inv.EquippedSpecial = null;

        int slot = PilotSaveStore.Save(tmp.Path, name: "NOSPEC", callsign: "NS",
            idPic: 0, score: 0u, inventory: inv);

        string path = System.IO.Path.Combine(tmp.Path, $"CHAR{slot:D4}.FIL");
        var loaded = PilotSaveStore.LoadInventory(path);

        // GetNext found no equipped special weapon → null.
        Assert.Null(loaded.EquippedSpecial);
        Assert.Equal(3, loaded.GetAmt(ObjType.MegaBomb));
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
