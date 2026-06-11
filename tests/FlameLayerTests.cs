using Raptor.View;
using Xunit;

namespace Raptor.Tests;

// Regression guard for the "vertical white gradient bars" bug: the additive engine-flame
// layer (DebugRenderer._flameLayer) is redrawn every frame from _flameQuads, but the quads
// are only cleared+repopulated in _Draw's playfield branch. On death (wave → death movie →
// main menu) _Draw takes the cutscene/menu branch, leaving the last gameplay frame's flame
// quads stale; they then bleed as white bars over the cutscene and the menu. The fix gates
// OnFlameLayerDraw on ShouldRenderPlayfield, which must be FALSE for those non-playfield states.
public class FlameLayerTests
{
    [Fact]
    public void Flame_layer_hidden_during_death_cutscene()
    {
        // Death/Landing/Victory cutscene: wave still exists, not a briefing, interactive,
        // menu not InGame, and gameplay no longer visually active (_waveActive == false).
        Assert.False(DebugRenderer.ShouldRenderPlayfield(
            hasWave: true,
            inLoadCompBriefing: false,
            interactiveUi: true,
            menuInGame: false,
            gameplayVisualActive: false));
    }

    [Fact]
    public void Flame_layer_hidden_at_main_menu()
    {
        // Same shape as the death cutscene: at the menu the WaveController persists
        // (hasWave true) but the wave is inactive, so the playfield is not rendered.
        Assert.False(DebugRenderer.ShouldRenderPlayfield(
            hasWave: true,
            inLoadCompBriefing: false,
            interactiveUi: true,
            menuInGame: false,
            gameplayVisualActive: false));
    }

    [Fact]
    public void Flame_layer_renders_during_active_gameplay()
    {
        // In a live wave the menu overlay is suppressed (gameplay visually active), so
        // the playfield — and the flames — render.
        Assert.True(DebugRenderer.ShouldRenderPlayfield(
            hasWave: true,
            inLoadCompBriefing: false,
            interactiveUi: true,
            menuInGame: true,
            gameplayVisualActive: true));
    }

    [Fact]
    public void Flame_layer_renders_during_demo_gameplay_even_when_not_ingame()
    {
        // Direct demo playback keeps GameplayVisualActive true while InGame stays false
        // (mirrors ShouldDrawMenuOverlayForState's demo case).
        Assert.True(DebugRenderer.ShouldRenderPlayfield(
            hasWave: true,
            inLoadCompBriefing: false,
            interactiveUi: true,
            menuInGame: false,
            gameplayVisualActive: true));
    }

    [Fact]
    public void Flame_layer_hidden_during_loadcomp_briefing()
    {
        Assert.False(DebugRenderer.ShouldRenderPlayfield(
            hasWave: true,
            inLoadCompBriefing: true,
            interactiveUi: true,
            menuInGame: false,
            gameplayVisualActive: true));
    }

    [Fact]
    public void Flame_layer_hidden_when_no_wave()
    {
        Assert.False(DebugRenderer.ShouldRenderPlayfield(
            hasWave: false,
            inLoadCompBriefing: false,
            interactiveUi: true,
            menuInGame: false,
            gameplayVisualActive: false));
    }
}
