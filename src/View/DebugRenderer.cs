using Godot;
using Raptor.Sim;
using Raptor.Sim.Bullet;
using Raptor.Sim.Enemy;

namespace Raptor.View;

public partial class DebugRenderer : Node2D
{
    private WaveController? _wave;
    private MenuStateMachine? _menu;
    private string? _shotDir;
    private int _lastShotSec = -1;

    public override void _Ready()
    {
        var wc = GetNodeOrNull<WaveController>("../WaveController");
        if (wc != null) _wave = wc;

        var menu = GetNodeOrNull<MenuController>("../MenuController");
        if (menu != null) _menu = menu.Menu;

        _shotDir = OS.GetEnvironment("RAPTOR_SHOT_DIR");
        if (!string.IsNullOrEmpty(_shotDir))
        {
            DirAccess.MakeDirRecursiveAbsolute(_shotDir);
        }

        ZIndex = 100;
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
        MaybeShoot();
    }

    private void MaybeShoot()
    {
        if (string.IsNullOrEmpty(_shotDir)) return;
        int sec = SimClock.Frame / 70;
        if (sec == _lastShotSec) return;
        _lastShotSec = sec;
        CallDeferred(nameof(WriteShot), sec, SimClock.Frame);
    }

    private void WriteShot(int sec, int fc)
    {
        var img = GetViewport().GetTexture().GetImage();
        if (img == null) return;
        string path = $"{_shotDir}/fc{fc:D5}_sec{sec:D3}.png";
        img.SavePng(path);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, 320, 200), new Color(0, 0, 0, 1));
        DrawRect(new Rect2(16, 0, 288, 200), new Color(0.05f, 0.05f, 0.1f, 1));

        if (_wave == null) return;

        var px = _wave.PlayerLogic.X;
        var py = _wave.PlayerLogic.Y;
        DrawRect(new Rect2(px - 16, py - 16, 32, 32), new Color(0, 1, 0, 0.7f));
        DrawRect(new Rect2(px - 1, py - 1, 2, 2), new Color(1, 1, 1));

        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            DrawRect(new Rect2(e.X - e.HalfW, e.Y - e.HalfH, e.HalfW * 2, e.HalfH * 2),
                new Color(1, 0.3f, 0.3f, 0.7f));
        }

        foreach (var b in _wave.GetEnemyBullets())
        {
            if (!b.Alive) continue;
            DrawRect(new Rect2(b.X - 2, b.Y - 2, 4, 4), new Color(1, 1, 0));
        }

        foreach (var b in _wave.GetPlayerBullets())
        {
            if (!b.Alive) continue;
            DrawRect(new Rect2(b.X - 2, b.Y - 2, 4, 4), new Color(0, 1, 1));
        }

        var sf = SimClock.Frame;
        var win = _menu?.State.ToString() ?? "?";
        var hud = $"fc={sf}  win={win}  shield={_wave.PlayerLogic.Shield}  score={_wave.Score}  E={_wave.GetEnemies().Count}  EB={_wave.GetEnemyBullets().Count}  PB={_wave.GetPlayerBullets().Count}";
        var font = ThemeDB.FallbackFont;
        DrawString(font, new Vector2(4, 195), hud, HorizontalAlignment.Left, -1, 8, new Color(1, 1, 1));
    }
}
