using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// SN-22 v4 (task 4.25, shape 17): what the player asks of a trail's grooming. <see cref="Default"/> is Autopilot on a
    /// run and Never on a glade or a zone (4.07's rule before 4.25: runs nightly, glades and zones never). Values are saved:
    /// never renumber.
    /// </summary>
    public enum GroomSetting : byte { Default = 0, Autopilot = 1, EveryNight = 2, UnlessSnowing = 3, WhenOrdered = 4, Never = 5 }

    /// <summary>SN-22 v4: a trail's place in the night's order. Values are saved: never renumber.</summary>
    public enum GroomPriority : byte { Normal = 0, High = 1, Low = 2 }

    /// <summary>GO-01 (task 4.25, owner round 6): the maker's two sizes, each also as a W with a winch. Values are saved.</summary>
    public enum GroomerModel : byte { S400 = 1, S400W = 2, S800 = 3, S800W = 4 }

    /// <summary>GO-01: a groomer's drive (a different drive is a different machine). Values are saved.</summary>
    public enum GroomerDrive : byte { Diesel = 1, Hybrid = 2, Electric = 3 }

    /// <summary>A trail's grooming setting and priority (SN-22 v4), held whole on a run's own trail and on a shaped trail.</summary>
    public readonly struct TrailGrooming : IEquatable<TrailGrooming>
    {
        public readonly GroomSetting Setting;
        public readonly GroomPriority Priority;

        public TrailGrooming(GroomSetting setting, GroomPriority priority) { Setting = setting; Priority = priority; }

        /// <summary>The setting that applies: <see cref="GroomSetting.Default"/> made Autopilot on a run, Never otherwise.</summary>
        public GroomSetting Resolve(TrailKind kind) =>
            Setting != GroomSetting.Default ? Setting : kind == TrailKind.Run ? GroomSetting.Autopilot : GroomSetting.Never;

        public bool IsDefault => Setting == GroomSetting.Default && Priority == GroomPriority.Normal;
        public bool Equals(TrailGrooming o) => Setting == o.Setting && Priority == o.Priority;
        public override bool Equals(object obj) => obj is TrailGrooming o && Equals(o);
        public override int GetHashCode() => (int)Setting * 8 + (int)Priority;

        public static bool Valid(GroomSetting s) => s <= GroomSetting.Never;
        public static bool Valid(GroomPriority p) => p <= GroomPriority.Low;
    }

    /// <summary>One groomer the resort owns (GO-01): its number (Groomer 1, 2, …, never reused while it stands), model,
    /// drive and whether its wider tiller is fitted.</summary>
    public sealed class Groomer
    {
        public int Number { get; }
        public GroomerModel Model { get; }
        public GroomerDrive Drive { get; }
        public bool WideTiller { get; }

        public Groomer(int number, GroomerModel model, GroomerDrive drive, bool wideTiller = false)
        {
            Number = number; Model = model; Drive = drive; WideTiller = wideTiller;
        }

        public Groomer WithTiller(bool on) => new Groomer(Number, Model, Drive, on);
        public bool HasWinch => Model == GroomerModel.S400W || Model == GroomerModel.S800W;
        public static bool Valid(GroomerModel m) => m >= GroomerModel.S400 && m <= GroomerModel.S800W;
        public static bool Valid(GroomerDrive d) => d >= GroomerDrive.Diesel && d <= GroomerDrive.Electric;
    }

    /// <summary>GO-05: the crews the player pinned per shift (<see cref="Auto"/>: Autopilot staffs just enough).</summary>
    public readonly struct GroomCrews : IEquatable<GroomCrews>
    {
        public const int Auto = -1;
        public readonly int Swing, Night, Day;

        public GroomCrews(int swing, int night, int day) { Swing = swing; Night = night; Day = day; }

        public static GroomCrews AllAuto => new GroomCrews(Auto, Auto, Auto);
        public int this[int shift] => shift == 0 ? Swing : shift == 1 ? Night : Day;
        public bool Equals(GroomCrews o) => Swing == o.Swing && Night == o.Night && Day == o.Day;
        public override bool Equals(object obj) => obj is GroomCrews o && Equals(o);
        public override int GetHashCode() => (Swing + 2) * 1369 + (Night + 2) * 37 + Day + 2;
    }

    /// <summary>
    /// GO-01, GO-05 (task 4.25, shape 17): the resort's groomers in number order and its crew pins. A new resort has none
    /// (owner round 1); an older save is given a free fleet of diesel 400s sized to its usual night (save v17).
    /// </summary>
    public sealed class GroomFleet
    {
        /// <summary>The most groomers a resort may own (the snapshot's lanes).</summary>
        public const int MaxGroomers = 32;

        readonly Groomer[] _groomers;
        public IReadOnlyList<Groomer> Groomers => _groomers;
        public GroomCrews Crews { get; }

        public static readonly GroomFleet Empty = new GroomFleet(Array.Empty<Groomer>(), GroomCrews.AllAuto);

        GroomFleet(Groomer[] groomers, GroomCrews crews) { _groomers = groomers; Crews = crews; }

        /// <summary>A fleet from stored parts, sorted by number; throws <see cref="FormatException"/> when it can't be one.</summary>
        public static GroomFleet From(IReadOnlyList<Groomer> groomers, GroomCrews crews)
        {
            var g = new Groomer[groomers?.Count ?? 0];
            for (int i = 0; i < g.Length; i++) g[i] = groomers[i];
            Array.Sort(g, (a, b) => a.Number.CompareTo(b.Number));
            var f = new GroomFleet(g, crews);
            if (f.Problem() is string p) throw new FormatException("Grooming: " + p);
            return f;
        }

        public bool IsEmpty => _groomers.Length == 0 && Crews.Equals(GroomCrews.AllAuto);
        public int NextNumber => _groomers.Length == 0 ? 1 : _groomers[_groomers.Length - 1].Number + 1;

        public Groomer Find(int number)
        {
            foreach (var g in _groomers) if (g.Number == number) return g;
            return null;
        }

        public GroomFleet WithAdded(Groomer g)
        {
            var a = new Groomer[_groomers.Length + 1];
            Array.Copy(_groomers, a, _groomers.Length);
            a[_groomers.Length] = g;
            Array.Sort(a, (x, y) => x.Number.CompareTo(y.Number));
            return new GroomFleet(a, ClampCrews(Crews, a.Length));
        }

        public GroomFleet WithRemoved(int number)
        {
            var a = new List<Groomer>(_groomers.Length);
            foreach (var g in _groomers) if (g.Number != number) a.Add(g);
            return new GroomFleet(a.ToArray(), ClampCrews(Crews, a.Count));
        }

        public GroomFleet WithReplaced(Groomer g)
        {
            var a = (Groomer[])_groomers.Clone();
            for (int i = 0; i < a.Length; i++) if (a[i].Number == g.Number) a[i] = g;
            return new GroomFleet(a, Crews);
        }

        public GroomFleet WithCrews(GroomCrews crews) => new GroomFleet(_groomers, crews);

        /// <summary>A pinned crew count never exceeds the fleet (a sold groomer takes its crew with it).</summary>
        static GroomCrews ClampCrews(GroomCrews c, int n) =>
            new GroomCrews(c.Swing > n ? n : c.Swing, c.Night > n ? n : c.Night, c.Day > n ? n : c.Day);

        public string Problem()
        {
            if (_groomers.Length > MaxGroomers) return $"a resort owns at most {MaxGroomers} groomers.";
            for (int i = 0; i < _groomers.Length; i++)
            {
                var g = _groomers[i];
                if (g == null) return "a groomer is missing.";
                if (g.Number < 1 || g.Number > 9999) return "a groomer's number is out of range.";
                if (i > 0 && _groomers[i - 1].Number == g.Number) return "two groomers share a number.";
                if (!Groomer.Valid(g.Model)) return "a groomer's model isn't one this build knows.";
                if (!Groomer.Valid(g.Drive)) return "a groomer's drive isn't one this build knows.";
            }
            for (int s = 0; s < 3; s++)
                if (Crews[s] < GroomCrews.Auto || Crews[s] > _groomers.Length) return "a pinned crew count is out of range.";
            return null;
        }
    }
}
