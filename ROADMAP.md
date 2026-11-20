# Roadmap

## Supporter tiers (Patreon)

Patrons get conveniences, never an edge over other players. Anything that decides who sees or gets an item first stays the same
for everyone: watched searches and new-item alerts, how often offers are checked, listings, buy orders and search ranking.

### 1. Link Patreon (needed by everything below)

- [x] Trade server: Patreon sign-in run by the server, like `BattleNetLogin` (the server holds the client secret; the app gets a
      one-time code on a loopback port), started from a "Link Patreon" button in Settings.
      Redirect URLs registered with the Patreon client: `http://localhost:5191/auth/patreon/callback` and `http://localhost:5180/auth/patreon/callback` (development servers) and
      `https://<trade server>/auth/patreon/callback`. Client id and secret in `Trade:Patreon:ClientId` and
      `Trade:Patreon:ClientSecret` (user secrets or the host's settings, never the repo).
- [x] Trade server: the player's patron tier, read from Patreon's API when linked (`PatreonLink`; both tiers lift the same limits, so it keeps whether they're an active patron).
- [x] Trade server: `POST /patreon/webhook`, keeping tiers current when a patron joins, changes tier or cancels. It verifies
      Patreon's signature with the webhook secret (`Trade:Patreon:WebhookSecret`) and ignores anything unsigned; it's the one
      endpoint without a player token.
- [ ] Register `https://<trade server>/patreon/webhook` in Patreon's developer portal once the server is on Azure (Patreon only
      calls public https addresses).
- [x] The app asks `GET /patreon` after sign-in and after linking, instead of a tier in the sign-in token (a pledge change then needs no new sign-in).
- [x] Privacy policy: what the Patreon link stores (`docs/privacy-policy.md`, published on synica.nl).

### 2. Limits by tier

| Feature | Free | Patrons | Kept in |
|---|---|---|---|
| Saved quick searches | 5 | 25 | `quick-searches.json` (app) |
| Builds in the library | 3 | unlimited | `builds.json` (app) |
| Stash sorting | the active build | every build in the library | `StashView` (app) |

- [x] Saved quick searches: the cap on Save filters, saying why when it's reached.
- [x] Build library: the cap on adding a build, saying why when it's reached.
- [x] Stash sorting: free accounts sort against the active build only.
- [x] App: Link and Unlink Patreon in Settings, and `Supporter` with the limits (waits on the server's endpoints).
- [ ] Ship the limits with the first public release, so free players never lose something they had. Accounts over a cap keep what
      they have; they just can't add more.

These are checked in the app only, so a patched app can skip them. That's acceptable for conveniences; anything that matters
must be checked by the trade server.

### 3. Early access (Champion tier)

Champions get new versions before everyone else, from a second update channel. The server decides who gets it, so a patched
app can't.

- [x] Trade server: which tier a patron is on. Keep `EarlyAccess` for patrons entitled to a tier listed in
      `Trade:Patreon:EarlyAccessTiers` (Patreon tier ids; prices change, ids don't), read at linking and kept by the webhook.
      `GET /patreon` adds `earlyAccess`.
- [x] Trade server: serve `/updates/early/` (the `early` folder under `Trade:UpdatesRoot`) only to early-access accounts; 404 for
      everyone else, like a missing file.
- [x] `tools/installer/pack.ps1 -Channel early`: packs with vpk's `--channel early` into its own release folder, copied to
      `early/` on the server.
- [x] App: `Supporter.EarlyAccess`; `Updater` reads `/updates/early/` (Velopack's explicit channel `early`) while it's on, the
      normal feed otherwise; Settings says which one this account gets. Losing early access just means the next normal
      release, never a downgrade.
- [x] Try it with packed releases: an installed copy moving to an early release and back (0.1.1 → early 0.1.2 →
      no downgrade without early access → 0.1.3 normal, on 2026-10-28).

### 4. Supporters in the credits (both tiers)

Patrons who want it get their name in Deckard's About dialog. Opt-in only: a name is personal data.

- [x] Trade server: `PUT /patreon/credit` (`{ show, name }`: a name the patron types, at most 32 characters, no links) and
      `GET /supporters` (the names of active patrons who opted in). Lapsing or unlinking takes the name off the list.
- [x] App: a switch and the name in Settings > Patreon, for patrons; About lists the supporters.
- [x] Privacy policy: the credit name (shown to every Deckard player, kept until opted out or unlinked) in
      `docs/privacy-policy.md`.
- [x] Republish the privacy policy on synica.nl (the 28 October 2026 version).

## Telemetry

Anonymous usage and crash data, sent to our own trade server (no third party), only with the player's consent. Never
screenshots, tooltip images, item contents, BattleTags, Battle.net or Patreon ids, build links, zone names, or anything
about the game process.

- [x] App: ask once at first start ("Help improve Deckard by sending anonymous usage and crash data"), and a switch in
      Settings; off until the player says yes.
- [x] App: a random installation id (never the account); events batched and sent at most every few minutes:
  - crashes: exception type, stack trace of Deckard's own code, app version;
  - reading quality: affixes matched per tooltip, 2x OCR used, screen resolution and DPI, game language;
  - usage: pages opened, builds loaded, item card shown, listings made (counts);
  - environment: app version, Windows version, update channel.
- [x] Trade server: `POST /telemetry`, keeping no IP address and no account with the events; raw events deleted after 90
      days, daily counts kept.
- [ ] Trade server: Azure Application Insights for the server itself (requests, errors, database), with IP masking.
- [x] Privacy policy: telemetry (what, consent, 90 days). Republish on synica.nl (the user).

## Bug reports and feature requests

Sent from the app, filed by the trade server as GitHub issues in this repository. Players need no GitHub account, and the
GitHub token stays on the server.

- [x] App: a "Send feedback" dialog: bug report or feature request, title, description, up to 3 screenshots (capture the
      game window, capture Deckard, or a file; each previewed and removable before sending), and diagnostics (app version,
      Windows, resolution and DPI, game language, recent errors), off by default.
- [x] App: personal data covered on the player's PC before upload, so an unredacted screenshot never leaves it:
  - automatic first pass: in Deckard's own window, exactly where it draws personal data (the account button's BattleTag,
    trade partners' BattleTags, zones on deals); in the game window, best effort with the OCR Deckard already runs
    (BattleTag-shaped text, `Name#1234`, and the chat box, party and clan frames at their fixed places);
  - the player reviews it in the preview: removes or adds boxes (drag over any area) and crops (cropping: not yet); boxes are solid black, never
    blur (blurred text can be recovered); the dialog says to check nothing private is visible before Send, since names
    above characters in game can be missed.
- [x] Trade server: `POST /feedback`: checks size and type (PNG or JPEG, at most 5 MB each), strips their metadata
      (`ImageSanitizer`; no image library on the server, so not re-encoded), at most 5 reports per player per day, and a moderator command to stop a player sending feedback.
- [x] Trade server: creates the issue (labels `bug` or `enhancement`, and `from-app`); the Project board adds it with its
      Auto-add workflow (docs/github-feedback.md); screenshots are not linked but copied out with `--feedback <report>`. The issue names no player: a report number only; the server keeps who sent it, for
      moderation. Issue text is escaped (no mentions, no active links). Token in `Trade:GitHub:Token` (a GitHub App or a
      fine-grained token allowed only issues on this repository).
- [x] Trade server: screenshots in private storage (`Trade:FeedbackRoot`: a local folder now, Azure storage later; never
      committed, never uploaded to GitHub), deleted 90 days after the issue closes.
- [x] Privacy policy: feedback and screenshots (redacted on the player's PC, but they may still show names or chat), GitHub as a processor, how
      long they're kept. Republish on synica.nl (the user).

## Release

Not started: waiting for the Azure account. In this order: Azure first (it blocks the webhook, the beta and real testing),
then the install test and crash safety, telemetry and feedback, the closed beta, and the launch; the certificate, the
terms of use and the data source's answer run alongside.

### Blockers

- [ ] Trade server on Azure: production database with daily backups, secrets in the host's settings or Key Vault, a domain
      with https (e.g. `deckard.synica.nl`), the Battle.net and Patreon production redirect URLs, and the Patreon webhook.
- [x] Code-signing certificate for Synica: Azure Artifact Signing, `pack.ps1 -AzureSignFile` (1.0.0 is signed).
- [ ] Permission from the item data's source, or the fallback (our own extraction from the game files).
- [ ] Terms of use on synica.nl (docs/terms-of-use.md, ready to publish; open source, official app only, no automation): gold-only trading, players trade by hand in game, no guarantees about trades or game
      accounts, blocking abusers, not affiliated with Blizzard.
- [x] Season data on the server each season, checked before players get it; a written routine (kept outside this
      repository).
- [x] Install-and-update test with packed releases: install, update to a newer release (normal and early channels),
      uninstall.

### Strongly recommended

- [ ] Closed beta (Champions on the early channel): resolutions, Windows scaling, HDR and ultrawide screens.
- [ ] Game language: what happens with a non-English game client; support it, or say "English game client only".
- [ ] Cost while playing: Deckard's CPU, GPU and memory with the game running.
- [x] Crash safety: a global error handler that logs, so one failure doesn't close the app.
- [ ] Telemetry, and bug reports and feature requests (above).
- [x] Moderation routine: who checks reports, flagged players and credit names, and how often (docs/moderation.md).

### Launch

- [ ] Download page on synica.nl: the installer, system requirements, what Deckard does and doesn't do.
- [ ] FAQ, especially "Can I get banned?": Deckard only reads the screen and never touches the game, but Blizzard hasn't
      approved it.
- [ ] Support channel: a Discord or an email address, besides the in-app feedback.
- [ ] Changelog for every release, also posted on Patreon.
- [ ] Patreon: remove "(coming at release)" from the tiers on launch day.
