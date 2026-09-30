# Hail

A keyboard launcher for Windows. Press **Alt+Space**, type, press **Enter**: an app starts, a
file opens, a sum is answered, a search goes to the browser, and the box is gone.

Everything Hail finds comes from a provider, and the built-in ones are written against the
same plugin contract (`Hail.Sdk`, MIT) a stranger would use.

> **Status: M2.** Apps, files, the calculator, web search and Hail's own commands, with
> history, and plugins: anyone can add results with a small .NET library, and the first one,
> Everything, finds every file on every drive. A settings window and an installer arrive in M3.

## What it does

- Lives in the notification area. **Alt+Space** shows the box on the monitor under the mouse;
  Alt+Space again, **Escape**, or clicking elsewhere hides it.
- **Apps**: everything the Start menu lists, desktop and Microsoft Store apps alike. `note`
  finds Notepad, `ps` finds PowerShell, `code studio` finds Visual Studio Code. The matched
  letters are in bold.
- **Files** by name, from the Windows Search index: only the folders Windows is told to index
  (by default your own). Type a path (`C:\Users\`, `~\Doc`) to list a folder instead; **Tab**
  completes the highlighted entry. Network paths and mapped network drives are neither listed
  nor opened: each keystroke would reach the server.
- **Sums** as you type: `15% of 240`, `2^10`, `sqrt(2)`, `2pi`, `0xFF + 1`. Start with `=` to
  force it. Your region's decimal separator works (`0,1 + 0,2`), and so does the point.
- **Web search**: a keyword first picks the engine (`g`, `ddg`, `b`, `yt`, `gh`, `w`), and
  every box ends with *Search Google for ...* when nothing else answered it better.
- **Commands**: Lock, Sleep, Sign out, Restart, Shut down, Hail settings, Quit Hail. Anything
  that can cost unsaved work asks first, in the box.
- **History**: what you pick is favoured the next time you type the same start, and an empty
  box shows what you pick most.
- **Plugins**: each one a folder in `%LOCALAPPDATA%\Hail\plugins\`. Hail asks before it runs
  one, and asks again if its files change. See *Plugins* below.

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

Right-click the icon for **Start with Windows**, **Plugins**, **Open settings file**, **Clear
history** and **Quit Hail**. A provider Hail had to switch off (one that kept failing) is named
there too.

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
The providers are `hail.apps`, `hail.calculator`, `hail.files`, `hail.commands` and `hail.web`; a plugin is switched off here by its own id as well, though **Switch off** in the tray is the usual way.

## Plugins

A plugin adds results to the box. It is a folder under `%LOCALAPPDATA%\Hail\plugins\`, named
after the plugin, holding its `plugin.json` and its assemblies. To install one, copy its folder
there and choose **Plugins**, **Reload plugins** in the tray (or type `reload plugins`).

**Nothing in a plugin's folder runs until you say so.** Hail finds the new folder, tells you,
and asks, showing the plugin's name, who it says it is from, which searches it joins and where
it lives. It remembers your answer for exactly those files: if any file in the folder changes,
the plugin is off until you answer again. A plugin is a program that runs as you, so enable
only the ones you would install as programs.

The tray's **Plugins** menu lists each plugin with where it stands, and offers **Enable...**,
**Switch off** and **Settings...** (a form drawn from what the plugin declares; a token or
password there is kept encrypted for your Windows account). **Reload plugins** unloads them all
and reads the folder again, so a plugin author can rebuild without restarting Hail.

**Everything** (`plugins/Hail.Plugin.Everything`) is the first plugin: `e report` finds every
file named like *report* on every drive, through
[Everything](https://www.voidtools.com/) by voidtools, which must be installed and running.
Build it and copy it in:

```
dotnet build plugins/Hail.Plugin.Everything -c Release -p:HailDeploy=true
```

**Writing one**: `docs/plugins.md` goes from an empty folder to a result on screen, and
`dotnet new install templates/hail-plugin` gives you a project that builds straight into the
plugins folder.

## Privacy

Nothing you type leaves your machine until you press Enter on a web search. Hail fetches no
suggestions and sends no telemetry.

These are written to `%LOCALAPPDATA%\Hail`:

- **One log a day** (`logs`, thirty kept), which records what Hail did and never what you typed.
- **History** (`usage.json`): the apps, files and commands you picked, and the first 16
  characters of what you had typed when you picked each. Sums and web searches are never
  recorded. **Clear history** in the tray empties it.
- **Your answers about plugins** (`plugins.json`) and **plugins' settings**
  (`plugin-settings.json`, with secrets encrypted for your Windows account), and each plugin's
  own `data` folder if it keeps one.

A plugin is not held to Hail's promises: it can do whatever a program running as you can.

## Build and run

Needs the .NET 10 SDK on Windows 10 or 11.

```
dotnet build Hail.slnx
dotnet test Hail.slnx
dotnet run --project src/Hail.App
```

The plugin contract packs on its own (`dotnet pack src/Hail.Sdk -c Release`); nothing else in
the solution is a package.

The icon is drawn from geometry by `python assets/build-icon.py` (Pillow).

## Layout

| Project | What it holds |
|---|---|
| `Hail.Sdk` | The plugin contract. No dependencies, no UI types. **MIT**, see `src/Hail.Sdk/LICENSE`. |
| `Hail.Core` | Pure: the supervisor, keyword parsing, the fuzzy matcher, ranking and history, the no-jump list, the Windows Search SQL. |
| `Hail.Providers` | The built-in providers (apps, files, calculator, web search, commands), written against `Hail.Sdk` and host ports only. |
| `Hail.Windows` | Every Win32, COM and OLE DB call: the hotkey, the Start menu, icons, the index, launching, session commands, the Run key, the tray. |
| `Hail.Persistence` | The log, the settings file, history, plugin answers and plugin settings, written atomically. |
| `Hail.Plugins` | Finding plugins, fingerprinting their files, loading each into a collectible context of its own, and unloading it. |
| `Hail.App` | The box, the tray, and the plugin question and settings form (WPF + WPF UI). |
| `plugins/Hail.Plugin.Everything` | The Everything plugin, built against `Hail.Sdk` alone. |
| `templates/hail-plugin` | The `dotnet new hail-plugin` template. |

`ArchitectureTests` hold each project to its line.

## Licence

Hail is source-available, not open source: free to use, including at work, but not to modify
or redistribute. See `LICENSE`. The plugin contract in `src/Hail.Sdk`, the template in
`templates/hail-plugin` and the guide in `docs/plugins.md` are the exception and are MIT
licensed, so plugins can be written and published under any terms.
