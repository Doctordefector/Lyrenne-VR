# Lyrenne VR

A music player on your wrist, inside any PC VR game. Look at your wrist and the player appears.
Tap it with your other hand to play, pause, skip, scrub, change the volume, shuffle or repeat.
The current line of synced lyrics shows under the song.

Works with games that run through **SteamVR** and with games that run on **Virtual Desktop's VDXR**
OpenXR runtime, so you don't have to change how you launch anything.

## How it works

```
 Lyrenne (desktop app) ── Windows media controls ──┐
                                                    │
                                    LyrenneVR.exe (tray app)
                         draws the panel, glance + touch logic, LRCLIB lyrics
                          │                                        │
                 shared memory                              OpenVR overlay
                          │                                        │
  OpenXR layer inside the game (VDXR, Oculus, WMR...)   SteamVR games, OpenXR games on SteamVR
```

- **LyrenneVR.exe** reads what's playing from Windows' media controls, which Lyrenne already
  publishes to. Lyrenne needs no changes. If Lyrenne isn't running, it follows whatever player
  Windows thinks is current, unless you tick *Only follow Lyrenne* in the tray menu.
- **Under SteamVR** it's a normal OpenVR overlay attached to your controller, like XSOverlay or
  OVR Toolkit.
- **Under VDXR** SteamVR never runs, so a SteamVR overlay can't show. Instead an implicit OpenXR
  API layer (`XR_APILAYER_LYRENNE_wrist_player`) loads into the game. It rides its own pose-only
  action set alongside the game's input, so it never takes a button away from the game. It sends
  your head and hand poses to LyrenneVR.exe and draws the panel as a quad locked to your wrist,
  with no added latency. When the game runs on SteamVR's runtime the layer stays passive, so the
  panel never shows up twice.
- When LyrenneVR.exe isn't running, the layer does nothing beyond one timer check per frame.

## Install

```
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Run it from the packaged `dist\LyrenneVR` folder. It copies the app to
`%LOCALAPPDATA%\Programs\LyrenneVR`, registers the OpenXR layer (one admin prompt, since OpenXR
only reads layers from HKLM), turns on start with Windows and starts the tray app. The first time
SteamVR is running with Lyrenne VR open, it also registers to start with SteamVR.

Needs the .NET 9 Desktop Runtime and 64-bit games.

`uninstall.ps1` removes all of it. SteamVR keeps a disabled entry in its startup overlay list
until you remove it in SteamVR's settings.

## Using it

- **Open:** raise your watch hand and look at the back of your wrist, like checking the time.
  It opens after a fifth of a second and closes about half a second after you look away. It stays
  open while your finger is on it.
- **Touch:** touch the panel with the tip of your other controller. A ring shows where you'll
  land and tightens as you get closer. Buttons fire on contact with a small vibration. The
  progress bar and volume slider can be dragged, and seeking happens when you let go.
- **Volume** changes Lyrenne's own volume in the Windows mixer, never the game's. Without a
  Lyrenne audio session it falls back to the system volume.

Tray menu: which wrist, when to show (glance, always, never), lyrics, vibration, start with
Windows or SteamVR, a desktop preview where the mouse stands in for your finger, and
*Adjust position and size* with live sliders. Changes show up in the headset straight away.

Settings: `%LOCALAPPDATA%\LyrenneVR\settings.json`. Logs: `host.log` and `layer.log` in the same
folder. The layer log names every game it loaded into and why it was active or passive.

## Building

Needs Visual Studio (C++ x64 build tools) and the .NET 9 SDK.

```
.\build.ps1 -Test
```

`-Test` also runs:

- `LyrenneVR.exe --selftest`: drives the glance and touch logic with synthetic head and hand poses
  for both wrists.
- `build\test\xrtest.exe d3d11|d3d12`: the official Khronos loader, the real layer and a mock
  OpenXR runtime with real D3D11 and D3D12 swapchains. It checks the action set merge, the wrist
  lock and the quad geometry, then reads the panel's pixels back from the GPU.

Other switches: `--preview` opens the desktop preview, `--always` keeps the panel on for this run,
and `--render out.png [--demo] [--pointer x,y[,pressed]]` renders one frame to a file.

## Limits

- The layer composites into D3D11 and D3D12 games, which covers Unity, Unreal and most others.
  Vulkan and OpenGL OpenXR games get no panel yet.
- Native Oculus SDK games run through Virtual Desktop's LibOVR shim, not OpenXR or SteamVR, so
  they get no panel.
- Like OpenKneeboard and OpenXR Toolkit, the layer is a DLL inside the game process. Games with
  strict anti-cheat might object. To keep it out of one game, set
  `DISABLE_XR_APILAYER_LYRENNE_wrist_player=1` in that game's environment.
- The wrist offsets are a best first guess from the OpenXR grip pose conventions. If the panel
  sits wrong on your controllers, fix it once with *Adjust position and size*.
