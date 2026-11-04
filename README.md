# Kitsune

A lean Android gaming emulator distribution for Windows. No ads, no telemetry, no bundled
"helper" software, no account. Just the fastest open emulator core there is, configured for
games, with the pieces a gaming wrapper actually needs.

## What it is

Kitsune is a native Windows launcher around the Android Emulator from the Android Open Source
Project (the QEMU-based emulator Google ships with Android Studio, Apache-2.0). That core already
has the best hypervisor and GPU story on Windows; what the commercial emulators add on top is
mostly bloat. Kitsune adds only:

- **Component management.** Downloads the emulator, platform-tools and system images straight from
  Google's public SDK repository, verifies every archive's size and SHA-1 against the repository
  manifest, and installs them atomically into a private folder. No Android Studio, no sdkmanager,
  no Java.
- **Hardware acceleration done right.** Probes the CPU with CPUID, detects whether a hypervisor is
  already running, and picks the fastest accelerator: Google's AEHD kernel driver when Hyper-V is
  off (direct VT-x/AMD-V ownership, the fastest path), WHPX when Hyper-V is on. Pins the QEMU
  process to the discrete GPU via the Windows per-application graphics preference and the Vulkan
  loader's driver selection, so hybrid laptops render on the right GPU.
- **Gaming-tuned instances.** Device profiles (phone, tablet, landscape 16:9), vCPU count derived
  from performance cores (the emulator caps vCPUs at 6), RAM from host memory, host GPU with
  Vulkan, 32-bit colour, large data partition, no cameras or microphone, quick-boot snapshots,
  60 Hz or higher refresh.
- **ARM games.** The x86_64 Google Play images ship `libndk_translation`, so arm64-only games run.
  Kitsune installs those images; the emulator does the translation.
- **A keymapper with real latency numbers.** Keyboard and mouse are hooked at the Win32 level and
  translated into multitouch through the emulator's own gRPC control stream, which writes directly
  into the virtual touchscreen. No adb round trip, no guest agent, no root. Tap, hold, autofire,
  virtual joystick with optional ramp, scripted swipes, pinch zoom, Android keys, and mouse-look
  with automatic re-anchoring. Profiles are JSON with normalized coordinates, edited by dragging
  badges over the running game.
- **Nothing phones home.** The emulator runs with `-no-metrics`, its metrics opt-in and crash
  reporter consent are pre-seeded to "no" in a private emulator home, and Kitsune itself only ever
  talks to `dl.google.com` when you click install.

## Requirements

- Windows 10 2004 or Windows 11, x86_64.
- A CPU with VT-x or AMD-V enabled in firmware.
- .NET 10 runtime (desktop) to run the launcher, .NET 10 SDK to build it.
- About 3 GB of disk for the emulator and one system image.
- For AEHD: Hyper-V, Virtual Machine Platform, WSL2, Windows Sandbox and memory integrity (VBS)
  must be off; they hold the hypervisor and AEHD cannot load beside them. If you need those,
  enable "Windows Hypervisor Platform" instead and Kitsune will use WHPX.

## Download

Prebuilt, self-contained builds are on the
[releases page](https://github.com/ReinaryuIlyen/kitsune/releases) (`Kitsune-<version>-win-x64.zip`
plus a `.sha256`). Unzip anywhere and run `Kitsune.exe`; no .NET install is required. The page to
share with friends is <https://kitsune-emulator.pages.dev/>; `site/deploy.ps1` fills `site/index.html`
from the newest release and redeploys it (`npx wrangler login` first).

## Quick start

```bash
dotnet build Kitsune.slnx -c Release
```

Then run `src\Kitsune.App\bin\Release\net10.0-windows\Kitsune.exe` (or the unzipped release):

1. **Install / update components** downloads the emulator, adb, the AEHD driver package and the
   Android 15 Google Play image (about 2.1 GB).
2. **Install AEHD driver** installs Google's hypervisor driver. One UAC prompt. Skip this on
   machines with Hyper-V enabled; WHPX is used automatically.
