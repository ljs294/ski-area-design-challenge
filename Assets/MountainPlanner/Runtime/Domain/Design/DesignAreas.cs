using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>An Area's saved ID (NW-15), keyed like <see cref="TrailId"/>.</summary>
    public readonly struct AreaId : IEquatable<AreaId>, IComparable<AreaId>
    {
        public readonly ulong Value;
        public AreaId(ulong value) { Value = value; }
        public bool IsNone => Value == 0;
        public bool Equals(AreaId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is AreaId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(AreaId other) => Value.CompareTo(other.Value);
        public static bool operator ==(AreaId a, AreaId b) => a.Value == b.Value;
        public static bool operator !=(AreaId a, AreaId b) => a.Value != b.Value;
        public override string ToString() => "area:" + Value.ToString("x16");
    }

    /// <summary>One lift end an Area held when it was named: the lift, and whether it's the bottom.</summary>
    public readonly struct AreaEnd : IEquatable<AreaEnd>
    {
        public readonly LiftId Lift;
        public readonly bool Bottom;
        public AreaEnd(LiftId lift, bool bottom) { Lift = lift; Bottom = bottom; }
        public bool Equals(AreaEnd o) => Lift == o.Lift && Bottom == o.Bottom;
        public override bool Equals(object obj) => obj is AreaEnd o && Equals(o);
        public override int GetHashCode() => Lift.GetHashCode() * 2 + (Bottom ? 1 : 0);
        internal int CompareTo(AreaEnd o) => Lift != o.Lift ? Lift.CompareTo(o.Lift) : Bottom == o.Bottom ? 0 : Bottom ? -1 : 1;
    }

    /// <summary>
    /// A named Area (NW-15, owner NT19): the player's name and the lift ends the hub held when they named it. Hubs are
    /// derived (NW-14), so the name finds its hub again by those ends (<see cref="DesignAreas.Of"/>). Immutable; ends by
    /// lift ID, the bottom first.
    /// </summary>
    public sealed class AreaRecord
    {
        public AreaId Id { get; }
        public string Name { get; }
        public IReadOnlyList<AreaEnd> Ends => _ends;

        readonly AreaEnd[] _ends;

        public AreaRecord(AreaId id, string name, IReadOnlyList<AreaEnd> ends)
        {
            Id = id;
            Name = name ?? "";
            _ends = new AreaEnd[ends?.Count ?? 0];
            for (int i = 0; i < _ends.Length; i++) _ends[i] = ends[i];
            Array.Sort(_ends, (a, b) => a.CompareTo(b));
        }

        internal AreaRecord WithoutLift(LiftId lift)
        {
            var kept = new List<AreaEnd>(_ends.Length);
            foreach (var e in _ends) if (e.Lift != lift) kept.Add(e);
            return kept.Count == _ends.Length ? this : new AreaRecord(Id, Name, kept);
        }

        internal bool Holds(StationMember m)
        {
            foreach (var e in _ends) if (e.Lift == m.Lift && e.Bottom == m.Bottom) return true;
            return false;
        }
    }

    /// <summary>An Area as it stands now: its hub, its name (the player's, or the default), and the record behind it.</summary>
    public sealed class Area
    {
        public Station Station { get; }
        public string Name { get; }
        /// <summary>The player's name, or null while it goes by its default.</summary>
        public AreaRecord Record { get; }
        public bool Named => Record != null;

        internal Area(Station station, string name, AreaRecord record)
        {
            Station = station;
            Name = name;
            Record = record;
        }

        /// <summary>The ends it holds now, for <see cref="RenameArea"/>.</summary>
        public AreaEnd[] Ends()
        {
            var ends = new AreaEnd[Station.Members.Count];
            for (int i = 0; i < ends.Length; i++) ends[i] = new AreaEnd(Station.Members[i].Lift, Station.Members[i].Bottom);
            return ends;
        }
    }

    /// <summary>
    /// NW-15 (owner NT19, 2026-10-26): every hub holding two or more lifts is an Area. A saved name binds to the hub that
    /// shares the most of its ends (records in ID order, each hub taken once), so it keeps through growth, merges (the
    /// older name wins) and lifts moved by Edit lift; a split brings the other name back. Others go by their default name.
    /// </summary>
    public static class DesignAreas
    {
        public static List<Area> Of(DesignDocument doc, IReadOnlyList<Station> stations)
        {
            var hubs = new List<Station>();
            foreach (var st in stations) if (st.IsArea) hubs.Add(st);
            var bound = new Dictionary<ulong, AreaRecord>();
            foreach (var record in doc.Areas)   // in ID order
            {
                Station best = null;
                int shared = 0;
                foreach (var st in hubs)   // in station ID order, so a tie keeps the lower
                {
                    if (bound.ContainsKey(st.Id)) continue;
                    int k = 0;
                    foreach (var m in st.Members) if (record.Holds(m)) k++;
                    if (k > shared) { shared = k; best = st; }
                }
                if (best != null) bound.Add(best.Id, record);
            }
            var areas = new List<Area>(hubs.Count);
            foreach (var st in hubs)
                areas.Add(bound.TryGetValue(st.Id, out var r) ? new Area(st, r.Name, r) : new Area(st, DefaultName(doc, st), null));
            return areas;
        }

        /// <summary>
        /// The name an Area goes by until it's named: its lifts by name, then "base" when only bottoms load there, "top"
        /// when only tops unload there, else "transfer" ("Chair 1 &amp; Chair 2 base", "Chair 3, Chair 4 &amp; Chair 5 top").
        /// </summary>
        public static string DefaultName(DesignDocument doc, Station st)
        {
            var names = new List<string>();
            foreach (var id in st.Lifts) names.Add(doc.FindLift(id)?.Name ?? "Lift");
            names.Sort(string.CompareOrdinal);
            bool bottoms = true, tops = true;
            foreach (var m in st.Members) { if (m.Bottom) tops = false; else bottoms = false; }
            string lifts = names.Count <= 2 ? string.Join(" & ", names) : string.Join(", ", names.GetRange(0, names.Count - 1)) + " & " + names[names.Count - 1];
            return lifts + (bottoms ? " base" : tops ? " top" : " transfer");
        }
    }

    /// <summary>
    /// Names an Area (NW-15, NT19): one undoable step. With <see cref="Record"/> set it renames that record and gives it the
    /// hub's ends now; without, it makes a new record from <see cref="Ends"/>.
    /// </summary>
    public sealed class RenameArea : DesignCommand
    {
        public AreaId Record { get; }
        public IReadOnlyList<AreaEnd> Ends { get; }
        public string Name { get; }

        public RenameArea(long baseRevision, AreaId record, IReadOnlyList<AreaEnd> ends, string name) : base(baseRevision)
        {
            Record = record;
            Ends = ends ?? Array.Empty<AreaEnd>();
            Name = name;
        }

        public override string Label => "Name area";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (DesignRules.NameProblem(Name) is string p) return Invalid(doc, p);
            if (Ends.Count == 0) return Invalid(doc, "An area holds at least one lift end.");
            foreach (var e in Ends) if (doc.FindLift(e.Lift) == null) return Missing(doc, "That lift");
            if (!Record.IsNone)
            {
                var old = doc.FindArea(Record);
                if (old == null) return Missing(doc, "That area");
                if (old.Name == Name) return Invalid(doc, "That's already its name.");
                return CommandResult.Accept(doc.PutArea(new AreaRecord(Record, Name, Ends)));
            }
            var mint = new IdMint(doc);
            var id = mint.Area();
            return CommandResult.Accept(doc.PutArea(new AreaRecord(id, Name, Ends)).WithOrdinal(mint.NextOrdinal), id.Value);
        }
    }
}
