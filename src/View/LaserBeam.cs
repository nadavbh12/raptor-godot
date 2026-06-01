using System.Collections.Generic;

namespace Raptor.View;

/// ESHOT.C:558-573 — ES_LASER draws ELASER_BLK every 3px from shot.y down to
/// move.y2 (exclusive), ELASEPOW_BLK at the gun, the impact sprite at move.y2-8.
internal static class LaserBeam
{
    public static IEnumerable<int> ColumnYs(int y, int y2)
    {
        for (int loop = y; loop < y2; loop += 3) yield return loop;
    }
}
