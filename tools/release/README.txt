Ski Area Design Challenge {VERSION}
by Alpine Labs

A ski resort designer on real mountains, built from U.S. Geological Survey lidar.


NEW IN 0.3.4

- Weather you can see: clouds and a cloud deck, valley fog, sun rays, falling snow and rain, travelling storm
  fronts, cloud shadows, trees that lean in the wind. Settings > Weather turns the picture, or parts of it, off.
- Live snow: every 8 m of the mountain gets its own snow from the weather. It falls, settles, drifts and melts
  faster on sunny faces. Shift+9 shows snow depth and Shift+0 the snow conditions, with a legend.
- Trails open by their snow: each hour a trail is rated Covered, Marginal or Bare, and closes without a skiable
  line. Skiers wear the snow; moguls and ice build where runs aren't groomed.
- The trail card shows Conditions, Cover, Snow rating and the surface, with an Auto / Open / Closed switch.
  Analysis > Trails shows Conditions, and Status says why a trail is closed.
- Lakes freeze and thaw, each by its depth, and carry snow on their ice.
- Ponds and dams (Toolbox > Snowmaking): build them with a live preview at full pool. Lakes, streams, ponds and
  dams have cards and names, and ponds fill through the season.
- Snowmaking network: pump houses, water lines, boosters, hydrants and guns, with Check network and the pipe
  pressure layer. The guns don't make snow yet.
- Gondola: the Sessellift G8 eight-seat cabin. Guests ride gondolas up and down.
- Grooming (Analysis > Grooming): buy groomers under Equipment, choose how each trail is groomed, and see tonight's
  plan, hours and cost. A new resort starts with no groomers; saves from 0.3.3 are given a fleet.
- Opening your mountain: the season still starts from the developer panel. Build a lift and a trail down from it,
  press F1 and click Start the season.
- Fixed: a save with lifts or trails keeps its clock when opened with Continue or Load, and a save with a dam opens.
- Saves from 0.3.3 open. Saves made in 0.3.4 don't open in 0.3.3.


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
