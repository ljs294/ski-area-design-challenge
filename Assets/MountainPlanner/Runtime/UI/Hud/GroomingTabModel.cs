using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;

namespace MountainPlanner.UI.Hud
{
    /// <summary>One player's trail as Analysis › Grooming lists it: the app fills these from the design.</summary>
    public struct GroomTrailInfo
    {
        public ulong Id;
        public string Name;
        /// <summary>The posted sign's symbol ("●", "■", "◆", "◆◆"), or "" before one is posted.</summary>
        public string Symbol;
        /// <summary>The setting the player sees (resolved: never <see cref="GroomSetting.Default"/>) and the priority.</summary>
        public GroomSetting Setting;
        public GroomPriority Priority;
    }

    /// <summary>The Trails sub-tab's filters (the approved mockup's grfilt, with the plan's All).</summary>
    public enum GroomFilter : byte { Tonight, Needs, All, Never }

    public enum GroomSeverity : byte { None, Quiet, Warn, Bad }

    /// <summary>A block on the night strip: a push, a till, one that needs you (amber), one not reached (red) or a groomer's
    /// drive over the trails (GO-15: out from the depot, between the runs and home).</summary>
    public enum GroomBlockKind : byte { Push, Till, Warn, Bad, Transit }

    /// <summary>A needs-you row's one-click fix.</summary>
    public enum GroomFix : byte { None, Order, Buy }

    /// <summary>
    /// Task 4.25 PR B (GO-08): Analysis › Grooming as words and figures, built from the snapshot's <see cref="GroomView"/>
    /// and the player's trails (the approved mockup's groomingTab: grStrip, grStripPlate, grTrailTable, grEquip,
    /// grSideCard, grCardBlock). Plain C#: the app builds one at the live rate only when <see cref="Signature"/> moves,
    /// and the HUD draws it; nothing here runs per frame.
    /// </summary>
    public sealed class GroomingTabModel
    {
        public sealed class Block
        {
            public ulong Row;
            public string Name, Tip;
            /// <summary>Minutes from the strip's start.</summary>
            public int Start, End;
            public GroomBlockKind Kind;
            /// <summary>Past the crews' hours: what doesn't fit, hung after the night's end.</summary>
            public bool Over;
        }

        public sealed class Lane
        {
            public int Number;
            public string Label, Hours;
            /// <summary>Whether a crew takes it out on the swing, night and day shifts (as last night's lanes were worked).</summary>
            public bool Swing, Night, Day;
            public readonly List<Block> Blocks = new List<Block>();
        }

        public sealed class Need
        {
            public ulong Row;
            /// <summary>The session-only dismiss key ("kind:row").</summary>
            public string Key;
            public int Number;
            public GroomSeverity Severity;
            public string Name, Symbol, Why, FixLabel;
            public GroomFix Fix;
            public GroomOrder Order;
            public GroomerModel BuyModel;
            public bool Dismissable;
        }

        public sealed class Row
        {
            public ulong Id;
            public string Name, Symbol, What, Hours;
            public GroomSetting Setting;
            public GroomPriority Priority;
            public GroomSeverity WhatSeverity;
            /// <summary>Its needs-you number (0 none) and how bad.</summary>
            public int Need;
            public GroomSeverity NeedSeverity;
            /// <summary>Whether it has work tonight (the Tonight filter), its setting is Never, and its place in the order of work.</summary>
            public bool Tonight, Never;
            public int Order;
            /// <summary>Whether a push makes sense for it tonight (whales or a thin strip): the trail card's Push tonight.</summary>
            public bool CanPush;
            /// <summary>The trail card's line ("Groomed 3 AM · tonight on Groomer 2").</summary>
            public string CardLine;
        }

        public sealed class FleetRow
        {
            public int Number;
            /// <summary>"Groomer 2", "Skogkatt 400 W, diesel" (the card) and "400 W" (the table: the maker is in its header).</summary>
            public string Label, Model, Short, Season;
            public GroomerModel Kind;
            public GroomerDrive Drive;
            public bool Tiller;
            public int SeasonMinutes;
            public readonly bool[] Crewed = new bool[3];
            /// <summary>Per shift (swing, night, day): minutes worked plus planned, and the minutes its crew is out for.</summary>
            public readonly int[] PlanMin = new int[3], OpenMin = new int[3], TransitMin = new int[3];
            public int PlanTotal => PlanMin[0] + PlanMin[1] + PlanMin[2];
            public int TransitTotal => TransitMin[0] + TransitMin[1] + TransitMin[2];
            public int OpenTotal => OpenMin[0] + OpenMin[1] + OpenMin[2];
            /// <summary>Round 3 (owner 2026-11-21: the hours count driving): grooming plus driving on a shift, and the night's.</summary>
            public int UsedOn(int s) => PlanMin[s] + TransitMin[s];
            public int UsedTotal => PlanTotal + TransitTotal;
            /// <summary>The Tonight column: "11.1 of 14.5 h", "Idle" when crewed with nothing it can do, "" uncrewed.</summary>
            public string TonightText => OpenTotal == 0 && UsedTotal == 0 ? "" : UsedTotal == 0 ? "Idle" : $"{H(UsedTotal)} of {H(OpenTotal)} h";
            /// <summary>A dev groomer (<c>-groomers</c>, numbered 9001 on): free, not in the design, so not for sale.</summary>
            public bool Dev;
        }

        public sealed class Shift
        {
            public string Name, Window, Cost;
            public int Crews;
            public bool Pinned, Closed;
        }

        public sealed class CatalogueRow
        {
            public GroomerModel Model;
            public string Name, Price, Ps, Till, Push, Slopes;
        }

        // ---------- the maker's figures (Skogkatt, owner round 6; the rest from Pacing) ----------

        public const int DevNumberFrom = 9001;
        public const int TillerPriceUsd = 40000;
        static readonly string[] ModelNames = { "400", "400 W", "800", "800 W" };
        static readonly int[] ModelPs = { 435, 435, 612, 612 };
        static readonly int[] WinchKn = { 0, 40, 0, 45 };
        static readonly string[] DriveNames = { "Diesel", "Hybrid", "Electric" };
        public static readonly string[] SettingNames = { "Auto", "Every night", "Unless it's snowing", "When ordered", "Never" };
        public static readonly string[] SettingTips =
            { "Grooms when the snow needs it", "Tills it every night", "Every night, but leaves new snow", "Only when you order it", "Glades and zones start here" };
        public static readonly string[] PriorityNames = { "High", "Normal", "Low" };
        public static readonly string[] PriorityTips = { "Worked first; pushes whales before they cure", "In order of yesterday's skiers", "After every other trail" };
        static readonly GroomPriority[] PriorityOrder = { GroomPriority.High, GroomPriority.Normal, GroomPriority.Low };

