# GX100 SimHub Plugin Plan

## Context

The user has a GX100 USB shifter with an existing Linux control tool (Foxblat plugin at `/home/rorth/src/foxblat/plugins/gx100/`) and a Linux kernel HID driver (`/home/rorth/src/hid-gx100-shifter/`). They also have a mature SimHub plugin for MOZA Racing hardware (`/home/rorth/src/moza-simhub-plugin/`) that demonstrates the exact patterns to follow.

**Goal:** Create a new standalone SimHub plugin that lets users configure GX100 shifter settings from within SimHub, with per-game profiles (e.g., H-pattern mode + sensitivity 180 for ETS2, sequential mode + sensitivity 200 for ACC).

## Device Summary

- **USB HID** -- VID `0x04B0`, PID `0x5750` (Nidec Copal Electronics)
- **Input:** 16 buttons in 2 bytes (gears 1-8, reverse, seq up/down, 5 extra buttons), 14 padding bytes
- **Output:** 64-byte HID reports, 2 report types:
  - `0x01` -- Calibration: `[0x01, cmd]` where cmd = 0x01/0x02 (H start/finish), 0x03/0x04 (seq start/finish)
  - `0x02` -- Config: `[0x02, combo_sw1, combo_sw2, seq_mode, h_sens, 0x00, seq_sens, 0x00, pull_btn]`
- **6 configurable settings:** h_sensitivity (0-255), seq_sensitivity (0-255), combo_switch1, combo_switch2, sequential_mode, pull_button

## Project Structure

New project at `/home/rorth/src/gx100-simhub-plugin/`:

```
GX100Plugin.sln
GX100Plugin.csproj
Properties/
  AssemblyInfo.cs
libs/
  SimHub/                          # copied from moza-simhub-plugin/libs/SimHub/
Protocol/
  GX100Protocol.cs                 # VID/PID, report byte constants, button bit mappings
  GX100HidConnection.cs            # HidSharp-based HID connection, send/receive, hotplug
Telemetry/
  GX100Data.cs                     # volatile fields for device + input state
UI/
  GX100PluginSettings.cs           # persisted settings via ReadCommonSettings
  GX100Profile.cs                  # ProfileBase<T,S> with -1 sentinels
  GX100ProfileStore.cs             # ProfileSettingsBase<T,S>
  SettingsControl.xaml              # WPF UI
  SettingsControl.xaml.cs           # code-behind with _suppressEvents + 500ms refresh
GX100Plugin.cs                     # IPlugin, IDataPlugin, IWPFSettingsV2 entry point
```

No `Devices/` or `DeviceTemplates/` directories -- the GX100 is a shifter with no LEDs, so no ILedDeviceManager or device extension is needed.

## Layer Design

### 1. Protocol (`Protocol/`)

**`GX100Protocol.cs`** -- Constants only (~30 lines)
- VID, PID, output report length (64)
- Calibration report bytes (0x01 + 4 sub-commands)
- Config report byte (0x02) and field offsets
- Button bit positions for input report parsing (buttons 1-16)

**`GX100HidConnection.cs`** -- HID I/O via HidSharp (~250 lines)
- `Connect()` -- enumerate by VID/PID via `DeviceList.Local.GetHidDevices()`, open stream
- `Disconnect()` -- close stream
- `SendCalibration(byte cmd)` -- build 64-byte report `[0x01, cmd, 0x00...]`, write to stream
- `SendConfiguration(config)` -- build 64-byte config report, write to stream
- `IsConnected` property
- Background read thread for input reports -- parses 2-byte button field, fires `ButtonStateChanged(ushort)`
- Hotplug via `DeviceList.Local.Changed` event for auto-reconnect
- **No command database or response parser needed** -- protocol is just 2 fixed report formats

**NuGet dependency: `HidSharp` (2.1.0+)**
- Check if SimHub already ships `HidSharp.dll` first. If so, reference theirs and set `<Private>false</Private>`. Otherwise ship it alongside the plugin DLL.

### 2. Data Model (`Telemetry/GX100Data.cs`) (~50 lines)

Volatile fields:
- `IsConnected` (bool)
- Configuration state: `HSensitivity`, `SeqSensitivity`, `ComboSwitch1`, `ComboSwitch2`, `SequentialMode`, `PullButton`
- Input state: `CurrentGear` (int: -1=R, 0=N, 1-8), `SeqUp`, `SeqDown`, `Button1-5` (bool), `RawButtons` (ushort)
- Calibration: `IsHCalibrating`, `IsSeqCalibrating`
- `UpdateFromButtons(ushort raw)` -- derive `CurrentGear` and individual button bools from the 16-bit button field

