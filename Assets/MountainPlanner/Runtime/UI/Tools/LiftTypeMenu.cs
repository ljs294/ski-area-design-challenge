using System;
using MountainPlanner.Domain.Design;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The lift type menu (owner 2026-10-19, LT13; the approved mockup), shared by New lift and Edit lift: the kind, then its
    /// carrier (Chair, or Cabin for gondolas and trams), then its maker, every choice a dropdown; and the note with its
    /// capacity, speed and spacing. LT41: only the types with their own models can be picked (the fixed-grip quads,
    /// Sessellift FGQ-4 and Monta FG4, and the Sessellift D6 detachable six); every other type stays in the menu greyed,
    /// a placeholder. Carrier ids are <see cref="LiftCatalogIds.Carriers"/>; "fixed-quad" stands for the two quad models.
    /// </summary>
    public sealed class LiftTypeMenu
    {
        static readonly string[] Kinds = { "Fixed-grip chair", "Detachable chair", "Detachable gondola", "Aerial tram" };
        static readonly string[][] KindCarriers =
        {
            new[] { "fixed-double", "fixed-triple", "fixed-quad" },
            new[] { "detach-quad", "detach-six", "detach-eight" },
            new[] { "gondola-6", "gondola-8", "gondola-10" },
            new[] { "tram-60", "tram-100" },
        };
        static readonly string[][] CarrierNames =
        {
            new[] { "Double", "Triple", "Quad" },
            new[] { "Quad", "Six", "Eight" },
            new[] { "6 person", "8 person", "10 person" },
            new[] { "60 person", "100 person" },
        };
        static readonly int[] KindDefault = { 2, 1, 1, 1 };   // the quad, the detachable six (the D6), 8 person, 100 person
        static readonly string[] QuadModels = { "Sessellift FGQ-4", "Monta FG4", "Chairworks (comes later)" };
        static readonly string[] QuadIds = { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.MontaFg4 };
        static readonly bool[] QuadOff = { false, false, true };
        static readonly string[] MakerNames = { "Sessellift", "Monta", "Chairworks" };

        readonly ToolWindow _window;
        readonly Action _changed;
        public readonly ToolDropdown Kind, Carrier, Model;
        Label _note;

        public string CatalogId { get; private set; }
        public LiftType Type { get; private set; }

        /// <summary>A type's model as the lists name it: "Sessellift FGQ-4", or its maker and type ("Monta gondola, 8 person").</summary>
        public static string ModelName(string catalogId)
        {
            if (catalogId == LiftCatalogIds.SesselliftD6) return "Sessellift D6";
            if (catalogId == LiftCatalogIds.SesselliftFgq4) return QuadModels[0];
            if (catalogId == LiftCatalogIds.MontaFg4) return QuadModels[1];
            int m = Array.IndexOf(LiftCatalogIds.Makers, LiftCatalogIds.Maker(catalogId));
            return (m >= 0 ? MakerNames[m] + " " : "") + LiftType.Of(catalogId).Name.ToLowerInvariant();
        }

        /// <summary>Adds the three rows; <paramref name="changed"/> runs after a pick changes the type.</summary>
        public LiftTypeMenu(ToolWindow window, string catalogId, Action changed, string kindTip)
        {
            _window = window;
            _changed = changed;
            Kind = window.AddDropdown("kind", "Type", PickKind, kindTip);
            Carrier = window.AddDropdown("carrier", "Chair", PickCarrier, "The carrier size: smaller carriers climb further (LT14)");
            Model = window.AddDropdown("model", "Model", PickModel, "LC-02: its maker; a type without its own models is drawn with the quad's");
            CatalogId = LiftCatalogIds.Known(catalogId) ? catalogId : LiftCatalogIds.SesselliftFgq4;
            Type = LiftType.Of(CatalogId);
        }

        /// <summary>Adds the note (after the tool's own rows) and shows the type.</summary>
        public void AddNote()
        {
            _note = _window.AddNote("capacity", "", "LC-02, LC-03: capacity is shown, not chosen");
            Show();
        }

        static int KindOf(string id)
        {
            string carrier = LiftCatalogIds.Carrier(id);
            for (int k = 0; k < KindCarriers.Length; k++)
                if (Array.IndexOf(KindCarriers[k], carrier) >= 0) return k;
            return 0;
        }

        static string IdFor(string carrier, string maker) =>
            carrier == "fixed-quad" ? (maker == "monta" ? LiftCatalogIds.MontaFg4 : LiftCatalogIds.SesselliftFgq4) : LiftCatalogIds.Of(carrier, maker);

        // LT41: a carrier or kind is offered when one of its makers' types can be built; that maker's is taken.
        static string BuildableFor(string carrier, string preferMaker)
        {
            string id = IdFor(carrier, preferMaker);
            if (LiftCatalogIds.Buildable(id)) return id;
            foreach (string maker in LiftCatalogIds.Makers)
                if (LiftCatalogIds.Buildable(id = IdFor(carrier, maker))) return id;
            return null;
        }

        static bool KindOffered(int k)
        {
            foreach (string carrier in KindCarriers[k]) if (BuildableFor(carrier, "sessellift") != null) return true;
            return false;
        }

        void PickKind(int k)
        {
            if (k == KindOf(CatalogId) || !KindOffered(k)) return;
            string id = BuildableFor(KindCarriers[k][KindDefault[k]], "sessellift");
            for (int c = 0; id == null && c < KindCarriers[k].Length; c++) id = BuildableFor(KindCarriers[k][c], "sessellift");
            Choose(id);
        }

        void PickCarrier(int c)
        {
            int k = KindOf(CatalogId);
            if (BuildableFor(KindCarriers[k][c], LiftCatalogIds.Maker(CatalogId)) is string id) Choose(id);
        }

        void PickModel(int m)
        {
            if (LiftCatalogIds.Carrier(CatalogId) == "fixed-quad") { if (m < QuadIds.Length) Choose(QuadIds[m]); }
            else
            {
                string id = LiftCatalogIds.Of(LiftCatalogIds.Carrier(CatalogId), LiftCatalogIds.Makers[m]);
                if (LiftCatalogIds.Buildable(id)) Choose(id);
            }
        }

        /// <summary>Picks a type by its catalog id, as the menus would; false if it's unknown or already chosen.</summary>
        public bool Choose(string catalogId)
        {
            if (!Set(catalogId)) return false;
            _changed?.Invoke();
            return true;
        }

        /// <summary>Shows a type without telling the tool (Edit lift's undo).</summary>
        public bool Set(string catalogId)
        {
            if (!LiftCatalogIds.Known(catalogId) || catalogId == CatalogId) return false;
            CatalogId = catalogId;
            Type = LiftType.Of(catalogId);
            Show();
            return true;
        }

        void Show()
        {
            int k = KindOf(CatalogId);
            string carrier = LiftCatalogIds.Carrier(CatalogId);
            var kindOff = new bool[Kinds.Length];
            for (int i = 0; i < Kinds.Length; i++) kindOff[i] = !KindOffered(i);
            Kind.Set(Kinds, k, kindOff);
            var carrierOff = new bool[KindCarriers[k].Length];
            for (int i = 0; i < carrierOff.Length; i++) carrierOff[i] = BuildableFor(KindCarriers[k][i], "sessellift") == null;
            Carrier.Set(CarrierNames[k], Array.IndexOf(KindCarriers[k], carrier), carrierOff);
            _window.SetRowLabel("carrier", k >= 2 ? "Cabin" : "Chair");
            bool quad = carrier == "fixed-quad";
            if (quad) Model.Set(QuadModels, Array.IndexOf(QuadIds, CatalogId), QuadOff);
            else
            {
                int m = Array.IndexOf(LiftCatalogIds.Makers, LiftCatalogIds.Maker(CatalogId));
                var names = new string[MakerNames.Length];
                var off = new bool[MakerNames.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    string id = LiftCatalogIds.Of(carrier, LiftCatalogIds.Makers[i]);
                    off[i] = !LiftCatalogIds.Buildable(id);
                    names[i] = id == LiftCatalogIds.SesselliftD6 ? "Sessellift D6" : MakerNames[i] + (off[i] ? " (comes later)" : "");
                }
                Model.Set(names, m, off, CatalogId == LiftCatalogIds.SesselliftD6 ? "Sessellift D6" : MakerNames[m] + " " + Type.Name.ToLowerInvariant());
            }
            if (_note == null) return;
            string spacing = k == 3 ? "two cabins" : $"{(k == 2 ? "cabins" : "chairs")} {ToolFormat.Length(Type.ChairSpacingMetres)} apart";
            _note.text = $"{Type.Capacity:#,0} an hour · {Type.SpeedMetresPerSecond * 196.850394:#,0} ft/min · {spacing}"
                         + (Type.Detachable ? $"\na {(k == 2 ? "cabin" : "chair")} every {Type.IntervalSeconds:0.#} s, slowing to load in the stations" : "");
        }
    }
}
