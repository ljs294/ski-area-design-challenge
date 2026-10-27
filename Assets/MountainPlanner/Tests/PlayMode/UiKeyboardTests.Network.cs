using System.Collections;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.UI;
using NUnit.Framework;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-10 in the real game (NW-04, NW-13; owner NT4, NT5, NT12): the bar's lift and trail counts are live and open
    /// Analysis › Network, which turns the Network view on with its key; a lift's or trail's card stacks under the
    /// legend in the right-hand column, the legend folded to its title while the card is open.
    /// </summary>
    public sealed partial class UiKeyboardTests
    {
        [UnityTest]
        public IEnumerator NW13_TheNetworkTabCountsAndStackedCards()
        {
            Http.NetworkDisabled = true;
            UiPreferences.SetChoice(UiThemeChoice.Dark, remember: false);
            UiPreferences.SetScale(100, remember: false);
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120);
            yield return Frames(3);
            var ui = flow.Screens.Document.rootVisualElement;
            yield return TabTo(ui, "title-continue");
            yield return Submit(ui);
            yield return WaitForMountain(120, inGame: true);
            yield return Frames(10);
            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            var hud = viewer.Hud;
            var root = hud.Document.rootVisualElement;

            // NT5: the counts are live, amber with a warning, and their tooltip says what.
            hud.SetNetworkCounts(2, 1, 3, 0);
            var lifts = root.Q<Button>("bar-lifts");
            var trails = root.Q<Button>("bar-trails");
            Assert.That(lifts.Q<Label>(className: "count").text, Is.EqualTo("2"));
            Assert.That(lifts.ClassListContains("stat--warn"), Is.True, "a lift with no way down turns the count amber");
            Assert.That(lifts.ClassListContains("stat--dis"), Is.False);
            Assert.That(lifts.tooltip, Does.Contain("1 has no way down"));
            Assert.That(trails.ClassListContains("stat--warn"), Is.False);

            // A click on the count opens Analysis › Network; the Network view and its key come on (NT4).
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = lifts;
                lifts.SendEvent(e);
            }
            yield return Frames(2);
            Assert.That(hud.AnalysisTab, Is.EqualTo("network"));
            Assert.That(hud.NetworkViewOn, Is.True);
            Assert.That(root.Q<Label>("legend-title").text, Is.EqualTo("Network"));
            Assert.That(root.Q("legend").ClassListContains("hidden"), Is.False, "the key shows in the Network view");

            // Game finding 1: the key's last row is "Reachable", its swatch four touching squares (green, blue, black, red).
            var reachable = root.Q("legend-body").Query<Label>(className: "lr__word").Where(l => l.text == "Reachable").First();
            Assert.That(reachable, Is.Not.Null);
            Assert.That(reachable.parent.Q(className: "lr__sw--stripes").Query(className: "lr__part").ToList().Count, Is.EqualTo(4));

            // Game finding 2: the key's ✕ closes it; opening the Network view again brings it back.
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                var x = root.Q<Button>("legend-x");
                e.target = x;
                x.SendEvent(e);
            }
            yield return Frames(2);
            Assert.That(root.Q("legend").ClassListContains("hidden"), Is.True, "the ✕ closes the Network key");
            hud.SetAnalysis(false);
            hud.SetAnalysis(true);
            yield return Frames(2);
            Assert.That(hud.NetworkViewOn, Is.True);
            Assert.That(root.Q("legend").ClassListContains("hidden"), Is.False, "the key is back when the view reopens");

            // NT12: a card stacks under the legend, and the legend folds while it's open.
            hud.SetTrails(new[] { new TrailRow { Id = 7, Name = "Test Run", RatingPercent = 30, LengthMetres = 500, VerticalMetres = 150, Built = true } });
            hud.OpenTrailCardFor(7);
            yield return Frames(2);
            Assert.That(root.Q("trail-card").parent.name, Is.EqualTo("rcol"));
            Assert.That(root.Q("legend").ClassListContains("legend--folded"), Is.True);
            hud.CloseTrailCardIfOpen();
            yield return Frames(2);
            Assert.That(root.Q("legend").ClassListContains("legend--folded"), Is.False);

            // NW-15 (NT19): the Areas table, and an Area's card in the right-hand column renaming it (click or F2).
            var area = new NetworkAreaRow { Id = 99, Name = "Chair 1 & Chair 2 base", Lifts = new[] { "Chair 1 loads", "Chair 2 loads" }, TrailsHere = 2, TrailsReached = 5, LiftsReached = 2 };
            hud.SetNetwork(new[] { new NetworkLiftRow { Id = 1, Name = "Chair 1", Reached = 3, Connected = true } }, new NetworkTrailRow[0], 3, 0, 0, new[] { area });
            yield return Frames(2);
            Assert.That(root.Q("an-list-network-areas").ClassListContains("hidden"), Is.False, "the Areas table shows with an Area");
            Assert.That(root.Q<Label>("network-tools").text, Does.Contain("1 area"));
            var renamed = hud.AreaRenamed;
            string typed = null;
            hud.AreaRenamed = (id, name) => { typed = id + ":" + name; return null; };
            try
            {
                hud.OpenAreaCardFor(99);
                yield return Frames(2);
                var card = root.Q("network-area-card");
                Assert.That(card.parent.name, Is.EqualTo("rcol"));
                Assert.That(card.ClassListContains("hidden"), Is.False);
                Assert.That(root.Q<Label>("network-area-card-name").text, Is.EqualTo("Chair 1 & Chair 2 base"));
                Assert.That(card.Query<Label>(className: "tw-hint").First().text, Does.Contain("Click the name to name it"));
                Assert.That(root.Q("legend").ClassListContains("legend--folded"), Is.True);
                hud.RenameAreaCardForTest("Village");
                Assert.That(typed, Is.EqualTo("99:Village"), "the rename goes to the app, one undoable RenameArea");
                Assert.That(root.Q<Label>("network-area-card-name").text, Is.EqualTo("Village"));
                Assert.That(hud.CloseAreaCardIfOpen(), Is.True);
                Assert.That(card.ClassListContains("hidden"), Is.True);
            }
            finally { hud.AreaRenamed = renamed; }

            // Leaving the tab turns the view off and gives the legend back.
            hud.SetAnalysis(false);
            yield return Frames(2);
            Assert.That(hud.NetworkViewOn, Is.False);

            yield return NT7_NamesOnTheMapNeverOverlap(hud);
            SceneManager.SetActiveScene(SceneManager.CreateScene("UiKeyboardTests empty"));
            yield return SceneManager.UnloadSceneAsync(viewer.gameObject.scene);
            yield return UnityEngine.Resources.UnloadUnusedAssets();
        }

        /// <summary>
        /// NT7 (owner, 2026-10-26) in the HUD's real panel: a lift's name always shows, slanted along its line; an Area's
        /// name on the same spot moves to a nudge; a run's name blocked at its middle takes its next spot; a name with nowhere
        /// free hides; a still camera places them again without allocating.
        /// </summary>
        static IEnumerator NT7_NamesOnTheMapNeverOverlap(MountainHud hud)
        {
            var go = new UnityEngine.GameObject("NT7 label camera");
            var placement = new LabelPlacement();
            try
            {
                var camera = go.AddComponent<UnityEngine.Camera>();
                camera.enabled = false;
                go.transform.SetPositionAndRotation(new UnityEngine.Vector3(0, 1500, 0), UnityEngine.Quaternion.Euler(90, 0, 0));
                UnityEngine.Vector3 p = UnityEngine.Vector3.zero, q = new UnityEngine.Vector3(300, 0, 0);
                var lifts = new LandmarkLabelOverlay(hud.ContourLabelLayer, placement, LabelPlacement.Lifts, yields: false);
                var areas = new LandmarkLabelOverlay(hud.ContourLabelLayer, placement, LabelPlacement.Areas,
                                                     nudges: new[] { UnityEngine.Vector2.zero, new UnityEngine.Vector2(0, -24), new UnityEngine.Vector2(0, 24) });
                var trails = new LandmarkLabelOverlay(hud.ContourLabelLayer, placement, LabelPlacement.Trails);
                lifts.SetLabels(new[] { ("NT7 Lift", new[] { new MountainPlanner.Presentation.Design.MapLabelSpot(p, new UnityEngine.Vector3(-100, 0, -30), new UnityEngine.Vector3(100, 0, 30)) }) });
                areas.SetLandmarks(new[] { ("NT7 Area", p) });
                trails.SetLabels(new[]
                {
                    ("NT7 Long run", new[] { Flat(p), Flat(q) }),
                    ("NT7 Boxed in", new[] { Flat(p) }),
                });
                for (int f = 0; f < 3; f++)
                {
                    lifts.Update(camera, null, true);
                    areas.Update(camera, null, true);
                    trails.Update(camera, null, true);
                    placement.Place();
                    yield return Frames(1);
                }
                var root = hud.Document.rootVisualElement;
                Label Named(string text) => root.Query<Label>(className: "landmark-label").Where(l => l.text == text).First();
                bool Shown(string text) => Named(text).style.display == DisplayStyle.Flex;
                Assert.That(placement.Placed, Is.EqualTo(3), "the lift, the Area and the long run");
                Assert.That(Shown("NT7 Lift") && Shown("NT7 Area") && Shown("NT7 Long run"), Is.True);
                Assert.That(Shown("NT7 Boxed in"), Is.False, "nowhere free: hidden");
                float slant = Named("NT7 Lift").style.rotate.value.angle.value;
                Assert.That(UnityEngine.Mathf.Abs(slant), Is.EqualTo(16.7f).Within(1f), "slanted along the lift's line, reading left to right");
                Assert.That(Named("NT7 Area").style.translate.value.y.value, Is.Not.EqualTo(Named("NT7 Lift").style.translate.value.y.value).Within(1f),
                            "the Area's name moved off the lift's");
                Assert.That(Named("NT7 Long run").style.translate.value.x.value, Is.GreaterThan(Named("NT7 Lift").style.translate.value.x.value + 50),
                            "the run's name took its second spot");
                Assert.That((TestDelegate)placement.Place, new NUnit.Framework.Constraints.NotConstraint(new UnityEngine.TestTools.Constraints.AllocatingGCMemoryConstraint()));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                foreach (var layer in hud.Document.rootVisualElement.Query("landmark-labels").ToList())
                    if (layer.Q<Label>(className: "landmark-label")?.text.StartsWith("NT7 ") == true) layer.RemoveFromHierarchy();
            }
        }

        static MountainPlanner.Presentation.Design.MapLabelSpot Flat(UnityEngine.Vector3 at) =>
            new MountainPlanner.Presentation.Design.MapLabelSpot(at, at - new UnityEngine.Vector3(20, 0, 0), at + new UnityEngine.Vector3(20, 0, 0));
    }
}