### 3. Plugin Entry (`GX100Plugin.cs`) (~250 lines)

Pattern: identical to `MozaPlugin.cs` (ref: `/home/rorth/src/moza-simhub-plugin/MozaPlugin.cs`)

```csharp
[PluginDescription("Configure GX100 USB shifter with per-game profiles")]
[PluginAuthor("giantorth")]
[PluginName("GX100 Shifter")]
public class GX100Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
```

- **`Init()`**: create data/connection/settings, init profile system (same pattern as Moza `InitProfileSystem()`), register SimHub properties, start reconnect timer (5s), subscribe to connection events
- **`DataUpdate()`**: empty (input read on background thread)
- **`End()`**: save settings, disconnect, stop timers
- **`GetWPFSettingsControl()`**: return `new SettingsControl(this)`
- **`SaveSettings()`**: capture into current profile + `SaveCommonSettings`
- **SimHub properties** (via `AttachDelegate`):
  - `GX100.Connected`, `GX100.CurrentGear`, `GX100.GearName`, `GX100.SequentialMode`
  - `GX100.SeqUp`, `GX100.SeqDown`, `GX100.Button1`-`GX100.Button5`
- **Profile system**: same `InitProfileSystem()` / `OnProfileChanged()` / `ApplyProfile()` flow as Moza
- **`ApplyProfile()`**: copy sentinel-checked values into `_data` + `_settings`, send config report if connected

### 4. Settings & Profile System (`UI/`)

**`GX100PluginSettings.cs`** (~30 lines)
- `ConnectionEnabled`, `AutoApplyProfileOnLaunch` (bools)
- Current config values: `HSensitivity` (default 128), `SeqSensitivity` (128), `ComboSwitch1`, `ComboSwitch2`, `SequentialMode`, `PullButton`
- `GX100ProfileStore ProfileStore`

**`GX100Profile.cs`** (~80 lines)
- Pattern: same as `MozaProfile` (ref: `/home/rorth/src/moza-simhub-plugin/UI/MozaProfile.cs`)
- `ProfileBase<GX100Profile, GX100ProfileStore>`
- 6 settings as `int` with `-1` sentinel (bools stored as 0/1/-1)
- `CaptureFromCurrent(settings, data)` -- snapshot current state
- `CopyProfilePropertiesFrom(GX100Profile p)` -- deep copy

**`GX100ProfileStore.cs`** (~15 lines)
- Pattern: same as `MozaProfileStore` (ref: `/home/rorth/src/moza-simhub-plugin/UI/MozaProfileStore.cs`)
- `ProfileSettingsBase<GX100Profile, GX100ProfileStore>`
- `FileFilter => "GX100 profile (*.shgx100profile)|*.shgx100profile"`

### 5. UI (`UI/SettingsControl.xaml + .cs`) (~400 lines total)

Pattern: same as Moza's `SettingsControl` -- `_suppressEvents` flag, 500ms `DispatcherTimer` refresh, event handlers that update data + write to device + save settings.

**Layout:**
```
[Connection toggle + status label]
[ProfileList control (SimHub native per-game selector)]

--- Configuration ---
  Mode toggle: H-Pattern / Sequential
  H-Pattern Sensitivity: slider 0-255 (default 80)
  Sequential Sensitivity: slider 0-255 (default 80)
  Combination Switch 1: checkbox
  Combination Switch 2: checkbox
  Pull Button: checkbox
  [Apply Settings] button

--- Calibration ---
  [Start/Finish H-Pattern Calibration] toggle button
  [Start/Finish Sequential Calibration] toggle button
  (mutually exclusive -- disable one while other is active)

--- Status ---
  Current Gear display (large: "N", "1"-"8", "R")
  Sequential Up/Down indicators
  Extra Buttons B1-B5 indicators

--- Options ---
  Auto-apply profile on launch: checkbox
  [Clear All Settings] button
```

**Event flow for setting change:**
1. User moves slider -> event handler fires
2. If `_suppressEvents` is true, return (prevents feedback loops during programmatic updates)
3. Update `_data.HSensitivity = value`
4. Update `_settings.HSensitivity = value`
5. Call `_connection.SendConfiguration(...)` to write to device
6. Call `plugin.SaveSettings()` -> captures into current profile + persists

