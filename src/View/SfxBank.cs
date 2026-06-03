using System.Collections.Generic;
using System.IO;
using Godot;

namespace Raptor.View;

/// <summary>
/// Loads and caches the DOS Raptor sound effects as <see cref="AudioStreamWav"/>,
/// and maps the sim's <c>SoundEmitter</c> labels to .wav files. The label→file
/// table mirrors dosraptor SOURCE/FX.C (the <c>fxitems[FX_*].item =
/// GLB_GetItemID("…_FX")</c> assignments), e.g. FX_PULSE and FX_ENEMYPLASMA both
/// resolve to ESHOT_FX in the C original.
///
/// Clips load lazily from the filesystem (assets/ is read-only and not run
/// through Godot's import pipeline), the same way DebugRenderer loads sprite
/// PNGs via <c>Image.LoadFromFile</c>.
/// </summary>
internal sealed class SfxBank
{
    /// <summary>
    /// sim SoundEmitter label → FX_FILE basename (without ".wav"). Mirrors FX.C.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> LabelToFile =
        new Dictionary<string, string>
        {
            // Player weapons — SHOTS.C SND_Patch(FX_*).
            ["sound.fx_gun"]           = "GUN_FX",     // FX_GUN
            ["sound.fx_missle"]        = "MISSLE_FX",  // FX_MISSLE
            ["sound.fx_pulse"]         = "ESHOT_FX",   // FX_PULSE  → ESHOT_FX (FX.C:476)
            ["sound.fx_turret"]        = "TURRET_FX",  // FX_TURRET
            // Pickups — BONUS.C SND_Patch(FX_BONUS).
            ["sound.fx_bonus"]         = "BONUS_FX",   // FX_BONUS
            // Enemy shots — ESHOT.C SND_3DPatch(FX_*).
            ["sound3d.fx_enemyshot"]   = "ESHOT_FX",   // FX_ENEMYSHOT
            ["sound3d.fx_enemylaser"]  = "LASER_FX",   // FX_ENEMYLASER
            ["sound3d.fx_enemymissle"] = "MISSLE_FX",  // FX_ENEMYMISSLE
            ["sound3d.fx_enemyplasma"] = "ESHOT_FX",   // FX_ENEMYPLASMA → ESHOT_FX (FX.C:432)
            ["sound3d.fx_coconut"]     = "ESHOT_FX",   // no FX_COCONUT — fall back to enemy shot
            // Explosions — ENEMY.C:1126 SND_3DPatch(FX_AIREXPLO); RAP.C:581-582
            // plays FX_AIREXPLO + FX_AIREXPLO2 when the player ship blows up.
            ["sound3d.fx_airexplo"]    = "EXPLO_FX",   // FX_AIREXPLO
            ["sound3d.fx_airexplo2"]   = "EXPLO2_FX",  // FX_AIREXPLO2 (player death)
            // Player damage — OBJECTS.C:1240/1254 (OBJS_Damage).
            ["sound.fx_shit"]          = "HIT_FX",     // FX_SHIT  (hit while super-shielded)
            ["sound.fx_hit"]           = "GUN_FX",     // FX_HIT   (normal shield hit)
            // Enemy body-crash — ENEMY.C:1117 SND_Patch(FX_CRASH).
            ["sound.fx_crash"]         = "CRASH_FX",   // FX_CRASH
            // End-wave fly-off — RAP.C:604 SND_Patch(FX_FLYBY).
            ["sound.fx_flyby"]         = "FLYBY_FX",   // FX_FLYBY
        };

    private readonly string _soundsRoot;
    private readonly Dictionary<string, AudioStreamWav?> _cache = new();

    public SfxBank(string soundsRoot) => _soundsRoot = soundsRoot;

    /// <summary>
    /// Resolve a sim label to a cached <see cref="AudioStreamWav"/>, or null if
    /// the label is unmapped or the file is missing/unloadable.
    /// </summary>
    public AudioStreamWav? ForLabel(string label)
    {
        if (!LabelToFile.TryGetValue(label, out var file)) return null;
        if (_cache.TryGetValue(file, out var cached)) return cached;
        var stream = Load(file);
        _cache[file] = stream;
        return stream;
    }

    private AudioStreamWav? Load(string file)
    {
        string path = Path.Combine(_soundsRoot, file + ".wav");
        if (!File.Exists(path))
        {
            GD.PushWarning($"SfxBank: sound file not found: {path}");
            return null;
        }

        var wav = WavData.Parse(File.ReadAllBytes(path));
        if (wav.BitsPerSample != 16)
        {
            GD.PushWarning($"SfxBank: {file} is {wav.BitsPerSample}-bit; expected 16-bit PCM.");
            return null;
        }

        // 16-bit WAV PCM is signed little-endian — exactly AudioStreamWav's
        // Format16Bits layout — so the data chunk bytes copy across verbatim.
        return new AudioStreamWav
        {
            Data = wav.Pcm,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = wav.SampleRate,
            Stereo = wav.Stereo,
        };
    }
}
