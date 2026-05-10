using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly Dictionary<string, Texture2D> _spriteCache = new();
    private readonly Dictionary<string, string> _spritePaths = new();
    private Texture2D? _playerTex;

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

        BuildSpriteIndex();
        _playerTex = LoadSprite("LPLAYER_PIC");

        ZIndex = 100;
    }

    private void BuildSpriteIndex()
    {
        // Files look like "0303_SHIP01G1_PIC.png". Map iname → first PNG path
        // (lowest sequential prefix becomes the canonical first frame).
        string root = ProjectSettings.GlobalizePath("res://assets/sprites");
        if (!Directory.Exists(root)) return;

        foreach (string path in Directory.GetFiles(root, "*.png").OrderBy(s => s))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int u = name.IndexOf('_');
            if (u < 0) continue;
            string iname = name.Substring(u + 1);
            if (!_spritePaths.ContainsKey(iname)) _spritePaths[iname] = path;
        }
    }

    private Texture2D? LoadSprite(string iname)
    {
        if (_spriteCache.TryGetValue(iname, out var cached)) return cached;
        if (!_spritePaths.TryGetValue(iname, out var path)) return null;
        var img = Image.LoadFromFile(path);
        if (img == null) return null;
        var tex = ImageTexture.CreateFromImage(img);
        _spriteCache[iname] = tex;
        return tex;
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

        if (_playerTex != null)
        {
            var sz = _playerTex.GetSize();
            DrawTexture(_playerTex, new Vector2(px - sz.X / 2f, py - sz.Y / 2f));
        }
        else
        {
            DrawRect(new Rect2(px - 16, py - 16, 32, 32), new Color(0, 1, 0, 0.7f));
        }

        foreach (var e in _wave.GetEnemies())
        {
            if (!e.Alive) continue;
            var tex = LoadSprite(e.Meta.IName);
            if (tex != null)
            {
                var sz = tex.GetSize();
                DrawTexture(tex, new Vector2(e.X - sz.X / 2f, e.Y - sz.Y / 2f));
            }
            else
            {
                DrawRect(new Rect2(e.X - e.HalfW, e.Y - e.HalfH, e.HalfW * 2, e.HalfH * 2),
                    new Color(1, 0.3f, 0.3f, 0.7f));
            }
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
