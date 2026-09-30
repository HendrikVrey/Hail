# Writing a Hail plugin

A plugin adds results to Hail's box. It is a .NET class library with one class that
implements `IProvider`, and a `plugin.json` beside it that tells Hail what it is before any of
its code is loaded. This guide goes from an empty folder to your result on screen, then covers
the contract, settings, and what Hail does and does not protect you from.

This file, the `Hail.Sdk` package and the template are MIT licensed: copy from them freely.

## From nothing to a result on screen

You need the .NET 10 SDK and Hail running.

1. **Create the project from the template.** Until the template is on nuget.org, install it
   from a clone of Hail's repository:

   ```
   dotnet new install <path to Hail>\templates\hail-plugin
   dotnet new hail-plugin -n Greeter --pluginId yourname.greeter --publisher "Your Name" --keyword gr
   ```

   That gives you `Greeter.csproj`, `Provider.cs` and `plugin.json`.

2. **Build it into Hail's plugins folder.**

   ```
   cd Greeter
   dotnet build -p:HailDeploy=true
   ```

   This copies the build output to `%LOCALAPPDATA%\Hail\plugins\yourname.greeter\`. (Without
   `HailDeploy`, copy `bin\Debug\net10.0\` there yourself.)

3. **Tell Hail.** Right-click Hail's tray icon, **Plugins**, **Reload plugins**, or type
   `reload plugins` in the box. Hail finds the new folder and says so; click the notification,
   or **Plugins**, **Greeter**, **Enable...**. Read what it shows you and press **Enable**.

4. **Use it.** Press Alt+Space and type `gr world`. The box shows *Hello, world*; Enter copies it.

Every rebuild changes the plugin's files, so Hail turns it off and asks again at the next
reload. That is on purpose (see *Trust*, below).

**When it loads.** Nothing of an enabled plugin is loaded at startup. It loads the first time
a search reaches it, or, for a plugin that implements `IRecall` and has picks in history, the
first time the box opens empty and wants to show them.

## The folder

```
%LOCALAPPDATA%\Hail\plugins\
  yourname.greeter\          named exactly as the id in plugin.json
    plugin.json
    Greeter.dll              the entry assembly
    Greeter.deps.json        written by the build; lists your dependencies
    Greeter.pdb              optional; with it, failures in Hail's log name your lines
    (your dependencies)
