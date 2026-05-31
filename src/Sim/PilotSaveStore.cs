using System;
using System.Collections.Generic;
using System.IO;
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
}

public static class PilotSaveStore
{
    private const int MaxSave = 10;
    private const int PlayerHeaderSize = 88;
    private const string SaveKey = "CASTLE";

    public static int Save(string directory, string name, string callsign, int idPic, uint score)
    {
        Directory.CreateDirectory(directory);
        int slot = NextAvailableSlot(directory);
        string path = Path.Combine(directory, $"CHAR{slot:D4}.FIL");

        byte[] header = new byte[PlayerHeaderSize];
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 19));
        byte[] callBytes = Encoding.ASCII.GetBytes(callsign);
        Array.Copy(callBytes, 0, header, 20, Math.Min(callBytes.Length, 11));
        BitConverter.GetBytes(idPic).CopyTo(header, 32);
        BitConverter.GetBytes(score).CopyTo(header, 36);

        Encrypt(header);
        File.WriteAllBytes(path, header);
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
            sweapon, curGame, gameWave, diff, trainFlag, finTrain);
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