        public static string ModelName(GroomerModel m) => Groomer.Valid(m) ? ModelNames[(int)m - 1] : "?";
        public static string DriveName(GroomerDrive d) => Groomer.Valid(d) ? DriveNames[(int)d - 1] : "?";
        public static int SettingIndex(GroomSetting s) => s >= GroomSetting.Autopilot && s <= GroomSetting.Never ? (int)s - 1 : 0;
        public static GroomSetting SettingAt(int i) => (GroomSetting)(i + 1);
        public static int PriorityIndex(GroomPriority p) => Array.IndexOf(PriorityOrder, p) is int i && i >= 0 ? i : 1;
        public static GroomPriority PriorityAt(int i) => PriorityOrder[Math.Max(0, Math.Min(2, i))];
        static bool Winch(GroomerModel m) => m == GroomerModel.S400W || m == GroomerModel.S800W;

        /// <summary>A groomer's price with its drive (diesel is the list price).</summary>
        public static int PriceUsd(GroomerModel m, GroomerDrive d, Pacing p = null)
        {
            p ??= Pacing.Default;
            if (!Groomer.Valid(m) || !Groomer.Valid(d)) return 0;
            return (int)((long)p.GroomPriceUsd[(int)m - 1] * p.GroomDrivePricePermille[(int)d - 1] / 1000);
        }

        // ---------- what the tab shows ----------

        public bool Empty, LastNight;
        /// <summary>The sub-tab line's right side ("2 need you", "No groomers", "Maker: Skogkatt · 3 groomers").</summary>
        public string Lead = "", EquipLead = "";
        public bool LeadWarn;
        /// <summary>The strip plate's line ("Tonight · 5 PM–7:30 AM · 3 groomers · ≈ $3,280") and its amber part ("3.5 h short").</summary>
        public string Header = "", HeaderShort = "";
        public string CostTip = "";
        /// <summary>
        /// GO-14 v3 (owner 2026-11-21: you pick a plan and clearly see your budget): the line at the top of both sub-tabs,
        /// "Tonight's plan · 12.5 h of 29.0 h crewed", then "2.0 h short" (amber) or "16.5 h spare", then "≈ $2,340", and a bar
        /// of the hours used. The hours count driving. Shown while there are groomers; "(preview)" by day, before the swing.
        /// </summary>
        public bool BudgetShown, BudgetWarn, Preview;
        public string Budget = "", BudgetRest = "", BudgetCost = "", BudgetTip = "";
        /// <summary>The bar: the plan's minutes (grooming and driving) and the crews' minutes.</summary>
        public int UsedMin, CrewedMin, GroomMin, DriveMin;
        /// <summary>Round 3 (owner 2026-11-21: the hours count driving): tonight's work is its grooming, its driving and what
        /// won't fit; the bar is the share that fits.</summary>
        public int WorkMin, ShortMin;
        public float BudgetFill => WorkMin <= 0 ? 0f : Math.Min(1f, UsedMin / (float)WorkMin);
        /// <summary>The strip: its length, the swing's end, the night's end and the opening (minutes from its start).</summary>
        public int StripEnd = 900, SwingEnd = 480, NightEnd = 840, Opens = 870;
        public bool DayShift;
        public string SwingLabel = "", NightLabel = "", OpensLabel = "", StripNote = "";
        public readonly List<(int Minute, string Label)> Ticks = new List<(int, string)>();
        public readonly List<Lane> Lanes = new List<Lane>();
        public readonly List<Need> Needs = new List<Need>();
        public readonly List<Row> Rows = new List<Row>();
        public int TonightCount, NeverCount;
        public readonly List<FleetRow> Fleet = new List<FleetRow>();
        public readonly Shift[] Shifts = { new Shift { Name = "Swing" }, new Shift { Name = "Night" }, new Shift { Name = "Day", Closed = true } };
        public string TotalCost = "—", FleetHeader = "none yet";
        /// <summary>Round 3: the Day column shows only when a day shift is crewed or has work; whether any shift's crews are
        /// set by the player (else Auto); the crews line under the table.</summary>
        public bool DayColumn, CrewsPinned;
        public string CrewsLine = "";
        public int SecondOfDay;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static string H(int minutes) => (minutes / 60.0).ToString("F1", Inv);
        public static string Usd(long v) => "$" + (Math.Round(v / 10.0) * 10).ToString("#,0", Inv);

        /// <summary>"5 PM", "4:30 PM" for a minute of the day (any minute; it wraps).</summary>
        public static string Hour(int minute)
        {
            int m = ((minute % 1440) + 1440) % 1440, h = m / 60, mm = m % 60;
            string ampm = h < 12 ? "AM" : "PM";
            int h12 = (h + 11) % 12 + 1;
            return mm == 0 ? $"{h12} {ampm}" : $"{h12}:{mm:D2} {ampm}";
        }

        /// <summary>The rows the filter shows, needs-you first (numbered), then in order of work.</summary>
        public List<Row> RowsFor(GroomFilter f)
        {
            var list = new List<Row>();
            foreach (var r in Rows)
            {
                bool on = f switch
                {
                    GroomFilter.Tonight => r.Need > 0 || r.Tonight,
                    GroomFilter.Needs => r.Need > 0,
                    GroomFilter.Never => r.Never,
                    _ => true,
                };
                if (on) list.Add(r);
            }
            return list;
        }

        public int CountFor(GroomFilter f) => f switch
        {
            GroomFilter.Tonight => TonightCount,
            GroomFilter.Needs => Needs.Count,
            GroomFilter.Never => NeverCount,
            _ => Rows.Count,
        };

        public Row RowOf(ulong id)
        {
            foreach (var r in Rows) if (r.Id == id) return r;
            return null;
        }

        // ---------- the signature: the app rebuilds only when it moves (no allocation) ----------

