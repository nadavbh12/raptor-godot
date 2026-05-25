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
    uint Score)
{
    public string CreditsText => Score.ToString("D7");
}

public static class PilotSaveStore
{
    private const int MaxSave = 10;
    private const int PlayerHeaderSize = 88;
    private const string SaveKey = "CASTLE";

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

        string name = ReadCString(header, 0, 20);
        string callsign = ReadCString(header, 20, 12);
        int idPic = BitConverter.ToInt32(header, 32);
        uint score = BitConverter.ToUInt32(header, 36);

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(callsign))
            return null;
        if (idPic < 0 || idPic > 3)
            idPic = 0;

        return new PilotSaveSummary(slot, name, callsign, idPic, score);
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
