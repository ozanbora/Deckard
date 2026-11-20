# Deploying the trade server on Azure

Deckard's app talks to one trade server (`src/Deckard.Trade.Api`): sign-in, the market, the season data and icons, feedback
and the app's updates. This is how to run it on Azure. The resources are in `infra/azure/main.bicep`; the deploy is
`.github/workflows/deploy-trade-server.yml`. Everything goes in **West Europe**, as `docs/privacy-policy.md` says.

## What it is made of

| Piece | Azure resource | Why |
|---|---|---|
| Database | PostgreSQL Flexible Server, Burstable B1ms, PostgreSQL 16, database `deckard` | Players, listings, offers, buy orders, patrons, telemetry. The server applies its EF Core migrations itself when it starts. |
| Server | App Service on Linux, Basic B1, .NET 10 | Runs the API. **Always On** stays on: the daily retention cleanup (`RetentionCleanup`) and the GitHub feedback filer run inside it. HTTPS only. One instance to start; it can run as several (see Scaling). |
| Files | Storage account, file share `deckard` mounted at `/mnt/deckard` | Three folders that outlive deploys: `data/` (season data and icons), `feedback/` (screenshots, never in the repo or on GitHub), `updates/` (the app's releases, `early/` for early-access patrons). |

Rough cost: €25-40 a month for these three at the smallest tiers (check the Azure pricing calculator for West Europe).

## Before you start

1. **Battle.net app.** At <https://develop.battle.net> create a client. Its redirect URL is the server's
   `https://<host>/auth/battlenet/callback`, where `<host>` is your custom domain (e.g. `trade.synica.nl`) or, without
   one, `<name>-trade.azurewebsites.net`. Note the client id and secret.
2. **A signing key**: a long random secret that signs players' tokens, e.g. in PowerShell
   `[Convert]::ToBase64String((1..64 | % { Get-Random -Max 256 }) -as [byte[]])`. Changing it later signs everyone out.
3. **A database password** for the PostgreSQL administrator.
4. The Azure CLI (`az login`) and a resource group: `az group create -n deckard -l westeurope`.

Keep the secrets out of the repo: pass them on the command line or keep them in a password manager.

## 1. Create the resources

```powershell
az deployment group create -g deckard -f infra/azure/main.bicep -p `
  name=deckard `
  hostName=trade.synica.nl `
  dbPassword='<database password>' `
  signingKey='<signing key>' `
  battleNetClientId='<client id>' `
  battleNetClientSecret='<client secret>'
```

The secrets can also come from a parameters file kept outside the repo (`-p '@C:\path\deckard.parameters.json'`). The
Battle.net pair can be left out at first: sign-in then answers that it isn't set up. Leave `hostName` out to use `deckard-trade.azurewebsites.net`. The outputs give the app's URL and the Battle.net redirect URL
to check against step 1 of "Before you start".

The template sets these on the App Service (Configuration):

| Setting | Value |
|---|---|
| `ConnectionStrings:Trade` | the database, over SSL |
| `Trade__SigningKey` | the signing key |
| `Trade__BattleNet__ClientId`, `__ClientSecret`, `__RedirectUri` | the Battle.net app; the redirect URL with the public host |
| `Trade__DataRoot`, `Trade__FeedbackRoot`, `Trade__UpdatesRoot` | `/mnt/deckard/data`, `/feedback`, `/updates` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true`: App Service ends HTTPS and forwards the request; without it the server sees plain http and the proxy's address, which breaks the HTTPS redirect, the sign-in callbacks and the per-address sign-in limit |
| `ASPNETCORE_ENVIRONMENT` | `Production`: development sign-in (`/auth/dev-login`) and the dev-only commands are off |

Optional settings, added in the portal (Configuration > Application settings) when you want them:

- **Patreon** (linking and early access, `PatreonLink`): `Trade__Patreon__ClientId`, `__ClientSecret`, `__CampaignId`, `__WebhookSecret`,
  `__EarlyAccessTiers`, `__RedirectUri` (`https://<host>/auth/patreon/callback`). In Patreon's developer portal the
  redirect URL is the same, and the webhook goes to `https://<host>/patreon/webhook`.
- **Feedback as GitHub issues** (`docs/github-feedback.md`): `Trade__GitHub__Token`, `Trade__GitHub__Repository`.

## 2. Custom domain

The first run creates the app without the custom host. At your DNS host add:

| Record | Name | Value |
|---|---|---|
| CNAME | the host, e.g. `trade` for `trade.synica.nl` | `<name>-trade.azurewebsites.net` |
| TXT | `asuid.` + the host, e.g. `asuid.trade` | the `customDomainVerificationId` output of step 1 |

Once they resolve (`nslookup -type=CNAME trade.synica.nl`), run step 1's command again with `bindHostName=true`: it binds the
host and gives it a free App Service managed certificate, which App Service renews itself. The host must be the same as
`hostName` and in the Battle.net app.

## 3. Upload the season data

The server serves the game data from the share's `data/` folder: copy the season's `season-<N>/` folder and `icons/`
there (Storage browser in the portal, Azure Storage Explorer, or `az storage file upload-batch`), then restart the server.
Producing that data isn't part of this repository. Do it again every season, and whenever the data's revision goes up
(the server logs a warning).

## 4. Deploy the server

Once, in GitHub: the repository secret `AZURE_WEBAPP_PUBLISH_PROFILE` (the App Service's Overview > Download publish
profile) and the repository variable `AZURE_WEBAPP_NAME` (`deckard-trade`). Then Actions > **Deploy trade server** > Run
workflow: it runs the Core and API tests, publishes the server and deploys it. The first start creates the tables.

Without GitHub Actions, from your PC:

```powershell
dotnet publish src/Deckard.Trade.Api -c Release -o publish
Compress-Archive publish/* publish.zip -Force
az webapp deploy -g deckard -n deckard-trade --src-path publish.zip --type zip
```

Check it: `https://<host>/patreon` answers **401** (every endpoint wants a signed-in player). The log stream (App Service >
Log stream) shows the migrations and, if the data is older than the code expects, the warning to extract again.

## 5. Release the app against it

```powershell
./tools/installer/pack.ps1 -Version 0.1.0 -Server https://<host> -SignParams '<signtool parameters>'
```

builds the app with the server's address in it, into `dist/releases`. Copy that folder's files into the share's `updates/`
(`dist/releases-early` into `updates/early/` for an early-access release); installed apps find updates there.

## Moderation and other commands

The server's commands (`--reports`, `--unflag`, `--ban`, `--uncredit`, `--feedback`, `--nofeedback`; `docs/moderation.md`)
run against the same database: from the App Service's SSH console (`dotnet Deckard.Trade.Api.dll --reports` in
`/home/site/wwwroot`), or on your PC with `ConnectionStrings:Trade` pointing at the Azure database (add your address to the
database's firewall first, and remove it after).

## Scaling

**Up first.** One instance carries a lot: the app's steady load is its offer check every 15 seconds, about 70 small
requests a second for 1,000 players online at once (an estimate; nothing has been load-tested). Watch the App Service's
CPU and response time and the database's CPU and connections (Monitoring > Metrics), and move up a tier when they climb:

| When | Change |
|---|---|
| The server's CPU is busy at peak | App Service B2/B3, or Premium P0v3-P1v3 |
| The database's CPU or connections are maxed | PostgreSQL Burstable B2s, then General Purpose D2ds |

Each is a setting (the plan's SKU, the server's compute tier), in the portal or `main.bicep`; nothing in the code changes.

**Then out.** The server runs as several instances behind App Service's load balancer (Scale out: a fixed count, or
autoscale on CPU, on a Premium plan). What makes that safe:

