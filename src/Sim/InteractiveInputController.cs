using System.Collections.Generic;
using Godot;
using Raptor.Sim.Player;
using Raptor.Sim.Shots;

namespace Raptor.Sim;

/// <summary>
/// Godot input bridge for real interactive play. Scripted parity runs keep
/// using PlaythroughDriver; this node stays idle whenever RAPTOR_PLAYTHROUGH is set.
/// </summary>
public partial class InteractiveInputController : Node
{
    private MenuController? _menuController;
    private readonly Queue<ObjType> _specialSelects = new();

    public bool Active { get; private set; }
    public InputState Current { get; private set; } = InputState.Idle;

    public override void _Ready()
    {
        EnsureDefaultActions();
        Active = string.IsNullOrEmpty(OS.GetEnvironment("RAPTOR_PLAYTHROUGH"));
        if (!Active) return;

        _menuController = GetNodeOrNull<MenuController>("../MenuController");
    }

    public override void _PhysicsProcess(double _)
    {
        if (!Active) return;

        Current = ReadInputState();

        if (_menuController?.Menu.InGame == true)
        {
            QueueSpecialSelects();
            return;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active) return;
        if (_menuController?.Menu.InGame == true) return;

        if (@event is InputEventKey keyEvent)
        {
            if (!keyEvent.Pressed || keyEvent.Echo) return;
            string? action = KeyEventToMenuAction(keyEvent);
            if (action == null) return;
            _menuController?.Menu.HandleInput(action, SimClock.Frame);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseButton mouseEvent)
        {
            if (!mouseEvent.Pressed || mouseEvent.ButtonIndex != MouseButton.Left) return;
            var pos = ToGameViewport(mouseEvent.Position, GetViewport().GetVisibleRect().Size);
            if (_menuController?.Menu.HandlePointerClick(pos.X, pos.Y, SimClock.Frame) == true)
                GetViewport().SetInputAsHandled();
        }
    }

    public bool TryDequeueSpecialSelect(out ObjType weapon)
    {
        if (_specialSelects.Count == 0)
        {
            weapon = default;
            return false;
        }

        weapon = _specialSelects.Dequeue();
        return true;
    }

    internal static InputState ComposeState(
        bool right,
        bool left,
        bool down,
        bool up,
        bool fire,
        bool special,
        bool bomb,
        bool pause)
    {
        int dx = (right ? 1 : 0) - (left ? 1 : 0);
        int dy = (down ? 1 : 0) - (up ? 1 : 0);
        return InputState.From(dx, dy, fire, special, bomb, pause);
    }

    internal static string? ActionToMenuKey(string action) => action switch
    {
        "menu_down" => "Down",
        "move_down" => "Down",
        "menu_up" => "Up",
        "move_up" => "Up",
        "menu_left" => "Left",
        "move_left" => "Left",
        "menu_right" => "Right",
        "move_right" => "Right",
        "menu_accept" => "Return",
        "menu_back" => "Escape",
        "menu_help" => "F1",
        _ => null,
    };

    internal static string? KeyToTextAction(Key keycode, long unicode)
    {
        if (keycode == Key.Backspace) return "Backspace";
        if (unicode >= 'a' && unicode <= 'z') return ((char)unicode).ToString();
        if (unicode >= 'A' && unicode <= 'Z') return ((char)unicode).ToString();
        if (unicode >= '0' && unicode <= '9') return ((char)unicode).ToString();
        return null;
    }

    private static ObjType? ActionToSpecial(string action) => action switch
    {
        "special_1" => ObjType.DumbMissile,
        "special_2" => ObjType.MiniGun,
        "special_3" => ObjType.Turret,
        "special_4" => ObjType.MissilePods,
        "special_5" => ObjType.AirMissile,
        "special_6" => ObjType.GrdMissile,
        "special_7" => ObjType.Bomb,
        "special_8" => ObjType.EnergyGrab,
        "special_9" => ObjType.PulseCannon,
        "special_0" => ObjType.DeathRay,
        "special_minus" => ObjType.ForwardLaser,
        _ => null,
    };

