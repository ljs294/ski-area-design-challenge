using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.Picker;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Terrain;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 13 acceptance (docs/plans/phase0-0.7-phase1-plan.md): the square's corners are exact in
    // EPSG:6350, and the rate-limit test passes. Engine-free; no test touches the network: responses
    // are fixtures recorded on 2026-10-02 (Nominatim data © OpenStreetMap contributors, ODbL 1.0).
    public sealed class PickerSquareTests
    {
        static readonly GeoPoint JacksonHole = new GeoPoint(43.593, -110.848);

        [TestCase(2.0)]
        [TestCase(3.3)]
        [TestCase(5.0)]
        public void TheCornersAreExactInAlbers(double km)
        {
            var site = PickedSite.Create("Jackson Hole", Albers6350.Forward(JacksonHole), km, false, default);
            var c = site.Square.Centre;
            double half = km * 500;
            var core = site.Square.Core;
            // Exactly the centre ± half the size, to the bit, on whole metres.
            Assert.That(core.West, Is.EqualTo(c.X - half));
            Assert.That(core.East, Is.EqualTo(c.X + half));
            Assert.That(core.South, Is.EqualTo(c.Y - half));
            Assert.That(core.North, Is.EqualTo(c.Y + half));
            Assert.That(c, Is.EqualTo(new AlbersPoint(-1188828, 2381976)));
        }

        [Test]
        public void TheDrawnCornersComeBackToTheExactCornersWithinAMillimetre()
        {
            var core = SiteSquare.Create(JacksonHole, 4.2).Core;
            var exact = new[]
            {
                new AlbersPoint(core.West, core.North), new AlbersPoint(core.East, core.North),
                new AlbersPoint(core.East, core.South), new AlbersPoint(core.West, core.South),
            };
            var drawn = SlippyMap.Corners(core);
            for (int i = 0; i < 4; i++)
            {
                // Through the map's pixels at a deep zoom and back, as a click on a drawn corner would be.
                var (px, py) = SlippyMap.ToPixels(drawn[i], 22);
                var back = Albers6350.Forward(SlippyMap.FromPixels(px, py, 22));
                Assert.That(back.DistanceTo(exact[i]), Is.LessThan(0.001), $"corner {i}");
            }
        }

        [Test]
        public void TheOutlineWalksTheStraightAlbersEdges()
        {
            var core = SiteSquare.Create(JacksonHole, 5).Core;
            var outline = SlippyMap.Outline(core, 8);
            Assert.That(outline.Length, Is.EqualTo(32));
            foreach (var p in outline)
            {
                var a = Albers6350.Forward(p);
                bool onEdge = Math.Abs(a.X - core.West) < 1e-6 || Math.Abs(a.X - core.East) < 1e-6 ||
                              Math.Abs(a.Y - core.South) < 1e-6 || Math.Abs(a.Y - core.North) < 1e-6;
                Assert.That(onEdge, Is.True, $"{a} lies on the square");
            }
        }

        [Test]
        public void PixelsRoundTrip()
        {
            var (x, y) = SlippyMap.ToPixels(JacksonHole, 10.5);
            var back = SlippyMap.FromPixels(x, y, 10.5);
            Assert.That(back.Latitude, Is.EqualTo(JacksonHole.Latitude).Within(1e-9));
            Assert.That(back.Longitude, Is.EqualTo(JacksonHole.Longitude).Within(1e-9));
            Assert.That(SlippyMap.ToPixels(new GeoPoint(0, 0), 0), Is.EqualTo((128.0, 128.0)));
        }
    }

    public sealed class NominatimTests
    {
        const string SearchFixture = "[{\"place_id\":323409813,\"licence\":\"Data © OpenStreetMap contributors, ODbL 1.0. http://osm.org/copyright\",\"osm_type\":\"relation\",\"osm_id\":11518092,\"lat\":\"46.9327365\",\"lon\":\"-121.4877287\",\"category\":\"landuse\",\"type\":\"winter_sports\",\"place_rank\":24,\"importance\":0.3298727163953751,\"addresstype\":\"winter_sports\",\"name\":\"Crystal Mountain\",\"display_name\":\"Crystal Mountain, Pierce County, Washington, United States\",\"boundingbox\":[\"46.9088953\",\"46.9567407\",\"-121.5089145\",\"-121.4671841\"]}]";
        const string ReverseRoad = "{\"place_id\":328859177,\"licence\":\"Data © OpenStreetMap contributors, ODbL 1.0. http://osm.org/copyright\",\"osm_type\":\"way\",\"osm_id\":39055279,\"lat\":\"43.5933100\",\"lon\":\"-110.8481497\",\"category\":\"highway\",\"type\":\"service\",\"place_rank\":27,\"importance\":0.04004000076295108,\"addresstype\":\"road\",\"name\":\"South Pass Traverse\",\"display_name\":\"South Pass Traverse, Teton Village, Teton County, Wyoming, 83025, United States\",\"address\":{\"road\":\"South Pass Traverse\",\"village\":\"Teton Village\",\"county\":\"Teton County\",\"state\":\"Wyoming\",\"ISO3166-2-lvl4\":\"US-WY\",\"postcode\":\"83025\",\"country\":\"United States\",\"country_code\":\"us\"},\"boundingbox\":[\"43.5932783\",\"43.5976395\",\"-110.8482154\",\"-110.8384579\"]}";
        const string ReverseCounty = "{\"place_id\":327839377,\"licence\":\"Data © OpenStreetMap contributors, ODbL 1.0. http://osm.org/copyright\",\"osm_type\":\"relation\",\"osm_id\":1153347,\"lat\":\"47.0022547\",\"lon\":\"-122.2117983\",\"category\":\"boundary\",\"type\":\"administrative\",\"place_rank\":12,\"importance\":0.553812912030847,\"addresstype\":\"county\",\"name\":\"Pierce County\",\"display_name\":\"Pierce County, Washington, United States\",\"address\":{\"county\":\"Pierce County\",\"state\":\"Washington\",\"ISO3166-2-lvl4\":\"US-WA\",\"country\":\"United States\",\"country_code\":\"us\"},\"boundingbox\":[\"46.7287986\",\"47.4038582\",\"-122.8529952\",\"-121.3746650\"]}";

        /// <summary>A clock that only moves when someone waits on it.</summary>
        sealed class FakeClock : IPickerClock
        {
            long _ticks;
            public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
            public TaskCompletionSource<bool> Hold;
            public Task Delay(TimeSpan delay, CancellationToken ct) { Interlocked.Add(ref _ticks, delay.Ticks); return Hold?.Task ?? Task.CompletedTask; }
            public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
        }

        [Test]
        public async Task RequestsAreAtLeastOneSecondApart()
        {
            var clock = new FakeClock();
            var sent = new List<TimeSpan>();
            var client = new NominatimClient((url, ct) =>
            {
                lock (sent) sent.Add(clock.Now);
                return Task.FromResult(Encoding.UTF8.GetBytes(url.Contains("/reverse?") ? ReverseRoad : SearchFixture));
            }, clock);
            // Searches and name lookups fired at once share one gate.
            await Task.WhenAll(
                client.SearchAsync("a", CancellationToken.None), client.SearchAsync("b", CancellationToken.None),
                client.SearchAsync("c", CancellationToken.None), client.SearchAsync("d", CancellationToken.None),
                client.SuggestNameAsync(new GeoPoint(43.6, -110.8), CancellationToken.None));
            Assert.That(sent.Count, Is.EqualTo(5));
            Assert.That(sent[0], Is.EqualTo(TimeSpan.Zero), "the first request doesn't wait");
            for (int i = 1; i < sent.Count; i++)
                Assert.That(sent[i] - sent[i - 1], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)), $"request {i}");

            // After a quiet spell longer than the interval, the next request goes at once.
            clock.Advance(TimeSpan.FromSeconds(5));
            var before = clock.Now;
            await client.SearchAsync("f", CancellationToken.None);
            Assert.That(sent.Last(), Is.EqualTo(before));
        }

        [Test]
        public async Task AStaleNameLookupIsDroppedWithoutARequest()
        {
            // Waits at the gate only finish when the test says so, so later placements can overtake them.
            var clock = new FakeClock { Hold = new TaskCompletionSource<bool>() };
            int requests = 0;
            var release = new TaskCompletionSource<byte[]>();
            var client = new NominatimClient((url, ct) =>
                Interlocked.Increment(ref requests) == 1 ? release.Task : Task.FromResult(Encoding.UTF8.GetBytes(ReverseRoad)), clock);
            var first = client.SuggestNameAsync(new GeoPoint(43.59, -110.84), CancellationToken.None);   // in flight
            var second = client.SuggestNameAsync(new GeoPoint(43.60, -110.85), CancellationToken.None);  // superseded
            var third = client.SuggestNameAsync(new GeoPoint(43.61, -110.86), CancellationToken.None);   // newest
            clock.Hold.SetResult(true);
            release.SetResult(Encoding.UTF8.GetBytes(ReverseRoad));
            await Task.WhenAll(first, second, third);
            Assert.That(second.Result, Is.Null);
            Assert.That(third.Result, Is.EqualTo("Teton Village"));
            Assert.That(requests, Is.EqualTo(2), "the superseded lookup never reached the server");
        }

        [Test]
        public async Task SearchSendsAnEscapedUsOnlyQuery()
        {
            string seen = null;
            var client = new NominatimClient((url, ct) => { seen = url; return Task.FromResult(Encoding.UTF8.GetBytes(SearchFixture)); }, new FakeClock());
            var places = await client.SearchAsync("  Crystal Mountain & Co ", CancellationToken.None);
            Assert.That(seen, Does.StartWith("https://nominatim.openstreetmap.org/search?q=Crystal%20Mountain%20%26%20Co&"));
            Assert.That(seen, Does.Contain("countrycodes=us").And.Contain("format=jsonv2").And.Contain("limit=5"));
            Assert.That(places.Count, Is.EqualTo(1));
            Assert.That(places[0].Name, Is.EqualTo("Crystal Mountain"));
            Assert.That(places[0].Location.Latitude, Is.EqualTo(46.9327365));
            Assert.That(places[0].West, Is.EqualTo(-121.5089145));
            Assert.That(places[0].North, Is.EqualTo(46.9567407));
        }

        [TestCase("")]
        [TestCase("   ")]
        public void AnEmptySearchSendsNothing(string query)
        {
            int requests = 0;
            var client = new NominatimClient((url, ct) => { requests++; return Task.FromResult(new byte[0]); }, new FakeClock());
            Assert.ThrowsAsync<ArgumentException>(() => client.SearchAsync(query, CancellationToken.None));
            Assert.That(requests, Is.Zero);
        }

        [Test]
        public void TheGameIdentifiesItself()
        {
            Assert.That(MountainPlanner.Acquisition.IO.Http.Client.DefaultRequestHeaders.UserAgent.ToString(), Does.StartWith("SkiAreaDesignChallenge/"));
        }

        [Test]
        public void NamesPreferPlacesOverRoadsAndCounties()
        {
            Assert.That(NominatimClient.SuggestName(ReverseRoad), Is.EqualTo("Teton Village"));
            Assert.That(NominatimClient.SuggestName(ReverseCounty), Is.EqualTo("Pierce County"));
            Assert.That(NominatimClient.SuggestName("{\"name\":\"Rendezvous Peak\",\"category\":\"natural\",\"address\":{\"village\":\"Teton Village\"}}"), Is.EqualTo("Rendezvous Peak"));
            Assert.That(NominatimClient.SuggestName("{\"error\":\"Unable to geocode\"}"), Is.Null);
        }
    }

    public sealed class CoverageAndEstimateTests
    {
        // An S3 folder listing (trimmed), USGS bucket, 2026-10-02.
        const string Listing = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>prd-tnm</Name><Prefix>StagedProducts/Elevation/S1M/n24w12/</Prefix><KeyCount>3</KeyCount><MaxKeys>1000</MaxKeys><Delimiter>/</Delimiter><IsTruncated>false</IsTruncated><CommonPrefixes><Prefix>StagedProducts/Elevation/S1M/n24w12/n2400w1200/</Prefix></CommonPrefixes><CommonPrefixes><Prefix>StagedProducts/Elevation/S1M/n24w12/n2410w1260/</Prefix></CommonPrefixes><CommonPrefixes><Prefix>StagedProducts/Elevation/S1M/n24w12/n2430w1200/</Prefix></CommonPrefixes></ListBucketResult>";

        [Test]
        public void TileNamesGiveTheirBoxes()
        {
            Assert.That(CoverageIndex.ParseListing(Listing), Is.EqualTo(new[] { "n2400w1200", "n2410w1260", "n2430w1200" }));
            Assert.That(CoverageIndex.TileBox("n2400w1200"), Is.EqualTo(new AlbersBox(-1200000, 2390000, -1190000, 2400000)));
            Assert.That(CoverageIndex.FolderAt(-1195000, 2395000), Is.EqualTo("n24w12"));
        }

        [Test]
        public async Task TheS1mShareListsEachFolderOnce()
        {
            var urls = new List<string>();
            var index = new CoverageIndex((url, ct) => { urls.Add(url); return Task.FromResult(Encoding.UTF8.GetBytes(Listing)); });
            // Half inside n2400w1200 (published), half inside n2400w1210 (not).
            var box = new AlbersBox(-1205000, 2394000, -1195000, 2396000);
            Assert.That(await index.S1mShareAsync(box, CancellationToken.None), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(await index.S1mShareAsync(box, CancellationToken.None), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(urls, Is.EqualTo(new[] { "https://prd-tnm.s3.amazonaws.com/?list-type=2&delimiter=/&prefix=StagedProducts/Elevation/S1M/n24w12/&max-keys=1000" }));
        }

        // The three downloads measured in tools/acquire/README.md: within 20% on size and time.
        // Crystal Mountain's measured mix: 16% 1 m, 24% 3 m, 60% 10 m.
        [TestCase(43.593, -110.848, 2.0, 1.0, 1.0, 85, 40, 100)]
        [TestCase(43.593, -110.848, 5.0, 1.0, 1.0, 252, 69, 100)]
        [TestCase(46.93, -121.49, 5.0, 0.16, 0.40, 484, 101, 48)]
        public void TheEstimateMatchesMeasuredDownloads(double lat, double lon, double km, double s1m, double threeMetre, double mb, double seconds, int score)
        {
            var site = SiteSquare.Create(new GeoPoint(lat, lon), km);
            var e = SiteEstimator.Estimate(site, new CoverageShares { S1m = s1m, OneMetre = s1m, ThreeMetre = threeMetre, RingS1m = s1m, Known = true });
            Assert.That(e.Bytes / 1e6, Is.EqualTo(mb).Within(mb * 0.2));
            Assert.That(e.Seconds, Is.EqualTo(seconds).Within(seconds * 0.2));
            Assert.That(e.TerrainScore, Is.EqualTo(score).Within(1));
            Assert.That(e.IsRough, Is.False);
        }

        [Test]
        public void UnknownCoverageAssumesTenMetres()
        {
            var e = SiteEstimator.Estimate(SiteSquare.Create(new GeoPoint(43.593, -110.848), 3), new CoverageShares { S1m = 1, Known = false });
            Assert.That(e.TerrainScore, Is.EqualTo(TerrainQuality.Weight(TerrainSource.TenMetre)));
            Assert.That(e.IsRough, Is.True);
            Assert.That(SiteEstimator.Line(e), Does.StartWith("Terrain at least 30 · 100% 10 m · about "));
        }

        [Test]
        public void TheLineReadsPlainly()
        {
            var e = SiteEstimator.Estimate(SiteSquare.Create(new GeoPoint(46.93, -121.49), 5),
                new CoverageShares { S1m = 0.16, OneMetre = 0.16, ThreeMetre = 0.40, RingS1m = 0.16, Known = true });
            Assert.That(SiteEstimator.Line(e), Is.EqualTo("Terrain about 48 · 16% S1M 1 m · 24% 3 m · 60% 10 m · about 490 MB · about 2 min"));
        }

        [Test]
        public void TileUrlsFollowTheUsgsScheme()
        {
            Assert.That(MapTileSource.Url(BaseMap.Topo, 10, 164, 365), Is.EqualTo("https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/tile/10/365/164"));
            Assert.That(MapTileSource.Url(BaseMap.Imagery, 4, 1, 2), Does.Contain("USGSImageryOnly/MapServer/tile/4/2/1"));
        }

        [Test]
        public async Task TilesAreFetchedOnceThenServedFromMemory()
        {
            int requests = 0;
            var tiles = new MapTileSource((url, ct) => { requests++; return Task.FromResult(new byte[] { 1, 2, 3 }); });
            await tiles.FetchAsync(BaseMap.Topo, 10, 1, 2, CancellationToken.None);
            Assert.That(await tiles.FetchAsync(BaseMap.Topo, 10, 1, 2, CancellationToken.None), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(requests, Is.EqualTo(1));
        }
    }
}