```

Do not ship `Hail.Sdk.dll`. Hail supplies its own copy and ignores one in your folder, so that
the types you return are the types it reads. Reference the package with
`ExcludeAssets="runtime"`, as the template does.

A plugin's folder may not contain links (symbolic links or junctions), more than 2,000 files
or more than 256 MB.

## plugin.json

```json
{
  "id": "yourname.greeter",
  "name": "Greeter",
  "publisher": "Your Name",
  "version": "1.0.0",
  "sdk": "1.0",
  "entry": "Greeter.dll",
  "type": "Greeter.Provider",
  "description": "Says hello to whatever you type after its keyword.",
  "keywords": [ "gr" ],
  "global": false,
  "debounceMs": 0,
  "settings": [ ]
}
```

| Field | Required | What it is |
|---|---|---|
| `id` | yes | Lower-case words joined by dots, conventionally `you.plugin`. The folder's name. Ids that start with `hail.` are Hail's own. |
| `name` | yes | What the box, the tray and the question call it. Up to 64 characters. |
| `publisher` | yes | Who you are. Hail shows it as *says it is from*, because nothing checks it. |
| `version` | yes | Your version, shown and never compared. |
| `sdk` | yes | The `Hail.Sdk` version you built against, `major.minor`. |
| `entry` | yes | The file name of the assembly holding your provider. |
| `type` | yes | The provider's full type name. It needs a public constructor with no arguments. |
| `description` | no | One or two sentences, shown when Hail asks to enable it. |
| `keywords` | no | Up to four words that, typed first, send the search to your plugin alone. A keyword of letters needs a space after it (`gr cats`); one of symbols does not. A keyword another provider already has stays theirs. |
| `global` | no | `true` (the default): your plugin is asked on every search as well. `false`: only after a keyword. A slow or noisy plugin should be `false`. |
| `debounceMs` | no | How long typing must pause before you are asked, 0 to 2000. Set it if you do I/O, so a word is one request and not one per letter. |
| `settings` | no | What the user can set; see *Settings*. |

Comments and trailing commas are allowed. Unknown fields are ignored. Anything else wrong
keeps the plugin off, and **Plugins** in the tray says which field and why.

## The contract

Everything is in the `Hail.Sdk` namespace; the XML documentation in the package is the
reference. In short:

```csharp
public interface IProvider
{
    ValueTask InitializeAsync(IPluginContext context, CancellationToken ct);
    IAsyncEnumerable<Result> QueryAsync(Query query, CancellationToken ct);
}
```

- **`InitializeAsync`** is called once, before the first search that reaches you. Keep the
  context; it is how you reach everything else. The user is waiting on that first search, so
  do little here.
- **`QueryAsync`** is called for every search that reaches you. `query.Search` is what was
  typed, without your keyword. Yield results as you find them. The token is cancelled by the
  next keystroke: pass it on and stop when it fires. A provider that ignores it is cut off
  after three seconds, and three failures or overruns in a minute switch it off until the
  next reload.
- A **`Result`** is a row: an id (stable, so history can remember it), a title, an optional
  subtitle, an icon (`IconSource.ForShellItem(path)` or `IconSource.ForGlyph("\uE8BD")`, a
  character of Segoe Fluent Icons), your own relevance from 0 to 1, what Enter does, and
  other actions on other chords. Use `context.Matcher.Match(query, text)` to score and to get
  the characters to bold, so your rows rank fairly against everyone else's.
- An **action** returns what the box does next: `ActionOutcome.Hide`, `KeepOpen`,
  `ReplaceQuery(text)`, or `AskFirst(question, action)` for anything that cannot be undone.

**Threading.** Every call comes on a background thread, never the UI thread, and a new query
can arrive while an older one is still finishing. Keep your state read-only after
`InitializeAsync`, or lock it.

**The context** gives you:

| Member | For |
|---|---|
| `Launcher` | Opening a web address (http and https only), a local file or folder, showing a file in Explorer, starting an app from the Start menu's list. Hail refuses the rest with a sentence it shows the user; you never need `Process.Start`. |
| `Clipboard` | Putting text, or a file, on the clipboard. |
| `Matcher` | Scoring text against the query the way Hail's own providers do. |
| `Log` | Writing to Hail's log. Never log what the user typed. An exception is logged by its type and stack, never its message. |
| `Settings` | The values of your declared settings. |
| `History` | For a provider that implements `IRecall`: the ids the user picks from you most, for when your keyword is typed with nothing after it. |
| `DataFolder` | A folder of your own under the user's profile, created when you first ask. |

**Being remembered.** Implement `IRecall` if your results are things worth offering again (a
file, a repository). Hail then records what the user picks, ranks it higher next time, and
shows it in an empty box by asking you to rebuild it from its id. Do not implement it if your
ids carry what the user typed: history is written to disk.

**Disposing.** If your provider implements `IDisposable` or `IAsyncDisposable`, Hail disposes
it when plugins are reloaded and when it quits. Stop every thread and timer there and unhook
every event you subscribed to outside your plugin, or your plugin cannot leave memory.

## Settings

Declare them in `plugin.json`; Hail draws the form (tray, **Plugins**, your plugin,
**Settings...**) and keeps the values. No code of yours runs to draw it.

```json
"settings": [
  { "key": "greeting", "type": "text", "label": "Greeting", "default": "Hello" },
  { "key": "loud", "type": "toggle", "label": "Shout", "default": false },
  { "key": "size", "type": "choice", "label": "Results", "choices": [ "10", "20" ], "default": "10" },
  { "key": "token", "type": "secret", "label": "API token", "description": "From your account page." }
]
```

Read them with `context.Settings.GetText("greeting")`, `GetToggle`, `GetChoice` and
`GetSecret`, when you need them rather than once at start: a saved change reaches you at once.
Asking for a key you did not declare, or with the wrong method, throws. A secret has no
default, is kept encrypted for the Windows account (DPAPI), is never shown again once
entered, and reads as null until the user sets it.

## Versions

`sdk` in your manifest is the `Hail.Sdk` you built against. Minor versions only add, so a
plugin built against 1.0 runs in every Hail that speaks 1.x. Hail refuses, with a sentence, a
plugin built for a newer minor or another major, before loading anything of it.

## Trust

A plugin is a program. It runs inside Hail, as the user, and can do anything the user can.
Hail does not sandbox it and cannot: .NET has no sandbox for code in its own process. What
Hail does:

- **Nothing is loaded until the user says so.** A new plugin folder is off until the user
  enables it, in a question that shows its name, its publisher (as the manifest says), what it
  answers, its folder and the start of its fingerprint.
- **The answer is pinned to the files.** Hail fingerprints every file in the folder. If any
  file changes, is added or is removed, the plugin is off until the user answers again, and
  the folder is checked again just before the plugin first loads. The .NET assemblies Hail
  loads are the bytes it checked, read into memory, so one swapped after the check is refused
  rather than run. A native library is checked and held against changes while it loads, but
  what Windows loads for it by itself (its own imports) is not checked at that moment.
- **Each plugin is loaded into a context of its own**, so two plugins can use different
  versions of the same library, and **Reload plugins** unloads them. If one cannot leave
  memory (a thread still running, an event still hooked), Hail says so and the old copy stays
  until Hail quits.
- **A failure costs the plugin, not the box.** Exceptions and overruns are caught and
  counted; the box never waits for a plugin longer than its budget.

What that cannot stop: a plugin that blocks a thread for ever, allocates without end, or ends
the process takes Hail with it. Only install plugins you would install as programs.

## Publishing

A plugin is its folder. Zip it, publish it however you like, and tell people to unzip it into
`%LOCALAPPDATA%\Hail\plugins\`. Hail has no store and downloads nothing, deliberately. Your
plugin is yours, under whatever licence you choose; the SDK's MIT licence asks nothing of it.
