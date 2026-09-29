# Hail

A keyboard launcher for Windows. Press **Alt+Space**, type, press **Enter**: the app starts
and the box is gone.

Everything Hail finds comes from a provider, and the built-in ones are written against the
same plugin contract (`Hail.Sdk`, MIT) a stranger would use.

> **Status: M0.** The box on the chord, with apps. Files, the calculator, web search,
> secondary actions and start-at-sign-in arrive in M1; plugins in M2; an installer in M3.

## What M0 does

- Lives in the notification area. **Alt+Space** shows the box on the monitor under the mouse;
  Alt+Space again, **Escape**, or clicking elsewhere hides it.
- Finds apps by name among everything the Start menu lists, desktop and Microsoft Store apps
  alike: `note` finds Notepad, `ps` finds PowerShell, `code studio` finds Visual Studio Code.
  The matched letters are in bold.
- **Up** and **Down** (or **Ctrl+K** and **Ctrl+J**) move the highlight; **Enter** or a click
  starts the app.
- Starting Hail again (from the Start menu, say) shows the box instead of starting a second
  copy. `Hail.exe --quit` ends the running one.
- Right-click the tray icon for **Quit Hail**.

If another program already holds Alt+Space (PowerToys Run does by default), Hail still starts
and says so in a notification; click the tray icon to open the box.

## Privacy

Nothing you type leaves your machine, and nothing you type is written to disk. Hail keeps one
log a day in `%LOCALAPPDATA%\Hail\logs` (thirty days), which records what Hail did and never
what you searched for.

## Build and run

Needs the .NET 10 SDK on Windows 10 or 11.

```
dotnet build Hail.slnx
dotnet test Hail.slnx
dotnet run --project src/Hail.App
```

The icon is drawn from geometry by `python assets/build-icon.py` (Pillow).

## Layout

| Project | What it holds |
|---|---|
| `Hail.Sdk` | The plugin contract. No dependencies, no UI types. **MIT**, see `src/Hail.Sdk/LICENSE`. |
| `Hail.Core` | Pure: the fuzzy matcher, ranking, the result list, the query runner. |
| `Hail.Providers` | The built-in providers, written against `Hail.Sdk` only. |
| `Hail.Windows` | Every Win32 and COM call: the hotkey, the Start menu, icons, the backdrop, the tray. |
| `Hail.Persistence` | The log. |
| `Hail.App` | The box and the tray (WPF + WPF UI). |

`ArchitectureTests` hold each project to its line.

## Licence

Hail is source-available, not open source: free to use, including at work, but not to modify
or redistribute. See `LICENSE`. The plugin contract in `src/Hail.Sdk` is the exception and is
MIT licensed, so plugins can be written and published under any terms.
