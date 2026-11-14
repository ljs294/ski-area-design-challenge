using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MountainPlanner.Acquisition.Updates;

namespace MountainPlanner.Launcher
{
    /// <summary>
    /// The launcher's one window (task 4.23). It stays out of sight unless there is something to ask (R2):
    /// - started by the game exe (--game, R1): the quick check, then that exe again, or "0.3.3 is out";
    /// - from the Start menu: the same check, then the newest installed version, or the offer;
    /// - the first run (Setup): Install; --update (the game's Update button): installs the newest at once;
    /// - --choose, or Shift held as it starts: the version list.
    /// Every install ends on the Launch screen (R3): "Launch Ski Area Design Challenge v0.3.3", with the version list and
    /// Check for updates (R4). A game it starts that closes with an error in its first minute offers the version before.
    /// Nothing is remembered: a version picked from the list plays that once, and the newest stays the default.
    /// </summary>
    sealed class LauncherForm : Form
    {
        const string Product = "Ski Area Design Challenge";
        static readonly TimeSpan EarlyExit = TimeSpan.FromSeconds(60);

        readonly Options _o;
        readonly VersionStore _store;
        readonly IReleaseFeed _feed;
        readonly Label _title = new Label(), _body = new Label(), _versionLabel = new Label(), _status = new Label();
        readonly ProgressBar _bar = new ProgressBar();
        readonly CheckBox _desktop = new CheckBox();
        readonly ComboBox _versions = new ComboBox();
        readonly Button _primary = new Button(), _secondary = new Button();
        readonly LinkLabel _link = new LinkLabel(), _other = new LinkLabel(), _check = new LinkLabel();
        Action? _onPrimary, _onSecondary, _onLink, _onOther;
        CancellationTokenSource? _install;
        /// <summary>GitHub's releases, once asked (the version list); null until then or when it couldn't be reached.</summary>
        List<ReleaseInfo>? _releases;
        bool _listing, _chooser;

        public int ExitCode { get; private set; }

        public LauncherForm(Options o)
        {
            _o = o;
            _store = new VersionStore(o.Root);
            _feed = o.Feed != null ? new FolderFeed(o.Feed) : (IReleaseFeed)new GitHubFeed();
            Build();
        }

        void Build()
        {
            Text = Product;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 270);
            BackColor = Color.FromArgb(0xfb, 0xf8, 0xf2);
            try { Icon = Icon.ExtractAssociatedIcon(Program.ExePath); } catch (Exception) { }

            _title.Font = new Font("Segoe UI Semibold", 15f);
            _title.ForeColor = Color.FromArgb(0x1d, 0x23, 0x2b);
            _title.SetBounds(24, 18, 512, 34);
            _body.ForeColor = Color.FromArgb(0x33, 0x3a, 0x44);
            _bar.SetBounds(24, 188, 512, 14);
            _bar.Maximum = 1000;
            _desktop.Text = "Put a shortcut on the desktop";
            _desktop.Checked = true;
            _desktop.SetBounds(24, 186, 400, 24);
            _link.LinkClicked += (s, e) => _onLink?.Invoke();
            _other.Text = "Other versions";
            _other.SetBounds(24, 188, 300, 22);
            _other.LinkClicked += (s, e) => _onOther?.Invoke();

            _versionLabel.Text = "Version";
            _versionLabel.SetBounds(24, 117, 64, 24);
            _versions.DropDownStyle = ComboBoxStyle.DropDownList;
            _versions.SetBounds(92, 113, 300, 28);
            _versions.SelectedIndexChanged += (s, e) => VersionPicked();
            _check.Text = "Check for updates";
            _check.SetBounds(408, 117, 136, 22);
            _check.LinkClicked += (s, e) => CheckFromList();
            _status.SetBounds(92, 148, 444, 44);
            _status.ForeColor = Color.FromArgb(0x5b, 0x63, 0x6e);