    private static void EnsureDefaultActions()
    {
        AddAction("move_up", Key.Up);
        AddAction("move_down", Key.Down);
        AddAction("move_left", Key.Left);
        AddAction("move_right", Key.Right);

        AddAction("fire_main", Key.A, Key.Ctrl, Key.Space);
        AddAction("fire_special", Key.Alt, Key.E);
        AddAction("drop_bomb", Key.Shift, Key.B);
        AddAction("pause", Key.P);

        AddAction("menu_up", Key.Up);
        AddAction("menu_down", Key.Down);
        AddAction("menu_left", Key.Left);
        AddAction("menu_right", Key.Right);
        AddAction("menu_accept", Key.Enter, Key.KpEnter);
        AddAction("menu_back", Key.Escape);
        AddAction("menu_help", Key.F1);

        AddAction("special_1", Key.Key1);
        AddAction("special_2", Key.Key2);
        AddAction("special_3", Key.Key3);
        AddAction("special_4", Key.Key4);
        AddAction("special_5", Key.Key5);
        AddAction("special_6", Key.Key6);
        AddAction("special_7", Key.Key7);
        AddAction("special_8", Key.Key8);
        AddAction("special_9", Key.Key9);
        AddAction("special_0", Key.Key0);
        AddAction("special_minus", Key.Minus);
    }

    private static void AddAction(string action, params Key[] keys)
    {
        if (!InputMap.HasAction(action))
            InputMap.AddAction(action);

        foreach (var key in keys)
        {
            var evt = new InputEventKey { PhysicalKeycode = key };
            if (!InputMap.ActionHasEvent(action, evt))
                InputMap.ActionAddEvent(action, evt);
        }
    }

    private static InputState ReadInputState() => ComposeState(
        Input.IsActionPressed("move_right"),
        Input.IsActionPressed("move_left"),
        Input.IsActionPressed("move_down"),
        Input.IsActionPressed("move_up"),
        Input.IsActionPressed("fire_main"),
        Input.IsActionPressed("fire_special"),
        Input.IsActionPressed("drop_bomb"),
        Input.IsActionPressed("pause"));

    private static string? KeyEventToMenuAction(InputEventKey keyEvent)
    {
        return keyEvent.Keycode switch
        {
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Enter => "Return",
            Key.KpEnter => "Return",
            Key.Escape => "Escape",
            Key.F1 => "F1",
            Key.F2 => "F2",
            Key.Alt => "Alt",       // registration: cycle the ID portrait
            Key.Ctrl => "Ctrl",
            Key.Space => "Space",
            Key.Home => "Home",
            Key.End => "End",
            Key.Pageup => "PageUp",
            Key.Pagedown => "PageDown",
            Key.Tab => "Tab",
            _ => KeyToTextAction(keyEvent.Keycode, keyEvent.Unicode),
        };
    }

    internal static Vector2I ToGameViewport(Vector2 pos, Vector2 viewportSize)
    {
        if (pos.X >= 0 && pos.X <= 320 && pos.Y >= 0 && pos.Y <= 200)
            return new Vector2I(Mathf.FloorToInt(pos.X), Mathf.FloorToInt(pos.Y));

        float sx = viewportSize.X > 0 ? 320f / viewportSize.X : 1f;
        float sy = viewportSize.Y > 0 ? 200f / viewportSize.Y : 1f;
        int x = Mathf.Clamp(Mathf.FloorToInt(pos.X * sx), 0, 319);
        int y = Mathf.Clamp(Mathf.FloorToInt(pos.Y * sy), 0, 199);
        return new Vector2I(x, y);
    }

    private void QueueSpecialSelects()
    {
        foreach (string action in SpecialActions)
        {
            if (!Input.IsActionJustPressed(action)) continue;
            var weapon = ActionToSpecial(action);
            if (weapon.HasValue)
                _specialSelects.Enqueue(weapon.Value);
        }
    }

    private static readonly string[] SpecialActions =
    {
        "special_1",
        "special_2",
        "special_3",
        "special_4",
        "special_5",
        "special_6",
        "special_7",
        "special_8",
        "special_9",
        "special_0",
        "special_minus",
    };
}