- **Sign-ins in progress** are in the database (`PendingSignIns`, the `pending_sign_ins` table), not in one instance's
  memory: Battle.net's callback and the app's exchange may reach another instance than the one that started the
  sign-in. Each state and one-time code works once; taking it deletes it in the same statement.
- **Background work runs once** (`SingleInstance`, PostgreSQL advisory locks): the daily retention cleanup and the
  feedback filer run on whichever instance gets the lock, the others skip that round, so each report becomes one GitHub
  issue. The schema migrations and the season rollover at start run one instance at a time; the others wait, then find
  nothing to do.
- **Files** are on the shared file share every instance mounts.

What stays per instance, on purpose:

- **Rate limits** count per instance: with three instances a player gets up to three times the limits. Tolerable; a
  shared counter (Azure Cache for Redis) would make them exact.
- **The credits list** is cached for 5 minutes per instance: a name added or removed shows on the others within that.

Leave ARR affinity (sticky sessions) off: nothing needs it.

**Icons before a season launch.** A new player's app loads a build's icons in one burst (a skill tree has ~200), the
burstiest load the server sees, and every new season brings many new players at once. If that shows in the metrics,
serve the icons from Blob Storage behind Azure Front Door and point the app there. The season data itself stays behind
sign-in: it is third-party game data, served to signed-in players only.

## Before players use it

- Sign in with Battle.net through a release built against the server, list an item and make an offer from a second account.
- Make sure the database firewall has only the "Allow Azure services" rule.
- Republish `docs/privacy-policy.md` and `docs/terms-of-use.md` on synica.nl.
