# CloverMP

Online co-op for **[CloverPit](https://store.steampowered.com/app/3314790/CloverPit/)**: one shared run for up to four friends over Steam. Everyone stands in the same room, with the same slot machine, the same debt, the same charms and the same deadline.

> **⚠️ Very early, experimental stage.** This is a test version built in a few days. Expect desyncs, rough edges and missing pieces. Back up your saves before you try it, and please report what breaks.

This mod was created with **[Coop Anything](https://github.com/cranebeaker/coop-anything)**, an open playbook and toolset that lets an AI coding agent add co-op to single-player games. The whole mod (reverse engineering, netcode, tests) was built by an agent following that playbook, guided and play-tested by the repo owner and a friend.

CloverMP is an independent fan project. It is not affiliated with or endorsed by Panik Arcade or the CloverPit developers, and it contains no game files.

---

## Features

- **Shared run** over Steam (lobby, invites, Steam relay: no port forwarding, no VPN).
- **Join any time**: a joining player receives the host's run and progress; their own save stays untouched.
- **Host is the authority**: after every action all games compare a hash of the run state. If a player differs, their game takes over the host's values (coins, tickets, deposit, interest and the coins in the interest tray, debt, deadline, rounds, spins, random streams). If that is not enough, the whole run is sent again.
- **One player at the slot machine at a time.** Everyone else stays free: walk, look around, buy and sell charms, deposit, collect interest, use drawers. Their actions run between two spins.
- **Machine view**: while someone else plays, a small window shows the slot machine with spins and coins.
- **Red button** on the machine and the **surgery machine** (DLC): synced, one player at a time at the surgery machine; the others see the surgery in the room.
- **Phone**: the host chooses in the `F8` menu whether only the host or everyone may use it. Choices are synced (pickup, reroll, ability), nobody else gets pulled in.
- **Bad connections**: a friend who loses the connection reconnects automatically and gets what they missed. Ping is shown in the `F8` menu.
- **Failed actions stay private**: "no space for this charm" only reaches the player who tried it.
- **Main menu**: an `F8` button, and friends land there when the host ends the session, to continue their own run or start a new one.
- **New run / death**: the host's new run is sent to everyone automatically.
- **Players** shown as simple colored figures with their Steam name.
- **Chat** on `T`, with a soft pop for messages from others (your own sound: a 16 bit PCM `chat.wav` next to the DLL) and a typing bubble above the name.
- **Co-op menu** on `F8`: movable, with switches for the settings.
- **Player list**: hold `Tab` to see everyone with their ping.
- **Discord Rich Presence**: your Discord status shows coins, clover tickets, the deadline (deposited / debt, rounds left) and, in co-op, the group size. Talks to the local Discord app directly, no extra DLL. Can be turned off in the `F8` menu.
- **Only the host saves.**
- **Automatic updates**: see below.

## Install

Every player needs CloverPit on Steam and the same mod version.

1. Install **BepInEx 6.0.0-be.785, Unity Mono, x64** into the CloverPit folder (Steam → right-click CloverPit → Manage → Browse local files). Official builds: <https://builds.bepinex.dev/projects/bepinex_be> (also on Thunderstore as the CloverPit BepInExPack).
2. Start the game once, then close it.
3. Download `CloverPitCoop.dll` from the [Releases](../../releases) and put it into `BepInEx\plugins\`.
4. Remove other co-op mods (e.g. `CloverCoopPlugin.dll`) from `BepInEx\plugins\`.
5. Back up the `SaveData` folder in the game folder.

## Play

- `F8`: co-op menu (available once you stand in the room). Host: **Host a session**, then invite friends through the Steam overlay.
- Friends: start the game, wait until you stand in the room, then accept the invite in Steam.
- `T`: chat. `F8` / `Esc`: close the menu. Hold `Tab`: player list.
- **Leave session** ends the session. A friend returns to the main menu with their own save; the host keeps playing alone.
- **Main menu** (click twice) goes to the title screen. As the host this ends the session for everyone. It saves in the room, not during a round, like the game's own pause menu.
- Discord status: on by default; switch it in the `F8` menu (stored in `BepInEx\config\coop-anything.cloverpit-coop.cfg`).
- Everyone should either own the DLC or not; the mod warns when it is mixed.

## Automatic updates

From version 0.7.7 on, the mod looks for a newer release of this repository on GitHub when the game starts (pre-releases included). If there is one, it downloads `CloverPitCoop.dll`, replaces the installed file and restarts the game. A player already in the room or in a co-op session is not interrupted; the file is replaced when they close the game.

This runs code from this repository on your PC without asking each time. Turn it off with the **Updates** switch in the `F8` menu, or `AutoUpdate = false` under `[Updates]` in `BepInExnfigop-anything.cloverpit-coop.cfg`.

It only accepts the DLL from this repository, with a higher version, whose SHA-256 matches what GitHub lists and whose name and version match the release. Each version is tried once.

## How it works

CloverPit's gameplay randomness comes from seeded random streams that are part of the run state, and every player action goes through a small set of entry points (`GameplayMaster.FCall_*`, the bet menu, the charm inspect menu, the phone UI). CloverMP uses **input lockstep with host authority**:

1. Every shared action passes a Harmony prefix. On the host it is checked against the co-op rules, numbered and broadcast; on a friend's machine it is sent to the host as a request and only runs when the host's broadcast comes back. So every machine runs the same actions in the same order, each in the same game phase.
2. After each action every machine hashes the run state (`GameplayData`, serialized the way the game saves it, wall-clock fields blanked). The host compares.
3. On a difference the host sends its values at the next quiet moment; the friend applies them right after the same action (and after any cutscene the action set off), the same way the game loads a save. If the hash still differs, the whole run is resent.
4. Joining sends the host's whole `GameData` (run plus meta progress) in memory; the friend's save is never written while in co-op.

Local game timers that would make the same action behave differently per machine (for example the ATM's one-second deposit lockout) are handled so that only actions the host actually ran are replayed.

## Build

Requires the .NET SDK and a CloverPit folder with BepInEx installed.

```
dotnet build src/CloverPitCoop/CloverPitCoop.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\CloverPit"
```

The plugin ends up in `src/CloverPitCoop/bin/Release/netstandard2.1/CloverPitCoop.dll`.

## Tests

The mod includes bots that play the game without a human, used to check sync with two real game copies on one PC:

- `--coop-selftest`: plugin loads, Steam lobby and relay work, a bot pulls the lever.
- `tools/run-dettest.ps1`: runs the same save twice and compares the run-state hash after every action (determinism).
- `tools/run-nettest.ps1`: host + friend on one PC over localhost (`-Transport steam` for the Steam relay), friend joins with an empty save (`-ClientFreshSave`), host gets coins to reach the deadline (`-Rich`), `-MutateSkip <n>` drops one action to prove the checks catch it.

These expect test copies of the game in `../test-installs/` (not part of this repo).

Last full test (v0.4.0): 110 actions over four rounds including paying the debt and the deadline cutscene, 77 state comparisons, 0 desyncs. Later versions were not run through these tests; 0.7.x is only compiled so far.

## Known issues

- Numbers can jump briefly while a correction is applied.
- Auto-spin is not yet limited to the player at the machine.
- Reconnecting, the surgery machine, the red button and the main menu are new and not tested in the game yet.
- A player without the DLC has no surgery machine, and a mixed group goes out of sync.
- The automatic update has not run in the game yet, and it is untested on Linux/Proton. If it fails, install the DLL by hand.
- If the host crashes, friends may see "reconnecting" for up to 2.5 minutes before they land in the main menu.
- If the mod waits for something on your side (an open dialogue), a notice shows; close it and play continues.

## Reporting bugs

Open an issue with `BepInEx\LogOutput.log` from the host and from the affected friend, and a short description.

## License

[GPL-3.0](LICENSE).