        /// <summary>A hash of everything in <paramref name="v"/> the tab shows, for the first <paramref name="trails"/> trails.</summary>
        public static long Signature(GroomView v, int trails)
        {
            if (v == null) return 0;
            unchecked
            {
                long h = 1469598103934665603L;
                void Mix(long x) { h = (h ^ x) * 1099511628211L; }
                Mix(v.Live ? 1 : 0); Mix(v.Preview ? 1 : 0); Mix(v.NightDay);   // the preview flag: the real plan at the swing may equal it Mix(v.SwingStartAbsMin); Mix(v.SwingEndAbsMin); Mix(v.NightEndAbsMin);
                Mix(v.CrewsSwing); Mix(v.CrewsNight); Mix(v.CrewsDay); Mix(v.SnowingTonight ? 1 : 0);
                Mix(v.WantMinutes); Mix(v.CrewMinutes); Mix(v.ShortMinutes); Mix(v.CostUsd);
                Mix(v.LastNotReached); Mix(v.LastShortMinutes); Mix(v.LastWorkMinutes); Mix(v.LastCostUsd); Mix(v.LastNightDay);
                Mix(v.LastSwingStartAbsMin); Mix(v.LastNightEndAbsMin);
                int n = Math.Min(trails, v.Job.Length);
                for (int k = 0; k < n; k++)
                {
                    Mix((long)v.Job[k] | ((long)v.State[k] << 8) | ((long)v.LastState[k] << 16) | ((long)v.Groomer[k] << 24));
                    Mix(((long)v.StartMin[k] << 32) ^ ((long)(ushort)v.EndMin[k] << 16) ^ (ushort)v.NeedMin[k]);
                    Mix(v.Lane[k]);
                }
                Mix(v.GroomerCount);
                for (int g = 0; g < v.GroomerCount && g < v.Number.Length; g++)
                {
                    Mix(v.Number[g]); Mix((long)v.Model[g] | ((long)v.Drive[g] << 8) | ((v.Tiller[g] ? 1L : 0) << 16) | ((long)v.Shifts[g] << 24));
                    Mix(v.SeasonMinutes[g] / 60);
                }
                Mix(v.WarningCount);
                for (int w = 0; w < v.WarningCount; w++) Mix(((long)v.WarningKind[w] << 16) | (ushort)v.WarningTrail[w]);
                return h;
            }
        }

        // ---------- building ----------

        sealed class Agg
        {
            public GroomRowState State, LastState;
            public GroomJob Job;
            public int Start = -1, End = -1, Need, Short, Since = -1, LastGot;
            public int Groomer, LastGroomer;
            public bool Task, Warned;
        }

        static int Rank(GroomRowState s) => s switch
        {
            GroomRowState.NotReached => 10,
            GroomRowState.NoGroomer => 9,
            GroomRowState.NoRoute => 9,
            GroomRowState.Partial => 8,
            GroomRowState.PitchSkipped => 7,
            GroomRowState.TooBare => 6,
            GroomRowState.Planned => 5,
            GroomRowState.Done => 4,
            GroomRowState.PowderHeld => 3,
            GroomRowState.Skipped => 2,
            GroomRowState.Never => 1,
            _ => 0,
        };

        /// <summary>The lane (fleet index) whose groomer's number truncates to <paramref name="number"/> under <paramref name="mask"/>.</summary>
        static int LaneOf(GroomView v, int number, int mask)
        {
            if (number <= 0) return -1;
            for (int g = 0; g < v.GroomerCount && g < v.Number.Length; g++) if ((v.Number[g] & mask) == (number & mask)) return g;
            return -1;
        }

