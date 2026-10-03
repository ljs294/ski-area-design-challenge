using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App.Picker;
using MountainPlanner.Domain.Geo;
using MountainPlanner.UI.Picker;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace MountainPlanner.Tests
{
    // Task 13 acceptance: the offline panel shows without a network (0.4 §3), and Retry recovers.
    public sealed class SitePickerPlayTests
    {
        GameObject _go;

        [TearDown]
        public void TearDown()
        {
            Http.NetworkDisabled = false;
            if (_go != null) Object.Destroy(_go);
        }

        SitePicker Open(ISitePickerServices services)
        {
            _go = new GameObject("Picker under test");
            _go.SetActive(false);
            var document = _go.AddComponent<UIDocument>();
            document.panelSettings = UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/MountainPlanner/Art/UI/PickerPanel.asset")
                ?? ScriptableObject.CreateInstance<PanelSettings>();
            document.visualTreeAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/MountainPlanner/Art/UI/SitePicker.uxml");
            var picker = _go.AddComponent<SitePicker>();
            picker.Document = document;
            _go.SetActive(true);
            picker.Services = services;
            picker.Show();
            return picker;
        }

        static void Press(Button button)
        {
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
        }

        static bool OfflinePanelShown(SitePicker picker) =>
            !picker.Document.rootVisualElement.Q("offline").ClassListContains("hidden");

        [UnityTest]
        public IEnumerator WithoutANetworkTheOfflinePanelShows()
        {
            Http.NetworkDisabled = true;
            var picker = Open(new SitePickerServices());
            for (int i = 0; i < 60 && !OfflinePanelShown(picker); i++) yield return null;
            Assert.That(OfflinePanelShown(picker), Is.True, "the offline panel replaces the map");
            Assert.That(picker.Model.Offline, Is.True);
            Assert.That(picker.Document.rootVisualElement.Q<Button>("download").enabledSelf, Is.False);
            Assert.That(picker.Document.rootVisualElement.Q<TextField>("search").enabledSelf, Is.False, "search waits for the network");

            // The connection returns: Retry brings the map back.
            Http.NetworkDisabled = false;
            picker.Services = new Online();
            picker.Map.Services = picker.Services;
            Press(picker.Document.rootVisualElement.Q<Button>("retry"));
            for (int i = 0; i < 60 && OfflinePanelShown(picker); i++) yield return null;
            Assert.That(OfflinePanelShown(picker), Is.False);
        }

        [UnityTest]
        public IEnumerator ASearchHiccupSaysSoWithoutTakingTheMapOffline()
        {
            var services = new Online { SearchFails = true };
            var picker = Open(services);
            yield return null;
            picker.Document.rootVisualElement.Q<TextField>("search").value = "Jackson Hole";
            picker.RunSearch();
            for (int i = 0; i < 30 && picker.Model.SearchMessage.Length == 0; i++) yield return null;
            Assert.That(picker.Model.Offline, Is.False, "only the map decides the picker is offline");
            Assert.That(OfflinePanelShown(picker), Is.False);
            Assert.That(picker.Model.SearchMessage, Does.StartWith("Search isn't answering"));
        }

        [UnityTest]
        public IEnumerator EnterOnTheMapPlacesTheSquareAtItsCentre()
        {
            var picker = Open(new Online());
            yield return null;
            picker.Map.SetCentre(new GeoPoint(43.593, -110.848), 12);
            picker.Map.Focus();
            using (var e = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None)) { e.target = picker.Map; picker.Map.SendEvent(e); }
            yield return null;
            Assert.That(picker.Model.Square.HasValue, Is.True, "the keyboard can place the square too");
            Assert.That(picker.Model.Square.Value.Centre.DistanceTo(Albers6350.Forward(picker.Map.Centre)), Is.LessThan(2));
        }

        [UnityTest]
        public IEnumerator ANudgeKeepsTheSuggestedName()
        {
            var services = new Online();
            var picker = Open(services);
            yield return null;
            picker.PlaceAt(new GeoPoint(43.593, -110.848));
            for (int i = 0; i < 30 && picker.Model.Name.Length == 0; i++) yield return null;
            Assert.That(picker.Model.Name, Is.EqualTo("Teton Village"));
            picker.Map.Focus();
            using (var e = KeyDownEvent.GetPooled('\0', KeyCode.RightArrow, EventModifiers.None)) { e.target = picker.Map; picker.Map.SendEvent(e); }
            for (int i = 0; i < 10; i++) yield return null;
            Assert.That(picker.Model.Name, Is.EqualTo("Teton Village"), "a 100 m nudge doesn't rename the site");
            Assert.That(services.NameLookups, Is.EqualTo(1), "and asks Nominatim nothing");
        }

        [UnityTest]
        public IEnumerator AClickPlacesTheExactSquareAndNamesIt()
        {
            var services = new Online();
            var picker = Open(services);
            yield return null;
            var point = new GeoPoint(43.593, -110.848);
            picker.PlaceAt(point);
            for (int i = 0; i < 60 && picker.Model.Name.Length == 0; i++) yield return null;
            Assert.That(picker.Model.Square.Value.Core, Is.EqualTo(SiteSquare.Create(point, 4.0).Core));
            Assert.That(picker.Model.Name, Is.EqualTo("Teton Village"));

            PickedSite chosen = null;
            picker.SiteChosen += s => chosen = s;
            var download = picker.Document.rootVisualElement.Q<Button>("download");
            Assert.That(download.enabledSelf, Is.True);
            Press(download);
            yield return null;
            Assert.That(chosen, Is.Not.Null);
            Assert.That(SiteSquare.Create(chosen.Centre, chosen.SizeKm).Core, Is.EqualTo(chosen.Square.Core));
            Assert.That(services.Searches, Is.Zero, "nothing searched without Enter");
        }

        /// <summary>A network that always answers: blank tiles, no coverage, one name.</summary>
        sealed class Online : ISitePickerServices
        {
            public int Searches;
            public bool SearchFails;
            public string Attribution => "test";
            public Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct)
            {
                Searches++;
                if (SearchFails) throw new IOException("Search is unavailable.");
                return Task.FromResult<IReadOnlyList<PlaceResult>>(new PlaceResult[0]);
            }
            public int NameLookups;
            public Task<string> SuggestNameAsync(GeoPoint centre, CancellationToken ct) => Task.FromResult(NameLookups++ == 0 ? "Teton Village" : "Teton County");
            public Task<byte[]> TileAsync(bool imagery, int zoom, int x, int y, CancellationToken ct) => Task.FromResult<byte[]>(null);
            public Task<IReadOnlyList<AlbersBox>> S1mTilesAsync(AlbersBox box, CancellationToken ct) => Task.FromResult<IReadOnlyList<AlbersBox>>(new AlbersBox[0]);
            public Task<byte[]> CoverageImageAsync(CoverageLayer layer, double west, double south, double east, double north, int width, int height, CancellationToken ct) => Task.FromResult<byte[]>(null);
            public SiteEstimate Estimate(SiteSquare site, double s1m, double oneMetre, double threeMetre, double ringS1m, bool known) =>
                new SiteEstimate(30, new double[] { 0, 0, 0, 1 }, 1, 1, !known);
            public string EstimateLine(SiteEstimate estimate) => "Terrain about 30";
        }
    }
}
