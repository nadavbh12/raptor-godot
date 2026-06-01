namespace Raptor.Sim.Shots;

/// One-shot, read-only View signal for megabomb detonation. Parity-inert:
/// not part of any checkpoint. Set in the sim, consumed by the renderer.
public sealed class MegaBombFlash
{
    private bool _pending;
    public void Signal() => _pending = true;
    public bool Consume() { bool p = _pending; _pending = false; return p; }
}
