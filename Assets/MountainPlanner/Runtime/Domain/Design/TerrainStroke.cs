using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>What a terrain brush stroke does (TB-01): the Terrain tab's four tools, Raise/Lower being two kinds.</summary>
    public enum BrushKind : byte
    {
        /// <summary>TB-04: left held raises.</summary>
        Raise = 1,
        /// <summary>TB-04: right held lowers.</summary>
        Lower = 2,
        /// <summary>TB-05: toward one target elevation.</summary>
        Flatten = 3,
        /// <summary>TB-06: toward the local mean.</summary>
        Smooth = 4,
        /// <summary>TB-07: toward the even ramp between the start point and the target point.</summary>
        Slope = 5,
    }

    /// <summary>
    /// Everything a stroke was painted with (TB-14): every effective number is stored, so a later change to a default
    /// never moves saved ground. Fields a kind doesn't use are 0. Elevations are absolute whole millimetres in the
    /// package's height datum, like a lift's terminal levels (SK14); lengths are whole millimetres.
    /// </summary>
    public readonly struct BrushSettings : IEquatable<BrushSettings>
    {
        public readonly BrushKind Kind;
        /// <summary>Half the diameter (TB-02: 10–400 ft across).</summary>
        public readonly int RadiusMm;
        /// <summary>The soft outer band's width as a share of the radius, ‰ (0 is a hard disc).</summary>
        public readonly int FalloffPermille;
        /// <summary>Raise/Lower: how far one full-weight dab moves the centre.</summary>
        public readonly int StepMm;
        /// <summary>Flatten, Smooth, Slope: the share of the way one full-weight dab moves the ground, ‰.</summary>
        public readonly int StrengthPermille;
        /// <summary>Flatten, Slope: the target elevation.</summary>
        public readonly int TargetMm;
        /// <summary>Slope: the start's elevation (the ground at the press).</summary>
        public readonly int StartMm;
        /// <summary>Slope: where the ramp starts (the press) and ends (the right click), in the local frame (TE5, BR16).</summary>
        public readonly PointMm StartAt, TargetAt;
        /// <summary>TB-10: the most any point may stand from natural ground.</summary>
        public readonly int LimitMm;
        /// <summary>
        /// TB-10 v2 (owner BR22, shape 9): a cell the stroke moves this far or more loses its trees, the moment the ground
        /// moves; <see cref="TerrainBrush.ClearMm"/> (1 ft) with the pane's Clear trees on, 0 with it off (the stroke clears
        /// nothing and the trees ride the new ground). Strokes from before shape 9 cleared at 1 ft.
        /// </summary>
        public readonly int ClearMm;

        public BrushSettings(BrushKind kind, int radiusMm, int falloffPermille, int stepMm, int strengthPermille,
                             int targetMm, int startMm, PointMm startAt, PointMm targetAt, int limitMm, int clearMm = TerrainBrush.ClearMm)
        {
            Kind = kind;
            RadiusMm = radiusMm;
            FalloffPermille = falloffPermille;
            StepMm = stepMm;
            StrengthPermille = strengthPermille;
            TargetMm = targetMm;
            StartMm = startMm;
            StartAt = startAt;
            TargetAt = targetAt;
            LimitMm = limitMm;
            ClearMm = clearMm;
        }

        /// <summary>Whether the stroke clears trees (TB-10 v2): the pane's Clear trees.</summary>
        public bool ClearsTrees => ClearMm > 0;

        /// <summary>The same settings with Clear trees on or off (BR22).</summary>
        public BrushSettings WithClearing(bool clear) =>
            new BrushSettings(Kind, RadiusMm, FalloffPermille, StepMm, StrengthPermille, TargetMm, StartMm, StartAt, TargetAt, LimitMm, clear ? TerrainBrush.ClearMm : 0);

        public static BrushSettings RaiseOrLower(bool lower, int radiusMm, int stepMm) =>
            new BrushSettings(lower ? BrushKind.Lower : BrushKind.Raise, radiusMm, TerrainBrush.FalloffPermille, stepMm, 0, 0, 0, default, default, TerrainBrush.LimitMm);

        public static BrushSettings Flatten(int radiusMm, int strengthPermille, int targetMm) =>
            new BrushSettings(BrushKind.Flatten, radiusMm, TerrainBrush.FalloffPermille, 0, strengthPermille, targetMm, 0, default, default, TerrainBrush.LimitMm);

        public static BrushSettings Smooth(int radiusMm, int strengthPermille) =>
            new BrushSettings(BrushKind.Smooth, radiusMm, TerrainBrush.FalloffPermille, 0, strengthPermille, 0, 0, default, default, TerrainBrush.LimitMm);

        public static BrushSettings Slope(int radiusMm, int strengthPermille, PointMm startAt, int startMm, PointMm targetAt, int targetMm) =>
            new BrushSettings(BrushKind.Slope, radiusMm, TerrainBrush.FalloffPermille, 0, strengthPermille, targetMm, startMm, startAt, targetAt, TerrainBrush.LimitMm);

        public bool Equals(BrushSettings o) => Kind == o.Kind && RadiusMm == o.RadiusMm && FalloffPermille == o.FalloffPermille && StepMm == o.StepMm
            && StrengthPermille == o.StrengthPermille && TargetMm == o.TargetMm && StartMm == o.StartMm && StartAt.Equals(o.StartAt) && TargetAt.Equals(o.TargetAt) && LimitMm == o.LimitMm && ClearMm == o.ClearMm;
        public override bool Equals(object obj) => obj is BrushSettings o && Equals(o);
        public override int GetHashCode() => ((int)Kind * 397) ^ RadiusMm ^ (TargetMm << 3) ^ (StepMm << 7) ^ StrengthPermille;
    }

    /// <summary>One dab (TB-03): where, in the local frame (TE5), and its share of a tick's weight, ‰.</summary>
    public readonly struct BrushDab : IEquatable<BrushDab>
    {
        public readonly PointMm At;
        public readonly int WeightPermille;
        public BrushDab(PointMm at, int weightPermille) { At = at; WeightPermille = weightPermille; }
        public bool Equals(BrushDab o) => At.Equals(o.At) && WeightPermille == o.WeightPermille;
        public override bool Equals(object obj) => obj is BrushDab o && Equals(o);
        public override int GetHashCode() => At.GetHashCode() * 31 + WeightPermille;
    }

    /// <summary>A terrain stroke's ID, keyed like <see cref="TrailId"/>.</summary>
    public readonly struct TerrainStrokeId : IEquatable<TerrainStrokeId>, IComparable<TerrainStrokeId>
    {
        public readonly ulong Value;
        public TerrainStrokeId(ulong value) { Value = value; }
        public static TerrainStrokeId Make(ulong seed, long ordinal) => new TerrainStrokeId(DesignKey.Hash(seed, DesignKey.TerrainStrokeTag, (ulong)ordinal));
        public bool IsNone => Value == 0;
        public bool Equals(TerrainStrokeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TerrainStrokeId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(TerrainStrokeId other) => Value.CompareTo(other.Value);
        public static bool operator ==(TerrainStrokeId a, TerrainStrokeId b) => a.Value == b.Value;
        public static bool operator !=(TerrainStrokeId a, TerrainStrokeId b) => a.Value != b.Value;
        public override string ToString() => "stroke:" + Value.ToString("x16");
    }

    /// <summary>
    /// A confirmed terrain brush stroke (task 15, TB-14): authored data the earthwork replay folds after every trail and
    /// lift (<see cref="BrushEarthwork"/>). Immutable. It has no stage: a stroke exists once its session is confirmed (TB-08).
    /// </summary>
    public sealed class TerrainStroke
    {
        public TerrainStrokeId Id { get; }
        public BrushSettings Settings { get; }
        public IReadOnlyList<BrushDab> Dabs => _dabs;

        readonly BrushDab[] _dabs;

        public TerrainStroke(TerrainStrokeId id, BrushSettings settings, IReadOnlyList<BrushDab> dabs)
        {
            Id = id;
            Settings = settings;
            _dabs = new BrushDab[dabs?.Count ?? 0];
            for (int i = 0; i < _dabs.Length; i++) _dabs[i] = dabs[i];
        }

        /// <summary>The same painting (settings and dabs), whatever the ID.</summary>
        public bool SamePainting(TerrainStroke o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o == null || !Settings.Equals(o.Settings) || _dabs.Length != o._dabs.Length) return false;
            for (int i = 0; i < _dabs.Length; i++) if (!_dabs[i].Equals(o._dabs[i])) return false;
            return true;
        }
    }
}
