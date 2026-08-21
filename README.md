# BitChroma

A lightweight screen color identifier for Windows. Zoom into any pixel anywhere on screen —
any app, any part of the UI — read its hex/RGB/HSL, and copy it in one click.

- **26 KB single .exe**, no installer, no runtime download, no dependencies.
  Uses the .NET Framework that ships with Windows.
- Portable: copy `BitChroma.exe` anywhere and run it.

## Build

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Produces `BitChroma.exe` next to the script. Rebuild any time you edit `BitChroma.cs`.

## Use

1. Run `BitChroma.exe`.
2. Hit **Ctrl+Alt+P** (works even when the window isn't focused), press **F2**, or click **Pick color**.
   The screen freezes and a magnifier follows your cursor.
3. **Click** the pixel you want. The hex is copied to your clipboard automatically.

### While picking

| Input | Action |
| --- | --- |
| Move mouse | Magnifier follows, live hex + screen coords under it |
| Left click / Enter / Space | Pick this pixel |
| Mouse wheel, `+` / `-` | Zoom 4x - 32x |
| Arrow keys | Nudge the cursor exactly 1 pixel |
| Esc / right click | Cancel |

The desktop is snapshotted the moment you enter pick mode, so the magnified image is stable
and the pixel you see is exactly the pixel you get. Works across all monitors.

### In the main window

- **Big swatch** — click it to copy the hex.
- **HEX / RGB / HSL** rows, each with its own Copy button. The HEX field is editable: type a
  value (`#1E90FF`, `1e90ff`, or shorthand `#abc`) and press Enter to load that color.
- **Fine-tune...** — opens the Windows color picker *seeded with the pixel you just picked*, so
  you can shift the shade slightly and keep the result.
- **Recent** — last 12 colors; click one to reload it. Hover for its hex.
- **Ctrl+C** copies the current hex, **Esc** closes the app.

### Options (remembered between runs)

- *Auto-copy hex on pick* — on by default.
- *Always on top*.
- *Hide this window while picking* — keeps BitChroma out of its own screenshot. On by default.

Settings and history live in `%APPDATA%\BitChroma\settings.ini`.

## Handy extras

- `BitChroma.exe -p` launches straight into pick mode — good for a desktop shortcut or a
  taskbar pin when you just want one quick color.
- On mixed-DPI setups the app runs per-monitor-DPI-aware, so coordinates and captured pixels
  are true physical pixels on every display.

## Files

- `BitChroma.cs` — entire app (~700 lines).
- `build.ps1` — one-step build.