        /// <summary>
        /// The tab for a snapshot's grooming. <paramref name="rowOfK"/> maps each snapshot trail to its player's trail (an
        /// index into <paramref name="trails"/>, −1 none); <paramref name="pins"/> are the design's crew pins and
        /// <paramref name="dismissed"/> the needs-you rows the player waved away this session.
        /// </summary>
        public static GroomingTabModel Build(GroomView v, int trailCount, IReadOnlyList<GroomTrailInfo> trails, IReadOnlyList<int> rowOfK,
                                             GroomCrews pins, ICollection<string> dismissed, bool lastNight, int secondOfDay, Pacing p = null)
        {
            p ??= Pacing.Default;
            var m = new GroomingTabModel { LastNight = lastNight, SecondOfDay = secondOfDay };
            trails ??= Array.Empty<GroomTrailInfo>();
            int fleet = v?.GroomerCount ?? 0;
            m.Empty = fleet == 0;
            var agg = new Agg[trails.Count];
            for (int r = 0; r < agg.Length; r++) agg[r] = new Agg();
            int n = v == null ? 0 : Math.Min(trailCount, Math.Min(v.Job.Length, rowOfK?.Count ?? 0));

            // The strip's axis: tonight's shifts (or the day shift's), else last night's.
            int zero;
            if (!lastNight && v != null && v.NightDay >= 0)
            {
                zero = v.SwingStartAbsMin;
                m.SwingEnd = Math.Max(0, v.SwingEndAbsMin - zero);
                m.NightEnd = Math.Max(m.SwingEnd, v.NightEndAbsMin - zero);
            }
            else if (!lastNight)
            {
                m.DayShift = true;
                zero = p.GroomDayStartHour * 60;
                m.SwingEnd = 0;
                m.NightEnd = (p.GroomDayEndHour - p.GroomDayStartHour) * 60;
            }
            else
            {
                zero = v?.LastSwingStartAbsMin ?? 0;
                int end = v != null ? v.LastNightEndAbsMin - zero : 840;
                m.NightEnd = Math.Max(60, end);
                int swingEnd = m.NightEnd / 2;   // GO-03 v2: two equal shifts
                m.SwingEnd = Math.Max(0, Math.Min(m.NightEnd, swingEnd));
            }
            m.Opens = m.DayShift ? m.NightEnd : m.NightEnd + p.GroomNightEndsBeforeOpenMinutes;
            m.StripEnd = m.Opens + 30;
            if (m.DayShift)
            {
                m.SwingLabel = "";
                m.NightLabel = $"Day {Hour(zero)}–{Hour(zero + m.NightEnd)} · closed trails only";
                m.OpensLabel = "";
            }
            else
            {
                m.SwingLabel = $"Swing {Hour(zero)}–{Hour(zero + m.SwingEnd)}";
                m.NightLabel = $"Night {Hour(zero + m.SwingEnd)}–{Hour(zero + m.NightEnd)}";
                m.OpensLabel = $"Opens {Hour(zero + m.Opens)}";
            }
            // Ticks every three hours on the hour (18, 21, 24, 27 in the mockup).
            int firstTick = ((zero + 59) / 60) * 60;
            for (int t = firstTick; t - zero <= m.StripEnd - 40; t += 60)
                if ((t / 60) % 3 == 0 && t - zero > 20) m.Ticks.Add((t - zero, Hour(t)));

            // Each player's trail: its runs' worst state, its job, its start and end, the minutes it needs.
            for (int k = 0; k < n; k++)
            {
                int r = rowOfK[k];
                if (r < 0 || r >= agg.Length) continue;
                var a = agg[r];
                var st = v.State[k];
                if (Rank(st) > Rank(a.State)) a.State = st;
                if (Rank(v.LastState[k]) > Rank(a.LastState)) a.LastState = v.LastState[k];
                if (v.Job[k] == GroomJob.Push || (v.Job[k] == GroomJob.Till && a.Job == GroomJob.None)) a.Job = v.Job[k];
                if (v.Job[k] != GroomJob.None) a.Task = true;
                a.Need += v.NeedMin[k];
                if (v.StartMin[k] >= 0 && (a.Start < 0 || v.StartMin[k] < a.Start)) { a.Start = v.StartMin[k]; a.Groomer = v.Groomer[k]; }
                if (v.EndMin[k] >= 0) a.End = Math.Max(a.End, v.EndMin[k]);
                if (st == GroomRowState.Partial && v.EndMin[k] < 0 && v.StartMin[k] >= 0 && !m.DayShift)
                    a.Short += Math.Max(0, v.StartMin[k] + v.NeedMin[k] - m.NightEnd);
                if (st == GroomRowState.NotReached) a.Short += v.NeedMin[k];
                int since = v.SinceHoursAt(k);
                if (since >= 0 && (a.Since < 0 || since < a.Since)) a.Since = since;
                if (v.LastEndAt(k) > 0)
                {
                    a.LastGot += v.LastEndAt(k) - Math.Max(0, v.LastStartAt(k));   // the minutes worked (round 4: the sim sizes the lane so)
                    int li = v.LastGroomerAt(k);   // a fleet index: shown by its number
                    if (a.LastGroomer == 0 && li >= 0 && li < v.GroomerCount) a.LastGroomer = v.Number[li];
                }
            }

            // ---------- the strip's lanes ----------
            for (int g = 0; g < fleet && g < v.Number.Length; g++)
            {
                var lane = new Lane { Number = v.Number[g], Label = $"Groomer {v.Number[g]}" };
                byte sh = v.Shifts[g];
                if (!lastNight) { lane.Swing = (sh & 1) != 0; lane.Night = (sh & 2) != 0; lane.Day = (sh & 4) != 0; }
                m.Lanes.Add(lane);
            }
            var over = new int[m.Lanes.Count];
            for (int i = 0; i < over.Length; i++) over[i] = m.NightEnd;
            var drawn = new bool[trails.Count];   // a trail split into several network trails is one task: one block
            for (int k = 0; k < n; k++)
            {
                int r = rowOfK[k];
                if (r < 0 || r >= trails.Count || drawn[r]) continue;
                string name = trails[r].Name ?? "Trail";
                if (!lastNight)
                {
                    var st = v.State[k];
                    int need = v.NeedMin[k], start = v.StartMin[k];
                    int g = LaneOf(v, v.Groomer[k], 255);
                    var job = v.Job[k];
                    var kind = job == GroomJob.Push ? GroomBlockKind.Push : GroomBlockKind.Till;
                    if (st == GroomRowState.PitchSkipped) kind = GroomBlockKind.Warn;
                    string jobWord = job == GroomJob.Push ? "Push" : "Till";
                    if (g >= 0 && start >= 0)
                    {
                        int end = v.EndMin[k] >= 0 ? v.EndMin[k] : Math.Min(start + Math.Max(1, need), Math.Max(start + 1, m.NightEnd));
                        string skip = st == GroomRowState.PitchSkipped ? " · the pitch is skipped" : "";
                        // one block a work piece (a run worked in two pieces is two blocks, so a lane's hours add up)
                        bool pieces = false;
                        for (int i = 0; i < v.TransitCount && i < GroomView.MaxTransit; i++)
                        {
                            int wk = v.TransitTrail[i];
                            if (wk < 0 || wk >= n || rowOfK[wk] != r) continue;
                            int wg = v.TransitLane[i], a = v.TransitFrom[i], b = v.TransitTo[i];
                            if (wg >= m.Lanes.Count || b <= a) continue;
                            m.Lanes[wg].Blocks.Add(new Block { Row = trails[r].Id, Name = name, Start = Math.Max(0, a), End = b, Kind = kind,
                                                               Tip = $"{name} · {jobWord} · {H(b - a)} h · {Hour(zero + a)}–{Hour(zero + b)}" + skip });
                            pieces = true;
                        }
                        if (!pieces)
                            m.Lanes[g].Blocks.Add(new Block { Row = trails[r].Id, Name = name, Start = start, End = end, Kind = kind,
                                                              Tip = $"{name} · {jobWord} · {H(end - start)} h · {Hour(zero + start)}–{Hour(zero + end)}" + skip });
                        drawn[r] = true;
                        int rest = v.EndMin[k] < 0 ? start + need - end : 0;
                        if (rest > 0 && !m.DayShift)
                        {
                            m.Lanes[g].Blocks.Add(new Block { Row = trails[r].Id, Name = name, Start = over[g], End = over[g] + rest, Kind = GroomBlockKind.Warn, Over = true,
                                                              Tip = $"{name} · {jobWord} · {H(rest)} h · past the crews' hours" });
                            over[g] += rest;
                        }
                    }
                    else if (st == GroomRowState.NotReached && need > 0 && over.Length > 0)
                    {
                        int best = 0;
                        for (int i = 1; i < over.Length; i++) if (over[i] < over[best]) best = i;
                        m.Lanes[best].Blocks.Add(new Block { Row = trails[r].Id, Name = name, Start = over[best], End = over[best] + need, Kind = GroomBlockKind.Bad, Over = true,
                                                             Tip = $"{name} · {jobWord} · {H(need)} h · not reached" });
                        over[best] += need;
                        drawn[r] = true;
                    }
                }
                else if (v.LastEndAt(k) > 0)
                {
                    int g = v.LastGroomerAt(k);   // the fleet index (GO-14)
                    if (g >= v.GroomerCount) g = -1;
                    if (g < 0) continue;
                    int start = Math.Max(0, v.LastStartAt(k)), end = v.LastEndAt(k);
                    var ls = v.LastState[k];
                    var kind = ls == GroomRowState.Partial || ls == GroomRowState.PitchSkipped ? GroomBlockKind.Warn : GroomBlockKind.Till;
                    string tip = $"{name} · {H(end - start)} h worked · from {Hour(zero + start)}" +
                                 (ls == GroomRowState.Partial ? " · cut short" : ls == GroomRowState.PitchSkipped ? " · the pitch was skipped" : "");
                    var lane = m.Lanes[g];
                    lane.Blocks.Add(new Block { Row = trails[r].Id, Name = name, Start = start, End = end, Kind = kind, Tip = tip });
                    drawn[r] = true;
                    if (start < m.SwingEnd) lane.Swing = true;
                    if (end > m.SwingEnd) lane.Night = true;
                }
            }
            if (!lastNight && v != null)
                for (int i = 0; i < v.TransitCount && i < GroomView.MaxTransit; i++)
                {
                    int g = v.TransitLane[i];
                    if (g >= m.Lanes.Count || v.TransitTrail[i] >= 0) continue;   // a work piece, drawn with its run
                    int a = v.TransitFrom[i], b = v.TransitTo[i];
                    if (b <= a) continue;
                    m.Lanes[g].Blocks.Add(new Block { Name = "", Start = Math.Max(0, a), End = Math.Max(1, b), Kind = GroomBlockKind.Transit,
                                                      Tip = $"Transit · {b - a} min · {Hour(zero + a)}–{Hour(zero + b)} · driving the trails" });
                }

            // ---------- the header lines ----------
            int crewed = v == null ? 0 : m.DayShift ? v.CrewsDay : Math.Max(v.CrewsSwing, v.CrewsNight);
            m.CostTip = $"Estimates: a crew is paid its whole window and grooms it less an hour; ${p.GroomPayUsdHour} an operator-hour " +
                        "(swing +10 %, night +20 %) and fuel and upkeep by the groomer-hour. Nothing is charged until money comes.";
            if (v == null || !v.Live && fleet == 0)
            {
                m.Header = trails.Count == 0 ? "No trails to groom yet" : "No groomers: nothing is groomed tonight";   // Gate B finding 5
            }
            else if (lastNight)
            {
                if (v.LastNightDay < 0) { m.Header = "Last night · nothing groomed yet"; }
                else
                {
                    m.Header = $"Last night · {Hour(zero)}–{Hour(zero + m.NightEnd)} · {H(v.LastWorkMinutes)} h worked · ≈ {Usd(v.LastCostUsd)}";
                    if (v.LastShortMinutes > 0) m.HeaderShort = $"{H(v.LastShortMinutes)} h short";
                }
            }
            else if (fleet == 0)
            {
                m.Header = $"{(m.DayShift ? "Today" : "Tonight")} · {H(v.WantMinutes)} h of work · no groomers";
            }
            else
            {
                string when = m.DayShift ? $"Day shift · {Hour(zero)}–{Hour(zero + m.NightEnd)}" : $"{(v.Preview ? "Tonight (preview)" : "Tonight")} · {Hour(zero)}–{Hour(zero + m.NightEnd)}";
                m.Header = $"{when} · {crewed} groomer{(crewed == 1 ? "" : "s")} out";
            }
            if (m.Empty && v != null && v.WantMinutes > 0 && !lastNight) m.StripNote = $"No groomers: none of tonight's {H(v.WantMinutes)} h can be done";
            else if (m.Empty) m.StripNote = "No groomers yet";

            // ---------- needs you, worst first ----------
            dismissed ??= Array.Empty<string>();
            var needOf = new Dictionary<int, Need>();
            void AddNeed(int r, GroomSeverity sev, string why, string fix, GroomFix kind, GroomOrder order, GroomerModel model, bool x, string key)
            {
                if (r < 0 || r >= trails.Count || needOf.ContainsKey(r)) return;
                if (x && dismissed.Contains(key)) return;
                var t = trails[r];
                var need = new Need { Row = t.Id, Key = key, Number = m.Needs.Count + 1, Severity = sev, Name = t.Name, Symbol = t.Symbol ?? "", Why = why,
                                      FixLabel = fix, Fix = kind, Order = order, BuyModel = model, Dismissable = x };
                m.Needs.Add(need);
                needOf[r] = need;
            }
            if (!m.Empty)
            {
                for (int r = 0; r < agg.Length; r++)
                {
                    var s = lastNight ? agg[r].LastState : agg[r].State;
                    if (s == GroomRowState.NoRoute)
                        AddNeed(r, GroomSeverity.Bad, "No way there: groomers drive on trails only, and none leads here from the groomers' parking at the lowest lift.",
                                null, GroomFix.None, GroomOrder.None, 0, false, "rt:" + trails[r].Id);
                    if (s == GroomRowState.NotReached)
                        AddNeed(r, GroomSeverity.Bad, lastNight ? "Out of hours: every groomer was out both shifts." : "Out of hours: every groomer is out both shifts.",
                                "Buy a groomer", GroomFix.Buy, GroomOrder.None, GroomerModel.S800, false, "nr:" + trails[r].Id);
                }
                for (int r = 0; r < agg.Length; r++)
                {
                    var s = lastNight ? agg[r].LastState : agg[r].State;
                    if (s != GroomRowState.Partial) continue;
                    if (lastNight) AddNeed(r, GroomSeverity.Warn, "Cut short: out of hours. The rest goes first tonight.", null, GroomFix.None, GroomOrder.None, 0, false, "cs:" + trails[r].Id);
                    else if (agg[r].Short > 0)
                        AddNeed(r, GroomSeverity.Warn, $"Out of hours after {H(Math.Max(0, agg[r].Need - agg[r].Short))} of {H(agg[r].Need)} h; the rest goes first tomorrow night.",
                                null, GroomFix.None, GroomOrder.None, 0, false, "cs:" + trails[r].Id);
                }
                for (int w = 0; v != null && w < v.WarningCount; w++)
                {
                    int k = v.WarningTrail[w];
                    if (k < 0 || k >= n) continue;
                    int r = rowOfK[k];
                    if (r < 0 || r >= trails.Count) continue;
                    var kind = v.WarningKind[w];
                    string key = (int)kind + ":" + trails[r].Id;
                    switch (kind)
                    {
                        case GroomWarningKind.WhalesBlockOpening:
                            agg[r].Warned = true;
                            AddNeed(r, GroomSeverity.Warn, "Whales keep it closed. A push clears them, tonight or on tomorrow's day shift.", "Order a push", GroomFix.Order, GroomOrder.Push, 0, true, key);
                            break;
                        case GroomWarningKind.TooBare:
                            agg[r].Warned = true;
                            AddNeed(r, GroomSeverity.Warn, "Too thin to till. A push fills the thin strip.", "Push tonight", GroomFix.Order, GroomOrder.Push, 0, true, key);
                            break;
                        case GroomWarningKind.PowderHeld:
                            AddNeed(r, GroomSeverity.Warn, "Left for new snow nights running; its moguls are growing.", "Groom it tonight", GroomFix.Order, GroomOrder.Till, 0, true, key);
                            break;
                        case GroomWarningKind.PitchSkippedNoWinch:
                            AddNeed(r, GroomSeverity.Warn, "Its steep pitch needs a W (winch) groomer; you have none, so the moguls there grow.",
                                    $"Buy an 800 W · {Usd(PriceUsd(GroomerModel.S800W, GroomerDrive.Diesel, p))}", GroomFix.Buy, GroomOrder.None, GroomerModel.S800W, true, key);
                            break;
                    }
                }
            }

            // ---------- the trail rows ----------
            for (int r = 0; r < trails.Count; r++)
            {
                var t = trails[r];
                var a = agg[r];
                var row = new Row { Id = t.Id, Name = t.Name ?? "Trail", Symbol = t.Symbol ?? "", Setting = t.Setting, Priority = t.Priority };
                row.Never = t.Setting == GroomSetting.Never;
                // Last night: the rows the night worked or left, from its states (tonight's plan is not last night's)
                var s = lastNight ? a.LastState : a.State;
                row.Tonight = (!lastNight && a.Task) || s == GroomRowState.NotReached || s == GroomRowState.Partial || s == GroomRowState.PitchSkipped || s == GroomRowState.TooBare ||
                              s == GroomRowState.Planned || s == GroomRowState.Done;
                row.CanPush = a.Warned || s == GroomRowState.TooBare || a.Job == GroomJob.Push;
                (row.What, row.WhatSeverity) = lastNight ? LastWhat(a, fleet) : TonightWhat(a, fleet, m.SwingEnd, m.DayShift, v);
                row.Hours = !lastNight && a.Need > 0 ? H(a.Need) : lastNight && a.LastGot > 0 ? H(a.LastGot) : "";
                row.Order = a.Start >= 0 ? a.Start : (row.Tonight ? 100_000 : 200_000) + r;
                if (needOf.TryGetValue(r, out var need)) { row.Need = need.Number; row.NeedSeverity = need.Severity; }
                row.CardLine = CardLineOf(a, v, secondOfDay, m.SwingEnd, m.DayShift);
                m.Rows.Add(row);
            }
            m.Rows.Sort((x, y) =>
            {
                if ((x.Need > 0) != (y.Need > 0)) return x.Need > 0 ? -1 : 1;
                if (x.Need > 0) return x.Need.CompareTo(y.Need);
                if (x.Never != y.Never) return x.Never ? 1 : -1;
                return x.Order.CompareTo(y.Order);
            });
            foreach (var r in m.Rows) { if (r.Need > 0 || r.Tonight) m.TonightCount++; if (r.Never) m.NeverCount++; }

            if (m.Empty) { m.Lead = "No groomers"; m.LeadWarn = true; }
            else if (m.Needs.Count > 0) { m.Lead = $"{m.Needs.Count} need{(m.Needs.Count == 1 ? "s" : "")} you"; m.LeadWarn = true; }
            else m.Lead = "Nothing needs you";
            m.EquipLead = $"Maker: Skogkatt · {fleet} groomer{(fleet == 1 ? "" : "s")}";

            // ---------- Equipment ----------
            for (int g = 0; g < fleet && g < v.Number.Length; g++)
            {
                var f = new FleetRow
                {
                    Number = v.Number[g], Label = $"Groomer {v.Number[g]}", Kind = v.Model[g], Drive = v.Drive[g], Tiller = v.Tiller[g],
                    SeasonMinutes = v.SeasonMinutes[g], Dev = v.Number[g] >= DevNumberFrom,
                };
                f.Model = $"Skogkatt {ModelName(f.Kind)}, {DriveName(f.Drive).ToLowerInvariant()}";
                f.Short = ModelName(f.Kind) + (f.Drive == GroomerDrive.Diesel ? "" : f.Drive == GroomerDrive.Hybrid ? " hybrid" : " electric") + (f.Dev ? " (dev)" : "");
                if (f.Tiller) f.Model += " + wider tiller";
                if (f.Dev) f.Model += " (dev)";
                f.Season = $"{v.SeasonMinutes[g] / 60} h";
                for (int s = 0; s < 3; s++)
                {
                    f.Crewed[s] = (v.Shifts[g] & (1 << s)) != 0;
                    f.PlanMin[s] = v.PlanMin[g * 3 + s];
                    f.TransitMin[s] = v.TransitMin[g * 3 + s];
                }
                m.Fleet.Add(f);
            }
            // Each shift's window: tonight's when it's planned, else last night's, else the rules'.
            int sz, se, ne;
            if (v != null && v.NightDay >= 0) { sz = v.SwingStartAbsMin; se = v.SwingEndAbsMin; ne = v.NightEndAbsMin; }
            else if (v != null && v.LastNightDay >= 0) { sz = v.LastSwingStartAbsMin; ne = v.LastNightEndAbsMin; se = sz + (ne - sz) / 2; }
            else { sz = -1; se = -1; ne = -1; }
            int[] winLen = { sz >= 0 ? se - sz : 0, sz >= 0 ? ne - se : 0, (p.GroomDayEndHour - p.GroomDayStartHour) * 60 };
            m.Shifts[0].Window = sz >= 0 ? $"{Hour(sz)}–{Hour(se)}" : "after the lifts stop";
            m.Shifts[1].Window = sz >= 0 ? $"{Hour(se)}–{Hour(ne)}" : "to before opening";
            m.Shifts[2].Window = $"{Hour(p.GroomDayStartHour * 60)}–{Hour(p.GroomDayEndHour * 60)}";
            // each crewed groomer's hours out on a shift (an electric one runs a charge's worth)
            foreach (var f in m.Fleet)
                for (int s = 0; s < 3; s++)
                    f.OpenMin[s] = !f.Crewed[s] ? 0 : f.Drive == GroomerDrive.Electric ? Math.Min(winLen[s], p.GroomElectricMinutes) : winLen[s];
            int[] crews = { v?.CrewsSwing ?? 0, v?.CrewsNight ?? 0, v?.CrewsDay ?? 0 };
            for (int s = 0; s < 3; s++)
            {
                var sh = m.Shifts[s];
                sh.Crews = crews[s];
                sh.Pinned = pins[s] != GroomCrews.Auto;
                if (crews[s] <= 0 || winLen[s] <= 0) { sh.Cost = "—"; continue; }
                long pay = (long)crews[s] * winLen[s] * p.GroomPayUsdHour * p.GroomShiftPayPermille[s] / 60_000;
                long fuel = 0;
                for (int g = 0; g < fleet && g < m.Fleet.Count; g++)
                    if (m.Fleet[g].Crewed[s] && Groomer.Valid(v.Model[g])) fuel += (long)Math.Max(0, winLen[s] - 60) * p.GroomFuelUsd[(int)v.Model[g] - 1] / 60;
                sh.Cost = Usd(pay + fuel);
            }
            m.TotalCost = v != null && fleet > 0 ? Usd(v.CostUsd) : "—";
            // ---------- the budget line (GO-14 v3) ----------
            m.Preview = v != null && v.Preview;
            if (v != null && fleet > 0)
            {
                foreach (var f in m.Fleet) { m.GroomMin += f.PlanTotal; m.DriveMin += f.TransitTotal; m.CrewedMin += f.OpenTotal; }
                m.UsedMin = m.GroomMin + m.DriveMin;
                m.ShortMin = Math.Max(0, v.ShortMinutes);
                m.WorkMin = m.UsedMin + m.ShortMin;
                string what = m.Preview ? "Tonight's plan (preview)" : m.DayShift ? "Today's plan" : "Tonight's plan";
                m.Budget = $"{what} · {H(m.WorkMin)} h of work · {H(m.UsedMin)} h fits";
                m.BudgetWarn = m.ShortMin > 0;
                m.BudgetRest = m.BudgetWarn ? $"{H(m.ShortMin)} h won't" : m.WorkMin > 0 ? "all of it" : "";
                m.BudgetCost = $"≈ {Usd(v.CostUsd)}";
                m.BudgetTip = $"The work: {H(m.GroomMin)} h grooming and {H(m.DriveMin)} h driving between the runs" +
                              (m.BudgetWarn ? $", and {H(m.ShortMin)} h that no groomer can fit in: crew another shift or buy a groomer" : "") +
                              $". The crews are out {H(m.CrewedMin)} h in all" +
                              (m.Preview ? ". A preview: the night is planned at the swing's start, as it stands then." : ".");
                m.BudgetShown = true;
            }
            // each lane's hours: its grooming and driving of its own hours tonight ("Idle" when crewed with nothing it can do)
            foreach (var lane in m.Lanes)
            {
                int used = 0;
                foreach (var b in lane.Blocks) if (!b.Over) used += b.End - b.Start;
                FleetRow fr = null;
                foreach (var f in m.Fleet) if (f.Number == lane.Number) fr = f;
                int open = fr?.OpenTotal ?? 0;
                lane.Hours = lastNight || fr == null ? $"{H(used)} h" : used == 0 && open > 0 ? "Idle" : $"{H(used)} of {H(open)} h";
            }
            m.FleetHeader = fleet == 0 ? "none yet" : $"Skogkatt · {fleet} groomer{(fleet == 1 ? "" : "s")}";
            foreach (var f in m.Fleet) if (f.Crewed[2] || f.UsedOn(2) > 0) m.DayColumn = true;
            if (m.Shifts[2].Crews > 0) m.DayColumn = true;
            m.CrewsPinned = m.Shifts[0].Pinned || m.Shifts[1].Pinned || m.Shifts[2].Pinned;
            m.CrewsLine = m.CrewsPinned ? "Crews: set by you" : "Crews: Auto staffs just enough";
            return m;
        }

