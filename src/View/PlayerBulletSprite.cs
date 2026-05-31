using Raptor.Sim;
using Raptor.Sim.Shots;

namespace Raptor.View;

internal static class PlayerBulletSprite
{
    public static (string Family, int Frame) FrameFor(ObjType weapon, int frameCounter)
    {
        var lib = ShotLib.Get(weapon);
        string family = FamilyFor(weapon);
        int frames = lib.NumFrames > 0 ? lib.NumFrames : 1;
        int frame = (lib.StartFrame + frameCounter) % frames;
        return (family, frame);
    }

    private static string FamilyFor(ObjType weapon) => weapon switch
    {
        ObjType.ForwardGuns  => "NMSHOT_BLK",
        ObjType.PlasmaGuns   => "PLASMA_BLK",
        ObjType.MicroMissile => "MICROM_BLK",
        ObjType.DumbMissile  => "MISDUM_BLK",
        ObjType.MiniGun      => "NMSHOT_BLK",
        ObjType.MissilePods  => "MISRAT_BLK",
        ObjType.AirMissile   => "MISRAT_BLK",
        ObjType.GrdMissile   => "MISGRD_BLK",
        ObjType.Bomb         => "BLDGBOMB_PIC",
        ObjType.EnergyGrab   => "POWDIS_BLK",
        ObjType.MegaBomb     => "MEGABM_BLK",
        ObjType.PulseCannon  => "SHOKWV_BLK",
        ObjType.ForwardLaser => "FRNTLAS_BLK",
        ObjType.DeathRay     => "DETHRY_BLK",
        _ => "NMSHOT_BLK",
    };
}