            _primary.Click += (s, e) => _onPrimary?.Invoke();
            _secondary.Click += (s, e) => _onSecondary?.Invoke();
            foreach (var b in new[] { _primary, _secondary }) { b.FlatStyle = FlatStyle.System; b.UseVisualStyleBackColor = true; }
            Controls.AddRange(new Control[] { _title, _body, _bar, _desktop, _link, _other, _versionLabel, _versions, _check, _status, _primary, _secondary });
            AcceptButton = _primary;
        }

        /// <summary>One screen of text and buttons (everything but the version list), shown if the window was hidden.</summary>
        void Screen(string title, string body, string? primary = null, Action? onPrimary = null, string? secondary = null, Action? onSecondary = null,
                    bool bar = false, bool desktop = false, string? link = null, Action? onLink = null, string? other = null, Action? onOther = null)
        {
            _chooser = false;
            foreach (var c in new Control[] { _versionLabel, _versions, _check, _status }) c.Visible = false;
            _body.SetBounds(24, 58, 512, 124);
            _primary.SetBounds(404, 222, 132, 34);
            _secondary.SetBounds(262, 222, 132, 34);
            _link.SetBounds(24, 228, 200, 24);
            _title.Text = title;
            _body.Text = body;
            _bar.Visible = bar;
            _desktop.Visible = desktop;
            Set(_primary, primary, onPrimary, a => _onPrimary = a);
            Set(_secondary, secondary, onSecondary, a => _onSecondary = a);
            _link.Visible = link != null;
            _link.Text = link ?? "";
            _onLink = onLink;
            _other.Visible = other != null;
            _other.Text = other ?? "";
            _onOther = onOther;
            Reveal();
        }

        static void Set(Button b, string? text, Action? action, Action<Action?> store)
        {
            b.Visible = text != null;
            b.Text = text ?? "";
            store(action);
        }

        /// <summary>Shows the window (it starts hidden) and brings it to the front.</summary>
        void Reveal()
        {
            if (!Visible) base.Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            // The game that started this has just quit; Windows may not give a new window the front without this.
            TopMost = true;
            Activate();
            TopMost = false;
        }

        /// <summary>Program.Main calls this once the message loop runs; the window shows only when a screen needs it.</summary>
        public async void Begin()
        {
            try { await Start(); }
            catch (Exception ex)
            {
                Log.Write($"Unexpected: {ex}");
                if (_o.Game != null && !Visible) { StartGame(); return; }
                Screen("Something went wrong", ex.Message + LogLine(), "Close", Close);
            }
        }

        async Task Start()
        {
            TryDelete(Path.Combine(_store.Root, "Launcher.old.exe"));
            if (_o.Uninstall) { AskUninstall(); return; }

            var current = _store.Current;
            bool here = SamePath(Program.ExePath, _store.Launcher);
            // A Setup.exe downloaded again, or a launcher run from elsewhere: hand over to the installed one. Not for
            // --game (a copy unzipped by hand checks with its own launcher) or --version.
            if (!here && current != null && File.Exists(_store.Launcher) && _o.Version == null && _o.Game == null)
            {
                InstallLauncher();
                Log.Write($"Handing over to {_store.Launcher}");
                Process.Start(new ProcessStartInfo(_store.Launcher, Program.Quote(_o.Raw)) { UseShellExecute = false, WorkingDirectory = _store.Root });
                Close();
                return;
            }

            if (_o.Version != null)
            {
                var wanted = GameVersion.Parse(_o.Version);
                if (_store.Installed().Contains(wanted)) { ShowChooser(wanted, $"{wanted} is installed", "Launch it, or pick another version."); return; }
                await Offer(() => _feed.TaggedAsync(wanted, CancellationToken.None), firstInstall: current == null);
                return;
            }
            if (_o.Game != null) { await QuickCheck(current); return; }
            if (current == null) { await Offer(() => _feed.LatestAsync(CancellationToken.None), firstInstall: true); return; }
            if (_o.Choose) { ShowChooser(current, Product, "Pick a version to play. The newest installed one plays when you start the game as usual."); return; }

            // From the Start menu, or the game's Update button (--update).
            bool check = _o.Update || (UpdatePrefs.CheckEnabled() && !UpdatePrefs.Offline());
            if (!check)
            {
                Log.Write(UpdatePrefs.Offline() ? "Offline mode is on (Settings › Data): no update check." : "Update check is off (Settings › Data).");
                Play(current.Value);
                return;
            }
            if (_o.Update) Screen(Product, "Checking for updates…");
            var result = await UpdateCheck.CheckAsync(_feed, current.Value.ToString(), CancellationToken.None);
            Log.Write(result.Message);
            if (result.State == UpdateState.Available && result.Latest != null)
            {
                if (_o.Update) await Install(result.Latest, desktop: false);
                else ShowOffer(result.Latest, current.Value.ToString());
                return;
            }
            if (!_o.Update) { Play(current.Value); return; }
            if (result.State == UpdateState.Unreachable)
                ShowChooser(current, "Couldn't reach GitHub", $"So this is still {current}. Check your connection, then Check for updates.");
            else
                ShowChooser(current, "You have the latest version", $"{current} is the newest release.");
        }