        static string Who(Agg a, GroomView v, int swingEnd, bool day)
        {
            string g = a.Groomer > 0 && v != null ? $"G{NumberOf(v, a.Groomer, 255)}" : "";
            string when = day ? "day" : a.Start >= 0 && a.Start < swingEnd ? (a.End > swingEnd || a.End < 0 ? "all night" : "swing") : "night";
            return g.Length > 0 ? $"{g} {when}" : when;
        }

        static int NumberOf(GroomView v, int truncated, int mask)
        {
            int g = LaneOf(v, truncated, mask);
            return g >= 0 ? v.Number[g] : truncated;
        }

        static (string, GroomSeverity) TonightWhat(Agg a, int fleet, int swingEnd, bool day, GroomView v)
        {
            string job = a.Job == GroomJob.Push ? "Push" : "Till";
            switch (a.State)
            {
                case GroomRowState.Planned:
                case GroomRowState.Done:
                    return ($"{(a.State == GroomRowState.Done ? (a.Job == GroomJob.Push ? "Pushed" : "Tilled") : job)} · {Who(a, v, swingEnd, day)}", GroomSeverity.None);
                case GroomRowState.Partial:
                    return a.Short > 0 ? ($"Short by {H(a.Short)} h", GroomSeverity.Warn) : ($"{job} · {Who(a, v, swingEnd, day)}", GroomSeverity.None);
                case GroomRowState.PitchSkipped: return ("Pitch skipped", GroomSeverity.Warn);
                case GroomRowState.TooBare: return ("Too bare to till", GroomSeverity.Warn);
                case GroomRowState.NotReached: return (fleet > 0 ? "No equipment available" : "No groomer", GroomSeverity.Bad);   // owner 2026-11-21: generic
                case GroomRowState.NoGroomer: return ("No groomer", GroomSeverity.Bad);
                case GroomRowState.NoRoute: return ("No way there", GroomSeverity.Bad);
                case GroomRowState.PowderHeld: return ("Powder kept", GroomSeverity.Quiet);
                case GroomRowState.Never: return ("Never groomed", GroomSeverity.Quiet);
                case GroomRowState.Skipped: return ("Skipped tonight", GroomSeverity.Quiet);
                default: return ("Nothing tonight", GroomSeverity.Quiet);
            }
        }

