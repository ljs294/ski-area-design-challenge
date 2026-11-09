using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.11, lift operations on screen: the built network goes to the simulation with the design it was built for
    /// (LO-10's way down), and twice a second the Lifts and Trails lists take each lift's Status, line and wait (LO-04 v1)
    /// and each trail's Status from the newest snapshot (UI from snapshots at a bounded rate, never per tick). The F1 panel
    /// has the feeder and the gust for the demo (LO-13, LO-11) until guests (4.12) and weather (4.05) exist.
    /// </summary>
    public sealed partial class MountainViewer
    {
        NetworkGraph _simGraph;
        INetworkFacts _simFacts;
        DesignDocument _simGraphDoc;
        DesignSnapshot _simSent;
        float _nextLiftLive;
        const float LiftLiveSeconds = 0.5f;

        /// <summary>The built network for <paramref name="doc"/>, when it's the one last built (else null: the stand-in way down).</summary>
        NetworkGraph SimGraphFor(DesignDocument doc) => doc != null && ReferenceEquals(doc, _simGraphDoc) ? _simGraph : null;

        /// <summary>SI-21: the trail facts that belong with that graph (GU-07, GU-10), or null.</summary>
        INetworkFacts SimFactsFor(DesignDocument doc) => doc != null && ReferenceEquals(doc, _simGraphDoc) ? _simFacts : null;

        /// <summary>A network build finished: the simulation gets it with the design it belongs to, if that's the one it runs.</summary>
        void NetworkForSimulation(DesignDocument doc, NetworkGraph graph, INetworkFacts facts = null)
        {
            _simGraph = graph;
            _simFacts = facts;
            _simGraphDoc = doc;
            LogGuestNetwork(graph);
            if (_sim != null && _simSent.Document != null && ReferenceEquals(_simSent.Document, doc)) SendDesign(_simSent);
        }

        /// <summary>LO-04 v1, the Status words (4.21's hooks): from the newest snapshot, twice a second.</summary>
        void UpdateLiftLive()
        {
            var s = _simNow;
            if (Hud == null || s == null || Time.unscaledTime < _nextLiftLive) return;
            _nextLiftLive = Time.unscaledTime + LiftLiveSeconds;
            // The wait in real seconds at the speed the clock runs (paused: at 1×).
            var speeds = Pacing.Default.Speeds;
            int factor = s.Paused || s.Speed < 1 || s.Speed > speeds.Length ? 1 : speeds[s.Speed - 1];
            for (int i = 0; i < s.LiftCount; i++)
                Hud.SetLiftLive(s.LiftIds[i], new MountainHud.LiftLive(Word(s.LiftStatus[i]), s.LineCount[i], s.WaitMs[i] / 1000.0 / factor));
            UpdateTrailLive(s);   // the trails' Status and Popularity, mapped from runs to the player's lines
            UpdateGuestLive(s);   // task 4.12: the lifts' Popularity cells
            Hud.FlushLive();
        }

        static OperatingStatus Word(LiftOpStatus s) => s switch
        {
            LiftOpStatus.Open => OperatingStatus.Open,
            LiftOpStatus.OnHold => OperatingStatus.OnHold,
            LiftOpStatus.Scheduled => OperatingStatus.Scheduled,
            LiftOpStatus.Planned => OperatingStatus.Planned,
            _ => OperatingStatus.Closed,   // Closed, and Downloading (closed to boarding, LO-10)
        };

        /// <summary>
        /// The F1 panel's lift operations rows (demo 91): a lift picker (every built lift, in turn), its line, feeder and
        /// schedule, and the gust with the lifts it holds. Stand-ins until guests (4.12) and weather (4.05) exist.
        /// </summary>
        void DrawLiftOpsRows(ref float y, float row, float x0, float w, Action<string> caption, Func<int, string, bool> button,
                             Func<float, int, string, float> note)
        {
            var s = _simNow;
            if (_sim == null || s == null) return;
            var history = Design;
            var doc = history?.Current.Document;
            // The picked lift: the one chosen with "Lift ▸", else the first built one.
            int pick = -1, built = 0;
            for (int i = 0; i < s.LiftCount; i++)
            {
                if (s.LiftStatus[i] == LiftOpStatus.Planned) continue;
                built++;
                if (pick < 0 || s.LiftIds[i] == _opsLiftId) pick = i;
            }
            Lift lift = null;
            if (doc != null && pick >= 0)
                foreach (var l in doc.Lifts) if (l.Id.Value == s.LiftIds[pick]) lift = l;

            y += row;
            caption("Lift ops");
            string text;
            int cols = pick < 0 ? 0 : s.Phase == SeasonPhase.PreSeason ? 1 : 4;   // where the row's buttons end, so the note starts there
            if (pick < 0) text = "Build a lift and a trail down from it";
            else if (s.Phase == SeasonPhase.PreSeason)
            {
                text = "Pre-season: Start the season opens the mountain and skips to the next opening (needs a built lift and trail)";
                if (button(0, "Start the season"))
                {
                    if (!s.Open) SendSim(SimCommandKind.SetMountainOpen, 1);
                    SendSim(SimCommandKind.SkipToMorning);
                }
            }
            else
            {
                ulong id = s.LiftIds[pick];
                string name = _opsAll ? "All lifts" : lift?.Name ?? "Lift";
                if (button(0, (name.Length > 12 ? name.Substring(0, 11) + "…" : name) + (built > 1 ? " ▸" : "")) && built > 1)
                {
                    // Each built lift in turn, then All lifts, then the first again.
                    int next = -1;
                    if (!_opsAll)
                        for (int j = pick + 1; j < s.LiftCount; j++)
                            if (s.LiftStatus[j] != LiftOpStatus.Planned) { next = j; break; }
                    if (next >= 0) _opsLiftId = s.LiftIds[next];
                    else if (!_opsAll) _opsAll = true;
                    else
                    {
                        _opsAll = false;
                        for (int j = 0; j < s.LiftCount; j++) if (s.LiftStatus[j] != LiftOpStatus.Planned) { _opsLiftId = s.LiftIds[j]; break; }
                    }
                }
                bool feeding = _opsAll ? AllFeeding(s) : _feeding.Contains(id);
                if (button(1, _opsAll ? "+300 each" : "+300 in line"))
                    for (int i = 0; i < s.LiftCount; i++)
                    {
                        if (s.LiftStatus[i] == LiftOpStatus.Planned || (!_opsAll && i != pick)) continue;
                        ulong lid = s.LiftIds[i];
                        _sim.Send(SimCommand.Of(SimCommandKind.FeedLine, 0, new LiftFeed(lid, 300, _feeding.Contains(lid) ? 40 : 0)));
                    }
                if (button(2, feeding ? "Feeder: 40/min" : "Feeder: off"))
                    for (int i = 0; i < s.LiftCount; i++)
                    {
                        if (s.LiftStatus[i] == LiftOpStatus.Planned || (!_opsAll && i != pick)) continue;
                        ulong lid = s.LiftIds[i];
                        if (feeding) _feeding.Remove(lid); else _feeding.Add(lid);
                        _sim.Send(SimCommand.Of(SimCommandKind.FeedLine, 0, new LiftFeed(lid, 0, feeding ? 0 : 40)));
                    }
                // LO-09: the schedule (a design edit, undoable) until the lift card has its control (4.11 PR B's mockup).
                if (!_opsAll && lift != null && button(3, lift.Schedule == LiftSchedule.PeakDays ? "Peak days only" : "Daily"))
                    history.Submit(new SetLiftSchedule(history.Revision, lift.Id,
                        lift.Schedule == LiftSchedule.PeakDays ? LiftSchedule.Daily : LiftSchedule.PeakDays, lift.Hours));
                int limit = lift != null ? SimWorld.HoldLimitMph(Pacing.Default, SimDesign.Input(lift)) : -1;
                if (_opsAll)
                {
                    int lines = 0, open = 0;
                    for (int i = 0; i < s.LiftCount; i++) { lines += s.LineCount[i]; if (s.LiftStatus[i] == LiftOpStatus.Open) open++; }
                    text = $"{open} of {built} lifts open · {lines} in line in all · each lift's line and wait in Tab, Lifts";
                }
                else text = $"{s.LiftStatus[pick]} · {s.LineCount[pick]} in line · " + (limit < 0 ? "never holds for wind" : $"holds at about {limit} mph")
                    + (s.LiftStatus[pick] == LiftOpStatus.Open || s.LiftStatus[pick] == LiftOpStatus.OnHold ? "" : " · lines take skiers only while it's Open");
            }
            y += note(y, cols, text);

            // LO-11: the gust at every top station, and which lifts it holds (each checks once a game hour, about 90 s at 1×).
            y += row;
            caption("Wind");
            if (button(0, _gustMph < 0 ? "Gust: weather" : $"Gust: {_gustMph} mph"))
            {
                _gustMph = _gustMph < 0 ? 40 : _gustMph == 40 ? 60 : -1;
                SendSim(SimCommandKind.SetGust, _gustMph);
            }
            _heldNames.Clear();
            for (int i = 0; i < s.LiftCount; i++)
            {
                if (s.LiftStatus[i] != LiftOpStatus.OnHold) continue;
                string n = "a lift";
                if (doc != null) foreach (var l in doc.Lifts) if (l.Id.Value == s.LiftIds[i]) n = l.Name;
                _heldNames.Add(n);
            }
            string held = _heldNames.Count == 0 ? "No lift on hold" : "On hold: " + string.Join(", ", _heldNames);
            y += note(y, 1, held + " · each lift reads the gust once a game hour (about 90 s at 1×); a held lift every 15 game minutes, and it runs again after a calm half hour");
        }

        ulong _opsLiftId;
        bool _opsAll;

        bool AllFeeding(SimSnapshot s)
        {
            for (int i = 0; i < s.LiftCount; i++)
                if (s.LiftStatus[i] != LiftOpStatus.Planned && !_feeding.Contains(s.LiftIds[i])) return false;
            return true;
        }
        readonly System.Collections.Generic.HashSet<ulong> _feeding = new System.Collections.Generic.HashSet<ulong>();
        readonly System.Collections.Generic.List<string> _heldNames = new System.Collections.Generic.List<string>();
        int _gustMph = -1;
    }
}
