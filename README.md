# Hail

A keyboard launcher for Windows. Press **Alt+Space**, type, press **Enter**: an app starts, a
file opens, a sum is answered, a search goes to the browser, and the box is gone.

Everything Hail finds comes from a provider, and the built-in ones are written against the
same plugin contract (`Hail.Sdk`, MIT) a stranger would use.

> **Status: M1.** Apps, files, the calculator, web search and Hail's own commands, with
> history. Plugins arrive in M2, a settings window and an installer in M3.

## What it does

- Lives in the notification area. **Alt+Space** shows the box on the monitor under the mouse;
  Alt+Space again, **Escape**, or clicking elsewhere hides it.
- **Apps**: everything the Start menu lists, desktop and Microsoft Store apps alike. `note`
  finds Notepad, `ps` finds PowerShell, `code studio` finds Visual Studio Code. The matched
  letters are in bold.
- **Files** by name, from the Windows Search index: only the folders Windows is told to index
  (by default your own). Type a path (`C:\Users\`, `~\Doc`) to list a folder instead; **Tab**
  completes the highlighted entry.
- **Sums** as you type: `15% of 240`, `2^10`, `sqrt(2)`, `2pi`, `0xFF + 1`. Start with `=` to
  force it. Your region's decimal separator works (`0,1 + 0,2`), and so does the point.
- **Web search**: a keyword first picks the engine (`g`, `ddg`, `b`, `yt`, `gh`, `w`), and
  every box ends with *Search Google for ...* when nothing else answered it better.
- **Commands**: Lock, Sleep, Sign out, Restart, Shut down, Hail settings, Quit Hail. Anything
  that can cost unsaved work asks first, in the box.
- **History**: what you pick is favoured the next time you type the same start, and an empty
  box shows what you pick most.

### Keys

| Key | Does |
|---|---|
| Up / Down, Ctrl+K / Ctrl+J | Move the highlight |
| Enter | The highlighted row's action (named on the row) |
| Ctrl+Enter, Shift+Enter, Ctrl+Shift+Enter | Other actions, listed under the rows |
| Ctrl+C | Copy the highlighted file's path (or the box's selected text) |
| Ctrl+Shift+C | Copy the highlighted file itself |
| Tab | Complete a path or a sum into the box |
| Ctrl+, | Open the settings file |
| Escape | Go back from a question, or hide the box |

Results that arrive late never move the highlighted row, so Enter always runs the row you
were looking at.

### The tray

Right-click the icon for **Start with Windows**, **Open settings file**, **Clear history** and
**Quit Hail**. A provider Hail had to switch off (one that kept failing) is named there too.

If another program already holds Alt+Space (PowerToys Run does by default), Hail still starts
and says so in a notification; click the tray icon to open the box.

## Settings

`%LOCALAPPDATA%\Hail\settings.json`, written with the defaults the first time Hail starts.
Restart Hail after editing it. A value Hail cannot use falls back to its default alone, and the
log says which.

```json
{
  "keepLastQuery": false,
  "webSearch": {
    "defaultEngine": "g",
    "engines": [ { "keyword": "g", "name": "Google", "template": "https://www.google.com/search?q={query}" } ]
  },
  "disabledProviders": [ "hail.files" ]
}
```

An engine's template must be an `https` address with `{query}` in its path or query string.
The providers are `hail.apps`, `hail.calculator`, `hail.files`, `hail.commands` and `hail.web`.

## Privacy

Nothing you type leaves your machine until you press Enter on a web search. Hail fetches no
suggestions and sends no telemetry.

Two things are written to `%LOCALAPPDATA%\Hail`:

- **One log a day** (`logs`, thirty kept), which records what Hail did and never what you typed.
- **History** (`usage.json`): the apps, files and commands you picked, and the first 16
  characters of what you had typed when you picked each. Sums and web searches are never
  recorded. **Clear history** in the tray empties it.

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
| `Hail.Core` | Pure: the supervisor, keyword parsing, the fuzzy matcher, ranking and history, the no-jump list, the Windows Search SQL. |
| `Hail.Providers` | The built-in providers (apps, files, calculator, web search, commands), written against `Hail.Sdk` and host ports only. |
| `Hail.Windows` | Every Win32, COM and OLE DB call: the hotkey, the Start menu, icons, the index, launching, session commands, the Run key, the tray. |
| `Hail.Persistence` | The log, the settings file and history, written atomically. |
| `Hail.App` | The box and the tray (WPF + WPF UI). |

`ArchitectureTests` hold each project to its line.

## Licence

Hail is source-available, not open source: free to use, including at work, but not to modify
or redistribute. See `LICENSE`. The plugin contract in `src/Hail.Sdk` is the exception and is
MIT licensed, so plugins can be written and published under any terms.