        static (string, GroomSeverity) LastWhat(Agg a, int fleet)
        {
            switch (a.LastState)
            {
                case GroomRowState.Done:
                case GroomRowState.Planned:
                    return (a.LastGroomer > 0 ? $"Groomed · G{a.LastGroomer}" : "Groomed", GroomSeverity.None);
                case GroomRowState.Partial: return (a.LastGot > 0 ? $"Cut short · {H(a.LastGot)} h" : "Cut short", GroomSeverity.Warn);
                case GroomRowState.PitchSkipped: return ("Pitch skipped", GroomSeverity.Warn);
                case GroomRowState.TooBare: return ("Too bare to till", GroomSeverity.Warn);
                case GroomRowState.NotReached: return (fleet > 0 ? "No equipment available" : "No groomer", GroomSeverity.Bad);   // owner 2026-11-21: generic
                case GroomRowState.NoGroomer: return ("No groomer", GroomSeverity.Bad);
                case GroomRowState.NoRoute: return ("No way there", GroomSeverity.Bad);
                case GroomRowState.PowderHeld: return ("Powder kept", GroomSeverity.Quiet);
                case GroomRowState.Never: return ("Never groomed", GroomSeverity.Quiet);
                case GroomRowState.Skipped: return ("Skipped", GroomSeverity.Quiet);
                default: return ("Not groomed", GroomSeverity.Quiet);
            }
        }

