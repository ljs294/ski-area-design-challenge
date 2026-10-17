using System;
using System.Collections.Generic;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The tools that work (P3-06). A tray tile whose id is here is live; the rest stay greyed ("comes in Phase 3").
    /// Tasks 08–10 add their tools with one line each (Edit and Remove lift and trail, Split, Join) and never touch
    /// the HUD's files.
    /// </summary>
    public static class ToolCatalog
    {
        public readonly struct Entry
        {
            public readonly string Id, Glyph, Badge;
            public readonly Func<IDesignTool> Make;
            public Entry(string id, string glyph, string badge, Func<IDesignTool> make) { Id = id; Glyph = glyph; Badge = badge; Make = make; }
        }

        static readonly List<Entry> Entries = new List<Entry>
        {
            new Entry(NewLiftTool.ToolId, "s-lift", "new", () => new NewLiftTool()),
            new Entry(NewTrailTool.ToolId, "s-trail", "new", () => new NewTrailTool()),
        };

        public static IReadOnlyList<Entry> All => Entries;

        public static bool IsLive(string id) => Find(id, out _);

        public static bool Find(string id, out Entry entry)
        {
            foreach (var e in Entries)
                if (e.Id == id) { entry = e; return true; }
            entry = default;
            return false;
        }

        /// <summary>Adds or replaces a tool (tasks 08–10, and tests' fake tools). Returns a handle that undoes it.</summary>
        public static IDisposable Register(string id, string glyph, string badge, Func<IDesignTool> make)
        {
            int at = Entries.FindIndex(e => e.Id == id);
            var old = at >= 0 ? Entries[at] : (Entry?)null;
            var entry = new Entry(id, glyph, badge, make);
            if (at >= 0) Entries[at] = entry; else Entries.Add(entry);
            return new Undo(() =>
            {
                int i = Entries.FindIndex(e => e.Id == id);
                if (i < 0) return;
                if (old.HasValue) Entries[i] = old.Value; else Entries.RemoveAt(i);
            });
        }

        sealed class Undo : IDisposable
        {
            Action _a;
            public Undo(Action a) { _a = a; }
            public void Dispose() { _a?.Invoke(); _a = null; }
        }
    }
}