3. **New** creates an instance. Pick a screen profile; cores and RAM default to what the host
   probe recommends.
4. **Launch**. The first boot takes a minute, later boots resume from a snapshot in a few seconds.
5. **Install APK...** (or drop an `.apk`, `.apks` or `.xapk` onto the window) for games that are not on
   the Play Store. Split bundles are installed in one transaction and their OBB files pushed along.
6. Pick a keymap profile, press **Edit on screen** and drag the badges onto the game's buttons.

**Screenshot** saves a PNG of the guest display to `screenshots\`, and **adb shell** opens a console on
the instance. A component download can be cancelled; it resumes from where it stopped next time.

Closing the launcher never kills a running game. Open it again and any emulator it did not start
(or started before it was closed) shows as "running outside the launcher" with an **Attach** button;
attaching restores the keymap overlay, macros and the Stop button for it. `kitsune ps` lists them.

Everything lives under `%LOCALAPPDATA%\Kitsune` (override with the `KITSUNE_HOME` environment
variable, or drop an empty `kitsune.portable` file next to `Kitsune.exe` for a portable install).

## Command line

The same engine is scriptable through `kitsune.exe`:

```
kitsune probe                         host CPU, hypervisor, GPU, recommendation
kitsune catalog                       what the repository offers
kitsune ps                            emulators running right now, with console and gRPC ports
kitsune install [--api 35] [--variant playstore|googleapis]
kitsune aehd status|install|uninstall
kitsune list                          instances and their configuration
kitsune create <name> [--profile desktop-fhd] [--cores 8] [--ram 8192]
kitsune delete <name>
kitsune plan <name>                   exact command line and environment the launcher will use
kitsune launch <name> [--cold] [--wipe] [--headless] [--wait] [--attach]
kitsune stop <name> [--force]         clean shutdown over gRPC, or kill
kitsune apk <name> <file>...          install .apk / .apks / .xapk on the running instance
kitsune adb <name> [args...]          adb against the running instance (no args: shell)
kitsune ctl <grpc-port> status|displays|screenshot f.png|tap X Y|swipe X0 Y0 X1 Y1 [ms]|key Back|shutdown
kitsune keymap validate <file.json>
kitsune keymap selftest
kitsune reroll create <set> --from <instance> [--count N] [--ram MB] [--cores N]
kitsune reroll launch <set> [--count N] [--stagger ms] [--wait] [--attach]
kitsune reroll list | add <set> [N] | reset <set> [clone#] | keep <set> <clone#> <new-instance> | delete <set>
kitsune macro list | validate <file> | play <file> --grpc <port,...> [--repeat N] [--speed X]
kitsune macro checkpoint <file> --grpc <port> --at X,Y [--size 0.12] [--tolerance 14] [--timeout ms]
```

`Kitsune.exe --launch <name>` starts the GUI and that instance in one go, for desktop shortcuts.

## Keymap profiles

Profiles are JSON files in `%LOCALAPPDATA%\Kitsune\profiles`. Coordinates are fractions of the
guest display (0..1), so a profile authored at 1080x2400 keeps working at 1440x3120.

| Kind          | What it does                                                                 |
|---------------|------------------------------------------------------------------------------|
| `Tap`         | Touch down at X,Y while the key is held, up on release.                       |
| `TapAtCursor` | Touch wherever the mouse cursor is while the key is held.                     |
| `Repeat`      | Autofire at X,Y every `IntervalMs` while held.                                |
| `Joystick`    | `Up`/`Down`/`Left`/`Right` keys drive a stick of `Radius` at X,Y. `RampMs` smooths it. |
| `Swipe`       | One swipe from X,Y to `ToX`,`ToY` over `DurationMs` per press. Bind to `WheelUp`/`WheelDown` for scrolling. |
| `Pinch`       | Two-finger pinch around X,Y; positive `Delta` zooms in, negative out.          |
| `AimToggle`   | Toggles mouse-look anchored at X,Y with `Sensitivity`; re-anchors past `ResetRadius`. |
| `AndroidKey`  | Sends `Back`, `Home`, `Menu`, `AppSwitch`, `VolumeUp`, ... via the virtual keyboard. |
| `ToggleKeymap`| Switches the whole profile on and off so you can type.                        |

Host key names: `A`..`Z`, `D0`..`D9`, `F1`..`F24`, `Space`, `Enter`, `Escape`, `Tab`, `Shift`,
`Ctrl`, `Alt`, arrows, `Numpad0`..`Numpad9`, `LeftButton`, `RightButton`, `MiddleButton`,
`XButton1`, `XButton2`, `WheelUp`, `WheelDown`, and the punctuation keys (`Comma`, `Period`,
`Minus`, `Plus`, `LeftBracket`, ...).

Mapping is active only while the emulator window is in front, and pauses automatically while a
text field in the guest has focus (the emulator reports that over gRPC), so chat boxes keep
working without toggling anything. The gesture clock runs at 250 Hz; Kitsune raises the Windows
timer resolution to 1 ms while an emulator is attached so swipes, autofire and mouse-look are not
quantised to the default 15.6 ms tick.

## Macros: click-by-click replay that survives lag

A macro records what the guest actually received, not what the screen looked like: every touch
contact (down, move, up) with its timestamp and normalized position, every Android key, and the
keymapped input the engine generated. Replay sends the same events through the gRPC input stream,
so a recording made at 1080x2400 plays back on 1440x3120, and on every reroll clone at once.

| Key | While the emulator window is in front |
|---|---|
| `F8` | Start or stop recording (the recording is saved to `macros\` and selected automatically). |
| `F9` | Insert a **checkpoint** at the mouse cursor: a small reference crop of the screen around it. |
| `F10` | Stop playback on that instance. |

Checkpoints are what make replay robust. When playback reaches one, it polls a scaled frame
several times a second and waits until that region matches the reference (mean grayscale
difference within the tolerance), then shifts the rest of the timeline by however long it waited.
Put one before every tap that follows a load, a network round trip or an animation and the macro
cannot run ahead of the game. A checkpoint that never matches stops the run (configurable per step)
rather than tapping blindly. That is the "babysit" mode: a macro that reaches its last checkpoint
has verified every screen along the way.

Macros are JSON in `%LOCALAPPDATA%\Kitsune\macros`; `kitsune macro validate` checks one,
`kitsune macro play <file> --grpc 8554,8555,8556 --repeat 0` runs it on three instances until
stopped, and `kitsune macro checkpoint` appends a checkpoint captured from a live instance to a
hand-written macro. Use a landscape device profile for landscape games: coordinates are in the
display's own orientation.

## Parallel reroll

Rerolling means starting a game over and over until the opening pulls are good. Kitsune runs that
in parallel from the **Parallel reroll...** button (or `kitsune reroll`):

1. Set up one normal instance as the template: install the game, get it to the screen where a new
   account begins, then stop it.
2. Create a reroll set from it. The template's three writable disks (user data, cache, encryption
   key) are flattened once into a frozen, read-only base. Each clone is a complete AVD whose disk
   files are NTFS hard links to that base plus an empty qcow2 overlay, so a clone costs under a
   megabyte until the game writes to it.
3. Launch all. Clones boot in parallel, each with its own console, adb and gRPC ports. The keymap
   overlay and hooks work on whichever clone is in front; **Tile windows** lays them out in a grid.
4. **Keep** a winner: its overlay chain is flattened into a brand-new standalone instance, laid out
   exactly as the emulator would have written it. **Reset** a loser: its overlay is recreated empty
   and the next boot is the template again.

Clones run without quick-boot (cold boot every time, no RAM-sized snapshot files); the set's RAM
and vCPU budget per clone is yours to pick, and the launcher stops adding clones when the host has
no memory left for another one (6 GB is always kept for the host and GPU translation).

Measured on the reference machine with 3 clones of an Android 15 Play template at 3 GB / 2 vCPU:
set creation 2.3 s, 1.78 GB of unique disk for the whole set, clones booting in 22 to 33 s in
parallel, overlays at 40 to 180 MB after a boot and a session, keep 2.0 s. All three clones report
the template's `android_id`, and a setting written on one is invisible on the others.

This is plain instance cloning. Kitsune does not spoof hardware identity; a game that bans
emulator rerolling will still see an emulator.

## How the pieces fit

```
Kitsune.App (WPF)      launcher UI, low-level hooks, overlay, instance dialogs
Kitsune.Cli            the same operations from a terminal
Kitsune.Core
  Sdk/                 repository manifests, resumable SHA-1-verified downloads, atomic extract
  Host/                CPUID + topology probe, AEHD service state, Vulkan ICDs, GPU preference
  Avd/                 device profiles, instance config, config.ini writer
  Emulator/            command line builder, process + log capture, gRPC control client,
                       discovery of running emulators, adb (APK / bundle install, shell)
  Input/               keymap model, engine (gestures, joystick, aim), gRPC input sink,
                       macro model, recorder, screen matcher, player
  Reroll/              frozen base + hard-linked overlay clones, keep/reset, qemu-img wrapper
  Protos/              emulator_controller.proto from AOSP external/qemu
```

Instances are plain AVD folders (`avd\<name>.avd\config.ini` plus a `kitsune.json`), so the stock
emulator tooling (`emulator -list-avds`, `adb`) sees them too. Logs for every launch are kept in
`logs\`, screenshots in `screenshots\`.

Every push builds the solution on Windows and runs `kitsune keymap selftest`, `kitsune macro selftest`
and `kitsune keymap validate` over the bundled profiles (`.github/workflows/ci.yml`); tags `v*` publish
a release.

## Verified on the reference machine

i9-13900K (8P+16E), 64 GB, RTX 4090, Windows 11 Pro 26220, Hyper-V off, emulator 37.2.13,
Android 15 Google Play image r9:

| Check | Result |
|---|---|
| Accelerator | `emulator -accel-check` returns 0, "AEHD (version 2.2) is installed and usable"; log says "AEHD is operational" |
| Cold first boot to `booted` over gRPC | 45.6 s; later boots resume from the quick-boot snapshot |
| Guest GPU | SurfaceFlinger: "Android Emulator OpenGL ES Translator (NVIDIA GeForce RTX 4090)", OpenGL ES 3.1 |
| Guest Vulkan | `cmd gpu vkjson` deviceName "NVIDIA GeForce RTX 4090", guest API 1.3 |
| ARM translation | `ro.product.cpu.abilist=x86_64,arm64-v8a`, `ro.dalvik.vm.native.bridge=libndk_translation.so` |
| gRPC touch | `getevent` shows the injected tap on `/dev/input/event2` with the expected scaled coordinates |
| gRPC swipe and key | Swipe from the top edge opens the shade (screenshot differs), Back closes it (screenshot identical to before) |
| Keymap engine | `kitsune keymap selftest`: 33 checks across every gesture kind |
| Macro replay | A hand-built macro with two live screen checkpoints (home, quick-settings shade) replays twice in 3.4 s from the CLI and once more through the launcher; `kitsune macro selftest` covers recorder, matcher and player |
| Attach | A CLI-started instance is discovered by a cold-started launcher, attached, driven (macro played) and stopped from it |

Known limitation: AEHD 2.2 does not expose XSAVE/AVX to the guest (the emulator logs "host doesn't
support requested feature: CPUID.01H:ECX.avx"). That is Google's driver, not a Kitsune setting; WHPX
exposes more CPU features but costs hypervisor overhead. Most mobile games never touch AVX.

## What is deliberately not here

- No root for Play images (Google does not allow `adb root` on them). Use a Google APIs image if
  you need root; it has no Play Store.
- No spoofing of device identity to defeat anti-emulator checks. Games that block emulators will
  still block this one.
- No video capture, streaming, or "multi-instance sync" gimmicks.

## License

MIT for Kitsune. The Android Emulator, platform-tools and system images are Google's and come with
their own licenses (see `NOTICE.txt` inside each package).
