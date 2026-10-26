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

            // Leaving the tab turns the view off and gives the legend back.
            hud.SetAnalysis(false);
            yield return Frames(2);
            Assert.That(hud.NetworkViewOn, Is.False);
            SceneManager.SetActiveScene(SceneManager.CreateScene("UiKeyboardTests empty"));
            yield return SceneManager.UnloadSceneAsync(viewer.gameObject.scene);
            yield return UnityEngine.Resources.UnloadUnusedAssets();
        }
    }
}
