# Save dialog + save/load round-trip Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for task-by-task inline execution. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Save the active pilot from the hangar via an ASK_SWD YES/NO dialog and have the saved pilot reappear in the LOAD list, closing the `save_load.txt` parity script.

**Architecture:** A new `PilotSaveStore.Save(slot, ...)` writes encrypted 88-byte headers using the inverse of the existing CASTLE-XOR decrypt. `MenuStateMachine` gains a generic `AskBool` sub-state (question text + YES/NO selection + on-confirm callback) wired into the hangar F2/S handler. `DebugRenderer` renders the ASK_SWD overlay on top of whatever state is underneath. Slot allocation: first unused `CHAR<slot>.FIL`.

**Tech Stack:** C# / .NET 8 / xUnit. Real filesystem I/O via `TempDir` fixtures (no mocking).

**Out of scope (deferred):** Persistent `filepos` across sessions, `RAP_LoadPlayer` body parsing (actually-loading the pilot into game state on Return in LOAD dialog), "No Pilots to Load" popup, multi-slot rotation, delete-pilot button. The `save_load.txt` script doesn't exercise these.

**External dependencies (tracer-bullet tested un-mocked):**
- Filesystem (write encrypted CHAR<slot>.FIL, read back via existing `PilotSaveStore.LoadAll`). All save/load tests use real files in `TempDir`.

---

### Task 1: `PilotSaveStore.Save` writes an encrypted header readable by LoadAll

**Files:**
- Modify: `src/Sim/PilotSaveStore.cs`
- Test: `tests/PilotSaveStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `tests/PilotSaveStoreTests.cs`:

```csharp
[Fact]
public void Save_writes_pilot_summary_readable_by_LoadAll()
{
    using var dir = new TempDir();

    int slot = PilotSaveStore.Save(dir.Path, name: "TEST", callsign: "T1", idPic: 2, score: 0);

    Assert.Equal(0, slot);
    var pilots = PilotSaveStore.LoadAll(dir.Path);
    Assert.Single(pilots);
    Assert.Equal("TEST", pilots[0].Name);
    Assert.Equal("T1", pilots[0].Callsign);
    Assert.Equal(2, pilots[0].IdPic);
    Assert.Equal(0u, pilots[0].Score);
}

[Fact]
public void Save_assigns_next_available_slot()
{
    using var dir = new TempDir();
    WriteFakePilot(dir.Path, slot: 0, name: "ALICE", callsign: "ACE", idPic: 0, score: 1000);
    WriteFakePilot(dir.Path, slot: 1, name: "BOB", callsign: "BEAR", idPic: 1, score: 2000);

    int slot = PilotSaveStore.Save(dir.Path, name: "CAROL", callsign: "CAT", idPic: 3, score: 99);

    Assert.Equal(2, slot);
    Assert.True(File.Exists(Path.Combine(dir.Path, "CHAR0002.FIL")));
}

