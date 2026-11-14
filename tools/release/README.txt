Ski Area Design Challenge {VERSION}
by Alpine Labs

A ski resort designer on real mountains, built from U.S. Geological Survey lidar.


NEW IN 0.3.3

- Installer and updates: SkiAreaDesignChallenge-Setup.exe installs the game, and every start checks for a new
  version and asks before updating (see INSTALLING THE GAME below).
- Opening your mountain: in this test build the season starts from the developer panel. Build a lift and a trail
  down from it, press F1 and click Start the season. Until then the lifts stay parked and nobody comes.
- Time passes: a ski year from October 1, with a 42-day season from December to April; a day on the hill takes
  about 12 minutes at 1x. Space pauses, and 1 to 4 pick 1x, 2x, 4x or 8x. Saves keep the day and the time.
- Guests: up to 3,000 skiers and snowboarders arrive, pick runs and lifts that suit their ability, wait in line,
  ride, ski and go home. How their day went sizes tomorrow's crowd. Click one for its card, with Follow.
- Lifts run: lines and waits, opening hours, Daily or Peak days only, and wind holds (tows and carpets never hold).
- Ride a lift: the video-camera button on a lift's card. Space pauses, H hides the bar, Esc takes you back.
- Lift plates: a 1 to 3 character plate for a lift (the "#" on its card, or Shift+F2).
- Traffic layer (Shift+5, or Layers > Traffic): runs by crowding, lifts by load, each line's length and wait.
- Weather: every area plays weather fitted to its own records (Get weather in Manage Areas for an older area). The
  line under the clock shows temperatures and the next 24 hours' snow, and notices warn of storms and high wind.
- Posted trail signs: choose a trail's sign in New trail's Posted row before Break ground; change it in Edit trail.
- Analysis lists sort and filter by column, and show Popularity and Status.
- Surface lifts and trails have a Cost row, with Grading and Clearing in every lift and trail price.
- Grading a run, removing one or adding a lift is about twice as quick.
- Fixed: the game runs on DirectX 11, which ends the quit crash some PCs still had, and it's a little faster.
- Fixed: resorts with many lifts run smoothly again; every trail on a big area shows its groomed look; a long
  Analysis list scrolls at 150% UI scale; the Guests tab in Analysis shows its own note.
- Saves from 0.3.2 open (the clock starts at October 1). Saves made in 0.3.3, and areas that get their weather,
  don't open in 0.3.2.


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