        /// <summary>
        /// R1/R2: the game exe handed this start over. No update (or GitHub unreachable, or the check off): start that
        /// exe again at once, with no window. An update: ask.
        /// </summary>
        async Task QuickCheck(GameVersion? current)
        {
            string have = _o.GameVersionText ?? "";
            if (_o.Choose && current != null) { ShowChooser(current, Product, "Pick a version to play."); return; }
            if (!UpdatePrefs.CheckEnabled() || UpdatePrefs.Offline())
            {
                Log.Write("Update check is off, or offline mode is on: starting the game.");
                StartGame();
                return;
            }
            var result = await UpdateCheck.CheckAsync(_feed, have, CancellationToken.None);
            Log.Write(result.Message);
            if (result.State != UpdateState.Available || result.Latest == null) { StartGame(); return; }
            var latest = result.Latest;
            if (_store.Installed().Contains(latest.Version))
            {
                ShowChooser(latest.Version, $"{latest.Version} is installed",
                            $"This copy is {have}; {latest.Version} is installed in {Friendly(_store.Root)}. Your saves and settings are shared.");
                return;
            }
            ShowOffer(latest, have);
        }

        /// <summary>Looks up a release, then asks to install it (first install) or installs it (a chosen version).</summary>
        async Task Offer(Func<Task<ReleaseInfo>> lookup, bool firstInstall)
        {
            Screen(Product, "Looking up the release on GitHub…");
            ReleaseInfo info;
            try { info = await lookup(); }
            catch (Exception ex)
            {
                Log.Write($"Lookup failed: {ex.Message}");
                Screen("Couldn't reach GitHub", $"The game downloads from GitHub, which didn't answer: {ex.Message}\n\nCheck your connection and try again.",
                       "Try again", async () => await Start(), "Close", Close);
                return;
            }
            if (!firstInstall) { await Install(info, desktop: false); return; }
            if (_o.Yes) { await Install(info, desktop: false); return; }
            Screen($"Install {Product}",
                   $"Version {info.Version}{Megabytes(info, " ({0} MB)")} goes into {Friendly(_store.Root)}.\n\nYour saves and downloaded areas live in %LOCALAPPDATA%\\SkiAreaDesignChallenge and stay there.",
                   "Install", async () => await Install(info, Desktop()), "Cancel", Close, desktop: !_o.NoShortcuts,
                   link: "What's new", onLink: () => Open(info.PageUrl));
        }

