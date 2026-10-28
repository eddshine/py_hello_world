# WinAct

A Windows desktop app that activates Windows and Office with one click. The activation engine
is **Microsoft Activation Scripts (MAS)**, currently 3.12, embedded inside the executable and kept
unmodified. The app supplies the window, the action buttons, a live terminal view and an input
box for menu answers.

The main window is deliberately free of implementation detail: no command lines, no file
paths, no `cmd` window. The engine's own messages still appear in the log — filtering them out
would mean hiding what actually runs.

## Run it

```
dist\
  WinAct 1.0.7.exe       ← double-click (accept the UAC prompt)
```

There is no engine file beside the EXE. On launch the app writes the embedded engine to a
private folder under `%LOCALAPPDATA%\WindowsActivator`, runs it through `cmd.exe` inside a
pseudoconsole, and removes the folder when the app closes. It deliberately does not use `%TEMP%`:
the engine refuses to run from a temp folder. The EXE is self-contained: the target PC needs no
.NET installation, just **Windows 10 version 1809 (build 17763) or newer, x64**.

Windows shows a UAC prompt because activation needs an elevated token (`slmgr`, WMI and
scheduled tasks). If the log says *"Elevated: NO"*, activation will fail — close and relaunch
the app.

## What the buttons do

| Button | Engine call | Notes |
|---|---|---|
| Activate Windows — HWID | `/HWID` | Permanent digital licence. Windows 10/11 only. |
| TSforge · Windows | `/Z-Windows` | Offline, also covers Server / ESU. |
| Online KMS · Windows | `/K-Windows` | 180-day licence, renews via scheduled task. |
| HWID · keep edition | `/HWID-NoEditionChange` | Skips the automatic Home → Pro change. |
| Activate Office — Ohook | `/Ohook` | Permanent, Office 2013+. |
| Online KMS · Office | `/K-Office` | 180-day licence. |
| Remove Ohook | `/Ohook-Uninstall` | Uninstalls the hook. |
| TSforge · Office | `/Z-Office` | Includes Project and Visio. |
| Everything at once | `/Z-WindowsESUOffice` | Windows + ESU + Office. |
| Check activation status | (in-app) | Queries the Software Protection Platform, prints every licensed product. |
| Classic menu | (no arguments) | The full text menu; answer it in the input box. |
| Open app folder | — | Explorer, for logs. |

The status line at the top is read-only: it shows the Windows edition/licence state and the
Office products found by the Software Protection Platform. **Refresh** re-queries it, and it
re-queries itself after every finished action.

## Answering prompts

The strip under the log is the keyboard for the running engine:

1. Start an action (or **Classic menu**).
2. When a prompt appears, type the menu number or the text it asks for.
3. Press **Enter** (or click **Send**). The answer goes to the console exactly as if it had
   been typed there.

While something is running you can simply start typing anywhere in the window — the first
keystroke jumps into the box. **Stop** terminates the run.

This works because the engine runs inside a Windows **pseudoconsole** (ConPTY), not a
redirected pipe. A redirected console has no keyboard, so `choice`-based menus fail instantly;
the pseudoconsole keeps a real console attached, so `choice`, `pause`, `set /p` and colour
output all behave normally — but the window stays inside this app.

## The log

The log is a small terminal, not a plain text dump. It keeps a screen grid — cursor moves,
in-place overwrites, erases — preserves ANSI colours, and folds lines that scroll off the top
into the transcript. A `cls` becomes a `────` divider instead of wiping the history, and empty
rows left over from cursor positioning are collapsed so each screen reads compactly instead of
leaving large gaps. **Clear** empties the pane, **Copy** copies the selection, and **Save log**
writes the pane to a text file.

## Updates

There is **one update channel**: the app's own GitHub Releases, compiled in as
`eddshine/py_hello_world`. The engine travels inside the EXE, so updating the app updates the
engine at the same time.

* **On launch** the app quietly reads the newest published release, compares its tag with the
  installed version, and offers a newer build with one Yes/No prompt. If the build is current,
  the network is unavailable, or no release exists yet, launch stays silent — an offline
  machine is unaffected.
* **Check updates** runs the same comparison on demand and reports "up to date", "update
  available", or that no release has been published.
* On confirmation the app downloads the `WinAct <app version>.exe` asset, checks the version inside
  the file and, when GitHub publishes an asset digest, its SHA-256. It then renames the running
  image to `.old`, moves the new one into place and restarts. The `.old` is removed on the next
  launch.