[Fact]
public void Save_round_trips_through_decrypt()
{
    using var dir = new TempDir();
    PilotSaveStore.Save(dir.Path, name: "ROUNDTRIP", callsign: "RT", idPic: 1, score: 12345);
    var pilots = PilotSaveStore.LoadAll(dir.Path);
    Assert.Single(pilots);
    Assert.Equal("ROUNDTRIP", pilots[0].Name);
    Assert.Equal("RT", pilots[0].Callsign);
    Assert.Equal(1, pilots[0].IdPic);
    Assert.Equal(12345u, pilots[0].Score);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DOTNET_ROLL_FORWARD=Major dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~PilotSaveStoreTests"`
Expected: FAIL on `Save` method missing (compile error).

- [ ] **Step 3: Write minimal implementation**

In `src/Sim/PilotSaveStore.cs`, add an `Encrypt` helper and the `Save` method:

```csharp
public static int Save(string directory, string name, string callsign, int idPic, uint score)
{
    Directory.CreateDirectory(directory);
    int slot = NextAvailableSlot(directory);
    string path = Path.Combine(directory, $"CHAR{slot:D4}.FIL");

    byte[] header = new byte[PlayerHeaderSize];
    byte[] nameBytes = Encoding.ASCII.GetBytes(name);
    Array.Copy(nameBytes, 0, header, 0, Math.Min(nameBytes.Length, 19));
    byte[] callBytes = Encoding.ASCII.GetBytes(callsign);
    Array.Copy(callBytes, 0, header, 20, Math.Min(callBytes.Length, 11));
    BitConverter.GetBytes(idPic).CopyTo(header, 32);
    BitConverter.GetBytes(score).CopyTo(header, 36);

    Encrypt(header);
    File.WriteAllBytes(path, header);
    return slot;
}

private static int NextAvailableSlot(string directory)
{
    for (int slot = 0; slot < MaxSave; slot++)
    {
        if (!File.Exists(Path.Combine(directory, $"CHAR{slot:D4}.FIL")))
            return slot;
    }
    throw new InvalidOperationException("All pilot slots in use.");
}

// Inverse of Decrypt: E[i] = P[i] + Key[ki] + prev_E[i-1].
private static void Encrypt(byte[] buffer)
{
    int keyIndex = 0x0019 % SaveKey.Length;
    int previous = SaveKey[keyIndex];
    for (int i = 0; i < buffer.Length; i++)
    {
        int encrypted = (buffer[i] + SaveKey[keyIndex] + previous) & 0xFF;
        buffer[i] = (byte)encrypted;
        previous = encrypted;
        keyIndex++;
        if (keyIndex >= SaveKey.Length) keyIndex = 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build raptor.csproj && DOTNET_ROLL_FORWARD=Major dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~PilotSaveStoreTests"`
Expected: PASS, 6/6 (3 existing + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/Sim/PilotSaveStore.cs tests/PilotSaveStoreTests.cs
git commit -m "Add PilotSaveStore.Save for encrypted pilot writes"
```

---

### Task 2: `MenuStateMachine` AskBool sub-state with YES/NO selection

**Files:**
- Modify: `src/Sim/MenuStateMachine.cs`
- Test: `tests/MenuStateMachineTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/MenuStateMachineTests.cs`:

```csharp
[Fact]
public void F2_in_hangar_opens_AskBool_save_prompt()
{
    var m = HangarReadyMachineWithSaveDir(out var dir);
    using (dir)
    {
        m.HandleInput("F2", 100);

        Assert.True(m.InAskBool);
        Assert.Equal("Save TEST - T1 ?", m.AskBoolQuestion);
        Assert.True(m.AskBoolYesSelected);
        Assert.Equal(WinState.Hangar, m.State);
    }
}

[Fact]
public void Return_with_YES_in_AskBool_save_writes_pilot_and_closes()
{
    var m = HangarReadyMachineWithSaveDir(out var dir);
    using (dir)
    {
        m.HandleInput("F2", 100);
        m.HandleInput("Return", 110);

        Assert.False(m.InAskBool);
        Assert.Equal(WinState.Hangar, m.State);
        var pilots = PilotSaveStore.LoadAll(dir.Path);
        Assert.Single(pilots);
        Assert.Equal("TEST", pilots[0].Name);
        Assert.Equal("T1", pilots[0].Callsign);
    }
}

[Fact]
public void Left_Right_Tab_toggle_AskBool_selection()
{
    var m = HangarReadyMachineWithSaveDir(out var dir);
    using (dir)
    {
        m.HandleInput("F2", 100);
        Assert.True(m.AskBoolYesSelected);

        m.HandleInput("Right", 110);
        Assert.False(m.AskBoolYesSelected);

        m.HandleInput("Left", 120);
        Assert.True(m.AskBoolYesSelected);

        m.HandleInput("Tab", 130);
        Assert.False(m.AskBoolYesSelected);
    }
}

[Fact]
public void Return_with_NO_in_AskBool_save_closes_without_writing()
{
    var m = HangarReadyMachineWithSaveDir(out var dir);
    using (dir)
    {
        m.HandleInput("F2", 100);
        m.HandleInput("Right", 110); // → NO
        m.HandleInput("Return", 120);

        Assert.False(m.InAskBool);
        Assert.Empty(PilotSaveStore.LoadAll(dir.Path));
    }
}

[Fact]
public void Escape_in_AskBool_closes_without_writing()
{
    var m = HangarReadyMachineWithSaveDir(out var dir);
    using (dir)
    {
        m.HandleInput("F2", 100);
        m.HandleInput("Escape", 110);

        Assert.False(m.InAskBool);
        Assert.Empty(PilotSaveStore.LoadAll(dir.Path));
    }
}

private static MenuStateMachine HangarReadyMachineWithSaveDir(out PilotSaveStoreTests.TempDir dir)
{
    dir = new PilotSaveStoreTests.TempDir();
    var m = new MenuStateMachine { PilotSaveDirectory = dir.Path };
    m.EnterMenu(0);
    // Create pilot TEST / T1 / difficulty 3 → lands in hangar
    m.HandleInput("Return", 1); // Return on NEW MISSION
    foreach (var ch in "TEST") m.HandleInput(ch.ToString(), 2);
    m.HandleInput("Return", 3);
    foreach (var ch in "T1") m.HandleInput(ch.ToString(), 4);
    m.HandleInput("Return", 5);
    m.HandleInput("Return", 6); // accept default difficulty
    return m;
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DOTNET_ROLL_FORWARD=Major dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests"`
Expected: FAIL on `InAskBool` / `AskBoolQuestion` / `AskBoolYesSelected` missing (compile error).

- [ ] **Step 3: Write minimal implementation**

In `src/Sim/MenuStateMachine.cs`:

Add fields near `_inLoadMission`:

```csharp
private bool _inAskBool = false;
private string _askBoolQuestion = "";
private bool _askBoolYes = true;
private Action? _askBoolOnYes;
```

Add public surface near `InLoadMission`:

```csharp
public bool InAskBool => _inAskBool;
public string AskBoolQuestion => _askBoolQuestion;
public bool AskBoolYesSelected => _askBoolYes;
```

In `HandleInput`, route AskBool first (it overlays any state). Just below the existing `if (_inOptions) return HandleOptionsInput(action);` (which is inside `HandleMenuInput`), and at the top of `HandleHangarInput`, add a shared check via a new private method:

```csharp
private bool HandleAskBoolInput(string action)
{
    switch (action)
    {
        case "Left":
        case "Right":
        case "Tab":
        case "Up":
        case "Down":
            _askBoolYes = !_askBoolYes;
            return true;
        case "Escape":
            _inAskBool = false;
            _askBoolOnYes = null;
            return true;
        case "Return":
        case "Space":
            bool yes = _askBoolYes;
            var cb = _askBoolOnYes;
            _inAskBool = false;
            _askBoolOnYes = null;
            if (yes) cb?.Invoke();
            return true;
    }
    return true; // absorb other keys
}
```

In `HandleHangarInput`, at the very top:

```csharp
if (_inAskBool) return HandleAskBoolInput(action);
if (action == "F2" || action == "S")
{
    OpenAskBoolSave();
    return true;
}
```

Add `OpenAskBoolSave` method on `MenuStateMachine`:

```csharp
private void OpenAskBoolSave()
{
    _askBoolQuestion = $"Save {PilotName} - {Callsign} ?";
    _askBoolYes = true;
    _askBoolOnYes = () => PilotSaveStore.Save(
        PilotSaveDirectory ?? Directory.GetCurrentDirectory(),
        PilotName, Callsign, idPic: 0, score: 0);
    _inAskBool = true;
}
```

Add `using System.IO;` at top if not present.

In `EnterMenu`, also reset the AskBool state:

```csharp
_inAskBool = false;
_askBoolQuestion = "";
_askBoolYes = true;
_askBoolOnYes = null;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build raptor.csproj && DOTNET_ROLL_FORWARD=Major dotnet test tests/RaptorTests.csproj --filter "FullyQualifiedName~MenuStateMachineTests"`
Expected: PASS, all tests including the 5 new AskBool tests.

- [ ] **Step 5: Commit**

```bash
git add src/Sim/MenuStateMachine.cs tests/MenuStateMachineTests.cs
git commit -m "Add AskBool sub-state with F2 hangar save wiring"
```

---

### Task 3: Render ASK_SWD overlay in DebugRenderer

**Files:**
- Modify: `src/View/DebugRenderer.cs`

- [ ] **Step 1: Add the overlay path (no unit test — pixel parity is the test)**

In `DebugRenderer.cs`, near `DrawLoadMissionOverlay`, add `DrawAskBoolOverlay`:

```csharp
private void DrawAskBoolOverlay(MenuStateMachine menu)
{
    var swd = LoadSwd("ASK_SWD");
    if (swd == null) return;

    int selectedFieldId = menu.AskBoolYesSelected ? 2 : 3;  // YES=id2, NO=id3
    SwdRenderer.Draw(_swdHost, swd, selectedFieldId: selectedFieldId);

    var dragbar = swd.Fields[5]; // DRAGBAR field, id=110, position (26,11)
    DrawDosFont(menu.AskBoolQuestion,
        swd.Window.X + dragbar.X,
        swd.Window.Y + dragbar.Y,
        dragbar.FontName,
        dragbar.FontBaseColor);
}
```

In `DrawHangarOverlay` (and wherever the hangar/menu overlays are drawn from `_Draw`), call after the underlying overlay:

```csharp
if (menu.InAskBool)
    DrawAskBoolOverlay(menu);
```

The AskBool overlay must paint over whatever state is current. Search for existing `if (menu.InLoadMission)` and add the analogous block right after.

- [ ] **Step 2: Verify build green**

Run: `dotnet build raptor.csproj`
Expected: PASS, no warnings.

- [ ] **Step 3: Run full unit suite**

Run: `DOTNET_ROLL_FORWARD=Major dotnet test tests/RaptorTests.csproj --logger "console;verbosity=minimal"`
Expected: PASS, 309+ tests (no regressions from prior 306).

- [ ] **Step 4: Commit**

```bash
git add src/View/DebugRenderer.cs
git commit -m "Render ASK_SWD overlay for AskBool prompts"
```

---

### Task 4: Run `save_load.txt` parity sweep and iterate

**Files:** No code edits expected unless parity fails.

- [ ] **Step 1: Run the sweep**

```bash
OUT_ROOT=/Users/nadavb/dev/raptor-godot/dumps/menu_pixel_parity_save_load_$(date +%Y%m%d_%H%M%S) \
    SCRIPTS=save_load \
    NO_FAIL=1 \
    MAX_MISMATCH_PCT=20 \
    RAPTOR_SAVE_DIR=/Users/nadavb/dev/dosraptor \
    tests/run_menu_pixel_parity.sh
```

Expected: 5 labels — `01_hangar`, `02_save_dialog`, `03_after_save`, `04_menu_after_save`, `05_load_screen` — should pass under 20% budget.

- [ ] **Step 2: Inspect failures (if any)**

For any label > 20% mismatch, look at `dumps/menu_pixel_parity_save_load_<stamp>/diff/save_load/<label>.panel.png`. Classify per `docs/playbooks/parity/failure-diagnosis.md`. Fix render-missing / state-missing issues; loop back to Task 3 if a fix is needed.

- [ ] **Step 3: Commit any fixes**

If any fixes were needed:

```bash
git add <files>
git commit -m "Fix save_load parity: <specific issue>"
```

---

### Task 5: Update coverage matrix + finalize state

**Files:**
- Modify: `tests/parity/scenarios/coverage.md`

- [ ] **Step 1: Update LOADSAVE.C row**

Replace the existing `LOADSAVE.C` row to include the save side and `save_load` probe. Edit in place:

```markdown
| `LOADSAVE.C` | Pilot save enumeration, encrypted CHAR<slot>.FIL load + save, RAP_LoadWin nav, ASK_SWD save confirm | `PilotSaveStoreTests`, `MenuStateMachineTests` load/AskBool suites, `load_mission` + `save_load` menu pixel probes | RAP_LoadPlayer body parsing, WIN_Msg "No Pilots" popup, delete-pilot | save/load probe | covered | Save/load round-trip closes via ASK_SWD; Return on LOAD currently dismisses without applying pilot to game state — deferred. |
```

- [ ] **Step 2: Commit**

```bash
git add tests/parity/scenarios/coverage.md
git commit -m "Mark save_load and load_mission probes covered in matrix"
```

---

## Self-Review

**Spec coverage:**
- Save dialog renders → Task 3
- F2/S hangar wiring → Task 2
- WIN_AskBool YES/NO + Esc → Task 2
- Encrypted file write → Task 1
- Save → reload round-trip visible in LOAD list → Task 4 (parity sweep is the acceptance)
- Coverage matrix → Task 5

**External dependency coverage:**
- Filesystem write of encrypted save → Task 1 tests (real file I/O, no mocks, `WriteAllBytes` then `LoadAll` reads back).
- Filesystem read in `LoadAll` → already covered by existing `PilotSaveStoreTests`.

**No placeholders:** every step has code or a concrete command.

**Type consistency:** `InAskBool`, `AskBoolQuestion`, `AskBoolYesSelected`, `Save(directory, name, callsign, idPic, score)` consistent across tasks.

**Out-of-scope notes (already in plan header):** RAP_LoadPlayer body apply, "No Pilots" popup, persistent filepos, delete-pilot.
