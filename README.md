# Deckard

A companion app for Diablo IV on Windows. Deckard reads what the game window shows, on your computer, and gives
advice next to it:

- **Builds:** import a build from a maxroll, d4builds.gg or Icy Veins link and see every item, the talisman, the
  mercenaries, the skill tree and the paragon boards it uses.
- **Items:** press Alt over an item in game for how it fits your build: keep, craft, sell or salvage. Sort a stash
  against your builds.
- **Skill tree and Paragon:** the build's points highlighted on the game's own skill tree and paragon boards, step by
  step while you level.
- **Trading for gold:** list items, make and answer offers, post buy orders and check prices. Players trade by hand in
  game; Deckard never touches gold or items.

**Download:** the signed installer is on the [Releases](https://github.com/ozanbora/Deckard/releases) page. Requires
Windows 11 (x64); run Diablo IV in **borderless windowed** mode. Installed copies update themselves.

**Website:** [synica.nl/deckard](https://www.synica.nl/deckard) · **Video:** [Deckard in action](https://www.youtube.com/watch?v=5PtE9AmSKgg)

**Support Deckard** on [GitHub Sponsors](https://github.com/sponsors/ozanbora) or
[Patreon](https://www.patreon.com/c/deckard_companion). Patrons on Patreon keep more saved searches and builds, sort
the stash against every build, can get early builds and their name in the credits. It never gives an advantage in the
market.

## Fair-play rules

- Strictly external. No memory reading, DLL injection, process hooks, or `OpenProcess` on the game.
- Capture reads composed window contents the same way the Snipping Tool and OBS do.
- No input automation, ever. Blizzard's terms forbid it, and the game runs Warden.
- Gold only: no real-money trading.

## Layout

| Project | What it is |
|---|---|
| `src/Deckard.Core` | Platform-neutral types: frames, the season's item catalog (`AffixCatalog`), build plans and their importers, trade contracts |
| `src/Deckard.Capture` | Window capture through Windows.Graphics.Capture, read back to BGRA8 |
| `src/Deckard.Perception` | Reading the screen: tooltips (pixel heuristics and Windows OCR), skill tree, paragon, mercenaries, stash verdicts |
| `src/Deckard.App` | The WinUI 3 app: build pages, the item card overlay, in-game highlights, trading |
| `src/Deckard.Cli` | `deckard` command line: capture, tooltip and affix checks against saved screenshots |
| `src/Deckard.Trade.Api` | The trade server: ASP.NET Core and PostgreSQL, hosted on Azure |
| `tests/` | xUnit tests; game screenshots are never committed |

## Development

Requires Windows 11 and the .NET 10 SDK.

```powershell
dotnet build
dotnet test                                                                    # the trade server's tests skip without a database:
$env:DECKARD_TRADE_TEST_DB = "Host=localhost;Database=deckard_trade_test;Username=postgres;Password=..."
dotnet test tests/Deckard.Trade.Tests                                          # (that database is dropped and recreated)
dotnet run --project src/Deckard.Cli -- capture --tooltips --crop              # save a tooltip crop whenever a new one appears
dotnet run --project src/Deckard.Cli -- tooltip captures                       # read item tooltips from saved screenshots
dotnet run --project src/Deckard.Cli -- affix "+1,284 Maximum Life"            # check which affixes a tooltip line matches
```

Windows.Graphics.Capture only delivers a new frame when the window's content changes, so a static window reports few
or no fps.

**A local trade server.** Set its secrets with `dotnet user-secrets` in `src/Deckard.Trade.Api`
(`ConnectionStrings:Trade` for a local PostgreSQL, and a `Trade:SigningKey` of 32+ characters), then
`dotnet run --project src/Deckard.Trade.Api`. It runs on http://localhost:5180 in Development, where the app offers a
development sign-in instead of Battle.net; a build without a server address built in talks to it, and
`DECKARD_TRADE_SERVER` points the app elsewhere. The server needs the season's item data in its data root
(`Trade:DataRoot`, else `data/d4`, git-ignored). That data isn't distributed with the source, so a server without it
doesn't start; the tests use small fixtures instead.

## Item data (changes every season)

Diablo IV's items, affixes, aspects and uniques change every season, so Deckard keeps its item data per season. The
app downloads the season's data and icons from the Deckard server after sign-in (into `%LOCALAPPDATA%\Deckard\data`
and `%LOCALAPPDATA%\Deckard\icons`, again only when they change); without them it shows why and loads nothing. How the
season data is produced isn't part of this repository. A server reads it from its data root:

```
data/d4/season-<N>/affix-templates.json   tooltip line templates loaded by AffixCatalog
data/d4/icons/<id>.webp                   item, skill and paragon icons
```

- **Item names (schema 2):** `baseNames` lists non-unique item names per item type and `rareNameWords` the words rare names use. Legendary names are `<aspect prefix> <base>` or `<base> <aspect suffix>`; `ItemNameResolver` uses this to correct OCR'd names and to know a legendary's aspect from its name alone.
- **Build plans (schema 3):** `items` (gear key -> name and type), `skills` (key -> name) and `skillTrees` (per class: node -> skill, modifier or passive and its max ranks) let the app name an imported build's items, skill bar and skill points.
- **`affix-templates.json` format:** each entry has a `kind` (`affix`, `implicit`, `tempered`, `aspect`, `unique`, `mythic`, `setBonus`), the game `keys`/`ids`, and the tooltip `lines` with rolled numbers written as `#` (`+#`, `#%` and `x#%` keep the sign and unit; `[x]` marks a multiplicative bonus). Regular affixes also carry their magic-item `prefix`/`suffix` names, `itemTypes` and `classes`. `unresolvedKeys` lists tempering affixes whose tooltip text isn't in the data.
- `AffixCatalog.SupportedSchemaVersion` must match the file's `schemaVersion`; a format change moves both.

## Releasing

`tools/installer/pack.ps1` builds an installer and the update files with [Velopack](https://velopack.io)
(`dotnet tool install -g vpk` once):

```powershell
./tools/installer/pack.ps1 -Version 1.0.1 -Server https://deckard.example.net -AzureSignFile C:\path\artifact-signing.json
./tools/installer/pack.ps1 -Version 0.1.0 -Server http://localhost:5180 -Unsigned   # a local test release
```

- **Players** download `Setup.exe` from the GitHub release. It installs per user, without admin rights, into
  `%LOCALAPPDATA%\DeckardApp` (Deckard's own data stays in `%LOCALAPPDATA%\Deckard`) and adds a Start menu shortcut.
- **Updates**: copy everything in the output folder except `Setup.exe` into the folder the server's
  `Trade:UpdatesRoot` names. Installed copies check after sign-in, download in the background (small delta packages
  when the previous release's files are kept), and update when the player restarts or exits Deckard.
- **Versions** must grow with every release. Keep the output folder between releases so deltas can be built.
- **Security**: updates run code on every installed copy. The server must be https (localhost is allowed for
  testing), installed copies only update over https and only send their session to that server, and a release must
  be signed, with Azure Artifact Signing (`-AzureSignFile`) or signtool (`-SignParams`), unless you pass `-Unsigned`
  on purpose. Official releases are signed by Synica.

## Bugs and security

Report bugs and ideas from the app (the title bar's Send feedback, which files a public GitHub issue without your
BattleTag) or as a [GitHub issue](https://github.com/ozanbora/Deckard/issues). Please report security problems
privately to info@synica.nl, not in an issue.

## License

Copyright © 2026 Synica. Deckard is free software under the [GNU Affero General Public License v3.0](LICENSE): the
app, the CLI and the trade server (`src/Deckard.Trade.Api`). If you run a modified trade server for others, the AGPL
asks you to offer them its source.

The trade service Synica runs is for the official Deckard app only (see the [terms of use](docs/terms-of-use.md)).
Builds from this source have no server address built in; `tools/installer/pack.ps1 -Server` sets one. A modified
version must not present itself as the official Deckard app or as coming from Synica.

Deckard is not made, endorsed or supported by Blizzard Entertainment. Diablo is a trademark of Blizzard Entertainment,
Inc. PT Serif is under the SIL Open Font License (`src/Deckard.App/Assets/Fonts`). The game data the server provides
belongs to its owners and is never part of this repository.
