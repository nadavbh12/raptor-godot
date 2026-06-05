using System.Collections.Generic;

namespace Raptor.View;

/// View-only cosmetic animations (muzzle flash, megabomb glow, boss smoke).
/// Parity-inert: never enters WaveController._explosions, the end-wave gate,
/// or the NDJSON schema. Age is measured against the game-loop iteration clock,
/// mirroring WaveController.AnimationAge.
internal sealed class ViewEffects
{
    // FollowPlayer mirrors C's ANIMS_Register playerflag (ANIMS.C:208): for such
    // anims X/Y is an OFFSET from the player center and the View re-anchors it to
    // the live player position each frame (ANIMS.C:380-383). Only the gun muzzle
    // flash (GUNSTR_BLK) uses it; everything else stores an absolute position.
    private readonly record struct Anim(string Family, int TotalFrames, int X, int Y, int SpawnIter, bool Ground, bool FollowPlayer);
    public readonly record struct ActiveAnim(string Family, int Frame, int X, int Y, bool Ground, bool FollowPlayer);

    private readonly List<Anim> _anims = new();

    public int Count => _anims.Count;

    public void Spawn(string family, int totalFrames, int x, int y, int spawnIter, bool ground, bool followPlayer = false)
        => _anims.Add(new Anim(family, totalFrames, x, y, spawnIter, ground, followPlayer));

    /// <summary>
    /// Resolves an active anim's draw position. A FollowPlayer anim stores an
    /// offset from the player and re-anchors to the live player position each
    /// frame (mirrors C's playerflag, ANIMS.C:380-383) so the gun muzzle flash
    /// tracks the jet as it strafes; all other anims keep their absolute spawn
    /// position. Pure — unit-testable without the Godot runtime.
    /// </summary>
    public static (int X, int Y) ResolvePos(ActiveAnim a, int playerX, int playerY)
        => a.FollowPlayer ? (playerX + a.X, playerY + a.Y) : (a.X, a.Y);

    public IEnumerable<ActiveAnim> Active(int currentIter)
    {
        foreach (var a in _anims)
        {
            int frame = currentIter - a.SpawnIter;
            if (frame >= 0 && frame < a.TotalFrames)
                yield return new ActiveAnim(a.Family, frame, a.X, a.Y, a.Ground, a.FollowPlayer);
        }
    }

    public void Prune(int currentIter)
        => _anims.RemoveAll(a => currentIter - a.SpawnIter >= a.TotalFrames);

    public void Clear() => _anims.Clear();
}
