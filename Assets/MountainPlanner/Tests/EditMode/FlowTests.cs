using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App.Flow;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 14: the download card's view model, built only from what the pipeline reports.</summary>
    public sealed class DownloadViewModelTests
    {
        bool _wasImperial;

        [SetUp]
        public void Metric()
        {
            _wasImperial = FlowUnits.Imperial;
            FlowUnits.Set(false, remember: false);   // never touches the player's saved choice
        }

        [TearDown]
        public void Restore() => FlowUnits.Set(_wasImperial, remember: false);

        static DownloadStatus At(string[] stages, int index, double overall = 0.2) => new DownloadStatus
        {
            Name = "Crystal Mountain", SizeKm = 2, Stages = stages, StageIndex = index, StageCount = stages.Length,
            Stage = stages[index - 1], Overall = overall, Bytes = 182_400_000, BytesPerSecond = 6_100_000, SecondsRemaining = 80,
            Detail = stages[index - 1] + ": downloading tile 3 of 6 · 35%",
        };

        [Test]
        public void TheStageListComesFromThePipeline()
        {
            var stages = new[] { "Terrain", "Terrain surroundings", "Forest", "A stage the UI has never heard of", "Building" };
            var vm = new DownloadViewModel();
            vm.Start("Crystal Mountain", 2);
            vm.Apply(At(stages, 3, 0.41));
            Assert.That(vm.StageNames, Is.EqualTo(stages), "every declared stage shows from the first snapshot, including new ones");
            Assert.That(vm.StageStates, Is.EqualTo(new[] { StageState.Done, StageState.Done, StageState.Current, StageState.Waiting, StageState.Waiting }));
            Assert.That(vm.Percent, Is.EqualTo("41%"));
            Assert.That(vm.TimeLeft, Is.EqualTo("about 1 min 20 s left"));
            Assert.That(vm.Detail, Is.EqualTo("Forest: downloading tile 3 of 6 · 35%"));
            Assert.That(vm.Transfer, Is.EqualTo("182.4 MB · 6.1 MB/s"));
            Assert.That(vm.Pill, Is.EqualTo("Downloading Crystal Mountain · 41%"));
            Assert.That(vm.Title, Is.EqualTo("Downloading Crystal Mountain · 2 km"));
        }

        [Test]
        public void RowsRebuildOnlyWhenTheListChanges()
        {
            var stages = new[] { "Terrain", "Forest", "Building" };
            var vm = new DownloadViewModel();
            vm.Start("X", 2);
            vm.Apply(At(stages, 1));
            int rows = vm.StagesVersion, ticks = vm.StatesVersion;
            vm.Apply(At(stages, 1, 0.3));
            Assert.That(vm.StagesVersion, Is.EqualTo(rows), "a tick within a stage rebuilds nothing");
            Assert.That(vm.StatesVersion, Is.EqualTo(ticks));
            vm.Apply(At(stages, 2, 0.5));
            Assert.That(vm.StagesVersion, Is.EqualTo(rows));
            Assert.That(vm.StatesVersion, Is.GreaterThan(ticks), "the ticks move on");
            vm.Apply(At(new[] { "Terrain", "Forest", "Calibrating", "Building" }, 2, 0.5));
            Assert.That(vm.StagesVersion, Is.GreaterThan(rows), "a changed list rebuilds the rows");
        }

        [Test]
        public void WithoutAStageListItIsLearnedAsStagesArrive()
        {
            var vm = new DownloadViewModel();
            vm.Start("X", 2);
            vm.Apply(new DownloadStatus { Name = "X", Stage = "Terrain", StageIndex = 1, StageCount = 8 });
            vm.Apply(new DownloadStatus { Name = "X", Stage = "Forest", StageIndex = 3, StageCount = 8 });
            Assert.That(vm.StageNames, Is.EqualTo(new[] { "Terrain", "Forest" }));
            Assert.That(vm.StageStates, Is.EqualTo(new[] { StageState.Done, StageState.Current }));
            Assert.That(vm.Title, Does.Contain("stage 3 of 8"));
        }

        [Test]
        public void FinishedTicksEverything()
        {
            var stages = new[] { "Terrain", "Building" };
            var vm = new DownloadViewModel();
            vm.Start("X", 2);
            vm.Apply(new DownloadStatus { Name = "X", Stages = stages, StageIndex = 2, StageCount = 2, Finished = true, Stage = "Done" });
            Assert.That(vm.StageStates, Is.All.EqualTo(StageState.Done));
            Assert.That(vm.Fraction, Is.EqualTo(1));
            Assert.That(vm.Phase, Is.EqualTo(DownloadPhase.Finished));
        }

        [Test]
        public void TheRealTrackerFeedsTheCard()
        {
            // AcquisitionProgress → DownloadStatus → view model, with no stage names in between.
            var tracker = new ProgressTracker(null, new TransferMeter());
            foreach (string s in new[] { "Terrain", "Ground cover", "Building" }) tracker.DefineStage(s, 1);
            tracker.BeginStage("Ground cover");
            var vm = new DownloadViewModel();
            vm.Start("X", 2);
            vm.Apply(PipelineDownloader.ToStatus(tracker.Snapshot(), "X", 2));
            Assert.That(vm.StageNames, Is.EqualTo(new[] { "Terrain", "Ground cover", "Building" }));
            Assert.That(vm.StageStates[1], Is.EqualTo(StageState.Current));
        }

        [TestCase(null, "estimating time left")]
        [TestCase(12.0, "about 15 s left")]
        [TestCase(80.0, "about 1 min 20 s left")]
        [TestCase(600.0, "about 10 min left")]
        [TestCase(3900.0, "about 1 h 5 min left")]
        public void TimeLeftReadsNaturally(double? seconds, string text) => Assert.That(DownloadViewModel.Remaining(seconds), Is.EqualTo(text));
    }

    public sealed class QualityAndLibraryViewModelTests
    {
        bool _wasImperial;

        [SetUp]
        public void Metric()
        {
            _wasImperial = FlowUnits.Imperial;
            FlowUnits.Set(false, remember: false);   // never touches the player's saved choice
        }

        [TearDown]
        public void Restore() => FlowUnits.Set(_wasImperial, remember: false);

        [Test]
        public void SizesFollowTheUnitsSetting()
        {
            int changed = 0;
            void Count() => changed++;
            FlowUnits.Changed += Count;
            try
            {
                FlowUnits.Set(true, remember: false);
                Assert.That(FlowUnits.SiteSize(2), Is.EqualTo("1.2 mi"), "the game's shared format (UnitFormat.SiteSize)");
                Assert.That(QualityCardViewModel.From(Manifest(90, 90), true).Place, Does.EndWith("1.2 mi"));
                FlowUnits.Set(false, remember: false);
                Assert.That(FlowUnits.SiteSize(2), Is.EqualTo("2 km"));
                Assert.That(changed, Is.EqualTo(2));
            }
            finally { FlowUnits.Changed -= Count; }
        }

        static PackageManifest Manifest(int terrain, int flora) => new PackageManifest
        {
            Site = new SiteInfo { Name = "Crystal Mountain", Latitude = 46.935, Longitude = -121.474, SizeMetres = 2000 },
            Quality = new QualityInfo { Score = terrain, OneLiner = "63% USGS S1M 1 m lidar · 37% 3DEP 10 m" },
            Flora = new FloraQualityInfo { Score = flora, OneLiner = "93% of forest as its real species" },
        };

        [Test]
        public void TheCardShowsEveryScoreWithItsWord()
        {
            var vm = QualityCardViewModel.From(Manifest(71, 80), justDownloaded: true);
            Assert.That(vm.Title, Is.EqualTo("Crystal Mountain is ready"));
            Assert.That(vm.Place, Is.EqualTo("46.935 N 121.474 W · 2 km"));
            Assert.That(vm.Lines, Has.Count.EqualTo(2));
            Assert.That(vm.Lines[0].Label, Is.EqualTo("Terrain"));
            Assert.That(vm.Lines[0].Word, Is.EqualTo("Fair"));
            Assert.That(vm.Lines[0].Caveat, Is.EqualTo("Parts of this area use coarser data."), "a fallback area says so");
            Assert.That(vm.Lines[0].Detail, Does.Contain("3DEP 10 m"));
            Assert.That(vm.Lines[1].Word, Is.EqualTo("Good"));
            Assert.That(QualityCardViewModel.WithoutScore("Terrain quality 30/100: 100% 3DEP 10 m"), Is.EqualTo("100% 3DEP 10 m"), "the score isn't said twice");
            Assert.That(vm.Lines[1].Caveat, Is.Empty);
        }

        [Test]
        public void LibraryRowsSortAndPausedDownloadsComeFirst()
        {
            var entries = new List<LibraryEntry>
            {
                new LibraryEntry { PackageId = "a", Name = "Sugarloaf", TerrainScore = 99, FloraScore = 70, BytesOnDisk = 600_000_000, Measured = true, CreatedUtc = "2026-09-01T00:00:00Z" },
                new LibraryEntry { PackageId = "b", Name = "Crystal Mountain", TerrainScore = 71, FloraScore = 80, BytesOnDisk = 188_000_000, Measured = true, CreatedUtc = "2026-09-30T00:00:00Z" },
                new LibraryEntry { PackageId = "c", Name = "Jackson Hole", TerrainScore = 97, FloraScore = 83, BytesOnDisk = 612_000_000, Measured = true, CreatedUtc = "2026-09-20T00:00:00Z" },
            };
            var pending = new List<PendingDownload> { new PendingDownload { Id = "p", Name = "Stowe", SizeKm = 3, LastOverall = 0.384, LastStage = "Forest" } };
            var recent = new RecentResorts();
            recent.Opened["c"] = "2026-10-02T08:00:00Z";
            recent.Opened["a"] = "2026-09-30T08:00:00Z";
            var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            var vm = LibraryViewModel.Build(entries, pending, recent, LibrarySort.LastOpened, now);
            Assert.That(vm.Rows.ConvertAll(r => r.Name), Is.EqualTo(new[] { "Stowe", "Jackson Hole", "Sugarloaf", "Crystal Mountain" }));
            Assert.That(vm.Rows[0].IsPaused, Is.True);
            Assert.That(vm.Rows[0].PausedText, Is.EqualTo("Paused at 38% · Forest"));
            Assert.That(vm.Rows[1].Opened, Is.EqualTo("today"));
            Assert.That(vm.Rows[2].Opened, Is.EqualTo("Sep 30"));
            Assert.That(vm.Rows[3].Opened, Is.EqualTo("not opened yet"));
            Assert.That(vm.Rows[1].TerrainText, Is.EqualTo("97 Excellent"));
            Assert.That(vm.Summary, Is.EqualTo("3 areas · 1.4 GB on disk · 1 paused"));

            Assert.That(LibraryViewModel.Build(entries, pending, recent, LibrarySort.Name, now).Rows.ConvertAll(r => r.Name),
                        Is.EqualTo(new[] { "Stowe", "Crystal Mountain", "Jackson Hole", "Sugarloaf" }));
            Assert.That(LibraryViewModel.Build(entries, pending, recent, LibrarySort.Quality, now).Rows.ConvertAll(r => r.Name),
                        Is.EqualTo(new[] { "Stowe", "Sugarloaf", "Jackson Hole", "Crystal Mountain" }));
        }

        [Test]
        public void SizesShowAsMeasuringUntilTheyArriveThenFreeSpaceOffersTheOldCaches()
        {
            var entry = new LibraryEntry { PackageId = "c", Name = "Jackson Hole", Folder = "jh", TerrainScore = 97 };
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var vm = LibraryViewModel.Build(new List<LibraryEntry> { entry }, new List<PendingDownload>(), new RecentResorts(), LibrarySort.Name, now);
            Assert.That(vm.Measured, Is.False);
            Assert.That(vm.Rows[0].Disk, Is.EqualTo("…"), "never \"0 MB\" before it's measured");
            Assert.That(vm.Summary, Is.EqualTo("1 area · measuring disk use…"));
            Assert.That(vm.FreeText, Is.Empty);

            ResortLibrary.SetDisk(entry, new DiskUse { Package = 200_000_000, Cache = 400_000_000, OlderCaches = 412_000_000 });
            vm = LibraryViewModel.Build(new List<LibraryEntry> { entry }, new List<PendingDownload>(), new RecentResorts(), LibrarySort.Name, now);
            Assert.That(vm.Rows[0].Disk, Is.EqualTo("1.0 GB"));
            Assert.That(vm.Rows[0].DiskDetail, Is.EqualTo("Area 200 MB · terrain cache 400 MB · old caches 412 MB (Free space removes them)"));
            Assert.That(vm.Summary, Is.EqualTo("1 area · 1.0 GB on disk"));
            Assert.That(vm.FreeableBytes, Is.EqualTo(412_000_000));
            Assert.That(vm.FreeText, Is.EqualTo("Free 412 MB"));
            Assert.That(vm.Rows[0].CanRename, Is.True);
            entry.RenameRefusal = "newer";
            Assert.That(vm.Rows[0].CanRename, Is.False, "a newer game's view state can't be renamed");
        }

        [Test]
        public void AreasFromANewerGameAreListedLastAndCantBeOpened()
        {
            var entries = new List<LibraryEntry> { new LibraryEntry { PackageId = "c", Name = "Jackson Hole", TerrainScore = 97, BytesOnDisk = 600_000_000, Measured = true } };
            var newer = new List<LibraryEntry> { new LibraryEntry { PackageId = "f", Name = "Big Sky", Folder = "f", BytesOnDisk = 400_000_000, Measured = true, Refusal = "a newer format" } };
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

            var vm = LibraryViewModel.Build(entries, new List<PendingDownload>(), new RecentResorts(), LibrarySort.Name, now, newer);
            Assert.That(vm.Rows.ConvertAll(r => r.Name), Is.EqualTo(new[] { "Jackson Hole", "Big Sky" }), "after the areas this game can open");
            Assert.That(vm.Rows[0].CanOpen, Is.True);
            var row = vm.Rows[1];
            Assert.That(row.IsNewer, Is.True);
            Assert.That(row.CanOpen, Is.False);
            Assert.That(row.Entry, Is.SameAs(newer[0]), "Delete removes its folder");
            Assert.That(row.NewerText, Is.EqualTo("Made by a newer version of Mountain Planner. Update the game to open it."));
            Assert.That(vm.Summary, Is.EqualTo("1 area · 1.0 GB on disk · 1 needs a newer version"));
            Assert.That(vm.EmptyText, Is.EqualTo(LibraryViewModel.NoAreas));

            var refused = LibraryViewModel.Refused("This library was saved by a newer version of Mountain Planner.", LibrarySort.Name);
            Assert.That(refused.IsEmpty, Is.True);
            Assert.That(refused.EmptyText, Does.StartWith("This library was saved by a newer version"), "says why, not \"no areas yet\"");
        }

        /// <summary>Task P2-03: the demo built into the game opens, but has no Rename or Delete and isn't the player's disk.</summary>
        [Test]
        public void TheBuiltInDemoOpensButIsntRenamedDeletedOrCounted()
        {
            var demo = new LibraryEntry { PackageId = "d", Name = "Jackson Hole", OriginalName = "Jackson Hole", Folder = "game/Demo/d", TerrainScore = 100, Bundled = true, RenameRefusal = BundledAreas.Refusal };
            var own = new LibraryEntry { PackageId = "c", Name = "Crystal Mountain", Folder = "c", TerrainScore = 30 };
            ResortLibrary.SetDisk(own, new DiskUse { Package = 100_000_000, Cache = 100_000_000, OlderCaches = 50_000_000 });
            var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

            var vm = LibraryViewModel.Build(new List<LibraryEntry> { demo, own }, new List<PendingDownload>(), new RecentResorts(), LibrarySort.Name, now);
            var row = vm.Rows.Find(r => r.Entry == demo);
            Assert.That(row.IsBuiltIn, Is.True);
            Assert.That(row.CanOpen, Is.True);
            Assert.That(row.CanRename, Is.False);
            Assert.That(row.CanDelete, Is.False);
            Assert.That(row.Disk, Is.EqualTo(LibraryViewModel.BuiltIn));
            Assert.That(row.DiskDetail, Is.EqualTo(LibraryViewModel.BuiltInDetail));
            Assert.That(vm.Measured, Is.True, "the demo is never measured, so nothing waits on it");
            Assert.That(vm.Summary, Is.EqualTo("2 areas · 250 MB on disk"), "only the library's own areas count towards the disk");
            Assert.That(vm.FreeableBytes, Is.EqualTo(50_000_000));
            Assert.That(vm.Rows.Find(r => r.Entry == own).CanDelete, Is.True);

            demo.Refusal = BundledAreas.StaleCache;
            Assert.That(row.CanOpen, Is.False, "a demo whose cache doesn't match this game can't be opened");
        }
    }

    /// <summary>Task 14: the flow's run-time assets load from Resources, so a build keeps them.</summary>
    public sealed class FlowAssetTests
    {
        [Test]
        public void TheFlowAndThePickerAssetsLoad()
        {
            Assert.That(UnityEngine.Resources.Load<UnityEngine.UIElements.VisualTreeAsset>(AppFlow.ResourceFolder + "Flow"), Is.Not.Null);
            Assert.That(UnityEngine.Resources.Load<UnityEngine.UIElements.PanelSettings>(AppFlow.ResourceFolder + "FlowPanel"), Is.Not.Null);
            var assets = UnityEngine.Resources.Load<FlowAssets>(AppFlow.ResourceFolder + "FlowAssets");
            Assert.That(assets, Is.Not.Null);
            Assert.That(assets.PickerTree, Is.Not.Null, "task 13's SitePicker.uxml");
            Assert.That(assets.PickerPanel, Is.Not.Null, "task 13's PickerPanel");
        }
    }

    /// <summary>Task 14: every transition of the screen flow (0.4 §3), with no Unity in the loop.</summary>
    public sealed class FlowControllerTests
    {
        sealed class Host : IFlowHost
        {
            public readonly List<string> Log = new List<string>();
            public bool CardOpen;
            public void ShowScreen(FlowScreen screen) => Log.Add("show " + screen);
            public void SetDownloadCardOpen(bool open) => CardOpen = open;
            public void OpenMountain(string folder) => Log.Add("open " + folder);
            public void ReturnToTitle(FlowScreen then) => Log.Add("title then " + then);
            public void Quit() => Log.Add("quit");
        }

        static (FlowController, Host) Title()
        {
            var host = new Host();
            var flow = new FlowController(host);
            flow.SceneReady(inGame: false);
            return (flow, host);
        }

        [Test]
        public void NewResortDownloadQualityOpen()
        {
            var (flow, host) = Title();
            flow.NewResort();
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Picker));
            flow.DownloadStarted();
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Title), "the picker closes back to where it came from");
            Assert.That(host.CardOpen, Is.True);
            flow.MinimiseDownload();
            Assert.That(host.CardOpen, Is.False);
            Assert.That(flow.DownloadActive, Is.True, "minimised, it keeps going as the pill");
            flow.DownloadFinished("D:/lib/Resorts/abc");
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Quality));
            flow.QualityOpen();
            Assert.That(host.Log[host.Log.Count - 1], Is.EqualTo("open D:/lib/Resorts/abc"));
        }

        [Test]
        public void LibraryOpenAndBack()
        {
            var (flow, host) = Title();
            flow.MyResorts();
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Library));
            flow.NewResort();
            flow.PickerCancelled();
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Library), "cancel goes back to the library it came from");
            flow.Open("D:/lib/Resorts/xyz");
            Assert.That(host.Log, Does.Contain("open D:/lib/Resorts/xyz"));
        }

        [Test]
        public void ExitToTitleOnlyLeavesAMountain()
        {
            var (flow, host) = Title();
            flow.ExitToTitle();
            Assert.That(host.Log, Has.None.StartsWith("title"), "on the title there is nothing to exit");
            flow.SceneReady(inGame: true);
            flow.ExitToTitle();
            Assert.That(host.Log[host.Log.Count - 1], Is.EqualTo("title then Title"));
        }

        [Test]
        public void EscapeBacksOutOneStep()
        {
            var (flow, host) = Title();
            Assert.That(flow.Escape(), Is.False, "nothing to back out of on the title");
            flow.MyResorts();
            flow.NewResort();
            Assert.That(flow.Escape(), Is.True);
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Library));
            flow.DownloadStarted();
            Assert.That(flow.Escape(), Is.True);
            Assert.That(host.CardOpen, Is.False, "an open card minimises first");
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Library));
            Assert.That(flow.Escape(), Is.True);
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Title));
            flow.DownloadFinished("p");
            Assert.That(flow.Escape(), Is.True);
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Library), "the quality card backs out to the library");
        }

        [Test]
        public void ADownloadFinishingInTheGameShowsTheCardThere()
        {
            var host = new Host();
            var flow = new FlowController(host);
            flow.SceneReady(inGame: false);
            flow.NewResort();
            flow.DownloadStarted();
            flow.MinimiseDownload();
            flow.SceneReady(inGame: true);   // the player opened another mountain meanwhile
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Game));
            Assert.That(host.CardOpen, Is.False, "still the pill");
            flow.DownloadFinished("p");
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Quality));
            flow.QualityBackToLibrary();
            Assert.That(host.Log[host.Log.Count - 1], Is.EqualTo("title then Library"));
            flow.SceneReady(inGame: false, FlowScreen.Library);   // the title scene is back up
            Assert.That(flow.Screen, Is.EqualTo(FlowScreen.Library));

            flow.SceneReady(inGame: true);
            Assert.That(flow.Escape(), Is.False, "in the game Esc belongs to the viewer's menu");
        }

        [Test]
        public void StoppingADownloadRemovesCardAndPill()
        {
            var (flow, host) = Title();
            flow.DownloadStarted();
            flow.DownloadStopped();
            Assert.That(flow.DownloadActive, Is.False);
            Assert.That(host.CardOpen, Is.False);
            flow.RestoreDownload();
            Assert.That(host.CardOpen, Is.False, "nothing to restore");
        }
    }

    /// <summary>Task 14: start, cancel, resume and finish, with a fake downloader in a scratch library.</summary>
    public sealed class DownloadServiceTests
    {
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-dl-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary>Reports three stages, then waits at the second until released or cancelled; counts its runs.</summary>
        sealed class Fake : ISiteDownloader
        {
            public int Runs;
            public bool Block = true;
            public string Fail;
            /// <summary>Thrown once instead of finishing (task P2-06), then cleared.</summary>
            public Exception Throw;
            public double Overall = 0.4;
            public readonly ManualResetEventSlim Reached = new ManualResetEventSlim();

            public async Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> progress, CancellationToken ct)
            {
                Interlocked.Increment(ref Runs);
                var stages = new[] { "Terrain", "Forest", "Building" };
                Directory.CreateDirectory(build);
                progress.Report(new DownloadStatus { Name = d.Name, Stages = stages, StageIndex = 2, StageCount = 3, Stage = "Forest", Overall = Overall });
                Reached.Set();
                while (Block) { ct.ThrowIfCancellationRequested(); await Task.Delay(5, ct); }
                if (Fail != null) throw new IOException(Fail);
                var once = Throw;
                Throw = null;
                if (once != null) throw once;
                string target = Path.Combine(ResortLibrary.ResortsFolder(_rootOf(build)), "pkg-" + d.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Directory.Move(build, target);
                return target;
            }

            static string _rootOf(string build) => Path.GetFullPath(Path.Combine(build, "..", "..", ".."));
        }

        static PendingDownload Site() => DownloadService.Request(
            PickedSite.Create("Crystal Mountain", Albers6350.Forward(new GeoPoint(46.935, -121.474)), 2, false, default), "2026-10-02T10:00:00Z");

        static void PumpUntil(DownloadService s, Func<bool> done)
        {
            for (int i = 0; i < 400 && !done(); i++)
            {
                Thread.Sleep(10);
                s.Pump(0.01);
            }
            Assert.That(done(), Is.True, "timed out");
        }

        [Test]
        public void CancelKeepsTheRecordAndResumeFinishes()
        {
            var fake = new Fake();
            var service = new DownloadService(_root, fake, () => "now");
            bool? kept = null;
            string finished = null;
            service.Stopped += k => kept = k;
            service.Finished += f => finished = f;

            var d = Site();
            Assert.That(service.Start(d), Is.True);
            Assert.That(service.Start(d), Is.False, "one at a time");
            Assert.That(PendingDownloads.List(_root), Has.Count.EqualTo(1), "the record is written at the start, so a crash leaves it");
            Assert.That(fake.Reached.Wait(5000), Is.True);
            PumpUntil(service, () => service.View.StageNames.Count == 3);
            Assert.That(service.View.StageStates[1], Is.EqualTo(StageState.Current));

            service.Cancel(keep: true);
            PumpUntil(service, () => kept != null);
            Assert.That(kept, Is.True);
            Assert.That(service.View.Phase, Is.EqualTo(DownloadPhase.Paused));
            var paused = PendingDownloads.List(_root);
            Assert.That(paused, Has.Count.EqualTo(1), "kept for resuming");

            fake.Block = false;
            Assert.That(service.Start(paused[0]), Is.True);
            PumpUntil(service, () => finished != null);
            Assert.That(fake.Runs, Is.EqualTo(2));
            Assert.That(Directory.Exists(finished), Is.True);
            Assert.That(PendingDownloads.List(_root), Is.Empty, "a finished download leaves no record");
            Assert.That(service.View.Phase, Is.EqualTo(DownloadPhase.Finished));
        }

        [Test]
        public void DiscardDeletesThePartialDownload()
        {
            var fake = new Fake();
            var service = new DownloadService(_root, fake, () => "now");
            bool? kept = null;
            service.Stopped += k => kept = k;
            var d = Site();
            service.Start(d);
            Assert.That(fake.Reached.Wait(5000), Is.True);
            service.Cancel(keep: false);
            PumpUntil(service, () => kept != null);
            Assert.That(kept, Is.False);
            Assert.That(PendingDownloads.List(_root), Is.Empty);
            Assert.That(Directory.Exists(PendingDownloads.FolderOf(_root, d)), Is.False);
        }

        [Test]
        public void AFailureKeepsTheRecordForRetry()
        {
            var fake = new Fake { Block = false, Throw = new IOException("The request failed after 4 attempts.", new System.Net.Http.HttpRequestException("Response status code does not indicate success: 404 (Not Found).")) };
            var service = new DownloadService(_root, fake, () => "now");
            DownloadProblem failed = null;
            service.Failed += m => failed = m;
            service.Start(Site());
            PumpUntil(service, () => failed != null);
            Assert.That(failed.Kind, Is.EqualTo(ProblemKind.ServerError));
            Assert.That(service.View.Detail, Does.Not.Contain("404"), "S11: no raw message on screen");
            Assert.That(service.View.Error, Does.Contain("404"), "the raw one is kept for the log");
            Assert.That(service.View.Phase, Is.EqualTo(DownloadPhase.Failed));
            Assert.That(service.Current, Is.Not.Null, "Retry restarts the same request");
            Assert.That(PendingDownloads.List(_root), Has.Count.EqualTo(1));
        }

        // ---------- task P2-06 ----------

        [Test]
        public void ALostConnectionWaitsAndTriesAgainByItself()
        {
            var fake = new Fake { Block = false, Throw = new IOException("The request failed after 4 attempts.", new System.Net.Http.HttpRequestException("No such host", new System.Net.Sockets.SocketException(11001))) };
            var service = new DownloadService(_root, fake, () => "now");
            DownloadProblem waited = null, failed = null;
            string finished = null;
            service.WaitStarted += p => waited = p;
            service.Failed += p => failed = p;
            service.Finished += f => finished = f;
            OfflineState.Reset();
            try
            {
                service.Start(Site());
                PumpUntil(service, () => waited != null);
                Assert.That(waited.Kind, Is.EqualTo(ProblemKind.NoConnection));
                Assert.That(failed, Is.Null, "a lost connection is not a failure");
                Assert.That(service.Waiting && service.Active, Is.True);
                Assert.That(service.View.Phase, Is.EqualTo(DownloadPhase.Waiting));
                Assert.That(service.View.Pill, Does.Contain("waiting for connection"));
                Assert.That(OfflineState.Mode, Is.EqualTo(OfflineMode.NoConnection));
                Assert.That(service.Start(DownloadService.Request(
                    PickedSite.Create("Sugarloaf", Albers6350.Forward(new GeoPoint(45.05, -70.31)), 2, false, default), "now")), Is.False, "the slot stays taken");

                service.Pump(DownloadService.RetrySeconds - 1);
                Assert.That(fake.Runs, Is.EqualTo(1), "not yet");
                Assert.That(service.View.Detail, Does.Contain("1 s"));
                service.Pump(1.5);   // the retry: this time it goes through
                PumpUntil(service, () => finished != null);
                Assert.That(fake.Runs, Is.EqualTo(2));
                Assert.That(PendingDownloads.List(_root), Is.Empty);
            }
            finally { OfflineState.Reset(); }
        }

        [Test]
        public void OfflineModeWaitsUntilItIsTurnedOff()
        {
            var fake = new Fake { Block = false, Throw = new MountainPlanner.Acquisition.IO.NetworkDisabledException() };
            var service = new DownloadService(_root, fake, () => "now");
            DownloadProblem waited = null;
            string finished = null;
            service.WaitStarted += p => waited = p;
            service.Finished += f => finished = f;
            service.Start(Site());
            PumpUntil(service, () => waited != null);
            Assert.That(waited.Kind, Is.EqualTo(ProblemKind.OfflineMode));
            Assert.That(service.View.Pill, Does.Contain("offline mode"));
            service.Pump(DownloadService.RetrySeconds * 4);
            Assert.That(fake.Runs, Is.EqualTo(1), "no retries while offline mode is on");
            service.TryNow();   // offline mode turned off
            PumpUntil(service, () => finished != null);
            Assert.That(fake.Runs, Is.EqualTo(2));
        }

        [Test]
        public void CancellingWhileWaitingKeepsOrDiscards()
        {
            var fake = new Fake { Block = false, Throw = new MountainPlanner.Acquisition.IO.NetworkDisabledException() };
            var service = new DownloadService(_root, fake, () => "now");
            bool? kept = null;
            service.Stopped += k => kept = k;
            service.Start(Site());
            PumpUntil(service, () => service.Waiting);
            service.Cancel(keep: true);
            Assert.That(kept, Is.True);
            Assert.That(service.Active, Is.False);
            Assert.That(PendingDownloads.List(_root), Has.Count.EqualTo(1));
        }

        [Test]
        public void AResumedDownloadShowsWhereItStoppedAndNeverGoesBack()
        {
            var fake = new Fake { Overall = 0.1 };
            var service = new DownloadService(_root, fake, () => "now");
            var d = Site();
            d.LastOverall = 0.62;
            d.LastStage = "Ground cover";
            service.Start(d);
            Assert.That(service.View.Percent, Is.EqualTo("62%"), "at once, before the pipeline reports");
            Assert.That(service.View.Detail, Does.Contain("Resuming at Ground cover"));
            Assert.That(fake.Reached.Wait(5000), Is.True);
            PumpUntil(service, () => service.View.StageNames.Count == 3);
            Assert.That(service.View.Fraction, Is.EqualTo(0.62f).Within(1e-6), "reading back what arrived (10%) doesn't pull the bar back");
            Assert.That(service.View.Detail, Does.StartWith("Resuming"));

            // The record keeps the furthest point reached, so a second kill loses nothing.
            service.Pump(DownloadService.RecordEverySeconds);
            fake.Block = false;
            Assert.That(PendingDownloads.List(_root)[0].LastOverall, Is.EqualTo(0.62).Within(1e-9));
            service.Cancel(keep: true);
        }

        [Test]
        public void PumpingWithoutNewsAllocatesNothing()
        {
            var fake = new Fake();
            var service = new DownloadService(_root, fake, () => "now");
            service.Start(Site());
            Assert.That(fake.Reached.Wait(5000), Is.True);
            PumpUntil(service, () => service.View.StageNames.Count == 3);
            service.Pump(0.016);   // warm
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++) service.Pump(0.016 / 300);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);
            Assert.That(allocated, Is.EqualTo(0), "per frame, with no new snapshot");
        }

        [Test]
        public void FailuresAreSortedIntoWhatThePlayerIsTold()
        {
            Assert.That(DownloadErrors.Classify(new MountainPlanner.Acquisition.IO.NetworkDisabledException()), Is.EqualTo(ProblemKind.OfflineMode));
            Assert.That(DownloadErrors.Classify(new InvalidOperationException("OpenStreetMap (Overpass) is unavailable.",
                new IOException("The request failed after 4 attempts.", new System.Net.Http.HttpRequestException("x", new IOException("reset"))))), Is.EqualTo(ProblemKind.NoConnection));
            Assert.That(DownloadErrors.Classify(new IOException("timeout", new TaskCanceledException())), Is.EqualTo(ProblemKind.NoConnection));
            Assert.That(DownloadErrors.Classify(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))), Is.EqualTo(ProblemKind.DiskFull));
            Assert.That(DownloadErrors.Classify(new UnauthorizedAccessException("denied")), Is.EqualTo(ProblemKind.AccessDenied));
            Assert.That(DownloadErrors.Classify(new InvalidDataException("Not a TIFF file")), Is.EqualTo(ProblemKind.ServerError));
            Assert.That(DownloadErrors.Classify(new NullReferenceException()), Is.EqualTo(ProblemKind.Unknown));
            // What USGS's gateway said on 2026-10-08, mid-download: busy, not broken.
            Assert.That(DownloadErrors.Classify(new IOException("The request failed after 4 attempts.", new System.Net.Http.HttpRequestException("502 (Bad Gateway)"))), Is.EqualTo(ProblemKind.ServiceBusy));
            Assert.That(DownloadProblem.Of(ProblemKind.ServiceBusy).Waits, Is.True);
            foreach (ProblemKind k in Enum.GetValues(typeof(ProblemKind)))
            {
                var p = DownloadProblem.Of(k, 240_000_000);
                Assert.That(p.Title, Is.Not.Empty);
                Assert.That(p.Text, Does.Not.Contain("Exception"));
            }
        }

        [Test]
        public void TheOfflineStateTellsTheSettingFromTheCable()
        {
            OfflineState.Reset();
            int changes = 0;
            Action count = () => changes++;
            OfflineState.Changed += count;
            try
            {
                Assert.That(OfflineState.IsOffline, Is.False);
                Assert.That(OfflineState.Describe(), Is.Empty);
                OfflineState.ReportConnection(false);
                Assert.That(OfflineState.Mode, Is.EqualTo(OfflineMode.NoConnection));
                OfflineState.SetSetting(true);
                Assert.That(OfflineState.Mode, Is.EqualTo(OfflineMode.Setting), "the setting wins");
                OfflineState.ReportConnection(false);   // no change
                OfflineState.SetSetting(false);
                OfflineState.ReportConnection(true);
                Assert.That(OfflineState.Mode, Is.EqualTo(OfflineMode.None));
                Assert.That(changes, Is.EqualTo(4));
            }
            finally
            {
                OfflineState.Changed -= count;
                OfflineState.Reset();
            }
        }
    }
}
