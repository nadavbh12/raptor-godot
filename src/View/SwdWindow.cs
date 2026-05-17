using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace Raptor.View;

// In-memory mirror of one extracted dosraptor SWD layout (assets/swd/*.json).
// Field positions are stored relative to the window — render code adds
// window.X/Y to land them on screen, matching SWD_PutField's `fld_x =
// curfld->x + curwin->x` rule.
internal sealed class SwdWindow
{
    public string Name { get; }
    public WindowSpec Window { get; }
    public IReadOnlyList<Field> Fields { get; }

    public readonly record struct WindowSpec(
        string Name, string ItemName, int X, int Y, int Lx, int Ly,
        int Picflag, int FirstFld);

    public readonly record struct Field(
        int Index, int Id, string Opt, int OptCode,
        int X, int Y, int Lx, int Ly,
        uint Hotkey, int Kbflag, int InputOpt,
        string Name, string ItemName, uint Item,
        string FontName, uint FontId, int FontBaseColor,
        int MaxChars, int PicFlag, int Color, int Lite,
        string Text);

    private SwdWindow(string name, WindowSpec window, IReadOnlyList<Field> fields)
    {
        Name = name;
        Window = window;
        Fields = fields;
    }

    public static SwdWindow Load(string resPath)
    {
        string p = ProjectSettings.GlobalizePath(resPath);
        var json = File.ReadAllText(p);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string name = root.GetProperty("name").GetString() ?? "";
        var w = root.GetProperty("window");
        var win = new WindowSpec(
            Name: w.GetProperty("name").GetString() ?? "",
            ItemName: w.GetProperty("item_name").GetString() ?? "",
            X: w.GetProperty("x").GetInt32(),
            Y: w.GetProperty("y").GetInt32(),
            Lx: w.GetProperty("lx").GetInt32(),
            Ly: w.GetProperty("ly").GetInt32(),
            Picflag: w.GetProperty("picflag").GetInt32(),
            FirstFld: w.GetProperty("firstfld").GetInt32());

        var fields = new List<Field>();
        foreach (var f in root.GetProperty("fields").EnumerateArray())
        {
            // opt is either a string ("FLD_TEXT") or an int (for opcodes
            // our extractor didn't symbolize). Track both.
            string optStr;
            int optCode;
            var optEl = f.GetProperty("opt");
            if (optEl.ValueKind == JsonValueKind.String)
            {
                optStr = optEl.GetString() ?? "";
                optCode = OptCodeOf(optStr);
            }
            else
            {
                optCode = optEl.GetInt32();
                optStr = NameOf(optCode);
            }

            fields.Add(new Field(
                Index: f.GetProperty("index").GetInt32(),
                Id: f.GetProperty("id").GetInt32(),
                Opt: optStr, OptCode: optCode,
                X: f.GetProperty("x").GetInt32(),
                Y: f.GetProperty("y").GetInt32(),
                Lx: f.GetProperty("lx").GetInt32(),
                Ly: f.GetProperty("ly").GetInt32(),
                Hotkey: f.GetProperty("hotkey").GetUInt32(),
                Kbflag: f.GetProperty("kbflag").GetInt32(),
                InputOpt: f.GetProperty("input_opt").GetInt32(),
                Name: f.GetProperty("name").GetString() ?? "",
                ItemName: f.GetProperty("item_name").GetString() ?? "",
                Item: f.GetProperty("item").GetUInt32(),
                FontName: f.GetProperty("font_name").GetString() ?? "",
                FontId: f.GetProperty("fontid").GetUInt32(),
                FontBaseColor: f.GetProperty("fontbasecolor").GetInt32(),
                MaxChars: f.GetProperty("maxchars").GetInt32(),
                PicFlag: f.GetProperty("picflag").GetInt32(),
                Color: f.GetProperty("color").GetInt32(),
                Lite: f.GetProperty("lite").GetInt32(),
                Text: f.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : ""));
        }
        return new SwdWindow(name, win, fields);
    }

    // Matches dosraptor/GFX/SWDAPI.H FLD_* defines.
    private static int OptCodeOf(string s) => s switch
    {
        "FLD_OFF"      => 0,
        "FLD_TEXT"     => 1,
        "FLD_BUTTON"   => 2,
        "FLD_INPUT"    => 3,
        "FLD_MARK"     => 4,
        "FLD_CLOSE"    => 5,
        "FLD_DRAGBAR"  => 6,
        "FLD_BUMPIN"   => 7,
        "FLD_BUMPOUT"  => 8,
        "FLD_ICON"     => 9,
        "FLD_OBJAREA"  => 10,
        "FLD_VIEWAREA" => 11,
        _ => -1,
    };

    private static string NameOf(int code) => code switch
    {
        0  => "FLD_OFF",
        1  => "FLD_TEXT",
        2  => "FLD_BUTTON",
        3  => "FLD_INPUT",
        4  => "FLD_MARK",
        5  => "FLD_CLOSE",
        6  => "FLD_DRAGBAR",
        7  => "FLD_BUMPIN",
        8  => "FLD_BUMPOUT",
        9  => "FLD_ICON",
        10 => "FLD_OBJAREA",
        11 => "FLD_VIEWAREA",
        _  => $"FLD_UNK({code})",
    };
}