### 6. Build System (`GX100Plugin.csproj`)

Same SDK-style as Moza (ref: `/home/rorth/src/moza-simhub-plugin/MozaPlugin.csproj`):
- `net48`, `x86`, `UseWPF`, `LangVersion=latest`, `Nullable=enable`
- SimHub DLL references from `libs/SimHub/` with `<Private>false</Private>`
- `HidSharp` NuGet package
- `Microsoft.NETFramework.ReferenceAssemblies.net48` for cross-platform build
- `CopyToSimHub` target keyed on `SIMHUB_PATH` env var
- No device template build targets (not needed)

## Implementation Phases

### Phase 1: Skeleton + HID Connection
- Create project structure, sln, csproj, copy SimHub libs
- `GX100Protocol.cs` constants
- `GX100HidConnection.cs` with connect/disconnect/send
- `GX100Data.cs` volatile fields
- Minimal `GX100Plugin.cs` (Init/End, connect, `GX100.Connected` property)
- **Verify:** builds, SimHub loads plugin, device connection logged

### Phase 2: Configuration + Settings
- `SendConfiguration()` in connection class
- `SendCalibration()` in connection class
- `GX100PluginSettings.cs`
- Wire up settings persistence (ReadCommonSettings/SaveCommonSettings)
- **Verify:** send config report, device changes mode/sensitivity

### Phase 3: Input Reading + SimHub Properties
- Background HID input read thread in connection class
- Button parsing into `GX100Data` fields
- All SimHub properties registered via `AttachDelegate`
- **Verify:** shift through gears, properties update in SimHub

### Phase 4: WPF UI
- `SettingsControl.xaml` layout
- `SettingsControl.xaml.cs` with refresh timer + event handlers
- Calibration button toggle logic
- **Verify:** full UI interaction, settings write to device

### Phase 5: Profile System
- `GX100Profile.cs` and `GX100ProfileStore.cs`
- Profile initialization in `GX100Plugin.Init()`
- `ApplyProfile()` and `OnProfileChanged()`
- ProfileList control wired into UI
- **Verify:** create profiles per game, switch games, settings change automatically

### Phase 6: Polish
- Auto-reconnect via `DeviceList.Changed` + reconnect timer
- Re-apply settings on reconnect
- Deploy skill configuration (like Moza's `/deploy`)
- `.gitignore`

## Key Technical Decisions

1. **HidSharp** for HID on Windows -- mature, .NET Framework 4.8 compatible, no native DLL deps
2. **No DeviceManager abstraction** -- protocol too simple for command database; connection class directly builds 2 report types
3. **No device extension** -- shifter has no LEDs; per-game profiles handled entirely by ProfileBase
4. **Direct HID input reading** -- background thread reads input reports for gear state; doesn't rely on SimHub's joystick mapping (more reliable, works regardless of how Windows enumerates the device)
5. **Send full config report on any change** -- the GX100 uses a single monolithic config report, so always send all 6 settings together

## Potential Issues

1. **HidSharp DLL conflict** -- SimHub may already bundle HidSharp. Check SimHub install dir; if present, reference theirs instead of shipping a second copy.
2. **Windows HID exclusive access** -- Windows game controller driver may hold the HID device. HidSharp opens in shared mode by default, but verify output reports work while the device is also read as a joystick.
3. **Output report ID byte** -- If device has no numbered reports, Windows HID API may prepend a 0x00 report ID. HidSharp handles this, but verify exact byte layout the GX100 firmware expects.
4. **Calibration UX** -- Must prevent starting one calibration while another is active. UI disables the other calibration button while one is in progress (same pattern as Foxblat plugin).

## Verification

1. **Build:** `dotnet build` succeeds targeting net48/x86
2. **Deploy:** DLL copies to SimHub directory
3. **Load:** SimHub shows "GX100 Shifter" in plugin list with settings panel
4. **Connect:** Plugin auto-detects GX100 when plugged in, `GX100.Connected` = true
5. **Configure:** Changing sensitivity slider sends config report, device responds
6. **Input:** Shifting gears updates `GX100.CurrentGear` and `GX100.GearName` properties
7. **Profiles:** Creating per-game profiles, switching games auto-applies correct settings
8. **Reconnect:** Unplugging and replugging device re-establishes connection and re-applies settings
9. **Calibration:** Start/finish calibration flows work correctly with mutually exclusive buttons
