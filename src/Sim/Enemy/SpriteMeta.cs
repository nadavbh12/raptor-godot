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
    // Additional fields present in JSON but not yet consumed by sim logic:
    // shoot_type, shotspace, ground, suck, frame_rate, num_frames, countdown,
    // rewind, animtype, shadow, bossflag, shootstart, shootcnt, repos,
    // numengs, sfx, song, bonus, exptype, engx, engy, englx.
}