The app has **its own version**, independent of the engine: this build is **1.0.7** and carries
MAS 3.12. Releases are tagged `v<app version>` (`v1.0.7`), the title bar shows only the app version,
and the engine version is stated in the release notes. A launcher change and an engine change
both produce a new app version; neither number is derived from the other.

Any build from 1.0.6 onward updates itself, so an installed 1.0.6 will replace itself with 1.0.7
as soon as that release exists. An installed 1.0.5 has no updater and must be replaced once by
hand. The repository address is compiled in, so moving to a different repository means
rebuilding the app.

### Automation

Two workflows in `.github/workflows/` keep that channel fed:

* **`sync.yaml`** runs weekly on Monday (or by hand). It reads the `set masver=` line from
  upstream MAS and from `MAS/All-In-One-Version-KL/MAS_AIO.cmd`. If the version is unchanged it
  stops without a merge or commit. If it is new it merges upstream, preserves the local
  `README.md`, saves the upstream one as `Original_Updated_Readme.md`, bumps the app's patch
  version in the csproj (1.0.7 → 1.0.8), and commits both. The bump skips tags that already
  exist and always starts above the newest published tag, so a hand-published build can never be
  followed by an older one.
* **`release.yml`** runs after a successful sync (or by hand). It reads the app version from the
  csproj, stops if that tag already exists, builds `WinAct.exe` with the engine embedded, runs
  the headless checks, and publishes a normal release tagged `v<app version>` with the asset
  named `WinAct <app version>.exe` (for example `WinAct 1.0.7.exe`) and the engine version in
  the notes.

A manual **Run workflow** with an optional version input (for example `1.0.8`) publishes a
launcher-only build without touching the engine. Leaving the input blank uses the csproj version;
an explicit version must be at or above it, so the channel only ever moves forward. The workflow
does not create drafts or pre-releases.

## Layout

```
.github/workflows/
  sync.yaml              upstream MAS sync, gated on the masver line
  release.yml            build + publish “WinAct <version>.exe” with the engine embedded
MAS/
  All-In-One-Version-KL/
    MAS_AIO.cmd          official MAS engine, unmodified
src/MasLauncher/         C# source (net8.0-windows, WinForms)
  Engine.cs              embedded engine: extract, validate, CRLF, cleanup
  ConsoleSession.cs      ConPTY host (pseudoconsole + pipes)
  TerminalView.cs        VT-lite renderer for the log pane
  AppUpdater.cs          launcher releases, verification, image swap
  HintTextBox.cs         answer box with an indented hint
  AppInfo.cs             version shown in the title bar
  Rounded.cs             rounded button and panel painting
  MainForm.cs            UI, actions, status query, update check
  Program.cs             diagnostics (--selftest, --enginetest, …)
.config/
  dotnet-tools.json      pinned Obfuscar version (dotnet tool restore)
tools/
  installer.iss          Inno Setup 6 script
  obfuscar.xml           rename + string-hiding settings for release builds
icon.ico                 EXE, window and installer icon
dist/                    published EXE
  WinAct <version>.exe
```

## Rebuild

```powershell
dotnet publish src\MasLauncher\MasLauncher.csproj -c Release -o dist -p:Version=1.0.7
```

The published file is named with its version (`dist\WinAct 1.0.7.exe`), so a new build can sit
beside the old one. The build embeds `MAS\All-In-One-Version-KL\MAS_AIO.cmd`; that file must
exist. To change the engine, replace that file and rebuild. Publishing the launcher does not
modify it.

Single-file, self-contained, compressed: ~63 MB. Drop `-r win-x64 --self-contained` from the
csproj if you would rather ship a ~200 KB EXE that needs the .NET 8 Desktop Runtime.

Release builds are obfuscated, so run `dotnet tool restore` once in a fresh clone (see below).

For an installer, install Inno Setup 6 and run `iscc tools\installer.iss`; the output lands in
`dist\WinAct-Setup-<version>.exe`.

## Obfuscation

