using System.Collections.Generic;

namespace Raptor.View;

/// View-only cosmetic animations (muzzle flash, megabomb glow, boss smoke).
/// Parity-inert: never enters WaveController._explosions, the end-wave gate,
/// or the NDJSON schema. Age is measured against the game-loop iteration clock,
/// mirroring WaveController.AnimationAge.
internal sealed class ViewEffects
{
    public readonly record struct Anim(string Family, int TotalFrames, int X, int Y, int SpawnIter, bool Ground);
    public readonly record struct ActiveAnim(string Family, int Frame, int X, int Y, bool Ground);

    private readonly List<Anim> _anims = new();

    public int Count => _anims.Count;

    public void Spawn(string family, int totalFrames, int x, int y, int spawnIter, bool ground)
        => _anims.Add(new Anim(family, totalFrames, x, y, spawnIter, ground));

    public IEnumerable<ActiveAnim> Active(int currentIter)
    {
        foreach (var a in _anims)
        {
            int frame = currentIter - a.SpawnIter;
            if (frame >= 0 && frame < a.TotalFrames)
                yield return new ActiveAnim(a.Family, frame, a.X, a.Y, a.Ground);
        }
    }

    public void Prune(int currentIter)
        => _anims.RemoveAll(a => currentIter - a.SpawnIter >= a.TotalFrames);

    public void Clear() => _anims.Clear();
}