        /// <summary>The trail card's line: when it was last groomed (to the hour) and tonight's job.</summary>
        static string CardLineOf(Agg a, GroomView v, int secondOfDay, int swingEnd, bool day)
        {
            string groomed = GroomedText(a.Since, secondOfDay);
            string tonight;
            switch (a.State)
            {
                case GroomRowState.Planned:
                case GroomRowState.Done:
                    tonight = a.Groomer > 0 && v != null ? $"tonight on Groomer {NumberOf(v, a.Groomer, 255)}" : "groomed tonight"; break;
                case GroomRowState.Partial: tonight = a.Short > 0 ? $"tonight short by {H(a.Short)} h" : a.Groomer > 0 && v != null ? $"tonight on Groomer {NumberOf(v, a.Groomer, 255)}" : "tonight in part"; break;
                case GroomRowState.NotReached: tonight = "not reached tonight"; break;
                case GroomRowState.NoGroomer: tonight = "no groomer tonight"; break;
                case GroomRowState.NoRoute: tonight = "no trail leads a groomer here"; break;
                case GroomRowState.PitchSkipped: tonight = "tonight, pitch skipped"; break;
                case GroomRowState.TooBare: tonight = "too bare to till tonight"; break;
                case GroomRowState.PowderHeld: tonight = "powder kept tonight"; break;
                case GroomRowState.Skipped: tonight = "skipped tonight"; break;
                case GroomRowState.Never: tonight = "never groomed"; break;
                default: tonight = "nothing tonight"; break;
            }
            if (day && (a.State == GroomRowState.Planned || a.State == GroomRowState.Done)) tonight = tonight.Replace("tonight", "today");
            return $"{groomed} · {tonight}";
        }