The launcher code is renamed and string-hidden so a copy of the EXE is not a readable copy of the
source. Every Release build runs [Obfuscar](https://github.com/obfuscar/obfuscar) (MIT) straight
after compilation, before the assembly is copied to `bin\` and bundled into the single-file EXE:

* types, namespaces, methods, fields, properties and events are renamed;
* string literals become calls to a lookup method;
* no PDB is produced and the assembly is marked to discourage `ildasm`.

The settings live in [tools/obfuscar.xml](tools/obfuscar.xml). The tool is pinned in
`.config/dotnet-tools.json`, so a fresh clone needs one restore:

```powershell
dotnet tool restore
```

To work on the code with real names — stepping in a debugger, reading a stack trace — build
readable:

```powershell
dotnet build src\MasLauncher\MasLauncher.csproj -c Debug
```

Debug builds are never obfuscated, and `-p:Obfuscate=false` turns it off for a Release build too.
The build keeps the original names in `obj\Release\...\obfuscated\Mapping.txt`; that file is for
reading a crash dump and must never be shipped.

What this does **not** do: it is not encryption, and .NET IL can still be decompiled — the rename
just makes the result hard to follow and easy to spot as copied. There is no anti-tamper,
antidebug or licence check, so a determined person can still patch the updater or lift the UI.
The activation engine is MAS and ships unmodified under GPL-3.0, so it stays readable by design.

## Diagnostics

The EXE has several headless modes, useful when the UI cannot start. They run against the
built DLL so they do not trigger the EXE's UAC prompt:

```powershell
dotnet exec src\MasLauncher\bin\Release\net8.0-windows\win-x64\WinAct.dll --selftest report.txt
```

* `--selftest <file>` — builds the window, extracts and validates the embedded engine, queries
  licence state, writes a report.
* `--enginetest <file>` — checks the embedded resource, extraction, validation, CRLF, that the
  extraction folder is outside `%TEMP%`, and reports the app and engine versions separately.
* `--enginelaunch <file>` — starts the real engine through the pty and waits for its main menu;
  a temp folder makes it refuse to start. Needs an elevated shell, otherwise it checks the folder
  only and skips the launch.
* `--screenshot <file>` — renders the window to a PNG.
* `--wording <file>` — fails if any visible string or tooltip still names the engine, calls the
  work a script, or says "component".
* `--updatecheck <file>` — runs the real app update check through the message loop.
* `--appupdate <file>` — checks version comparison, release parsing, asset selection and the
  image swap, and reports what the live release feed currently holds.
* `--inputtest <png>` — end-to-end: starts a mock engine file that asks a question, types the
  answer into the input box, writes the log next to the PNG (and `.raw.txt` with the pty
  stream).
* `--ptytest <file>` — proves a `choice` prompt receives typed input and the exit code
  round-trips.
* `--ptyseq <file>` — two prompts in a row: checks the Enter appended to an answer does not
  leak into the next prompt.
* `--ptyecho <file>` — minimal pseudoconsole check (`cmd /c echo`).
* `--ptygap <file>` — cursor-addressed screen (top text, jump to a low row, two-column
  line): checks that empty-row padding is collapsed and columns stay aligned.
* `--ptyscroll <file>` — prints 60 lines through a 38-row screen and checks that nothing is
  lost from the scrollback.
* `--termtest <file>` — renderer check for live lines, colours and `cls` dividers.

## Notes and limits

* **Verify the engine.** The copy in `MAS/All-In-One-Version-KL/` is the official 3.12 file
  (SHA-256 `850f9796…a0bfa`). MAS opens with comments addressed to antivirus and AI scanners,
  asking not to be flagged. That text is unusual and should not be trusted at face value.
  Licence reporting is local; the only network calls are the update checks against
  `api.github.com` and the file downloaded when you agree.
* **Antivirus.** MAS is already flagged by many engines. Embedding the engine does not hide it
  from scanners: the app writes it to `%LOCALAPPDATA%` and runs it elevated, which is also
  behaviour scanners watch for. Expect the EXE to be flagged too; that is a consequence of what
  it runs, not of the packaging.
* **Licensing.** Activating Windows or Office without a valid licence is outside Microsoft's
  terms of service. The app only provides a launcher; it does not alter the engine.
* **GPL.** The app ships MAS unmodified under GPL-3.0. The complete corresponding source is
  this repository (the engine mirror plus `src/MasLauncher`).
* **Staleness.** MAS has its own update check (`updatecheck312.massgrave.dev`) and may offer to
  fetch a newer build during a session. That download lasts only for the session; the app
  always starts from the engine embedded in the installed EXE. To make a new engine permanent,
  the release workflow must publish a new app build.
* **Log limits.** The pane keeps roughly the last 400 KB; **Save log** writes what is visible
  if you need to report a failure.
