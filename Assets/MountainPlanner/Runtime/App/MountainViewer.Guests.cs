using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.12, the guests on the screen's side of the boundary. Two jobs, both small, because the crowd itself lives
    /// in the worker:
    /// - **What the submitter tells it (SI-21).** Each trail segment's rating, sign, width, area and a reference ski time
    ///   are worked out from the Domain's own ratings and footprints, on the background task that builds the network, and
    ///   travel in with the design. The worker never computes geometry.
    /// - **What it shows.** The Popularity cells on the Trails and Lifts lists (4.21's hooks), and a Guests section in
    ///   the developer panel until 4.17 builds the real tab.
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>GU-02: whether the panel has asked to watch a skier (so it can say when that one has gone home).</summary>
        bool _watching;

        // ================= the submitter (SI-21) =================

        /// <summary>
        /// SI-21: the trail facts for one network build, taken from the ratings the trail layer has already worked out.
        /// It is made on the main thread (the ratings are the layer's) and then only read, on the network task.
        /// </summary>
        sealed class TrailFacts : INetworkFacts
        {
            readonly Dictionary<ulong, Entry> _byTrail;

            struct Entry
            {
                public TrailRating.Result Result;
                public byte TrueBand;
                public byte PostedBand;
                public int WidthCm;
                public int LengthMm;
            }

            public TrailFacts(DesignDocument doc, Func<TrailId, TrailMapView.Facts?> factsOf, TrailSections sections,
                              Func<TrailId, double> areaOf)
            {
                _byTrail = new Dictionary<ulong, Entry>(doc.Trails.Count);
                foreach (var t in doc.Trails)
                {
                    if (t.Stage != WorkStage.Built) continue;
                    var f = factsOf(t.Id);
                    if (f == null) continue;
                    var result = f.Value.Result;
                    var band = result != null ? result.Band : SlopeBands.Of(f.Value.Rating);
                    // GU-17: the sign the player posted on the trail this run belongs to, through the sections.
                    var posted = sections?.TrailOf(t.Id)?.Posted ?? t.Posted;
                    int lengthMm = (int)Math.Max(1, Math.Round(f.Value.LengthMetres * 1000));
                    // GU-07: no width is stored anywhere, so a run's mean is its footprint's area over its length and
                    // every segment of it reads that. A per-station width would be a Domain change, not a guess here.
                    double areaM2 = areaOf(t.Id);
                    int widthCm = f.Value.LengthMetres > 1 ? (int)Math.Round(areaM2 / f.Value.LengthMetres * 100) : 0;
                    _byTrail[t.Id.Value] = new Entry
                    {
                        Result = result,
                        TrueBand = (byte)band,
                        PostedBand = posted.HasValue ? (byte)posted.Value : TrailEdgeFacts.NoPosting,
                        WidthCm = widthCm > 0 ? widthCm : 3048,
                        LengthMm = lengthMm,
                    };
                }
            }

            public bool TrailSegment(ulong trailId, int startMm, int endMm, out TrailEdgeFacts facts)
            {
                facts = default;
                if (!_byTrail.TryGetValue(trailId, out var e)) return false;
                int lengthMm = Math.Max(1, endMm - startMm);
                int areaM2 = (int)Math.Max(1, (long)lengthMm / 1000 * e.WidthCm / 100);
                facts = new TrailEdgeFacts(e.TrueBand, e.PostedBand, e.WidthCm, areaM2, SkiMs(e, SkiTimeTable.Reference, startMm, endMm, lengthMm),
                                           SkiMs(e, 0, startMm, endMm, lengthMm), SkiMs(e, 1, startMm, endMm, lengthMm),
                                           SkiMs(e, 2, startMm, endMm, lengthMm));
                return true;
            }

            /// <summary>
            /// GU-10 v3: a skier of <paramref name="skierBand"/>'s time over this piece, from the table the renderer also
            /// draws by (the expert's is the reference).
            /// </summary>
            static int SkiMs(in Entry e, byte skierBand, int startMm, int endMm, int lengthMm) =>
                SkiTimeTable.TotalMs(e.Result, e.TrueBand, skierBand, startMm, endMm, lengthMm);
        }

        /// <summary>SI-21: the trail facts for the design a network build is about to run over, or null before it is rated.</summary>
        INetworkFacts FactsFor(DesignDocument doc, TrailSections sections)
        {
            if (doc == null || _trailLayer == null) return null;
            return new TrailFacts(doc, id => _trailLayer.TryGetFacts(id, out var f) ? f : (TrailMapView.Facts?)null, sections,
                                  id => (_edits?.Earthwork?.DetailOf(id.Value) as TrailBuild)?.Footprint?.AreaSquareMetres ?? 0);
        }

        // ================= Popularity (4.21's cells) =================

        /// <summary>
        /// 4.21's Popularity column for the lifts, from the newest snapshot: a share of the busiest, or "—" before
        /// anybody has ridden. A lift's row is keyed by the lift's own id, so it needs no mapping.
        /// </summary>
        void UpdateGuestLive(SimSnapshot s)
        {
            if (Hud == null || s == null) return;
            var g = s.Guests;
            for (int i = 0; i < s.LiftCount && i < g.LiftPopularity.Length; i++)
                Hud.SetLiftPopularity(s.LiftIds[i], g.LiftPopularity[i]);
        }

        readonly Dictionary<ulong, OperatingStatus> _lineStatus = new Dictionary<ulong, OperatingStatus>();
        readonly Dictionary<ulong, int> _linePopularity = new Dictionary<ulong, int>();

        /// <summary>
        /// The trails' live Status and Popularity, **mapped from runs to the player's lines**.
        ///
        /// The Analysis list shows resolved trails — the lines the player drew and named, with their own
        /// <see cref="TrailLineId"/> — while the simulation and the network work in *runs*
        /// (<see cref="TrailId"/>, what an edge owns). The two ids are different hashes of different things, so a run's
        /// id never matches a row and every cell stayed on its placeholder: the Status column has read Closed since
        /// task 4.11 for that reason, and Popularity would have read "—" for ever.
        ///
        /// A line is only as open as its least open run (you can't ski a line whose middle is shut), and its Popularity
        /// is its busiest run's, because a skier going down the whole line passes through each of its runs in turn.
        /// </summary>
        void UpdateTrailLive(SimSnapshot s)
        {
            if (Hud == null || s == null || !s.TrailsKnown) return;
            _lineStatus.Clear();
            _linePopularity.Clear();
            var sections = _sections;
            var g = s.Guests;
            for (int k = 0; k < s.TrailCount; k++)
            {
                ulong run = s.TrailIds[k];
                // With no sections yet (the trail list isn't built), the run's own id is the best key there is.
                ulong line = sections?.TrailOf(new TrailId(run))?.Id.Value ?? run;
                var word = Word(s.TrailStatus[k]);
                _lineStatus[line] = _lineStatus.TryGetValue(line, out var was) ? LessOpen(was, word) : word;
                int pop = k < g.TrailPopularity.Length ? g.TrailPopularity[k] : -1;
                if (!_linePopularity.TryGetValue(line, out int best) || pop > best) _linePopularity[line] = pop;
            }
            foreach (var kv in _lineStatus) Hud.SetTrailLive(kv.Key, kv.Value);
            foreach (var kv in _linePopularity) Hud.SetTrailPopularity(kv.Key, kv.Value);
        }

        /// <summary>The less open of two words: a line is as open as its least open run.</summary>
        static OperatingStatus LessOpen(OperatingStatus a, OperatingStatus b) => Rank(a) <= Rank(b) ? a : b;

        static int Rank(OperatingStatus s) => s switch
        {
            OperatingStatus.Planned => 0,
            OperatingStatus.Closed => 1,
            OperatingStatus.OnHold => 2,
            OperatingStatus.Scheduled => 3,
            _ => 4,   // Open
        };

        // ================= the developer panel (until 4.17's tab) =================

        /// <summary>
        /// The F1 panel's Guests rows: the crowd and a crowd on demand (GU-15), the day's demand and what it is up
        /// against (GU-03), where everyone is, how the day added up (SI-10), what moved satisfaction (GU-13), what the
        /// mountain offers each band (GU-06, GU-07), and one skier to watch (GU-02).
        ///
        /// A row is a short label, its buttons, and one sentence in <paramref name="note"/>, which wraps inside the
        /// panel and pushes the rows below it down. Each row says one thing, so the sentence is short enough to read:
        /// the first cut of this panel had the whole day in one line and it ran off the screen.
        /// </summary>
        void DrawGuestRows(ref float y, float row, float x0, float w, Action<string> caption, Func<int, string, bool> button,
                           Func<float, int, string, float> note)
        {
            var s = _simNow;
            if (_sim == null || s == null) return;
            var g = s.Guests;

            // GU-01, GU-15, GU-18: who is here, and a crowd on demand for a look at the hill.
            y += row;
            caption("Guests");
            for (int a = 0; a < Pacing.Archetypes; a++)
                if (button(a, "+25 " + Colour(a)))
                    _sim.Send(SimCommand.Of(SimCommandKind.SpawnSkiers, 0, new SkierSpawn(a, 25)));
            y += note(y, 4, s.Phase == SeasonPhase.PreSeason
                ? "Pre-season: Start the season (above) before anybody comes"
                : $"{g.SkierCount} on the hill · reputation {g.HeadlineReputation / 10}% " +
                  $"(green {g.Reputation[0] / 10}%, blue {g.Reputation[1] / 10}%, black {g.Reputation[2] / 10}%, double {g.Reputation[3] / 10}%)");
            y += row;

            // GU-03: the day's crowd, and the capacity the player is building against it.
            caption("Demand");
            var demand = new System.Text.StringBuilder();
            int total = 0;
            for (int a = 0; a < Pacing.Archetypes; a++) total += g.Demand[a];
            demand.Append(total).Append(" today (");
            for (int a = 0; a < Pacing.Archetypes; a++)
                demand.Append(a == 0 ? "" : ", ").Append(Colour(a)).Append(' ').Append(g.Demand[a]).Append('/').Append(g.Arrived[a]);
            demand.Append(") · lifts for ").Append(g.LiftCapacity).Append(", terrain for ").Append(g.TerrainCapacity);
            demand.Append(" · rep ×").Append(Permille(g, 0, DemandLine.Reputation))
                  .Append(" day ×").Append(Permille(g, 0, DemandLine.DayType))
                  .Append(" snow ×").Append(Permille(g, 0, DemandLine.Conditions))
                  .Append(" powder ×").Append(Permille(g, 0, DemandLine.PowderPull))
                  .Append(" forecast ×").Append(Permille(g, 0, DemandLine.Forecast));
            y += note(y, 0, demand.ToString());
            y += row;

            // GU-01: where everyone is this moment.
            caption("On the hill");
            y += note(y, 0, $"{g.ByState[(int)GuestState.Arriving]} on their way · {g.ByState[(int)GuestState.Choosing]} deciding" +
                    $" · {g.ByState[(int)GuestState.Queued]} in line · {g.ByState[(int)GuestState.Riding]} riding up" +
                    $" · {g.ByState[(int)GuestState.Skiing]} skiing · {g.ByState[(int)GuestState.Walking]} walking");
            y += row;

            // SI-10: the day's ledger, which always balances (arrived = on the hill + left).
            caption("Day so far");
            y += note(y, 0, $"arrived {Total(g.Arrived)} · left {g.Left} · went home from a line {g.WentHome}" +
                    $" · stranded {g.Stranded} · came to a closed mountain {g.ArrivedClosed} · found it full {g.Capped}");
            y += row;

            // GU-13: what moved satisfaction today, and by how much.
            caption("Satisfaction");
            y += note(y, 0, Reasons(g));
            y += row;

            // GU-06, GU-07: what the mountain offers each band. A band with arrival places that offer it nothing has
            // part of the crowd standing about all day, which nothing else on this panel would show.
            caption("Network");
            var where = new System.Text.StringBuilder();
            if (g.Bases == 0)
                where.Append("Nobody can arrive: a place to arrive is a built lift's bottom station, and this mountain has none yet");
            else
            {
                where.Append(g.Bases).Append(g.Bases == 1 ? " place to arrive" : " places to arrive").Append(" (every built lift's bottom), ")
                     .Append(g.Nodes).Append(" stations · ");
                int shut = 0;
                for (int b = 0; b < Pacing.Bands; b++)
                {
                    where.Append(b == 0 ? "" : " · ").Append(Colour(b)).Append(' ');
                    if (g.UsableBases[b] == 0) { where.Append("nothing to ski"); shut++; }
                    else where.Append("skis home from ").Append(g.Reachable[b]).Append(" of ").Append(g.Nodes)
                              .Append(", arrives at ").Append(g.UsableBases[b]).Append(" of ").Append(g.Bases);
                }
                // Nothing is reachable with the ropes stopped, so at night the line above says "nothing to ski" for
                // every colour, which is the clock, not the gate. Say which it is.
                bool running = s.Phase == SeasonPhase.Season && s.Open
                               && s.SecondOfDay >= s.OpenSecond && s.SecondOfDay < s.StopSecond;
                if (shut > 0)
                    where.Append(running
                        ? " — a colour with nothing it may ski is turned away at the gate (GU-05), so that part of the crowd stays home"
                        : " — no lift is running at this hour, so nothing is reachable from anywhere; this line reads true once the mountain opens");
            }
            y += note(y, 0, where.ToString());
            y += row;

            // GU-02: one skier's day. Selecting one changes no result.
            caption("Watch a skier");
            // −1 asks the worker for whoever is next: it knows every serial on the hill, and the screen does not.
            if (button(0, g.SelectedSerial == 0 ? "Pick one" : "Next")) { _sim.Send(SimCommand.Of(SimCommandKind.InspectSkier, -1)); _watching = true; }
            if (g.SelectedSerial != 0 && button(1, "Stop")) { _sim.Send(SimCommand.Of(SimCommandKind.InspectSkier, 0)); _watching = false; }
            y += note(y, 2, g.SelectedSerial == 0
                // A skier skis its 3–5 laps and goes home (GU-06), taking the watch with it. Say so, or the row looks
                // broken: it read "Pick one" again two hours after a skier was picked, with 217 people on the hill.
                ? _watching
                    ? "The one you were watching has finished its laps and gone home — Pick one follows somebody else"
                    : "Pick one to follow its band, its party, its laps, why it chose what it did and where it has been"
                : $"#{g.SelectedSerial} {Colour(Math.Min(3, (int)g.SelectedBand))}, ability {g.SelectedAbility / 10}%," +
                  $" party of {g.SelectedPartySize}, lap {g.SelectedLaps}/{g.SelectedStayLaps}," +
                  $" waited {g.SelectedWaitSeconds} s, happy {g.SelectedSatisfaction / 10}%, {State(g.SelectedState)}");
            if (g.SelectedSerial != 0)
            {
                y += row;
                caption("Why, and where");
                y += note(y, 0, $"chose it for {Why(g)} · been: {Record(g)}");
            }
        }

        static int Total(int[] v) { int n = 0; for (int i = 0; i < v.Length; i++) n += v[i]; return n; }

        /// <summary>GU-05: a band by the trail colour it skis, which is what the panel has room for.</summary>
        static string Colour(int band) => band == 0 ? "green" : band == 1 ? "blue" : band == 2 ? "black" : "double";

        static string Permille(GuestView g, int archetype, DemandLine line)
        {
            int v = g.Stack[archetype * SimWorld.DemandLines + (int)line];
            return (v / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string State(byte state) => ((GuestState)state) switch
        {
            GuestState.Arriving => "on its way",
            GuestState.Choosing => "deciding",
            GuestState.Queued => "in a line",
            GuestState.Riding => "riding up",
            GuestState.Skiing => "skiing",
            GuestState.Walking => "walking",
            GuestState.Dwelling => "stopped",
            _ => "gone",
        };

        static string Reasons(GuestView g)
        {
            var sb = new System.Text.StringBuilder();
            for (var r = SatisfactionReason.Wait; r <= SatisfactionReason.Stranded; r++)
            {
                long sum = 0;
                for (int a = 0; a < Pacing.Archetypes; a++) sum += g.Reasons[a * SimWorld.Reasons + (int)r];
                if (sum == 0) continue;
                if (sb.Length > 0) sb.Append("  ·  ");
                sb.Append(r).Append(' ').Append(sum > 0 ? "+" : "").Append(sum / 1000.0);
            }
            return sb.Length == 0 ? "nothing yet" : sb.ToString();
        }

        static string Why(GuestView g)
        {
            var sb = new System.Text.StringBuilder();
            for (var f = ChoiceFactor.Fit; f <= ChoiceFactor.Wait; f++)
            {
                int v = g.SelectedWhy[(int)f];
                if (v == 0) continue;
                if (sb.Length > 0) sb.Append(" × ");
                sb.Append(f).Append(' ').Append((v / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "no factors yet" : sb.ToString();
        }

        static string Record(GuestView g)
        {
            var sb = new System.Text.StringBuilder();
            int from = Math.Max(0, g.SelectedRecordCount - 6);
            for (int k = from; k < g.SelectedRecordCount; k++)
            {
                if (sb.Length > 0) sb.Append(", ");
                ushort seg = g.SelectedRecordSegment[k];
                bool lift = (seg & GuestView.LiftMark) != 0;
                sb.Append(lift ? "lift " : "run ").Append(seg & ~GuestView.LiftMark);
            }
            return sb.ToString();
        }

        /// <summary>
        /// What the guests' network actually looks like, once per build: how many edges of each kind there are, and for
        /// every lift whether anything can be skied *into* its bottom station. A skier can only lap a mountain whose
        /// trails reach its lifts, and when they don't, nothing else on the screen says so.
        /// </summary>
        void LogGuestNetwork(NetworkGraph graph)
        {
            if (graph == null) return;
            int lifts = 0, segments = 0, paths = 0;
            foreach (var e in graph.Edges)
            {
                if (e.Kind == EdgeKind.Lift) lifts++;
                else if (e.Kind == EdgeKind.TrailSegment) segments++;
                else paths++;
            }
            var line = new System.Text.StringBuilder();
            line.Append("[GuestNetwork] ").Append(graph.Nodes.Count).Append(" nodes, ").Append(segments).Append(" trail segments, ")
                .Append(paths).Append(" paths, ").Append(lifts).Append(" lift edges");
            var doc = Design?.Current.Document;
            if (doc != null)
                foreach (var l in doc.Lifts)
                {
                    if (l.Stage != WorkStage.Built) continue;
                    ulong bottom = graph.StationOf(l.Id, top: false), top = graph.StationOf(l.Id, top: true);
                    int intoBottom = 0, outOfTop = 0;
                    foreach (var e in graph.Edges)
                    {
                        if (e.Kind == EdgeKind.Lift) continue;
                        if (e.To == bottom) intoBottom++;
                        if (e.From == top) outOfTop++;
                    }
                    line.Append(System.Environment.NewLine).Append("  ").Append(l.Name).Append(": ").Append(intoBottom).Append(" ski in at the bottom, ")
                        .Append(outOfTop).Append(" ski out at the top");
                }
            Debug.Log(line.ToString());
        }

        // ================= -guestshot: a look at the guests, for a review =================

        /// <summary>
        /// <c>-guestshot &lt;folder&gt;</c> (task 4.12): opens the mountain, lets a morning's crowd arrive, and takes the
        /// pictures a review needs — the developer panel with real numbers in it, and the Trails and Lifts lists with
        /// their live Status, Wait and Popularity. It exists because the panel and those columns are the only places
        /// 4.12 shows itself (the dots are 4.13), so they have to be *looked at*, not just asserted.
        /// </summary>
        void StartGuestShots(string[] args)
        {
            int i = Array.IndexOf(args, "-guestshot");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(GuestShots(args[i + 1]));
        }

        System.Collections.IEnumerator GuestShots(string dir)
        {
            // Unity resolves a relative capture path against the player's own folder and never makes the directory, so
            // a relative one writes nothing and says nothing. Resolve it here, where the folder is made.
            BeginScriptedRun();
            dir = System.IO.Path.GetFullPath(dir);
            System.IO.Directory.CreateDirectory(dir);
            // Any mountain, any save: the edit session is *asked for* here rather than waited for, because on a save
            // nothing has edited this run nobody else asks for it and the wait would never end; and the wait is
            // bounded, so a capture on an area that never finishes opening says so instead of hanging all night.
            bool ready = false;
            for (int k = 0; k < 120_000 && !ready; k++)
            {
                if (_resort != null && _resort.CoverReady.IsCompleted) EnsureTerrainEdits();
                ready = _resort != null && _resort.CoverReady.IsCompleted && _edits != null && !WorkBusy
                        && _sections != null && !NetworkBusy && _sim != null && _simNow != null;
                if (!ready) yield return null;
            }
            if (!ready) Debug.LogWarning("[GuestShots] the area never finished opening; taking the pictures anyway");
            for (int k = 0; k < 60; k++) yield return null;
            _help = true;                                        // the developer panel
            SendSim(SimCommandKind.SetSpeed, 4);                 // 8×, so a morning takes a couple of minutes to capture

            // Open the mountain and land on the next morning (what "Start the season" does).
            SendSim(SimCommandKind.SetMountainOpen, 1);
            SendSim(SimCommandKind.SkipToMorning);
            // The skip moves the clock itself, so wait for it to finish before timing anything from the clock.
            for (int k = 0; k < 600 && (_simNow == null || _simNow.Skip != SkipKind.None); k++) yield return null;
            for (int k = 0; k < 120; k++) yield return null;
            yield return WaitUntilFaceSecondOfDay(9 * 3600);     // mid-morning, with the crowd coming in

            yield return GuestShot(dir, "1-opening");
            yield return WaitUntilFaceSecondOfDay(11 * 3600);    // the crowd is in and the lines have formed
            yield return GuestShot(dir, "2-mid-morning");
            yield return TrafficShots(dir, "2e-traffic");   // task 4.13 PR C (SN-31)
            yield return LineShots(dir);   // task 4.13: the busiest line close up, then one of its skiers picked and followed

            // The lists, with their live Status, Wait and Popularity.
            _help = false;
            Hud.SetAnalysisTab("trails");
            Hud.SetAnalysis(true);
            yield return GuestShot(dir, "3-trails");
            Hud.SetAnalysisTab("lifts");
            yield return GuestShot(dir, "4-lifts");
            Hud.SetAnalysis(false);

            // GU-15 and GU-02: a crowd on demand, and one skier watched.
            _help = true;
            _sim.Send(SimCommand.Of(SimCommandKind.SpawnSkiers, 0, new SkierSpawn(0, 25)));
            _sim.Send(SimCommand.Of(SimCommandKind.InspectSkier, -1));   // whoever is next, the worker's pick
            _watching = true;
            yield return WaitUntilFaceSecondOfDay(13 * 3600);
            _sim.Send(SimCommand.Of(SimCommandKind.InspectSkier, -1));   // the first pick may have gone home by now
            for (int k = 0; k < 60; k++) yield return null;
            yield return GuestShot(dir, "5-watching");

            // Demo findings 1-2 (owner, 2026-11-10): late in the day the longest line, close up, stands in rows.
            _watching = false;
            SelectSkier(0);
            // A rush, so the lines get long: 600 more skiers over the bands the mountain has runs for (GU-05 sends the
            // others home at the gate).
            yield return WaitUntilFaceSecondOfDay(14 * 3600);
            var gv = _simNow?.Guests;
            if (gv != null)
            {
                int open = 0;
                for (int b = 0; b < Pacing.Bands; b++) if (gv.UsableBases[b] > 0) open++;
                for (int b = 0; b < Pacing.Bands && open > 0; b++)
                    if (gv.UsableBases[b] > 0) _sim.Send(SimCommand.Of(SimCommandKind.SpawnSkiers, 0, new SkierSpawn(b, 600 / open)));
            }
            yield return WaitUntilFaceSecondOfDay(15 * 3600 + 30 * 60);
            yield return LateLineShot(dir);
            yield return TrafficShots(dir, "5d-traffic-late");

            yield return WaitUntilFaceSecondOfDay(16 * 3600 + 45 * 60);   // past the stop: the day is counted, the hill clears
            yield return GuestShot(dir, "6-after-the-stop");
            yield return TrafficShots(dir, "6b-traffic-the-day");
            Debug.Log("[GuestShots] done → " + dir);
            Application.Quit();
        }

        /// <summary>
        /// Task 4.13: the lift with the longest line framed from close by (its dots in the line, as wide as its chair),
        /// then the skier at the front of it clicked and followed, with its card.
        /// </summary>
        System.Collections.IEnumerator LineShots(string dir)
        {
            var s = _simNow;
            var design = _skierDesign;
            var doc = _skierDoc;
            if (s == null || design == null || doc == null || Camera == null) yield break;
            int best = -1, most = 0;
            for (int i = 0; i < s.LiftCount; i++) if (s.LineCount[i] > most) { most = s.LineCount[i]; best = i; }
            if (best < 0) { Debug.Log("[GuestShots] no line anywhere: no line shots"); yield break; }
            var lift = doc.FindLift(new MountainPlanner.Domain.Design.LiftId(s.LiftIds[best]));
            if (lift == null) yield break;
            var b = MountainPlanner.Presentation.Design.DesignFrame.ToLocal(lift.Bottom);
            float y = ReadoutHeight(b.x, b.y);
            _help = false;   // the panel covers the middle of the screen
            Camera.Frame(new Vector3(b.x, float.IsNaN(y) ? 0 : y, b.y), 90f);
            for (int k = 0; k < 90; k++) yield return null;
            Debug.Log($"[GuestShots] line shot: {lift.Name}, {most} in line, {_skiers?.Lines?.Describe(DesignLiftIndex(s.LiftIds[best])) ?? "no layout"}, lines grown {_skiers?.LinesGrown ?? 0} times, {_skiers?.Drawn ?? 0} dots drawn");
            var camT = Camera.transform.position;
            int logged = 0;
            for (int i = 0; i < _skiers.Count && logged < 6; i++)
            {
                _skiers.Describe(i, out var dot, out var sp);
                if (dot.w < 0) continue;
                logged++;
                Debug.Log($"[GuestShots] dot {i}: world ({dot.x:F1}, {dot.y:F1}, {dot.z:F1}) packed {dot.w} screen ({sp.x:F0}, {sp.y:F0}, w {sp.z:F1}); " +
                          $"ground {ReadoutHeight(dot.x, dot.z):F1}; camera ({camT.x:F0}, {camT.y:F0}, {camT.z:F0})");
            }
            yield return GuestShot(dir, "2b-line");
            yield return PickTargetCheck();
            uint front = 0;
            if (_skiers != null)
            {
                int index = DesignLiftIndex(s.LiftIds[best]);
                front = _skiers.FrontOfLine(index);
            }
            if (front == 0 && _skiers != null)
            {
                // No line to pick from: a skier on a run instead (the first dot drawn that is skiing).
                for (int i = 0; i < _skiers.Count && front == 0; i++)
                    if (_skiers.ScreenPointOf(i, out _, out uint any) && _skiers.TryFind(any, out _, out var rec) && rec.State == (byte)GuestState.Skiing) front = any;
            }
            if (front == 0) { _help = true; yield break; }
            SelectSkier(front);
            if (_skiers.TryFind(front, out var spot, out _)) Camera.Frame(spot, 120f);
            StartFollowingSkier();
            for (int k = 0; k < 120; k++) yield return null;
            yield return GuestShot(dir, "2c-picked");
            for (int k = 0; k < 600; k++) yield return null;   // a while later at 8x: riding up, or off at the top
            yield return GuestShot(dir, "2d-followed");
            SelectSkier(0);
            _help = true;
        }

        /// <summary>The longest line late in the day, framed close (its places logged for every lift).</summary>
        System.Collections.IEnumerator LateLineShot(string dir)
        {
            var s = _simNow;
            var doc = _skierDoc;
            if (s == null || doc == null || Camera == null) yield break;
            int best = -1, most = 0;
            for (int i = 0; i < s.LiftCount; i++)
            {
                int index = DesignLiftIndex(s.LiftIds[i]);
                var lift = doc.FindLift(new MountainPlanner.Domain.Design.LiftId(s.LiftIds[i]));
                Debug.Log($"[GuestShots] late line: {lift?.Name ?? "?"}, {s.LineCount[i]} in line, {_skiers?.Lines?.Describe(index) ?? "no layout"}");
                if (s.LineCount[i] > most) { most = s.LineCount[i]; best = i; }
            }
            if (best < 0) yield break;
            var l = doc.FindLift(new MountainPlanner.Domain.Design.LiftId(s.LiftIds[best]));
            if (l == null) yield break;
            var b = MountainPlanner.Presentation.Design.DesignFrame.ToLocal(l.Bottom);
            float y = ReadoutHeight(b.x, b.y);
            _help = false;
            Camera.Frame(new Vector3(b.x, float.IsNaN(y) ? 0 : y, b.y), 150f);
            for (int k = 0; k < 120; k++) yield return null;
            Debug.Log($"[GuestShots] late line shot: {l.Name}, {most} in line, {_skiers?.Lines?.Describe(DesignLiftIndex(s.LiftIds[best])) ?? "no layout"}, lines grown {_skiers?.LinesGrown ?? 0} times");
            yield return GuestShot(dir, "5b-late-line");
            LogLinePlaces(DesignLiftIndex(s.LiftIds[best]), b, l.Name);
            yield return LineVisibility(DesignLiftIndex(s.LiftIds[best]), l.Name);
            bool onTop = _dotsOnTop;
            _dotsOnTop = true;   // where the whole line stands, trees or not
            yield return GuestShot(dir, "5b-late-line-ontop");
            _dotsOnTop = onTop;
            // The back of that line, picked: its card says its place, and it stands in a row.
            uint tail = _skiers?.LastInLine(DesignLiftIndex(s.LiftIds[best])) ?? 0;
            if (tail != 0)
            {
                SelectSkier(tail);
                if (_skiers.TryFind(tail, out var at, out _)) Camera.Frame(at, 70f);
                for (int k = 0; k < 120; k++) yield return null;
                yield return GuestShot(dir, "5c-late-tail");
                SelectSkier(0);
            }
            _help = true;
        }

        /// <summary>
        /// How much of one lift's line the player can see: its queued dots on screen, and how many of them show (the
        /// frame with the dots shown and hidden differs there; a tree or a ridge in front hides one). Paused; logged.
        /// </summary>
        System.Collections.IEnumerator LineVisibility(int lift, string name)
        {
            var layer = _skiers;
            if (layer == null || _sim == null || lift < 0) yield break;
            SendSim(SimCommandKind.Pause);
            for (int k = 0; k < 600 && (_simNow == null || !_simNow.Paused); k++) yield return null;
            for (int k = 0; k < 10; k++) yield return null;
            yield return new WaitForEndOfFrame();
            var shown = ScreenCapture.CaptureScreenshotAsTexture();
            var targets = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i < layer.Count; i++)
            {
                if (!layer.ScreenPointOf(i, out var at, out uint serial) || PointerOverUi(at)) continue;
                if (!layer.TryFind(serial, out _, out var rec) || rec.State != (byte)GuestState.Queued || rec.Index != lift) continue;
                if (at.x >= 2 && at.y >= 2 && at.x < shown.width - 2 && at.y < shown.height - 2) targets.Add(at);
            }
            _skiersShown = false;
            for (int k = 0; k < 3; k++) yield return null;
            yield return new WaitForEndOfFrame();
            var hidden = ScreenCapture.CaptureScreenshotAsTexture();
            _skiersShown = true;
            int seen = 0;
            foreach (var at in targets)
            {
                Color a = shown.GetPixel((int)at.x, (int)at.y), b = hidden.GetPixel((int)at.x, (int)at.y);
                if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 0.15f) seen++;
            }
            Destroy(shown);
            Destroy(hidden);
            Debug.Log($"[GuestShots] line visibility: {name}, {targets.Count} in line on screen, {seen} of them show (the rest behind trees or ground)");
            SendSim(SimCommandKind.Resume);
        }

        /// <summary>Where a lift's line places are (from its bottom terminal, and on screen), and its queued dots (logs).</summary>
        void LogLinePlaces(int lift, Vector2 bottom, string name)
        {
            var lines = _skiers?.Lines;
            if (lines == null || lift < 0 || Camera == null) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            var sb = new System.Text.StringBuilder($"[GuestShots] places of {name} ({lines.Capacity(lift)}):");
            foreach (int k in new[] { 0, 1, 4, 10, 20, 40, 60, 80, 100, 120, 137, 200, 300 })
            {
                if (!lines.PlaceOf(lift, k, out var p)) break;
                var sp = cam != null ? cam.WorldToScreenPoint(p) : Vector3.zero;
                sb.Append($" #{k} ({p.x - bottom.x:F0}, {p.z - bottom.y:F0}) screen ({sp.x:F0}, {sp.y:F0})");
            }
            Debug.Log(sb.ToString());
            var q = new System.Text.StringBuilder($"[GuestShots] queued at {name}:");
            int shown = 0, hidden = 0;
            for (int i = 0; i < _skiers.Count; i++)
            {
                _skiers.Describe(i, out var dot, out var sp);
                if (!_skiers.RecordAt(i, out var rec) || rec.State != (byte)GuestState.Queued || rec.Index != lift) continue;
                if (dot.w < 0) hidden++; else shown++;
                if (shown + hidden <= 12) q.Append($" rank {rec.Rank} dot ({dot.x - bottom.x:F0}, {dot.z - bottom.y:F0}, w {dot.w:F0}) screen ({sp.x:F0}, {sp.y:F0}, {sp.z:F0});");
            }
            q.Append($" {shown} drawn, {hidden} hidden");
            Debug.Log(q.ToString());
        }

        int DesignLiftIndex(ulong id)
        {
            var d = _skierDesign;
            if (d == null) return -1;
            for (int i = 0; i < d.Lifts.Length; i++) if (d.Lifts[i].Id == id) return i;
            return -1;
        }

        /// <summary>Waits for the face clock to reach a time of day, however fast the preset is running.</summary>
        System.Collections.IEnumerator WaitUntilFaceSecondOfDay(int secondOfDay)
        {
            for (int frames = 0; frames < 200_000; frames++)
            {
                if (_simNow != null && _simNow.SecondOfDay >= secondOfDay) yield break;
                // A stopped worker never reaches the hour: take the rest of the pictures (the panel says why) and go.
                if (_sim?.Failure != null) { Debug.LogError("[GuestShots] the simulation stopped: " + _sim.Failure); yield break; }
                yield return null;
            }
        }

        /// <summary>
        /// Finding 3 (owner, 2026-11-10): a click lands on the dot as drawn. Paused, with the dots drawn over everything (so
        /// nothing hides one), the same frame is taken with the dots shown and hidden; at each of up to 20 dots' click
        /// targets the two must differ, which they only do where a dot is drawn. Logged; under 90% says FAIL.
        /// </summary>
        System.Collections.IEnumerator PickTargetCheck()
        {
            var layer = _skiers;
            if (layer == null || _sim == null) yield break;
            SendSim(SimCommandKind.Pause);
            for (int k = 0; k < 600 && (_simNow == null || !_simNow.Paused); k++) yield return null;
            bool onTop = _dotsOnTop;
            _dotsOnTop = true;
            for (int k = 0; k < 10; k++) yield return null;
            yield return new WaitForEndOfFrame();
            var shown = ScreenCapture.CaptureScreenshotAsTexture();
            var targets = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i < layer.Count && targets.Count < 20; i++)
                if (layer.ScreenPointOf((i * 7919) % Math.Max(1, layer.Count), out var at, out _) && !PointerOverUi(at)
                    && at.x >= 2 && at.y >= 2 && at.x < shown.width - 2 && at.y < shown.height - 2) targets.Add(at);
            _skiersShown = false;
            for (int k = 0; k < 3; k++) yield return null;
            yield return new WaitForEndOfFrame();
            var hidden = ScreenCapture.CaptureScreenshotAsTexture();
            _skiersShown = true;
            _dotsOnTop = onTop;
            int hits = 0;
            foreach (var at in targets)
            {
                Color a = shown.GetPixel((int)at.x, (int)at.y), b = hidden.GetPixel((int)at.x, (int)at.y);
                if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 0.15f) hits++;
            }
            Destroy(shown);
            Destroy(hidden);
            bool pass = targets.Count > 0 && hits >= targets.Count * 0.9f;
            Debug.Log($"[GuestShots] pick targets: {hits} of {targets.Count} land on their drawn dot → {(pass ? "PASS" : "FAIL")}");
            SendSim(SimCommandKind.Resume);
        }

        System.Collections.IEnumerator GuestShot(string dir, string name)
        {
            float until = UnityEngine.Time.unscaledTime + 1.2f;
            while (UnityEngine.Time.unscaledTime < until) yield return null;
            string path = System.IO.Path.Combine(dir, name + ".png");
            UnityEngine.ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            var g = _simNow?.Guests;
            Debug.Log($"[GuestShots] {path}  ({g?.SkierCount ?? 0} on the hill)");
        }
    }
}