        /// <summary>"Groomed 3 AM", "Groomed 2 days ago", "Groomed more than 5 days ago" or "Not groomed yet".</summary>
        public static string GroomedText(int sinceHours, int secondOfDay)
        {
            if (sinceHours < 0) return "Not groomed yet";
            if (sinceHours >= 126) return "Groomed more than 5 days ago";
            if (sinceHours < 24) return "Groomed " + Hour((secondOfDay / 3600 - sinceHours) * 60);
            int days = sinceHours / 24;
            return days == 1 ? "Groomed yesterday" : $"Groomed {days} days ago";
        }

        // ---------- the cards and the catalogue ----------

        /// <summary>The Skogkatt catalogue (400, 400 W, 800, 800 W; diesel prices) from the figures.</summary>
        public static List<CatalogueRow> Catalogue(Pacing p = null)
        {
            p ??= Pacing.Default;
            var list = new List<CatalogueRow>();
            for (int i = 0; i < 4; i++)
            {
                var model = (GroomerModel)(i + 1);
                list.Add(new CatalogueRow
                {
                    Model = model, Name = ModelNames[i], Price = Usd(PriceUsd(model, GroomerDrive.Diesel, p)), Ps = ModelPs[i].ToString(Inv),
                    Till = (p.GroomTillCentiAcres[i] / 100.0).ToString("F1", Inv), Push = p.GroomPushYd3[i].ToString("#,0", Inv),
                    Slopes = Winch(model) ? $"to {p.GroomWinchMaxGrade} % · {WinchKn[i]} kN winch" : $"to {p.GroomWinchGrade} %",
                });
            }
            return list;
        }

        /// <summary>The groomer card's figures: (name, value) pairs, the mockup's .kv.</summary>
        public static List<(string K, string V)> GroomerFigures(FleetRow f, Pacing p = null)
        {
            p ??= Pacing.Default;
            var list = new List<(string, string)>();
            if (f == null || !Groomer.Valid(f.Kind)) return list;
            int i = (int)f.Kind - 1;
            double acres = p.GroomTillCentiAcres[i] / 100.0 * (f.Tiller ? p.GroomTillerPermille / 1000.0 : 1);
            list.Add(("Model", $"Skogkatt {ModelNames[i]}"));
            list.Add(("Power", $"{ModelPs[i]} PS"));
            list.Add(("Drive", DriveName(f.Drive)));
            list.Add(("Till", $"{acres.ToString("F1", Inv)} acres an hour"));
            list.Add(("Push", $"{p.GroomPushYd3[i].ToString("#,0", Inv)} yd³ an hour"));
            list.Add(("Slopes", Winch(f.Kind) ? $"to {p.GroomWinchMaxGrade} %" : $"to {p.GroomWinchGrade} %"));
            list.Add(("Half speed", $"past {p.GroomFullSpeedGrade} %"));
            list.Add(("This season", $"{f.SeasonMinutes / 60} h"));
            return list;
        }

        /// <summary>The groomer card's winch line.</summary>
        public static (string Value, string Hint) WinchText(GroomerModel m, Pacing p = null)
        {
            p ??= Pacing.Default;
            return Winch(m) ? ($"{WinchKn[(int)m - 1]} kN, built in", $"Steep pitches to {p.GroomWinchMaxGrade} %, at half speed.")
                            : ("None: a W model has one", $"Pitches past {p.GroomWinchGrade} % are skipped.");
        }

        /// <summary>The tiller's hint: "+12 % acres an hour; not on trails under 30 ft".</summary>
        public static string TillerHint(Pacing p = null)
        {
            p ??= Pacing.Default;
            int feet = (int)Math.Round(p.GroomTillerMinWidthMm / 304.8);
            return $"Wider: +{(p.GroomTillerPermille - 1000) / 10} % acres an hour; not on trails under {feet} ft.";
        }

        /// <summary>The groomer card's status beside its name ("Swing, night", "In the bay").</summary>
        public static string ShiftsText(FleetRow f)
        {
            if (f == null) return "";
            bool sw = f.Crewed[0], ni = f.Crewed[1], dy = f.Crewed[2];
            if (sw && ni) return "Swing, night";
            if (sw) return "Swing";
            if (ni) return "Night";
            if (dy) return "Day";
            return "In the bay";
        }

        /// <summary>A guess at what a groomer sells for: half its price with its drive.</summary>
        public static int SellUsd(FleetRow f, Pacing p = null) => f == null ? 0 : PriceUsd(f.Kind, f.Drive, p) / 2;

        /// <summary>The buy card's figures for a model and drive: (name, value) pairs, and the total.</summary>
        public static (List<(string K, string V)> Rows, string Total) BuyFigures(GroomerModel model, GroomerDrive drive, Pacing p = null)
        {
            p ??= Pacing.Default;
            var list = new List<(string, string)>();
            if (!Groomer.Valid(model) || !Groomer.Valid(drive)) return (list, "");
            int i = (int)model - 1;
            int list0 = p.GroomPriceUsd[i], total = PriceUsd(model, drive, p);
            list.Add(("Price", Usd(list0)));
            if (total != list0) list.Add((DriveName(drive), "+" + Usd(total - list0)));
            list.Add(("Power", $"{ModelPs[i]} PS"));
            list.Add(("Till", $"{(p.GroomTillCentiAcres[i] / 100.0).ToString("F1", Inv)} acres an hour"));
            list.Add(("Push", $"{p.GroomPushYd3[i].ToString("#,0", Inv)} yd³ an hour"));
            list.Add(("Slopes", Winch(model) ? $"to {p.GroomWinchMaxGrade} % on its {WinchKn[i]} kN winch" : $"to {p.GroomWinchGrade} %"));
            list.Add(("Runs", drive == GroomerDrive.Electric ? $"About {p.GroomElectricMinutes / 60} h a charge" : "The whole shift"));
            return (list, Usd(total));
        }
    }
}
