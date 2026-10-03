namespace MountainPlanner.App.Flow
{
    /// <summary>The full screens of the flow (0.4 §3). The download card and its pill sit on top of any of them.</summary>
    public enum FlowScreen { Title, Library, Picker, Quality, Game }

    /// <summary>What the controller asks of the app: show a screen, open a mountain, go back to the title, quit.</summary>
    public interface IFlowHost
    {
        void ShowScreen(FlowScreen screen);
        /// <summary>Open = the S4 card; closed = the pill while a download runs, else nothing.</summary>
        void SetDownloadCardOpen(bool open);
        /// <summary>Leaves the title (or the current mountain) and opens this package from disk, with no network.</summary>
        void OpenMountain(string packageFolder);
        /// <summary>From a mountain back to the title scene, showing <paramref name="then"/> once it's up.</summary>
        void ReturnToTitle(FlowScreen then);
        void Quit();
    }

    /// <summary>
    /// The screen flow (task 14; 0.4 §3) as a small state machine with no Unity in it, so EditMode tests
    /// can drive every transition. AppFlow feeds it button presses and download events and carries out
    /// what it asks through <see cref="IFlowHost"/>.
    /// </summary>
    public sealed class FlowController
    {
        readonly IFlowHost _host;
        FlowScreen _pickerReturn = FlowScreen.Title;

        public FlowScreen Screen { get; private set; } = FlowScreen.Title;
        public bool InGame { get; private set; }
        public bool DownloadActive { get; private set; }
        public bool DownloadCardOpen { get; private set; }
        /// <summary>The package a finished download produced, shown on the quality card.</summary>
        public string FinishedPackage { get; private set; }

        public FlowController(IFlowHost host) => _host = host;

        /// <summary>The scene came up: in title mode (the signpost) or with a mountain open (the game).</summary>
        public void SceneReady(bool inGame, FlowScreen titleScreen = FlowScreen.Title)
        {
            InGame = inGame;
            Show(inGame ? FlowScreen.Game : titleScreen);
            // A download keeps going across the reload; it comes back as the pill unless its card was open.
            _host.SetDownloadCardOpen(DownloadActive && DownloadCardOpen);
        }

        public void Continue(string packageFolder)
        {
            if (string.IsNullOrEmpty(packageFolder)) return;
            Open(packageFolder);
        }

        public void NewResort()
        {
            if (Screen == FlowScreen.Picker) return;
            _pickerReturn = Screen == FlowScreen.Library ? FlowScreen.Library : FlowScreen.Title;
            Show(FlowScreen.Picker);
        }

        public void MyResorts() => Show(FlowScreen.Library);

        public void PickerCancelled() => Show(_pickerReturn);

        /// <summary>The picker chose a site and its download has started.</summary>
        public void DownloadStarted()
        {
            DownloadActive = true;
            FinishedPackage = null;
            if (Screen == FlowScreen.Picker) Show(_pickerReturn);
            SetCard(true);
        }

        public void MinimiseDownload() => SetCard(false);

        public void RestoreDownload()
        {
            if (DownloadActive) SetCard(true);
        }

        /// <summary>Cancelled (kept for resuming or discarded) or closed after a failure: the card and pill go away.</summary>
        public void DownloadStopped()
        {
            DownloadActive = false;
            SetCard(false);
        }

        /// <summary>The package is in the library: the quality card shows wherever the player is.</summary>
        public void DownloadFinished(string packageFolder)
        {
            DownloadActive = false;
            SetCard(false);
            FinishedPackage = packageFolder;
            Show(FlowScreen.Quality);
        }

        public void QualityOpen()
        {
            string folder = FinishedPackage;
            FinishedPackage = null;
            if (!string.IsNullOrEmpty(folder)) Open(folder);
        }

        public void QualityBackToLibrary()
        {
            FinishedPackage = null;
            if (InGame) _host.ReturnToTitle(FlowScreen.Library);
            else Show(FlowScreen.Library);
        }

        /// <summary>Open from the library (Enter or Open).</summary>
        public void Open(string packageFolder)
        {
            if (string.IsNullOrEmpty(packageFolder)) return;
            _host.OpenMountain(packageFolder);
        }

        /// <summary>
        /// The in-game menu's Exit to title. Iteration 1 has nothing to save (the area is read-only and a
        /// running download carries on across the reload), so it goes straight back with no confirmation.
        /// </summary>
        public void ExitToTitle()
        {
            if (InGame) _host.ReturnToTitle(FlowScreen.Title);
        }

        public void Quit() => _host.Quit();

        /// <summary>
        /// Esc backs out one step (controls-key-map.md): quality card → library, picker → where it came from,
        /// an open download card → minimised, library → title. Returns false when there is nothing to back out
        /// of here (on the title, or in the game, where the viewer's own Esc menu takes it).
        /// </summary>
        public bool Escape()
        {
            switch (Screen)
            {
                case FlowScreen.Quality: QualityBackToLibrary(); return true;
                case FlowScreen.Picker: PickerCancelled(); return true;
            }
            if (DownloadActive && DownloadCardOpen) { SetCard(false); return true; }
            if (Screen == FlowScreen.Library) { Show(FlowScreen.Title); return true; }
            return false;
        }

        void Show(FlowScreen screen)
        {
            if (InGame && (screen == FlowScreen.Title || screen == FlowScreen.Library)) screen = FlowScreen.Game;
            Screen = screen;
            _host.ShowScreen(screen);
        }

        void SetCard(bool open)
        {
            DownloadCardOpen = open && DownloadActive;
            _host.SetDownloadCardOpen(DownloadCardOpen);
        }
    }
}