        /// <summary>"0.3.3 is out": Update, Play the version this is, What's new (and Other versions once installed).</summary>
        async void ShowOffer(ReleaseInfo latest, string have)
        {
            if (_o.Yes) { await Install(latest, desktop: false); return; }
            bool fresh = _store.Current == null;   // nothing installed here yet: a copy unzipped by hand
            string body = fresh
                ? $"You have {have}.{Megabytes(latest, " It's a {0} MB download.")} Update installs {latest.Version} into {Friendly(_store.Root)}, with a Start-menu entry; this copy stays as it is. Your saves and settings carry over."
                : $"You have {have}.{Megabytes(latest, " It's a {0} MB download.")} Your saves and settings carry over, and {have} stays installed in case you want to go back.";
            Action play = _o.Game != null ? (Action)StartGame : () => Play(GameVersion.Parse(have));
            Screen($"{latest.Version} is out", body, "Update", async () => await Install(latest, Desktop()), $"Play {have}", play,
                   desktop: fresh && !_o.NoShortcuts, link: "What's new", onLink: () => Open(latest.PageUrl),
                   other: fresh ? null : "Other versions", onOther: () => ShowChooser(_store.Current, Product, "Pick a version to play."));
        }

        bool Desktop() => _desktop.Visible && _desktop.Checked;

        static string Megabytes(ReleaseInfo info, string format) =>
            info.Zip.Size > 0 ? string.Format(format, Math.Ceiling(info.Zip.Size / 1048576.0)) : "";

