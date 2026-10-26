# GGX EN Patch

A fan English patch for **マブラヴ ガールズガーデンX** (Muv-Luv Girls Garden X), the Japanese
DMM GAMES PC client. It is a [BepInEx](https://github.com/BepInEx/BepInEx) plugin that swaps the
game's Japanese text for English on your PC while you play.

What is translated:

- all story text: main chapters, events, side stories, character and spot episodes, with speaker
  names, choices and episode/chapter titles;
- the game's data text: items, skills, relics, missions, home and talk lines, Tweety posts, profiles,
  and Pico's tutorials;
- the fixed menu text: buttons, tabs, headers and notices.

Some text exists only on the server and appears for the first time in game (new events, rare menus).
See [Reporting untranslated text](#reporting-untranslated-text).

This is an unofficial fan project, not affiliated with or endorsed by the game's publishers. It is
for the Japanese client. An official English release is planned for late 2026; when it ships, its
UI and system terms win, and this patch will follow them where it can.

> [!WARNING]
> **Use this patch at your own risk.** Modifying the game client may be against the game's terms of
> service. The developers of this patch are not responsible for any action taken against your
> account, including suspension or a ban, or for any other consequence of using it.

## Install

1. Install **BepInEx 6.0.0-be.788, Unity IL2CPP, Windows x64** from
   <https://builds.bepinex.dev/projects/bepinex_be> (the file name starts with
   `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788`). Unzip it into the game folder, next to
   `muv_luv_girlsgardenx_cl.exe`.
2. Start the game once and close it at the title screen. The first launch takes a few minutes while
   BepInEx sets itself up (a `BepInEx\interop` folder appears).
3. Download the latest release zip from [Releases](https://github.com/sorakee/MLGG-EnTL/releases) and copy
   its `BepInEx` folder into the game folder, merging folders. You should end up with
   `BepInEx\plugins\GGXEnPatch\GGXEnPatch.dll`.
4. Start the game. On the first launch the plugin downloads the translations (about 6.5 MB) into
   `BepInEx\plugins\GGXEnPatch\translations\`. Restart the game once that's done (a few seconds) so
   menus and game data text switch to English too.

### Adult scenes

The adult (R18) scenes' text is a separate pack, `translations-r18.json`, downloaded along with the
rest. To leave it out, set `[Update] IncludeR18 = false` (see [Options](#options)); the plugin then
deletes it, and those scenes stay in Japanese.

## Updating

Translations improve every week: new events, fixes, and text players report. The plugin checks for
them each time the game starts and downloads only what changed, usually a few kilobytes, in the
background. Story text switches over at once; menus and game data text after the next restart.

- It contacts only the patch's pack host and sends nothing about you or the game. Every file is
  checked against the manifest's SHA-256 before it replaces anything.
- To update by hand instead, set `[Update] AutoUpdate = false`. The manifests list the current files:
  <https://pub-c34991024c2e4fa89421fb9c498384c0.r2.dev/main/manifest-main.json> (and `r18/manifest-r18.json`).
  Download the base `url` and every update `url`, unzip each (`.gz`) and save it under its `file` name in
  `translations`.
- A new plugin version (`GGXEnPatch.dll`) is not downloaded automatically; the BepInEx log says when
  one is out.
- Your own overrides: any other `.json` file you put in `translations` (for example `zz_mine.json`
  with `{"lines": {"<Japanese>": "<English>"}}`) loads last and wins. The updater never touches it.

## Options

`BepInEx\config\ggx.enpatch.cfg` is created on the first run:

| Setting | Default | What it does |
| --- | --- | --- |
| `[Text] LineBreaks` | `Keep` | `Strip` replaces the translation's line breaks with spaces and lets the text box wrap. |
| `[Text] FitUiLabels` | `true` | Shrinks long English menu labels (down to 60%) to fit their box. |
| `[Update] AutoUpdate` | `true` | Downloads new translation packs on launch. |
| `[Update] IncludeR18` | `true` | Also downloads the adult scenes' text. `false` removes it. |
| `[Update] PackUrl` | (empty) | Where the packs are published; empty means the patch's own host. |
| `[Debug] MarkUntranslated` | `false` | Prefixes story lines that have no translation yet with `[JP] `. |
| `[Debug] LogUntranslatedUi` | `false` | Lists Japanese menu text still on screen in `BepInEx\plugins\GGXEnPatch\ui-untranslated.tsv`. |

## Reporting untranslated text

1. Set `[Debug] LogUntranslatedUi = true` and play normally through the screens that still show
   Japanese.
2. Open an issue and attach `BepInEx\plugins\GGXEnPatch\ui-untranslated.tsv`, with a screenshot if you
   can. **Check the file first:** it can contain your player name, circle name and circle chat
   lines; delete those rows.
3. Wrong or awkward English: open an issue with a screenshot and where it appeared.

Fixes reach everyone through the next translation update.

## Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` and `dotnet`
folders from the game folder.

After a game update, BepInEx regenerates its files on the next launch (slow start again). If the
game breaks after an update, remove BepInEx until the patch is updated.

## How it works

- Nothing is sent to the game server, and no game files are changed. The plugin only changes the
  text shown on your PC, after the game has loaded it. Its only network use is the translation
  update check above.
- Translations are matched by their exact Japanese text: story lines as the scenario plays, data
  text in the game's in-memory tables a couple of seconds after they load, and menu labels as
  TextMeshPro draws them.
- Translations come as packs in `translations\`: a full **base** pack per channel (`main`, `r18`),
  then small **update** packs holding only the entries changed since that base (an entry set to
  `null` is removed), applied in order, then any packs of your own. An update made for another base
  is ignored. Every few weeks a new base replaces the old base and its updates. The packs are built in
  the translation project and published to a separate file host; they are not in this repository.

## Building from source

Needs the .NET 6 SDK (or newer) and the game with BepInEx installed and launched once, because the
build references `BepInEx\core` and the generated `BepInEx\interop` assemblies. The code is in `src\`;
run the build from the repository root, where `GGXEnPatch.csproj` is.

```
dotnet build -c Release -p:GameDir="C:\path\to\muv_luv_girlsgardenx_cl"
```

The output is staged as a ready-to-zip folder in `..\dist\GGXEnPatch-Community` (the DLL, an empty
`translations` folder and `INSTALL.txt`). The MSB3246 warning ("PE image does not have metadata")
comes from a native DLL in the references and is harmless.

## License

The plugin source is under the [MIT License](LICENSE). The game, its text and its assets belong to
their owners; this repository contains none of them.
