# Moderation routine

What needs a person's eye, and how often. All commands run on the trade server, from the repository
(`dotnet run --project src\Deckard.Trade.Api -- <command>`), and exit when done.

## Weekly (daily in the first weeks after launch)

1. **Reported players.** `--reports` lists players taken off the market automatically (reported by 3 or more
   different trade partners, with more reports than completed trades), with every report's words. For each:
   - the reports look like misunderstandings or one bad week: `--unflag <BattleTag>` puts them back;
   - they look like scams: `--ban <BattleTag>`. Their listings and buy orders stay hidden and they can't trade.
2. **Bug reports.** New issues labelled `from-app` on the project board (*To triage*). `--feedback <report>` copies a
   report's screenshots to a folder. Someone abusing the form: `--nofeedback <BattleTag>`.
3. **Credit names.** Look at the supporters list in the app's About page. An offensive name: `--uncredit <BattleTag>`
   (they can choose another; if they keep at it, unlink their Patreon from the database by hand or ban them).

## Each season

- Update the game data (the season data routine is kept outside this repository).
- Glance at listings in the new season's realm for impossible items (the server already refuses affixes that don't
  exist or don't roll on the item type).

## When something looks wrong

- A wave of reports against one player that all read alike may be a group gaming the system: unflag, and keep an eye on
  the reporters.
- Anything that suggests real-money trading (prices "in $", links to payment sites): ban, as the terms of use say.
- A legal request about a player's data: answer from info@synica.nl, and check the privacy policy's promises first.

Keep a short note of each ban and why (date, BattleTag, reason), outside the repository.
