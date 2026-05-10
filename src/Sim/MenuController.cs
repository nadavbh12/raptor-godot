using Godot;
using Raptor.Test;

namespace Raptor.Sim;

/// <summary>
/// Godot Node wrapper that owns a <see cref="MenuStateMachine"/> and wires it
/// to the <see cref="ParityEmitter"/>. Notifies the emitter whenever the menu
/// state machine transitions so the emitter can reset its per-context counter.
///
/// _Ready is called after all nodes are in the tree; GetNode calls are safe.
/// </summary>
public partial class MenuController : Node
{
    public MenuStateMachine Menu { get; } = new MenuStateMachine();

    public override void _Ready()
    {
        var emitter = GetNodeOrNull<ParityEmitter>("../ParityEmitter");
        if (emitter != null)
        {
            emitter.Menu = Menu;
            Menu.OnStateChanged += emitter.OnStateChanged;
        }

        // Enter MENU state immediately — mirrors raptor_parity_set_win_state(1)
        // called right after WIN_MainMenu shows its window.
        Menu.EnterMenu(SimClock.Frame);
    }
}
