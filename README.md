# Brimstone Core for Xbox

Brimstone's supported Xbox client / renderer for Xbox One X (including Project Scorpio) and compatible Xbox consoles.

## Beta 1 goals

- Native 10-foot Brimstone UI designed for Xbox controllers
- Connect to an existing Brimstone Core over the local network
- Browse the Core music library and artwork
- Play Core-hosted music through the Xbox audio stack
- Background audio so music can continue while a game is running
- System Media Transport Controls integration
- Register the Xbox with Core as a network playback endpoint so iPad/macOS/Windows Control can target it
- HDMI / console-selected audio output; Xbox One X S/PDIF remains selectable through Xbox system audio settings
- Supported Microsoft APIs only: no exploits, console modification, unsigned retail code or DRM bypass
- Optical-disc capability probe. If Xbox permits supported access to music media, expose it; otherwise ripping remains on the main Core and is controlled from Xbox/Control.

## Architecture

```
Brimstone Core
    |
    | HTTPS / authenticated Core API
    |
Xbox Core
    |-- 10-foot controller UI
    |-- MediaPlayer background renderer
    |-- Core endpoint registration
    |-- System Media Transport Controls
    `-- optional supported optical capability probe

iPad / macOS / Windows Control
    |
    `---- controls Core ----> Xbox endpoint
```

The Xbox app deliberately does **not** contain provider credentials or duplicate the full Core database. Core remains the source of truth.

## Development target

- UWP
- C# / XAML
- x64
- Xbox One X / Project Scorpio first test hardware
- Visual Studio 2022 + Windows 10 SDK
- Developer Mode for local testing; Store packaging later

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/TESTING-XBOX.md](docs/TESTING-XBOX.md).
