using System;
using System.Text.Json.Serialization;

namespace Raptor.Sim.Enemy;

/// <summary>
/// Subset of the C SPRITE struct (SOURCE/MAP.H) used at Stage 3.
/// Deserialized from assets/sprites_meta/SPRITE1_ITM.json.
/// Note: the JSON is wrapped in an outer object with keys "name",
/// "num_sprites", and "sprites" (an array). SpriteMetaLibrary reads
/// the "sprites" array into a list of SpriteMeta.
/// More fields can be added later as we wire in additional behaviour.
/// </summary>
public sealed class SpriteMeta
{
    [JsonPropertyName("iname")]      public string IName       { get; set; } = "";
    [JsonPropertyName("item")]       public uint   Item        { get; set; }
    [JsonPropertyName("hits")]       public int    Hits        { get; set; } = 1;
    [JsonPropertyName("money")]      public int    Money       { get; set; }
    [JsonPropertyName("movespeed")]  public int    MoveSpeed   { get; set; }
    [JsonPropertyName("numflight")]  public int    NumFlight   { get; set; }
    [JsonPropertyName("flighttype")] public int    FlightType  { get; set; }  // 0=REPEAT, 1=LINEAR, ...
    [JsonPropertyName("flightx")]    public int[]  FlightX     { get; set; } = Array.Empty<int>();
    [JsonPropertyName("flighty")]    public int[]  FlightY     { get; set; } = Array.Empty<int>();
    [JsonPropertyName("numguns")]    public int    NumGuns     { get; set; }
    [JsonPropertyName("shootframe")] public int    ShootFrame  { get; set; }
    [JsonPropertyName("shootx")]     public int[]  ShootX      { get; set; } = Array.Empty<int>();
    [JsonPropertyName("shooty")]     public int[]  ShootY      { get; set; } = Array.Empty<int>();
    // Additional fields consumed by sim logic:
    [JsonPropertyName("countdown")]  public int    Countdown   { get; set; }
    [JsonPropertyName("shootcnt")]   public int    ShootCnt    { get; set; } = 1;
    [JsonPropertyName("shotspace")]  public int    ShotSpace   { get; set; } = 4;
    [JsonPropertyName("shootstart")] public int    ShootStart  { get; set; }
    [JsonPropertyName("shoot_type")] public int[]  ShootType   { get; set; } = Array.Empty<int>();
    // Engine-flame placement (rendered via View/DebugRenderer.DrawEngineFlames,
    // mirroring C ENEMY_DisplaySky's FLAME_Up loop in SOURCE/ENEMY.C:1211).
    [JsonPropertyName("numengs")]    public int    NumEngs     { get; set; }
    [JsonPropertyName("engx")]       public int[]  EngX        { get; set; } = Array.Empty<int>();
    [JsonPropertyName("engy")]       public int[]  EngY        { get; set; } = Array.Empty<int>();
    [JsonPropertyName("englx")]      public int[]  EngLx       { get; set; } = Array.Empty<int>();
    [JsonPropertyName("ground")]     public int    Ground      { get; set; }
    [JsonPropertyName("shadow")]     public int    Shadow      { get; set; }
    [JsonPropertyName("exptype")]    public int    ExpType     { get; set; }
    // Multi-frame sprite animation (e.g. SHIP07G1_PIC helicopter rotor).
    // C ENEMY.C:727-774: frame advances when frame_rate timer hits 0, then
    // resets; wraps with curframe -= rewind when curframe >= num_frames.
    [JsonPropertyName("num_frames")] public int    NumFrames   { get; set; } = 1;
    [JsonPropertyName("frame_rate")] public int    FrameRate   { get; set; }
    [JsonPropertyName("rewind")]     public int    Rewind      { get; set; } = 1;
    // C SOURCE/MAP.H SPRITE.bonus — OBJ_TYPE value to drop when this enemy is
    // destroyed (-1 = no drop). Consumed by WaveController's death handler.
    [JsonPropertyName("bonus")]      public int    Bonus       { get; set; } = -1;
    // F_REPEAT ping-pong lower bound. Mirrors C SPRITE.repos (MAP.H). The enemy
    // walks waypoints forward to numflight-1, then backward to `repos`, then
    // forward again. For repos==0 this is a full ping-pong; for repos>0 the
    // forward end of the bounce skips the early waypoints once initial flight
    // completes. See ENEMY.C:894-900.
    [JsonPropertyName("repos")]      public int    Repos       { get; set; }
    // Additional fields present in JSON but not consumed:
    // suck, animtype, bossflag, sfx, song.

    // ── Sprite image dimensions (populated by SpriteMetaLibrary from PNG files) ──
    // Not in JSON; set after deserialisation.
    // Default to 32×24 (SHIP01G1_PIC dimensions, the most common enemy sprite).
    public int Width  { get; set; } = 32;
    public int Height { get; set; } = 24;

    /// <summary>Half-width: mirrors C's hlx = width >> 1.</summary>
    public int HalfX => Width  >> 1;
    /// <summary>Half-height: mirrors C's hly = height >> 1.</summary>
    public int HalfY => Height >> 1;
    /// <summary>Body-crash damage to player: mirrors C's suben = max(width,height), then suben>>2.</summary>
    public int BodyCrashDamage => Math.Max(Width, Height) >> 2;
}
