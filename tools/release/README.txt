Ski Area Design Challenge {VERSION}
by Alpine Labs

A ski resort designer on real mountains, built from U.S. Geological Survey lidar.


NEW IN 0.3.2

- More lifts: fixed-grip doubles and triples from Sessellift and Monta, the Sessellift D4 detachable quad, and the
  Sessellift Stern quad, triple and double.
- Surface lifts: the Monta T-bar and J-bar, and the Glidepath magic carpet, single or double, covered or open.
- Drive at either end: on every fixed-grip chairlift, choose whether the drive sits at the top or the bottom (the
  Drive row in New lift and Edit lift).
- Lift prices: New lift and Edit lift show what a chairlift costs. Hover the Cost row to see the parts.
- Towers are numbered on the profile and over the lift; drag a tower on the profile to move it or change its height.
- Lift lines are cleared through the forest, and snow lies in the clearings.
- A download held up by a server says which one after 2 minutes, with Try now, Pause for later and Cancel.
- Get water and roads in Manage Areas, for an area downloaded without them.
- The game no longer crashes as it closes on some DirectX 12 graphics cards.
- Lift glass is see-through again: the glass on the detachable stations showed solid and dark.
- A lift line over some ground no longer asks for a cut about 1,000 m deep.


INSTALLING THE GAME

Download SkiAreaDesignChallenge-Setup.exe from the release page and run it.

1. This test build isn't signed, so Windows says "Windows protected your PC" once: click "More info", then
   "Run anyway".
2. Click Install. The game goes into %LOCALAPPDATA%\Programs\SkiAreaDesignChallenge (no administrator needed),
   with a Start-menu entry and, if you like, a desktop shortcut. When it's in, click
   "Launch Ski Area Design Challenge".
3. From then on, start the game from the Start menu or the shortcut. Each start checks for a new version first.
   When one is out, it asks: Update downloads it, checks it against its SHA-256 (no "Unblock" needed) and ends on
   "Launch Ski Area Design Challenge" with the new version. The version before stays installed: Other versions
   (or holding Shift as the game starts) lists it and GitHub's older releases, and plays the one you pick.
   If a new version won't start, the game offers the older one.

The update check asks GitHub only whether a newer version is out; nothing about you or your computer is sent.
Settings > Data > Check for updates turns it off. Without an internet connection the game starts as usual.

To uninstall: Start menu > Ski Area Design Challenge > Uninstall Ski Area Design Challenge.

The game opens on the title over Jackson Hole, which is built in and works offline. New Area downloads another
mountain, which needs an internet connection; downloads go to %LOCALAPPDATA%\SkiAreaDesignChallenge.

Your downloaded areas, saves, settings and error logs stay in %LOCALAPPDATA%\SkiAreaDesignChallenge through every
update and uninstall, until you delete that folder too.


BUILDING AND SAVING

Press T for the Toolbox: draw a lift by clicking its bottom, then its top; paint trails down the slope; reshape the
ground with the terrain brushes. Ctrl+Z undoes and Ctrl+Y redoes.

Ctrl+S saves under a name; the game also autosaves every 10 minutes (Settings > Gameplay changes it). Load Area on
the title opens an area and its saves again. Saves are kept in %LOCALAPPDATA%\SkiAreaDesignChallenge\Saves.


WHAT YOU NEED

- Windows 10 or 11, 64-bit, with a DirectX 11 or 12 graphics card
- About {SIZE} of disk space for the game, plus about 200 MB for each area you download
- Tested on Windows 11 with a GeForce RTX 3060 Ti. The minimum hardware hasn't been measured yet; if the game runs
  slowly, lower Settings > Graphics > Quality preset.


REPORTING A PROBLEM OR AN IDEA

In the game, use Send feedback: in the menu (top right), on the "A log was saved" notice (Send logs), or in
Settings > Data > Logs > Send logs. Pick Problem or Suggestion, say what happened, and choose what goes in:
a screenshot, the error logs, the game log, your save, and your computer's details (Windows, processor, graphics
card, screen and settings). If the game closed by itself, the next start offers a report once.

Save and open GitHub saves one zip file, shows it in a folder window and opens a GitHub issue with the title and
version filled in: drag the zip into the issue and submit it. GitHub issues are public. Without a GitHub account,
send the zip to the developer any way you like. The game never sends anything by itself, and your Windows user
name is taken out of the logs. Reports are kept in:

    %LOCALAPPDATA%\SkiAreaDesignChallenge\Reports

If the game won't start at all, send these files instead:
    %LOCALAPPDATA%\SkiAreaDesignChallenge\Logs (the newest errors-....log)
    %USERPROFILE%\AppData\LocalLow\Alpine Labs\Ski Area Design Challenge\Player.log
    %USERPROFILE%\AppData\LocalLow\Alpine Labs\Ski Area Design Challenge\Player-prev.log

Please include the version: {VERSION}. It is also shown at the bottom left of the title and in Credits.

NOTICES.txt lists the fonts, data and software the game uses, and their licences.
