using System.Collections.Generic;

namespace Raptor.Sim;

/// <summary>
/// FNV-1a 64-bit hash over a player object inventory list, mirroring C
/// compute_obj_hash (dosraptor port/platform/parity.c:160): for each object in
/// linked-list order, fold in (type &amp; 0xff) then (num &amp; 0xff). The empty
/// list yields the FNV offset basis (0xcbf29ce484222325) — exactly what C emits
/// for the empty inventory in menu contexts.
/// </summary>
public static class ObjHash
{
    private const ulong OffsetBasis = 14695981039346656037UL; // FNV-1a 64-bit basis
    private const ulong Prime       = 1099511628211UL;        // FNV-1a 64-bit prime

    public static ulong Compute(IEnumerable<(int type, int num)> objs)
    {
        ulong h = OffsetBasis;
        foreach (var (type, num) in objs)
        {
            h ^= (byte)(type & 0xff);
            h *= Prime;
            h ^= (byte)(num & 0xff);
            h *= Prime;
        }
        return h;
    }
}
