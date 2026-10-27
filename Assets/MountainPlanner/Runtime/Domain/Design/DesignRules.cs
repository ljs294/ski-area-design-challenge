using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The document's own invariants, shared by every command and by <see cref="DesignDocument.From"/>. Each returns
    /// null when fine, or the reason in the player's words. Rules that need terrain or geometry (siting, clearance,
    /// spacing, a connected footprint) are task 08's and 09's analyses, not document invariants.
    /// </summary>
    public static class DesignRules
    {
        public const int NameMaxLength = 64;
        public const int MaxPointsPerStroke = 100_000;
        public const int MaxStrokesPerPart = 10_000;
        public const int MaxPartsPerTrail = 256;
        public const int MaxCutsPerPart = 256;
        public const int MaxTowersPerLift = 400;
        /// <summary>TB-14: a stroke holds at most 4,096 dabs (about 2 minutes held); a longer drag carries on as a new stroke.</summary>
        public const int MaxDabsPerStroke = 4096;
        /// <summary>A design holds at most this many strokes (about 3 MB of save.json at full length).</summary>
        public const int MaxStrokes = 50_000;

        public static string NameProblem(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "A name can't be empty.";
            if (name.Length > NameMaxLength) return $"A name can be at most {NameMaxLength} characters.";
            if (name.Trim() != name) return "A name can't start or end with a space.";
            foreach (char c in name) if (char.IsControl(c)) return "A name can't hold control characters.";
            return null;
        }

        /// <summary>TR-01: 25–400 ft in 5 ft steps.</summary>
        public static string BrushWidthProblem(int widthMm)
        {
            if (widthMm < DesignUnits.BrushMinMm || widthMm > DesignUnits.BrushMaxMm || widthMm % DesignUnits.FiveFeetMm != 0)
                return "A brush is 25–400 ft wide, in 5 ft steps.";
            return null;
        }

        /// <summary>TW-02: 25–100 ft in 5 ft steps; v2, a station tower (within 100 ft of a bullwheel) from 15 ft.</summary>
        public static string TowerHeightProblem(int heightMm, bool station = false, int maxMm = DesignUnits.TowerMaxMm)
        {
            int min = station ? DesignUnits.StationTowerMinMm : DesignUnits.TowerMinMm;
            if (heightMm < min || heightMm > maxMm || heightMm % DesignUnits.FiveFeetMm != 0)
            {
                int top = maxMm / DesignUnits.FiveFeetMm * 5;
                return station ? $"A tower by a terminal is 15–{top} ft tall, in 5 ft steps." : $"A tower is 25–{top} ft tall, in 5 ft steps.";
            }
            return null;
        }

        /// <summary>TW-02 v2: whether a tower stands in a station zone, within 100 ft of either bullwheel.</summary>
        public static bool InStationZone(long lineLengthSquaredMm, int alongMm)
        {
            double length = Math.Sqrt(lineLengthSquaredMm);
            return alongMm <= DesignUnits.StationZoneMm || length - alongMm <= DesignUnits.StationZoneMm;
        }

        public static string PartsProblem(IReadOnlyList<FootprintPart> parts)
        {
            if (parts == null || parts.Count == 0) return "A trail needs a painted footprint.";
            if (parts.Count > MaxPartsPerTrail) return "A trail has too many parts.";
            bool painted = false;
            foreach (var part in parts)
            {
                if (part == null || part.Strokes.Count == 0) return "Each part of a footprint needs a stroke.";
                if (part.Strokes.Count > MaxStrokesPerPart) return "A footprint has too many strokes.";
                if (part.Cuts.Count > MaxCutsPerPart) return "A footprint has too many splits.";
                if (part.Strokes[0].Erase) return "A footprint starts with paint, not an erase.";
                foreach (var stroke in part.Strokes)
                {
                    if (stroke == null || stroke.Points.Count == 0) return "A stroke needs a point.";
                    if (stroke.Points.Count > MaxPointsPerStroke) return "A stroke has too many points.";
                    string w = BrushWidthProblem(stroke.WidthMm);
                    if (w != null) return w;
                    foreach (var p in stroke.Points) if (!p.InRange) return "A stroke is outside the area.";
                    painted |= !stroke.Erase;
                }
                foreach (var cut in part.Cuts)
                {
                    string c = CutProblem(cut.At, cut.DirX, cut.DirY);
                    if (c != null) return c;
                }
            }
            return painted ? null : "A trail needs a painted footprint.";
        }

        public static string CutProblem(PointMm at, int dirX, int dirY)
        {
            if (!at.InRange) return "The split is outside the area.";
            if (dirX == 0 && dirY == 0) return "A split needs a direction.";
            if (System.Math.Abs((long)dirX) > DesignUnits.DirectionLimitMm || System.Math.Abs((long)dirY) > DesignUnits.DirectionLimitMm)
                return "A split's direction is too long.";
            return null;
        }

        /// <summary>TR-03: the head and tail are both set or both unset, and differ.</summary>
        public static string EndsProblem(PointMm? head, PointMm? tail)
        {
            if (head.HasValue != tail.HasValue) return "A trail needs both a head and a tail, or neither.";
            if (head.HasValue && (!head.Value.InRange || !tail.Value.InRange)) return "A trail's end is outside the area.";
            if (head.HasValue && head.Value == tail.Value) return "The head and tail can't be the same point.";
            return null;
        }

        public static string SettingsProblem(TrailSettings settings)
        {
            if (!settings.Defined) return "Unknown trail settings.";
            if (!settings.AvailableNow) return "Glades, zones and thinning come later; Phase 3 builds cleared runs.";
            return null;
        }

        public static string StageProblem(WorkStage stage) =>
            stage == WorkStage.Plan || stage == WorkStage.Built ? null : "Unknown stage.";

        /// <summary>LT41: only types with their own models can be added (the rest are in the menu as placeholders). A design
        /// that already holds one (an older save) still loads.</summary>
        public static string BuildableProblem(string catalogId) =>
            LiftCatalogIds.Buildable(catalogId) ? null : "That lift type can't be built yet: its models are still to come.";

        public static string LiftLineProblem(string catalogId, PointMm bottom, PointMm top, TowerMode mode, TowerStance stance)
        {
            if (!LiftCatalogIds.Known(catalogId)) return "Unknown lift type.";
            if (!bottom.InRange || !top.InRange) return "A terminal is outside the area.";
            if (bottom == top) return "A lift needs two different terminals.";
            if (mode != TowerMode.Automatic && mode != TowerMode.ByHand) return "Unknown tower mode.";
            if (stance != TowerStance.Plumb && stance != TowerStance.Leaned) return "Unknown tower stance.";
            return null;
        }

        /// <summary>A tower stands strictly between the terminals' origins (TW-15); TW-03's spacing is task 09's.</summary>
        public static string TowerAlongProblem(long lineLengthSquaredMm, int alongMm)
        {
            if (alongMm <= 0 || (long)alongMm * alongMm >= lineLengthSquaredMm) return "A tower stands between the terminals.";
            return null;
        }

        public static string TowersProblem(long lineLengthSquaredMm, IReadOnlyList<Tower> towers, string catalogId = null)
        {
            if (towers.Count > MaxTowersPerLift) return "A lift has too many towers.";
            for (int i = 0; i < towers.Count; i++)
            {
                string p = TowerAlongProblem(lineLengthSquaredMm, towers[i].AlongMm)
                           ?? TowerHeightProblem(towers[i].HeightMm, InStationZone(lineLengthSquaredMm, towers[i].AlongMm), LiftCatalogIds.TowerMaxMm(catalogId));
                if (p != null) return p;
                if (towers[i].Id.IsNone) return "A tower needs an ID.";
                if (i > 0 && towers[i].AlongMm == towers[i - 1].AlongMm) return "Two towers can't stand in the same place.";
            }
            return null;
        }

        internal static string TrailProblem(Trail t, bool settingsMustBeAvailable)
        {
            if (t.Id.IsNone) return "A trail needs an ID.";
            return NameProblem(t.Name) ?? StageProblem(t.Stage) ?? PartsProblem(t.Parts) ?? EndsProblem(t.Head, t.Tail)
                ?? (settingsMustBeAvailable ? SettingsProblem(t.Settings) : (t.Settings.Defined ? null : "Unknown trail settings."));
        }

        internal static string LiftProblem(Lift l)
        {
            if (l.Id.IsNone) return "A lift needs an ID.";
            return NameProblem(l.Name) ?? StageProblem(l.Stage) ?? LiftLineProblem(l.CatalogId, l.Bottom, l.Top, l.TowerMode, l.Stance)
                ?? TowersProblem(l.LengthSquaredMm, l.Towers, l.CatalogId);
        }

        /// <summary>A whole document, as read back: every entity valid, IDs unique across kinds.</summary>
        /// <summary>
        /// TB-02 to TB-10: a stroke's numbers are in range and only the ones its kind uses are set. Elevations stay within
        /// ±10 km of the datum, a diameter is 10–400 ft in 5 ft steps, the limit at most TB-10's 40 ft.
        /// </summary>
        public static string StrokeProblem(BrushSettings s, IReadOnlyList<BrushDab> dabs)
        {
            const int elevation = 10_000_000;
            if (s.Kind < BrushKind.Raise || s.Kind > BrushKind.Slope) return "A stroke's brush isn't one this build knows.";
            if (s.RadiusMm < TerrainBrush.RadiusMinMm || s.RadiusMm > TerrainBrush.RadiusMaxMm || s.RadiusMm % (DesignUnits.FiveFeetMm / 2) != 0)
                return "A brush is 10–400 ft across, in 5 ft steps.";
            if (s.FalloffPermille < 0 || s.FalloffPermille > 1000) return "A brush's soft edge is out of range.";
            if (s.LimitMm < 1 || s.LimitMm > TerrainBrush.LimitMm) return "A stroke's depth limit is out of range.";
            bool raise = s.Kind == BrushKind.Raise || s.Kind == BrushKind.Lower;
            if (raise ? s.StepMm < 1 || s.StepMm > TerrainBrush.StepMaxMm : s.StepMm != 0) return "A stroke's step is out of range.";
            if (raise ? s.StrengthPermille != 0 : s.StrengthPermille < 1 || s.StrengthPermille > 1000) return "A stroke's strength is out of range.";
            bool target = s.Kind == BrushKind.Flatten || s.Kind == BrushKind.Slope;
            if (target ? s.TargetMm < -elevation || s.TargetMm > elevation : s.TargetMm != 0) return "A stroke's target elevation is out of range.";
            bool slope = s.Kind == BrushKind.Slope;
            if (slope ? s.StartMm < -elevation || s.StartMm > elevation : s.StartMm != 0) return "A stroke's start elevation is out of range.";
            if (slope ? OutOfRange(s.StartAt) || OutOfRange(s.TargetAt) : !s.StartAt.Equals(default(PointMm)) || !s.TargetAt.Equals(default(PointMm)))
                return "A stroke's start or target point is out of range.";
            if (dabs == null || dabs.Count == 0) return "A stroke needs at least one dab.";
            if (dabs.Count > MaxDabsPerStroke) return $"A stroke holds at most {MaxDabsPerStroke} dabs.";
            foreach (var d in dabs)
            {
                if (Math.Abs((long)d.At.X) > DesignUnits.CoordinateLimitMm || Math.Abs((long)d.At.Y) > DesignUnits.CoordinateLimitMm)
                    return "A dab is out of range.";
                if (d.WeightPermille < 1 || d.WeightPermille > 1000) return "A dab's weight is out of range.";
            }
            return null;
        }

        static bool OutOfRange(PointMm p) =>
            Math.Abs((long)p.X) > DesignUnits.CoordinateLimitMm || Math.Abs((long)p.Y) > DesignUnits.CoordinateLimitMm;

        internal static string DocumentProblem(DesignDocument doc)
        {
            if (doc.NextOrdinal < 1) return "The ID counter is invalid.";
            var seen = new HashSet<ulong>();
            for (int i = 0; i < doc.Trails.Count; i++)
            {
                // A saved file may hold settings a later phase allows; only commands gate availability.
                string p = TrailProblem(doc.Trails[i], false);
                if (p != null) return p;
                if (!seen.Add(doc.Trails[i].Id.Value)) return "Two entities share an ID.";
            }
            for (int i = 0; i < doc.Lifts.Count; i++)
            {
                string p = LiftProblem(doc.Lifts[i]);
                if (p != null) return p;
                if (!seen.Add(doc.Lifts[i].Id.Value)) return "Two entities share an ID.";
                foreach (var tower in doc.Lifts[i].Towers) if (!seen.Add(tower.Id.Value)) return "Two entities share an ID.";
            }
            if (doc.Strokes.Count > MaxStrokes) return "The design holds too many terrain strokes.";
            for (int i = 0; i < doc.Strokes.Count; i++)
            {
                var k = doc.Strokes[i];
                if (k.Id.IsNone) return "A terrain stroke has no ID.";
                string p = StrokeProblem(k.Settings, k.Dabs);
                if (p != null) return p;
                if (!seen.Add(k.Id.Value)) return "Two entities share an ID.";
            }
            return null;
        }
    }
}
