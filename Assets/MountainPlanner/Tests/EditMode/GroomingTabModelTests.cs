using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Hud;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.25 PR B: Analysis › Grooming's view model from a hand-filled <see cref="GroomView"/>.</summary>
    public sealed class GroomingTabModelTests
    {
        // Tonight: swing 5 PM–10 PM, night to 7:30 AM; two Skogkatt 800 W groomers (1 and 2), both crewed swing and night.
        const int Day = 3, Zero = Day * 1440 + 17 * 60;

        static readonly string[] Names = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Fox" };

        static List<GroomTrailInfo> Trails() => Names.Select((n, i) => new GroomTrailInfo
        {
            Id = (ulong)(100 + i), Name = n, Symbol = "●", Priority = GroomPriority.Normal,
            Setting = n == "Delta" ? GroomSetting.Never : GroomSetting.Autopilot,
        }).ToList();

        static readonly List<int> RowOfK = new List<int> { 0, 1, 2, 3, 4, 5 };

        static GroomView Night(int groomers = 2)
        {
            var v = new GroomView(8)
            {
                Live = true, NightDay = Day, SwingStartAbsMin = Zero, SwingEndAbsMin = Zero + 300, NightEndAbsMin = Zero + 870,
                CrewsSwing = groomers, CrewsNight = groomers, CostUsd = 1234, ShortMinutes = 210, WantMinutes = 600, GroomerCount = groomers,
            };
            for (int g = 0; g < groomers; g++)
            {
                v.Number[g] = g + 1; v.Model[g] = GroomerModel.S800W; v.Drive[g] = GroomerDrive.Diesel; v.Shifts[g] = 3; v.SeasonMinutes[g] = 600;
            }
            for (int k = 0; k < 6; k++) { v.StartMin[k] = -1; v.EndMin[k] = -1; v.Lane[k] = GroomView.PackLane(-1, 0, 0, -1); }
            // Alpha: tilled on Groomer 2 from 5 PM for two hours.
            Set(v, 0, GroomRowState.Planned, GroomJob.Till, 2, 0, 120, 120);
            // Bravo: not reached (90 minutes no one has).
            Set(v, 1, GroomRowState.NotReached, GroomJob.Till, 0, -1, -1, 90);
            // Charlie: Groomer 1 starts it late and runs out of hours 50 minutes short.
            Set(v, 2, GroomRowState.Partial, GroomJob.Till, 1, 800, -1, 120);
            Set(v, 3, GroomRowState.Never, GroomJob.None, 0, -1, -1, 0);
            Set(v, 4, GroomRowState.TooBare, GroomJob.None, 0, -1, -1, 0);
            Set(v, 5, GroomRowState.PowderHeld, GroomJob.None, 0, -1, -1, 0);
            v.WarningCount = 2;
            v.WarningKind[0] = GroomWarningKind.TooBare; v.WarningTrail[0] = 4;
            v.WarningKind[1] = GroomWarningKind.PowderHeld; v.WarningTrail[1] = 5;
            return v;
        }

        static void Set(GroomView v, int k, GroomRowState s, GroomJob job, int groomer, int start, int end, int need)
        {
            v.State[k] = s; v.Job[k] = job; v.Groomer[k] = (byte)groomer; v.StartMin[k] = (short)start; v.EndMin[k] = (short)end; v.NeedMin[k] = (short)need;
        }

        static GroomingTabModel Build(GroomView v, ICollection<string> dismissed = null, bool lastNight = false) =>
            GroomingTabModel.Build(v, 6, Trails(), RowOfK, GroomCrews.AllAuto, dismissed ?? new HashSet<string>(), lastNight, 18 * 3600);

        [Test]
        public void LastNightListsWhatTheNightGroomedNotTonightsPlan()
        {
            var v = Night();
            for (int k = 0; k < 6; k++) { v.State[k] = GroomRowState.None; v.Job[k] = GroomJob.None; }
            v.WarningCount = 0;
            v.LastState[3] = GroomRowState.Done;   // Delta was groomed last night; nothing is planned tonight
            var m = Build(v, lastNight: true);
            Assert.That(m.RowsFor(GroomFilter.Tonight).Select(r => r.Name), Is.EqualTo(new[] { "Delta" }));
            Assert.That(Build(v).RowsFor(GroomFilter.Tonight), Is.Empty);
        }

        [Test]
        public void ARunWorkedInTwoPiecesIsTwoBlocksAndItsHoursAddUp()
        {
            // owner 2026-11-21 capture: Village Way on the swing and the night with Sunny Side between showed 19.8 h on a 14.5 h night
            var v = Night();
            Set(v, 0, GroomRowState.Partial, GroomJob.Till, 2, 0, 800, 300);   // Alpha: first piece 0..180, second 620..740
            Set(v, 2, GroomRowState.Planned, GroomJob.Till, 2, 180, 600, 420); // Charlie between them
            v.TransitCount = 3;
            v.TransitLane[0] = 1; v.TransitTrail[0] = 0; v.TransitFrom[0] = 0; v.TransitTo[0] = 180;
            v.TransitLane[1] = 1; v.TransitTrail[1] = 2; v.TransitFrom[1] = 180; v.TransitTo[1] = 600;
            v.TransitLane[2] = 1; v.TransitTrail[2] = 0; v.TransitFrom[2] = 620; v.TransitTo[2] = 740;
            var lane = Build(v).Lanes[1];
            Assert.That(lane.Blocks.Count(b => b.Name == "Alpha"), Is.EqualTo(2));
            Assert.That(lane.Hours, Is.EqualTo(GroomingTabModel.H(180 + 420 + 120) + " of 14.5 h"));   // its work of its own hours
        }

        [Test]
        public void NeedsYouComeWorstFirst()
        {
            var m = Build(Night());
            Assert.That(m.Needs.Select(n => n.Name), Is.EqualTo(new[] { "Bravo", "Charlie", "Echo", "Fox" }));
            Assert.That(m.Needs[0].Severity, Is.EqualTo(GroomSeverity.Bad));
            Assert.That(m.Needs[0].Fix, Is.EqualTo(GroomFix.Buy));
            Assert.That(m.Needs[0].Dismissable, Is.False);
            Assert.That(m.Needs[2].FixLabel, Is.EqualTo("Push tonight"));
            Assert.That(m.Needs[2].Order, Is.EqualTo(GroomOrder.Push));
            Assert.That(m.Needs[3].FixLabel, Is.EqualTo("Groom it tonight"));
            Assert.That(m.Needs.Select(n => n.Number), Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(m.Lead, Is.EqualTo("4 need you"));
        }

        [Test]
        public void DismissedWarningsStayAwayForTheSession()
        {
            var first = Build(Night());
            var fox = first.Needs.Single(n => n.Name == "Fox");
            var m = Build(Night(), new HashSet<string> { fox.Key });
            Assert.That(m.Needs.Select(n => n.Name), Is.EqualTo(new[] { "Bravo", "Charlie", "Echo" }));
            Assert.That(m.Lead, Is.EqualTo("3 need you"));
        }

        [Test]
        public void FiltersAndRowOrder()
        {
            var m = Build(Night());
            Assert.That(m.Rows.Select(r => r.Name), Is.EqualTo(new[] { "Bravo", "Charlie", "Echo", "Fox", "Alpha", "Delta" }));
            Assert.That(m.RowsFor(GroomFilter.Tonight).Select(r => r.Name), Is.EqualTo(new[] { "Bravo", "Charlie", "Echo", "Fox", "Alpha" }));
            Assert.That(m.RowsFor(GroomFilter.Needs).Count, Is.EqualTo(4));
            Assert.That(m.RowsFor(GroomFilter.Never).Select(r => r.Name), Is.EqualTo(new[] { "Delta" }));
            Assert.That(m.RowsFor(GroomFilter.All).Count, Is.EqualTo(6));
            Assert.That(m.CountFor(GroomFilter.Tonight), Is.EqualTo(5));
            Assert.That(m.CountFor(GroomFilter.Never), Is.EqualTo(1));
        }

        [Test]
        public void HeaderAndRowWords()
        {
            var m = Build(Night());
            Assert.That(m.Header, Is.EqualTo("Tonight · 5 PM–7:30 AM · 2 groomers out"));
            Assert.That(m.HeaderShort, Is.EqualTo(""));   // round 3: the hours and the cost are on the budget line
            var alpha = m.RowOf(100);
            Assert.That(alpha.What, Is.EqualTo("Till · G2 swing"));
            Assert.That(alpha.Hours, Is.EqualTo("2.0"));
            Assert.That(alpha.CardLine, Is.EqualTo("Not groomed yet · tonight on Groomer 2"));
            Assert.That(m.RowOf(101).What, Is.EqualTo("No equipment available"));   // owner 2026-11-21: generic
            Assert.That(m.RowOf(101).WhatSeverity, Is.EqualTo(GroomSeverity.Bad));
            Assert.That(m.RowOf(102).What, Is.EqualTo("Short by 0.8 h"));
            Assert.That(m.RowOf(103).What, Is.EqualTo("Never groomed"));
            Assert.That(m.RowOf(104).CanPush, Is.True);
            Assert.That(m.EquipLead, Is.EqualTo("Maker: Skogkatt · 2 groomers"));
            Assert.That(m.Fleet[0].Model, Is.EqualTo("Skogkatt 800 W, diesel"));
            Assert.That(m.TotalCost, Is.EqualTo("$1,230"));
        }

        [Test]
        public void TheBudgetLineIsThePlansHoursWithDrivingOfTheCrews()
        {
            // owner 2026-11-21: you pick a plan and clearly see the budget; the hours count driving
            var v = Night();
            v.PlanMin[3] = 120; v.TransitMin[3] = 30; v.PlanMin[1] = 70;   // G2 swing grooms 2 h and drives 0.5 h, G1 night 70 min
            var m = Build(v);
            Assert.That(m.BudgetShown, Is.True);
            Assert.That((m.UsedMin, m.GroomMin, m.DriveMin, m.CrewedMin), Is.EqualTo((220, 190, 30, 2 * (300 + 570))));
            Assert.That((m.WorkMin, m.ShortMin), Is.EqualTo((430, 210)));   // the work counts driving and what won't fit
            Assert.That(m.Budget, Is.EqualTo("Tonight's plan · 7.2 h of work · 3.7 h fits"));
            Assert.That((m.BudgetRest, m.BudgetWarn), Is.EqualTo(("3.5 h won't", true)));
            Assert.That(m.BudgetCost, Is.EqualTo("≈ $1,230"));
            Assert.That(m.BudgetFill, Is.EqualTo(220f / 430f).Within(1e-4f));

            v.ShortMinutes = 0; v.Preview = true;   // by day: tonight's preview, with hours to spare
            m = Build(v);
            Assert.That(m.Budget, Is.EqualTo("Tonight's plan (preview) · 3.7 h of work · 3.7 h fits"));
            Assert.That((m.BudgetRest, m.BudgetWarn), Is.EqualTo(("all of it", false)));
            Assert.That(m.Header, Does.StartWith("Tonight (preview) · "));
            Assert.That(Build(Night(0)).BudgetShown, Is.False);
        }

        [Test]
        public void TheEquipmentTableIsOneRowAGroomerAndOneCrewsLine()
        {
            // owner 2026-11-21: "way too complicated": the maker once, a short model, no Day column unless used, Auto or set by you
            var v = Night();
            v.PlanMin[0] = 60; v.TransitMin[0] = 12;
            var m = Build(v);
            Assert.That(m.FleetHeader, Is.EqualTo("Skogkatt · 2 groomers"));
            Assert.That(m.Fleet.Select(f => f.Short), Is.EqualTo(new[] { "800 W", "800 W" }));
            Assert.That((m.Fleet[0].UsedOn(0), m.Fleet[0].UsedTotal), Is.EqualTo((72, 72)));
            Assert.That((m.Fleet[0].TonightText, m.Fleet[1].TonightText), Is.EqualTo(("1.2 of 14.5 h", "Idle")));
            Assert.That(m.DayColumn, Is.False);
            Assert.That((m.CrewsPinned, m.CrewsLine), Is.EqualTo((false, "Crews: Auto staffs just enough")));
            Assert.That(m.Fleet[0].Season, Is.EqualTo("10 h"));   // on the groomer card now, not in the table

            v.CrewsDay = 1; v.Shifts[0] = 7;
            m = GroomingTabModel.Build(v, 6, Trails(), RowOfK, new GroomCrews(GroomCrews.Auto, 2, 1), new HashSet<string>(), false, 18 * 3600);
            Assert.That(m.DayColumn, Is.True);
            Assert.That((m.CrewsPinned, m.CrewsLine), Is.EqualTo((true, "Crews: set by you")));
        }

        [Test]
        public void NoSnapshotYetBuildsAnEmptyTab()
        {
            // the HUD's first fill, before any snapshot (the drawn-resort capture logged a NullReferenceException here)
            var m = GroomingTabModel.Build(null, 0, null, null, GroomCrews.AllAuto, null, false, 0);
            Assert.That(m.Empty, Is.True);
            Assert.That(GroomingTabModel.Build(null, 0, null, null, GroomCrews.AllAuto, null, true, 0).Empty, Is.True);
        }

        [Test]
        public void StripBlocksFollowTheNight()
        {
            var m = Build(Night());
            Assert.That(m.SwingLabel, Is.EqualTo("Swing 5 PM–10 PM"));
            Assert.That(m.NightLabel, Is.EqualTo("Night 10 PM–7:30 AM"));
            Assert.That(m.Lanes.Select(l => l.Label), Is.EqualTo(new[] { "Groomer 1", "Groomer 2" }));
            var alpha = m.Lanes[1].Blocks.Single();
            Assert.That((alpha.Name, alpha.Start, alpha.End, alpha.Kind, alpha.Over), Is.EqualTo(("Alpha", 0, 120, GroomBlockKind.Till, false)));
            Assert.That(alpha.Tip, Is.EqualTo("Alpha · Till · 2.0 h · 5 PM–7 PM"));
            var lane1 = m.Lanes[0].Blocks;
            var bravo = lane1.Single(b => b.Name == "Bravo");
            Assert.That((bravo.Kind, bravo.Over, bravo.Start, bravo.End - bravo.Start), Is.EqualTo((GroomBlockKind.Bad, true, m.NightEnd, 90)));
            var charlie = lane1.Where(b => b.Name == "Charlie").ToList();
            Assert.That(charlie.Count, Is.EqualTo(2));
            Assert.That((charlie[0].Start, charlie[0].End, charlie[0].Kind), Is.EqualTo((800, 870, GroomBlockKind.Till)));
            Assert.That((charlie[1].End - charlie[1].Start, charlie[1].Kind, charlie[1].Over), Is.EqualTo((50, GroomBlockKind.Warn, true)));
            Assert.That(m.Lanes[1].Hours, Is.EqualTo("2.0 of 14.5 h"));
        }

        [Test]
        public void EmptyFleet()
        {
            var v = Night(0);
            var m = Build(v);
            Assert.That(m.Empty, Is.True);
            Assert.That(m.Lead, Is.EqualTo("No groomers"));
            Assert.That(m.Header, Is.EqualTo("Tonight · 10.0 h of work · no groomers"));
            Assert.That(m.StripNote, Is.EqualTo("No groomers: none of tonight's 10.0 h can be done"));
            Assert.That(m.Needs, Is.Empty);
            Assert.That(m.Lanes, Is.Empty);
            Assert.That(m.Fleet, Is.Empty);
            Assert.That(m.FleetHeader, Is.EqualTo("none yet"));
            Assert.That(m.TotalCost, Is.EqualTo("—"));
            Assert.That(m.RowOf(101).What, Is.EqualTo("No groomer"));
        }

        [Test]
        public void GroomedWords()
        {
            Assert.That(GroomingTabModel.GroomedText(-1, 0), Is.EqualTo("Not groomed yet"));
            Assert.That(GroomingTabModel.GroomedText(6, 9 * 3600), Is.EqualTo("Groomed 3 AM"));
            Assert.That(GroomingTabModel.GroomedText(126, 0), Is.EqualTo("Groomed more than 5 days ago"));
            Assert.That(GroomingTabModel.Hour(16 * 60 + 30), Is.EqualTo("4:30 PM"));
        }
    }
}
