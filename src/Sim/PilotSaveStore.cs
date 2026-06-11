using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Raptor.Sim;

public sealed record PilotSaveSummary(
    int Slot,
    string Name,
    string Callsign,
    int IdPic,
    uint Score,
    int SWeapon,
    int CurGame,
    int[] GameWave,    // 3 entries (one per game/episode)
    int[] Diff,        // 4 entries (one per game/episode)
    bool TrainFlag,
    bool FinTrain)
{
    public string CreditsText => Score.ToString("D7");

    /// <summary>
    /// The exact CHAR####.FIL path this summary was read from (set by LoadAll
    /// via the resolved candidate directory). Carried so the inventory loads
    /// from the same file the summary came from — no path re-derivation.
    /// </summary>
    public string FilePath { get; init; } = string.Empty;
}

public static class PilotSaveStore
{
    private const int MaxSave = 10;
    private const int PlayerHeaderSize = 88;
    private const string SaveKey = "CASTLE";

    /// <summary>
    /// Writes a CHAR####.FIL save file for the given pilot. Mirrors RAP_SavePlayer
    /// (LOADSAVE.C:333-347).
    ///
    /// If <paramref name="inventory"/> is provided, the method:
    /// <list type="bullet">
    ///   <item>Sets numobjs (int32 LE @ header offset 60) = number of owned slots.</item>
    ///   <item>Sets sweapon (int32 LE @ header offset 40) = (int)EquippedSpecial, or -1
    ///         (C EMPTY) when EquippedSpecial is null.</item>
    ///   <item>Encrypts and writes the 88-byte header.</item>
    ///   <item>For each slot: builds a 40-byte OBJ record (num@16, type@20, inuse@32),
    ///         encrypts it INDEPENDENTLY (Encrypt resets per call), appends it.</item>
    /// </list>
    /// When <paramref name="inventory"/> is null, writes only the 88-byte header with
    /// numobjs=0; offset 40 (sweapon) is left zero-filled, preserving the exact
    /// legacy byte layout (sweapon=0) for existing inventory-less callers.
    /// </summary>
    public static int Save(string directory, string name, string callsign, int idPic, uint score,
        Inventory? inventory = null, int curGame = 0, int gameWave = 0)
    {
        Directory.CreateDirectory(directory);
        int slot = NextAvailableSlot(directory);
        string path = Path.Combine(directory, $"CHAR{slot:D4}.FIL");

        // Collect slots before building the header so we know the count.
        (ObjType type, int num, bool inuse)[] slots =
            inventory != null
                ? inventory.Slots().ToArray()
                : Array.Empty<(ObjType, int, bool)>();

        byte[] header = new byte[PlayerHeaderSize];
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 19));
        byte[] callBytes = Encoding.ASCII.GetBytes(callsign);
        Array.Copy(callBytes, 0, header, 20, Math.Min(callBytes.Length, 11));
        BitConverter.GetBytes(idPic).CopyTo(header, 32);
        BitConverter.GetBytes(score).CopyTo(header, 36);

        // Campaign progress (C RAP_SavePlayer LOADSAVE.C:330-333): cur_game @44 and
        // game_wave[cur_game] @(48 + cur_game*4). Defaults (0/0) preserve the exact
        // legacy byte layout (these offsets were previously zero-filled). Godot plays
        // episode 1 only (cur_game 0), so the other game_wave slots stay zero.
        int g = curGame < 0 ? 0 : (curGame > 2 ? 2 : curGame);
        BitConverter.GetBytes(g).CopyTo(header, 44);
        BitConverter.GetBytes(gameWave).CopyTo(header, 48 + g * 4);

        // sweapon @ offset 40: only written when an inventory is provided.
        // (int)EquippedSpecial, or -1 (C EMPTY) when no special is equipped.
        // For the null-inventory legacy path, offset 40 stays zero-filled (sweapon=0).
        if (inventory != null)
        {
            int sweapon = inventory.EquippedSpecial.HasValue
                ? (int)inventory.EquippedSpecial.Value
                : -1;
            BitConverter.GetBytes(sweapon).CopyTo(header, 40);
        }

        // numobjs @ offset 60: count of OBJ records that follow the header.
        BitConverter.GetBytes(slots.Length).CopyTo(header, 60);

        Encrypt(header);

        using var fs = new FileStream(path, FileMode.Create);
        fs.Write(header, 0, header.Length);

        // Write each OBJ record (40 bytes), encrypted INDEPENDENTLY — one Encrypt call
        // per record, mirroring RAP_SavePlayer's per-record GLB_EnCrypt loop.
        const int RecordSize = 40;
        foreach (var (type, num, inuse) in slots)
        {
            byte[] rec = new byte[RecordSize];
            // OBJ record layout (OBJECTS.H): num@16, type@20, inuse@32 (int32 LE); other bytes 0.
            BitConverter.GetBytes(num).CopyTo(rec, 16);
            BitConverter.GetBytes((int)type).CopyTo(rec, 20);
            BitConverter.GetBytes(inuse ? 1 : 0).CopyTo(rec, 32);
            Encrypt(rec);
            fs.Write(rec, 0, rec.Length);
        }

        return slot;
    }

    private static int NextAvailableSlot(string directory)
    {
        for (int slot = 0; slot < MaxSave; slot++)
        {
            if (!File.Exists(Path.Combine(directory, $"CHAR{slot:D4}.FIL")))
                return slot;
        }
        throw new InvalidOperationException("All pilot slots in use.");
    }

    private static void Encrypt(byte[] buffer)
    {
        int keyIndex = 0x0019 % SaveKey.Length;
        int previous = SaveKey[keyIndex];
        for (int i = 0; i < buffer.Length; i++)
        {
            int encrypted = (buffer[i] + SaveKey[keyIndex] + previous) & 0xFF;
            buffer[i] = (byte)encrypted;
            previous = encrypted;
            keyIndex++;
            if (keyIndex >= SaveKey.Length) keyIndex = 0;
        }
    }

    public static PilotSaveSummary? LoadFirstAvailable(string? directory = null)
    {
        var all = LoadAll(directory);
        return all.Count > 0 ? all[0] : null;
    }

    public static List<PilotSaveSummary> LoadAll(string? directory = null)
    {
        var result = new List<PilotSaveSummary>();
        foreach (string dir in CandidateDirectories(directory))
        {
            if (!Directory.Exists(dir))
                continue;

            for (int slot = 0; slot < MaxSave; slot++)
            {
                string path = Path.Combine(dir, $"CHAR{slot:D4}.FIL");
                if (!File.Exists(path))
                    continue;

                var summary = TryLoadSummary(path, slot);
                if (summary != null)
                    result.Add(summary);
            }

            if (result.Count > 0)
                return result;
        }
        return result;
    }

    private static PilotSaveSummary? TryLoadSummary(string path, int slot)
    {
        byte[] encrypted = File.ReadAllBytes(path);
        if (encrypted.Length < PlayerHeaderSize)
            return null;

        byte[] header = new byte[PlayerHeaderSize];
        Array.Copy(encrypted, header, PlayerHeaderSize);
        Decrypt(header);

        // PLAYEROBJ layout (C PUBLIC.H, sizeof = 88):
        //   0..19   name[20]
        //   20..31  callsign[12]
        //   32..35  id_pic (INT)
        //   36..39  score (DWORD)
        //   40..43  sweapon (INT — current special weapon)
        //   44..47  cur_game (INT — current game/episode 0..3)
        //   48..59  game_wave[3] (INT each — current wave per game)
        //   60..63  numobjs (INT — count of OBJ records following)
        //   64..79  diff[4] (INT each — difficulty 0..3 per game)
        //   80..83  trainflag (BOOL)
        //   84..87  fintrain (BOOL)
        string name = ReadCString(header, 0, 20);
        string callsign = ReadCString(header, 20, 12);
        int idPic = BitConverter.ToInt32(header, 32);
        uint score = BitConverter.ToUInt32(header, 36);
        int sweapon = BitConverter.ToInt32(header, 40);
        int curGame = BitConverter.ToInt32(header, 44);
        var gameWave = new[] {
            BitConverter.ToInt32(header, 48),
            BitConverter.ToInt32(header, 52),
            BitConverter.ToInt32(header, 56),
        };
        // numobjs at 60..63 (used by RAP_SavePlayer to write OBJ count; we don't load the inventory yet).
        var diff = new[] {
            BitConverter.ToInt32(header, 64),
            BitConverter.ToInt32(header, 68),
            BitConverter.ToInt32(header, 72),
            BitConverter.ToInt32(header, 76),
        };
        bool trainFlag = BitConverter.ToInt32(header, 80) != 0;
        bool finTrain = BitConverter.ToInt32(header, 84) != 0;

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(callsign))
            return null;
        if (idPic < 0 || idPic > 3)
            idPic = 0;

        return new PilotSaveSummary(slot, name, callsign, idPic, score,
            sweapon, curGame, gameWave, diff, trainFlag, finTrain)
        {
            FilePath = path,
        };
    }

    private static string ReadCString(byte[] bytes, int offset, int length)
    {
        int end = offset;
        int limit = offset + length;
        while (end < limit && bytes[end] != 0)
            end++;
        return Encoding.ASCII.GetString(bytes, offset, end - offset).TrimEnd();
    }

    private static void Decrypt(byte[] buffer)
    {
        int keyIndex = 0x0019 % SaveKey.Length;
        int previous = SaveKey[keyIndex];

        for (int i = 0; i < buffer.Length; i++)
        {
            int encrypted = buffer[i];
            buffer[i] = unchecked((byte)(encrypted - SaveKey[keyIndex] - previous));
            previous = encrypted;
            keyIndex++;
            if (keyIndex >= SaveKey.Length)
                keyIndex = 0;
        }
    }

    // -----------------------------------------------------------------------
    // Task 2.2 — LoadInventory
    // -----------------------------------------------------------------------
    /// <summary>
    /// Reads a CHAR####.FIL save file and returns its <see cref="Inventory"/>.
    ///
    /// Mirrors RAP_LoadPlayer (LOADSAVE.C:260-282) + OBJS_Load (OBJECTS.C:708-732):
    /// <list type="bullet">
    ///   <item>Decrypt the 88-byte player header independently.</item>
    ///   <item>Read numobjs @ offset 60 and sweapon @ offset 40.</item>
    ///   <item>For each OBJ record (40 bytes): decrypt it independently, extract
    ///         num@16, type@20, inuse@32; bound-check type (0..23); call inv.Load.</item>
    ///   <item>After the loop: if sweapon maps to an owned special weapon,
    ///         set EquippedSpecial; otherwise call GetNext() — mirrors C's
    ///         <c>if (!OBJS_IsEquip(plr.sweapon)) OBJS_GetNext()</c>.</item>
    /// </list>
    /// Each record is decrypted with its own fresh key/seed (Decrypt resets per call).
    /// </summary>
    /// <exception cref="InvalidDataException">File is too short to contain a valid header.</exception>
    public static Inventory LoadInventory(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < PlayerHeaderSize)
            throw new InvalidDataException($"Save file too short: {path}");

        // Decrypt the header block into a separate buffer (leaves data[] intact for records).
        byte[] header = new byte[PlayerHeaderSize];
        Array.Copy(data, header, PlayerHeaderSize);
        Decrypt(header);

        // PLAYEROBJ layout (PUBLIC.H):
        //   40..43  sweapon (INT — index of currently equipped special weapon)
        //   60..63  numobjs (INT — count of OBJ records that follow the header)
        int sweapon = BitConverter.ToInt32(header, 40);
        int numObjs = BitConverter.ToInt32(header, 60);

        var inv = new Inventory();
        int offset = PlayerHeaderSize;
        const int RecordSize = 40;

        for (int i = 0; i < numObjs; i++)
        {
            // Graceful truncation: stop if fewer than RecordSize bytes remain.
            if (offset + RecordSize > data.Length)
                break;

            // Decrypt each record INDEPENDENTLY — Decrypt resets key/seed each call.
            byte[] rec = new byte[RecordSize];
            Array.Copy(data, offset, rec, 0, RecordSize);
            Decrypt(rec);
            offset += RecordSize;

            // OBJ record layout (OBJECTS.H): num@16, type@20, inuse@32 (int32 LE).
            int num    = BitConverter.ToInt32(rec, 16);
            int typeRaw = BitConverter.ToInt32(rec, 20);
            int inuseRaw = BitConverter.ToInt32(rec, 32);

            // Bound-check type: valid range is 0..LastObject-1 (LastObject is the sentinel, no ObjLib entry).
            if (typeRaw < 0 || typeRaw >= (int)ObjType.LastObject)
                continue;

            var type = (ObjType)typeRaw;
            bool inuse = inuseRaw != 0;

            inv.Load(type, num, inuse);
        }

        // Mirrors RAP_LoadPlayer:279 — keep saved sweapon iff owned, else cycle to next valid.
        // The range guard (0..LastObject-1) only avoids calling IsEquip with an out-of-range
        // int; an out-of-range sweapon is treated as not-equipped → GetNext().
        if (sweapon >= 0 && sweapon < (int)ObjType.LastObject && inv.IsEquip((ObjType)sweapon))
            inv.EquippedSpecial = (ObjType)sweapon;
        else
            inv.GetNext();

        return inv;
    }

    /// <summary>Delete a pilot's CHAR####.FIL save file (C RAP_LoadWin LOAD_DEL → remove()).
    /// No-op if the path is empty or the file is already gone.</summary>
    public static void Delete(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            File.Delete(filePath);
    }

    private static string[] CandidateDirectories(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return new[] { explicitDirectory };

        string? env = Environment.GetEnvironmentVariable("RAPTOR_SAVE_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return new[] { env };

        return new[]
        {
            Directory.GetCurrentDirectory(),
            "../dosraptor",
            FindSiblingDosRaptor(Directory.GetCurrentDirectory()),
            FindSiblingDosRaptor(AppContext.BaseDirectory),
        };
    }

    private static string FindSiblingDosRaptor(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "dosraptor");
            if (Directory.Exists(candidate))
                return candidate;

            candidate = Path.Combine(dir.FullName, "../dosraptor");
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);

            dir = dir.Parent;
        }

        return "../dosraptor";
    }
}
