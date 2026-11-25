# GGX EN Patch

A fan English patch for **マブラヴ ガールズガーデンX** (Muv-Luv Girls Garden X), the Japanese DMM GAMES
PC client. It's a [BepInEx](https://github.com/BepInEx/BepInEx) plugin that replaces the game's
Japanese text with English on your PC.

It translates all story text (with names, choices and titles), game data (items, skills, relics,
missions, home lines, Tweety, profiles, tutorials) and menus. Text that only exists on the server can
show up untranslated until someone reports it.

> [!NOTE]
> **Rare releases don't mean a dead project.** The plugin is only updated when a game update breaks
> it. Translations are updated weekly and downloaded on every launch, so an old plugin release still
> gets this week's text.

Unofficial and not affiliated with the publishers. Translated from the Japanese client, separately
from the official English version.

> [!WARNING]
> **Use at your own risk.** Modding the client may break the game's terms of service. We are not
> responsible for bans or any other consequences.

## Install

1. Unzip **BepInEx 6.0.0-be.788, Unity IL2CPP, win-x64** from
   <https://builds.bepinex.dev/projects/bepinex_be> into the game folder (next to
   `muv_luv_girlsgardenx_cl.exe`).
2. Start the game once and close it at the title screen. The first launch takes a few minutes.
3. Copy the `BepInEx` folder from the latest [release](https://github.com/sorakee/MLGG-EnTL/releases)
   into the game folder, merging folders (`BepInEx\plugins\GGXEnPatch\GGXEnPatch.dll`).
4. Start the game. It downloads the translations (~6.5 MB) within a few seconds; then restart once so menus
   and data text switch to English too.

R18 scene text is a separate pack. Set `IncludeR18 = false` to remove it.

## Updating

Translations update automatically on launch (only the changes, usually a few KB). Story text switches
right away; menus and data text after a restart. Files are verified by SHA-256, and the check sends
nothing about you.

- **Manual updates:** set `AutoUpdate = false`, then download the base and update `url`s listed in
  the [manifest](https://pub-c34991024c2e4fa89421fb9c498384c0.r2.dev/main/manifest-main.json)
  (and `r18/manifest-r18.json`), gunzip them, and save each under its `file` name in `translations`.
- **New plugin versions** aren't auto-downloaded; the BepInEx log tells you when one is out.
- **Your own fixes:** any other `.json` in `translations` (e.g. `zz_mine.json` with
  `{"lines": {"<Japanese>": "<English>"}}`) loads last and is never touched by the updater.

## Options

Edit `BepInEx\config\ggx.enpatch.cfg` (created on first run):

| Setting | Default | Effect |
| --- | --- | --- |
| `[Text] LineBreaks` | `Keep` | `Strip` replaces line breaks with spaces so text wraps. |
| `[Text] FitUiLabels` | `true` | Shrinks long menu labels (down to 60%) to fit. |
| `[Update] AutoUpdate` | `true` | Downloads translation updates on launch. |
| `[Update] IncludeR18` | `true` | Downloads R18 scene text; `false` deletes it. |
| `[Update] PackUrl` | (empty) | Custom pack host. |
| `[Report] LogUntranslatedUi` | `true` | Logs untranslated menu text to `ui-untranslated.tsv`. |
| `[Report] SendUntranslatedUi` | `true` | Sends that list to the translators. |
| `[Report] ShareScenes` | `true` | Sends links to story scenes the translators are missing. |
| `[Report] ReportUrl` | (empty) | Custom report service. |
| `[Debug] MarkUntranslated` | `false` | Prefixes untranslated story lines with `[JP] `. |

## Reporting

The plugin reports gaps automatically:

- **Menu text** with no English is logged to `BepInEx\plugins\GGXEnPatch\ui-untranslated.tsv`
  and sent in small batches. Names, typed text, chat, news and story lines are filtered out.
- **Missing story scenes:** if you open a scene on the translators' short "missing" list, the plugin
  sends its id and the game's download link so the scene can be fetched. Most players never hit one.

Only that text (with its UI element name), scene links and the plugin version are sent. Never your
name, account, login, circle, chat or PC details. IPs are used only for rate limiting and aren't
stored. To opt out, set `SendUntranslatedUi` / `ShareScenes` to `false`. The `.tsv` is still
written, so you can attach it to an issue instead. Check it first, since the filters can miss a name.

For wrong or awkward English, open an issue with a screenshot and its location.

## Uninstall

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx` and `dotnet`
folders. If a game update breaks things, remove BepInEx until the patch is updated.

## How it works

The plugin never modifies game files or talks to the game server; it only swaps text on screen
after loading, matching exact Japanese strings. Translations come as a base pack per channel
(`main`, `r18`) plus small update packs (a `null` entry removes a line), then your own packs. A new
base replaces the old one every few weeks. Packs are built and hosted separately from this repo.

## Building

Requires the .NET 6+ SDK and the game with BepInEx launched once (for `BepInEx\core` and
`BepInEx\interop`). From the repo root:

```
dotnet build -c Release -p:GameDir="C:\path\to\muv_luv_girlsgardenx_cl"
```

Output goes to `..\dist\GGXEnPatch-Community`. The MSB3246 warning is harmless.

## License

Plugin source: [MIT](LICENSE). The game, its text and its assets belong to their owners; none are
included here.
