# Project Scorpio test path

## First-console development test

1. On a Windows development PC install Visual Studio 2022 with Universal Windows
   Platform development tools and the Windows 10 SDK.
2. Clone this repository.
3. Run `powershell -ExecutionPolicy Bypass -File .\scripts\Generate-Assets.ps1`.
4. Open `BrimstoneXbox.sln`, choose **x64**, and build.
5. Put the Xbox One X into Microsoft Developer Mode and sign a user in.
6. In Visual Studio choose **Remote Machine**, enter the Xbox IP, and use the
   Universal (Unencrypted Protocol) authentication mode documented for Xbox UWP
   development.
7. Deploy and start Brimstone Core.
8. Enter the normal Core address and administrator credentials. The Xbox will
   obtain its own per-device Core credential; the password is not stored.

## Beta 1 acceptance

Verify, in order:

- controller focus works without a mouse pointer;
- Core login succeeds;
- Xbox appears in Core/Control as **Brimstone Xbox One X**;
- Your Music loads;
- album and track playback start on the Xbox;
- pause/resume/stop work from the Xbox;
- pause/resume/stop work from iPad/macOS/Windows Control;
- a multi-track album advances correctly;
- Xbox system media controls show the playing item;
- playback survives leaving the Brimstone foreground app;
- HDMI audio works;
- S/PDIF is tested separately on Xbox One X using Xbox system audio settings.

## Background music while gaming

Developer Mode is for sideloaded testing. Retail games run in Retail Mode, so
the final **Brimstone music while playing a normal retail game** acceptance test
requires a Store-installed/retail Brimstone package.

That is the intended legal production path. We do not use exploits or console
modification to bridge the two modes.

## Optical test

The manifest already declares the supported Windows `optical` device
capability. Do not assume internal UHD access until the real Project Scorpio
console proves it.

If the Xbox API does not expose sufficient read access:

- keep Xbox as the renderer/UI;
- attach the UHD/audio drive to normal Core;
- start and monitor the rip from Xbox/iPad Control.

## Network

The Xbox endpoint listener currently uses TCP port **8095** on the local LAN.
Core must be able to reach the Xbox on that port while the Brimstone app/process
is active.
