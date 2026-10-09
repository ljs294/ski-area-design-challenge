using System;
using MountainPlanner.UI.Flow;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-03.1: the loading screen's stages. The bar never goes backwards, stages that don't apply drop out, the
    /// cache build's out-of-order reports are handled, and the view model formats nothing when nothing changed.
    /// </summary>
    public sealed class LoadingTests
    {
        /// <summary>A cached open of 121 tiles, as ResortOpener reports it, with a check after every report.</summary>
        static void CachedOpen(LoadProgress p, Action after)
        {
            p.Begin(LoadStage.Package, 0, 0.0); after();
            p.End(LoadStage.Package, 0.2); after();
            p.Skip(LoadStage.Preparing); after();
            p.Begin(LoadStage.Terrain, 121, 0.2); after();
            for (int n = 1; n <= 121; n++) { p.Advance(LoadStage.Terrain, n); after(); }
            p.End(LoadStage.Terrain, 5.9); after();
            p.Begin(LoadStage.GroundCover, 121, 5.9);
            for (int n = 1; n <= 121; n++) { p.Advance(LoadStage.GroundCover, n); after(); }
            p.End(LoadStage.GroundCover, 6.3); after();
            p.Begin(LoadStage.Snowpack, 0, 6.3); p.End(LoadStage.Snowpack, 6.5); after();
            p.Begin(LoadStage.Cliffs, 2_025_688, 6.5); p.Advance(LoadStage.Cliffs, 1_000_000); after(); p.End(LoadStage.Cliffs, 6.53); after();
            p.Begin(LoadStage.Forest, 622_823, 6.53); p.Advance(LoadStage.Forest, 300_000); after(); p.End(LoadStage.Forest, 6.55); after();
        }

        [Test]
        public void TheBarOnlyRisesAndEndsFull()
        {
            var p = new LoadProgress(0);
            float last = 0;
            CachedOpen(p, () =>
            {
                Assert.That(p.Fraction, Is.GreaterThanOrEqualTo(last));
                last = p.Fraction;
            });
            Assert.That(p.Complete, Is.True);
            Assert.That(p.Fraction, Is.EqualTo(1f));
            Assert.That(p.TotalSeconds(100), Is.EqualTo(6.55).Within(1e-9), "the total stops at the last stage, not the clock");
            Assert.That(p.Seconds(LoadStage.Terrain, 100), Is.EqualTo(5.7).Within(1e-9));
        }

        [Test]
        public void TerrainIsMostOfACachedOpen()
        {
            var p = new LoadProgress(0);
            p.Begin(LoadStage.Package, 0, 0); p.End(LoadStage.Package, 0.2); p.Skip(LoadStage.Preparing);
            p.Begin(LoadStage.Terrain, 121, 0.2); p.Advance(LoadStage.Terrain, 121);
            Assert.That(p.Fraction, Is.InRange(0.85f, 0.92f), "the measured shares: terrain is about 90% of the time");
        }

        [Test]
        public void AFirstOpenGivesMostOfTheBarToPreparingAndNeverGoesBack()
        {
            var p = new LoadProgress(0);
            p.Begin(LoadStage.Package, 0, 0);
            p.End(LoadStage.Package, 0.2);
            float before = p.Fraction;
            p.Begin(LoadStage.Preparing, 0, 0.2);   // joins after the package: the shares grow, the bar mustn't drop
            Assert.That(p.Fraction, Is.GreaterThanOrEqualTo(before));
            // The build reports from many threads: out of order, sometimes repeated.
            p.Advance(LoadStage.Preparing, 5, 121);
            p.Advance(LoadStage.Preparing, 3, 121);
            Assert.That(p.Done(LoadStage.Preparing), Is.EqualTo(5), "only the furthest report counts");
            p.Advance(LoadStage.Preparing, 121, 121);
            Assert.That(p.Fraction, Is.InRange(0.7f, 0.8f), "preparing is three times an open");
            p.End(LoadStage.Preparing, 20);
            p.Advance(LoadStage.Preparing, 200, 121);
            Assert.That(p.Done(LoadStage.Preparing), Is.EqualTo(121), "nothing moves once it's done");
        }

        [Test]
        public void SkippedStagesDropOut()
        {
            var p = new LoadProgress(0);
            CachedOpenWithout(p);
            Assert.That(p.Complete, Is.True);
            Assert.That(p.Fraction, Is.EqualTo(1f));
            Assert.That(p.State(LoadStage.Cliffs), Is.EqualTo(LoadState.Skipped));
        }

        static void CachedOpenWithout(LoadProgress p)
        {
            p.Begin(LoadStage.Package, 0, 0); p.End(LoadStage.Package, 0.1); p.Skip(LoadStage.Preparing);
            p.Begin(LoadStage.Terrain, 4, 0.1); p.End(LoadStage.Terrain, 1);
            p.Begin(LoadStage.GroundCover, 4, 1); p.End(LoadStage.GroundCover, 1.1);
            p.Begin(LoadStage.Snowpack, 0, 1.1); p.End(LoadStage.Snowpack, 1.2);
            p.Skip(LoadStage.Cliffs);
            p.Skip(LoadStage.Forest);
        }

        [Test]
        public void TheViewModelShowsCountsAndTimes()
        {
            var p = new LoadProgress(0);
            var vm = new LoadingViewModel();
            Assert.That(vm.Rows[(int)LoadStage.Package].State, Is.EqualTo(StageState.Current), "reading the area from the start");
            Assert.That(vm.Rows[(int)LoadStage.Preparing].Shown, Is.False);

            p.Begin(LoadStage.Package, 0, 0); p.End(LoadStage.Package, 0.2); p.Skip(LoadStage.Preparing);
            p.Begin(LoadStage.Terrain, 121, 0.2); p.Advance(LoadStage.Terrain, 37);
            Assert.That(vm.Refresh(p, 3.0), Is.True);
            var terrain = vm.Rows[(int)LoadStage.Terrain];
            Assert.That(terrain.State, Is.EqualTo(StageState.Current));
            Assert.That(terrain.Count, Is.EqualTo("37 of 121 tiles"));
            Assert.That(vm.Rows[(int)LoadStage.Package].Time, Is.EqualTo("0.2 s"));
            Assert.That(vm.Elapsed, Is.EqualTo("3.0 s"));

            CachedOpenRest(p);
            vm.Refresh(p, 9);
            Assert.That(vm.Rows[(int)LoadStage.Forest].Count, Is.EqualTo("622,823 trees"));
            Assert.That(vm.Rows[(int)LoadStage.Cliffs].Count, Is.EqualTo("2,025,688 triangles"));
            Assert.That(vm.Rows[(int)LoadStage.Terrain].Time, Is.EqualTo("5.7 s"));
            Assert.That(vm.Percent, Is.EqualTo("100%"));
            Assert.That(vm.Elapsed, Is.EqualTo("6.5 s"), "the total stops when the last stage is done");
        }

        static void CachedOpenRest(LoadProgress p)
        {
            p.Advance(LoadStage.Terrain, 121); p.End(LoadStage.Terrain, 5.9);
            p.Begin(LoadStage.GroundCover, 121, 5.9); p.End(LoadStage.GroundCover, 6.3);
            p.Begin(LoadStage.Snowpack, 0, 6.3); p.End(LoadStage.Snowpack, 6.4);
            p.Begin(LoadStage.Cliffs, 2_025_688, 6.4); p.Advance(LoadStage.Cliffs, 1_500_000); p.End(LoadStage.Cliffs, 6.45);
            p.Begin(LoadStage.Forest, 622_823, 6.45); p.End(LoadStage.Forest, 6.5);
        }

        /// <summary>The loading screen's picture ships at full size: Unity's defaults would shrink it to 2048 and stretch it square.</summary>
        [Test]
        public void TheTitlePictureLoadsAtFullSize()
        {
            var still = Resources.Load<Texture2D>(FlowScreens.TitleStillResource);
            Assert.That(still, Is.Not.Null, "capture it with -titlestill (demo.bat 57)");
            Assert.That(still.width, Is.EqualTo(App.Flow.AppFlow.TitleStillWidth));
            Assert.That(still.height, Is.EqualTo(App.Flow.AppFlow.TitleStillHeight));
        }

        [Test]
        public void NothingIsFormattedWhenNothingChanged()
        {
            var p = new LoadProgress(0);
            var vm = new LoadingViewModel();
            p.Begin(LoadStage.Package, 0, 0); p.End(LoadStage.Package, 0.2); p.Skip(LoadStage.Preparing);
            p.Begin(LoadStage.Terrain, 121, 0.2);
            for (int n = 1; n <= 121; n++) { p.Advance(LoadStage.Terrain, n); vm.Refresh(p, 1 + n * 0.01); }   // warms the tables
            vm.Refresh(p, 3);
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool changed = false;
            for (int i = 0; i < 1000; i++) changed |= vm.Refresh(p, 3.04);   // the same tenth of a second, nothing new
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
            Assert.That(changed, Is.False);
        }
    }
}
