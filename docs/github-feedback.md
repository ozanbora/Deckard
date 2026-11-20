# Bug reports from the app on GitHub

The trade server files every report sent from Deckard's feedback dialog as an issue in this repository, and the
project board (github.com/users/ozanbora/projects/2) picks those issues up by itself. Two one-time steps set it up.

## 1. A token for the trade server

On GitHub: Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token.

- Repository access: *Only select repositories* → `ozanbora/deckard`.
- Permissions: Repository permissions → **Issues: Read and write**. Nothing else (Metadata: read-only is added
  automatically).
- Expiration: up to a year; set a reminder to renew it.

Put it in the trade server's settings (user secrets in development, the host's settings on Azure), never in the repo:

```powershell
$token = Read-Host "GitHub token" -MaskInput
dotnet user-secrets set "Trade:GitHub:Token" $token --project src\Deckard.Trade.Api
dotnet user-secrets set "Trade:GitHub:Repository" "ozanbora/deckard" --project src\Deckard.Trade.Api
Remove-Variable token
```

Restart the server. Reports sent before the token was there are filed within half an hour.

## 2. The project board adds them itself

A fine-grained token can't write to a user's project board, so the board does it: open the project → **⋯** →
**Workflows** → **Auto-add to project** → filter `is:issue label:from-app` → repository `ozanbora/deckard` → turn it
on. New reports then land in the board's first column (*To triage*).

## Working on a report

An issue names no player: it has a report number, the player's words, the diagnostics they chose to send, and how many
screenshots the server keeps. To look at the screenshots, run on the server:

```powershell
dotnet run --project src\Deckard.Trade.Api -- --feedback <report number>
```

It copies them to a folder and prints where. They are deleted 90 days after the issue is closed. A player who sends
abuse can be stopped with `--nofeedback <BattleTag>`.