        async Task Install(ReleaseInfo info, bool desktop)
        {
            PrepareRoot(desktop);
            _install = new CancellationTokenSource();
            var token = _install.Token;
            Screen($"Installing {info.Version}", "Starting the download…", null, null, "Cancel", () => _install?.Cancel(), bar: true);
            _bar.Value = 0;
            var progress = new Progress<InstallProgress>(p =>
            {
                _bar.Value = (int)(p.Fraction * 1000);
                _body.Text = p.Step == "Downloading" && p.Total > 0
                    ? $"Downloading {p.Done / 1048576} of {p.Total / 1048576} MB from GitHub…"
                    : p.Step == "Unzipping" ? $"Unzipping… {p.Fraction:P0}" : "Checking the download…";
            });
            Log.Write($"Installing {info.Version} from {_feed.Name}");
            var previous = _store.Current;
            try
            {
                var v = await Task.Run(() => ReleaseInstaller.InstallAsync(_feed, info, _store, progress, token), token);
                Log.Write($"Installed {v} in {_store.FolderOf(v)}; installed now: {string.Join(", ", _store.Installed())}");
                SelfUpdate(v);
                if (_o.NoStart) { ExitCode = 0; Close(); return; }
                var other = _store.Installed().Where(x => !x.Equals(v)).Select(x => (GameVersion?)x).FirstOrDefault();
                ShowChooser(v, $"{v} is installed", other != null
                    ? $"Your saves and settings carry over. {other} stays installed too: pick it below to go back."
                    : "Your saves and settings live in %LOCALAPPDATA%\\SkiAreaDesignChallenge.");
            }
            catch (OperationCanceledException)
            {
                Log.Write("Cancelled");
                Failed(info, "Cancelled. What was downloaded is kept, so the next try carries on from there.", previous, desktop);
            }
            catch (Exception ex)
            {
                Log.Write($"Install failed: {ex}");
                Failed(info, (ex is UpdateException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}") + LogLine(), previous, desktop);
            }
        }

        void Failed(ReleaseInfo info, string message, GameVersion? previous, bool desktop)
        {
            ExitCode = 1;
            string title = $"{info.Version} wasn't installed";
            Action retry = async () => await Install(info, desktop);
            if (previous != null)
                Screen(title, message + $"\n\n{previous} is still installed and plays as before.", $"Play {previous}", () => Play(previous.Value), "Try again", retry,
                       other: "Other versions", onOther: () => ShowChooser(previous, Product, "Pick a version to play."));
            else if (_o.Game != null)
                Screen(title, message, $"Play {_o.GameVersionText}", StartGame, "Try again", retry);
            else
                Screen(title, message, "Try again", retry, "Close", Close);
        }

        // ---- The Launch screen and its version list (R3, R4) ----

        /// <summary>One line of the version list.</summary>
        sealed class VersionItem
        {
            public readonly GameVersion V;
            public readonly ReleaseInfo? Info;
            public readonly bool Installed;
            readonly string _text;

            public VersionItem(GameVersion v, ReleaseInfo? info, bool installed, bool newer)
            {
                V = v;
                Info = info;
                Installed = installed;
                string size = info != null ? Megabytes(info, "{0} MB download") : "download";
                _text = installed ? $"{v}   (installed)" : newer ? $"{v}   (new: {size})" : $"{v}   ({size})";
            }

            public override string ToString() => _text;
        }

        /// <summary>
        /// The Launch screen: "Launch Ski Area Design Challenge v0.3.3" for the version picked, the list of installed
        /// versions and GitHub's other releases (asked once, unless offline mode is on), and Check for updates.
        /// </summary>
        void ShowChooser(GameVersion? select, string title, string body)
        {
            Screen(title, body, "", null, "Close", Close);
            _chooser = true;
            _body.SetBounds(24, 58, 512, 48);
            _primary.SetBounds(226, 222, 310, 34);
            _secondary.SetBounds(124, 222, 92, 34);
            _link.SetBounds(24, 230, 96, 24);
            foreach (var c in new Control[] { _versionLabel, _versions, _check, _status }) c.Visible = true;
            FillVersions(select);
            _primary.Focus();
            if (_releases == null && !_listing && !UpdatePrefs.Offline()) _ = LoadReleases();
        }

        VersionItem? Picked => _versions.SelectedItem as VersionItem;

        void FillVersions(GameVersion? select)
        {
            var installed = _store.Installed();
            var newest = installed.Count > 0 ? installed[0] : (GameVersion?)null;
            var items = installed.Select(v => new VersionItem(v, _releases?.FirstOrDefault(r => r.Version.Equals(v)), true, false)).ToList();
            if (_releases != null)
                foreach (var r in _releases)
                    if (!installed.Contains(r.Version)) items.Add(new VersionItem(r.Version, r, false, newest == null || r.Version > newest.Value));
            items.Sort((a, b) => b.V.CompareTo(a.V));
            _versions.BeginUpdate();
            _versions.Items.Clear();
            foreach (var item in items) _versions.Items.Add(item);
            _versions.EndUpdate();
            int index = select is GameVersion s ? items.FindIndex(i => i.V.Equals(s)) : -1;
            if (index < 0) index = items.FindIndex(i => i.Installed);
            _versions.SelectedIndex = index >= 0 ? index : items.Count > 0 ? 0 : -1;
            VersionPicked();
        }

        void VersionPicked()
        {
            if (!_chooser) return;
            var item = Picked;
            _primary.Visible = item != null;
            _link.Visible = item != null;
            if (item == null) return;
            _link.Text = "What's new";
            _onLink = () => Open(item.Info?.PageUrl ?? $"https://github.com/{GitHubFeed.Repository}/releases/tag/{item.V.Tag}");
            if (item.Installed)
            {
                _primary.Text = $"Launch {Product} v{item.V}";
                _onPrimary = () => Play(item.V);
            }
            else
            {
                _primary.Text = $"Install v{item.V}…";
                _onPrimary = () => AskInstall(item);
            }
        }

        /// <summary>A version not installed yet: says what it downloads and which version makes room, then installs it.</summary>
        async void AskInstall(VersionItem item)
        {
            if (item.Info == null) return;
            var installed = _store.Installed();
            // What VersionStore.Prune(picked) keeps: the newest, and the one just picked (or the one before the newest).
            var after = installed.Concat(new[] { item.V }).OrderByDescending(v => v).ToList();
            var keep = new List<GameVersion> { after[0], after[0].Equals(item.V) ? (after.Count > 1 ? after[1] : item.V) : item.V };
            var removed = installed.Where(v => !keep.Contains(v)).ToList();
            string room = removed.Count > 0
                ? $"\n\n{string.Join(" and ", removed)} is removed to make room: two versions stay installed. Your saves aren't touched."
                : "";
            var answer = MessageBox.Show(this, $"Download {item.V}{Megabytes(item.Info, " ({0} MB)")} from GitHub and install it?{room}", Product,
                                         MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            await Install(item.Info, desktop: false);
        }

        /// <summary>Check for updates: asks GitHub for its releases again and picks a newer one in the list.</summary>
        async void CheckFromList()
        {
            if (_listing) return;
            Log.Write("Check for updates");
            await LoadReleases();
            if (_releases == null) return;
            var current = _store.Current;
            var newest = _releases.FirstOrDefault();
            if (newest != null && (current == null || newest.Version > current.Value))
            {
                FillVersions(newest.Version);
                _status.Text = $"Checked GitHub just now: {newest.Version} is out. Install it with the button below.";
            }
            else _status.Text = current != null ? $"Checked GitHub just now: you have the newest version, {current}." : "";
        }

        async Task LoadReleases()
        {
            _listing = true;
            _status.Text = "Asking GitHub for its releases…";
            try
            {
                _releases = await Task.Run(() => _feed.AllAsync(CancellationToken.None));
                Log.Write($"Releases on {_feed.Name}: {string.Join(", ", _releases.Select(r => r.Version))}");
                var current = _store.Current;
                _status.Text = _releases.Count == 0 ? ""
                    : current == null || _releases[0].Version > current.Value ? $"{_releases[0].Version} is out: pick it in the list to install it."
                    : $"{current} is the newest release.";
            }
            catch (Exception ex)
            {
                Log.Write($"Releases list failed: {ex.Message}");
                _status.Text = "Couldn't reach GitHub, so only the installed versions are listed.";
            }
            finally { _listing = false; }
            if (_chooser) FillVersions(Picked?.V);
        }

        // ---- Starting the game ----

        /// <summary>--game: starts the exe that handed over, with -updatechecked, and closes.</summary>
        void StartGame()
        {
            if (Program.StartGame(_o))
            {
                ExitCode = 0;
                Close();
                return;
            }
            ExitCode = 1;
            Screen($"{Product} didn't start", $"Windows couldn't start {_o.Game}.{LogLine()}", "Close", Close);
        }

        /// <summary>Starts an installed version and waits for it out of sight; an early error exit offers the version before.</summary>
        async void Play(GameVersion v)
        {
            string exe = _store.ExeOf(v);
            var args = new[] { "-launcher", _store.Launcher, "-updatechecked" }.Concat(_o.GameArgs).ToArray();
            Log.Write($"Starting {exe} {Program.Quote(args)}");
            Process game;
            try
            {
                game = Process.Start(new ProcessStartInfo(exe, Program.Quote(args)) { UseShellExecute = false, WorkingDirectory = _store.FolderOf(v) })
                       ?? throw new InvalidOperationException("Windows didn't start it.");
            }
            catch (Exception ex)
            {
                Log.Write($"Start failed: {ex.Message}");
                ExitCode = 1;
                var other = _store.Installed().Where(x => !x.Equals(v)).Select(x => (GameVersion?)x).FirstOrDefault();
                Screen($"{v} didn't start", ex.Message + LogLine(), other != null ? $"Play {other}" : "Close",
                       other != null ? (Action)(() => Play(other.Value)) : Close, "Close", Close);
                return;
            }
            ExitCode = 0;
            Hide();
            var started = DateTime.UtcNow;
            await Task.Run(() => game.WaitForExit());
            int code = game.ExitCode;
            var ran = DateTime.UtcNow - started;
            Log.Write($"{v} closed with {code} after {ran.TotalSeconds:0} s");
            GameVersion? older = _store.Installed().Where(x => x < v).Select(x => (GameVersion?)x).FirstOrDefault();
            if (code != 0 && ran < EarlyExit && older != null)
            {
                Screen($"{v} closed unexpectedly", $"It closed after {ran.TotalSeconds:0} seconds (code {code}). The error log is in %LOCALAPPDATA%\\SkiAreaDesignChallenge\\Logs.",
                       $"Play {older} instead", () => Play(older.Value), $"Try {v} again", () => Play(v));
                return;
            }
            Close();
        }

        // ---- The install folder ----

        /// <summary>Before the first install into the root: the folder, this launcher in it, the shortcuts.</summary>
        void PrepareRoot(bool desktop)
        {
            bool fresh = !File.Exists(_store.Launcher);
            Directory.CreateDirectory(_store.Root);
            Log.MoveTo(_store.Root);
            InstallLauncher();
            if (!fresh || _o.NoShortcuts) return;
            try { Shortcuts.Create(_store.Launcher, desktop); }
            catch (Exception ex) { Log.Write($"Shortcuts: {ex.Message}"); }
        }

        /// <summary>Copies this exe to the install root, unless the one there is as new.</summary>
        void InstallLauncher()
        {
            if (SamePath(Program.ExePath, _store.Launcher)) return;
            try
            {
                if (File.Exists(_store.Launcher) && !(Program.OwnVersion > Program.VersionOf(_store.Launcher))) return;
                Directory.CreateDirectory(_store.Root);
                File.Copy(Program.ExePath, _store.Launcher, true);
                Log.Write($"Launcher {Program.OwnVersion} copied to {_store.Launcher}");
            }
            catch (Exception ex) { Log.Write($"Couldn't copy the launcher: {ex.Message}"); }
        }

        /// <summary>UP8: a release ships its own Launcher.exe; a newer one replaces the installed launcher.</summary>
        void SelfUpdate(GameVersion v)
        {
            string fresh = Path.Combine(_store.FolderOf(v), VersionStore.LauncherExe);
            if (!File.Exists(fresh)) return;
            var freshVersion = Program.VersionOf(fresh);
            var installed = File.Exists(_store.Launcher) ? Program.VersionOf(_store.Launcher) : new GameVersion(0, 0, 0);
            if (!(freshVersion > installed)) return;
            try
            {
                string old = Path.Combine(_store.Root, "Launcher.old.exe");
                TryDelete(old);
                // A running exe can be renamed, not overwritten: move this one aside, copy the new one in.
                if (File.Exists(_store.Launcher)) File.Move(_store.Launcher, old);
                File.Copy(fresh, _store.Launcher);
                Log.Write($"Launcher updated {installed} → {freshVersion}");
            }
            catch (Exception ex) { Log.Write($"Launcher update failed: {ex.Message}"); }
        }

        void AskUninstall()
        {
            Screen($"Remove {Product}?",
                   $"This removes the game from {Friendly(_store.Root)} and its shortcuts.\n\nYour saves, downloaded areas and settings stay in %LOCALAPPDATA%\\SkiAreaDesignChallenge; delete that folder too to remove them.",
                   "Remove", Uninstall, "Cancel", Close);
        }

        void Uninstall()
        {
            if (!Directory.Exists(_store.Versions) || !File.Exists(_store.Launcher) || Path.GetPathRoot(_store.Root) == _store.Root)
            {
                Screen("Nothing to remove", $"{_store.Root} isn't an install of {Product}.", "Close", Close);
                return;
            }
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(VersionStore.GameExe)).Any(p => InRoot(p)))
            {
                Screen($"Close {Product} first", "The game is still running. Close it, then choose Remove again.", "Remove", Uninstall, "Cancel", Close);
                return;
            }
            Shortcuts.Remove();
            Log.Write($"Uninstalling {_store.Root}");
            // This exe lives in the folder, so a short-lived cmd removes the folder once the launcher has closed.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{_store.Root}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
            Close();
        }

        bool InRoot(Process p)
        {
            try { return VersionStore.IsInside(_store.Root, p.MainModule?.FileName ?? ""); }
            catch (Exception) { return false; }
        }

        static void Open(string url)
        {
            if (url.StartsWith("https://github.com/", StringComparison.Ordinal)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>A folder as a player reads it: %LOCALAPPDATA%\Programs\SkiAreaDesignChallenge, not the full path.</summary>
        static string Friendly(string folder)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
            return folder.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + folder.Substring(local.Length) : folder;
        }

        static string LogLine() => Log.Path != null ? $"\n\nDetails: {Log.Path}" : "";

        static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _install?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
