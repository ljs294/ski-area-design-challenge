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
        static readonly string[] Kinds = { "Fixed-grip chair", "Detachable chair", "Detachable gondola", "Aerial tram", "Surface lift" };
        /// <summary>The surface lifts' kind (v0.3.2): the Monta T-bar and J-bar, and the Glidepath carpets (surface lifts PR B).</summary>
        const int SurfaceKind = 4;
        static readonly string[][] KindCarriers =
        {
            new[] { "fixed-double", "fixed-triple", "fixed-quad" },
            new[] { "detach-quad", "detach-six", "detach-eight" },
            new[] { "gondola-6", "gondola-8", "gondola-10" },
            new[] { "tram-60", "tram-100" },
            new[] { "tbar", "jbar", "carpet" },
        };
        static readonly string[][] CarrierNames =
        {
            new[] { "Double", "Triple", "Quad" },
            new[] { "Quad", "Six", "Eight" },
            new[] { "6 person", "8 person", "10 person" },
            new[] { "60 person", "100 person" },
            new[] { "T-bar", "J-bar", "Carpet" },
        };
        static readonly int[] KindDefault = { 2, 1, 1, 1, 0 };   // the quad, the detachable six (the D6), 8 person, 100 person, the T-bar
        // The fixed grips' models by carrier (v0.3.2): each maker's, with the Sessellift's two styles; Chairworks comes later.
        static readonly string[] FixedCarriers = { "fixed-double", "fixed-triple", "fixed-quad" };
        static readonly string[][] FixedModels =
        {
            new[] { "Sessellift FGD-2", "Sessellift Stern D-2", "Monta FG2", "Chairworks (comes later)" },
            new[] { "Sessellift FGT-3", "Sessellift Stern T-3", "Monta FG3", "Chairworks (comes later)" },
            new[] { "Sessellift FGQ-4", "Sessellift Stern Q-4", "Monta FG4", "Chairworks (comes later)" },
        };
        static readonly string[][] FixedIds =
        {
            new[] { LiftCatalogIds.SesselliftFgd2, LiftCatalogIds.SesselliftStern2, LiftCatalogIds.MontaFg2 },
            new[] { LiftCatalogIds.SesselliftFgt3, LiftCatalogIds.SesselliftStern3, LiftCatalogIds.MontaFg3 },
            new[] { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.SesselliftStern4, LiftCatalogIds.MontaFg4 },
        };
        static readonly bool[] FixedOff = { false, false, false, true };
        static readonly string[] Drives = { "Top", "Bottom" };
        static readonly string[] MakerNames = { "Sessellift", "Monta", "Chairworks" };
        // The Glidepath carpets (C3, C9): one belt or two side by side, open or under a gallery; all four in the id (no save bump).
        static readonly string[] CarpetModels = { "Glidepath single", "Glidepath double" };
        static readonly string[] Covers = { "Open", "Covered" };

        static string CarpetId(bool two, bool covered) =>
            two ? (covered ? LiftCatalogIds.GlidepathCarpet2Covered : LiftCatalogIds.GlidepathCarpet2)
                : (covered ? LiftCatalogIds.GlidepathCarpetCovered : LiftCatalogIds.GlidepathCarpet);

        readonly ToolWindow _window;
        readonly Action _changed;
        public readonly ToolDropdown Kind, Carrier, Model, DriveEnd, Cover;
        Label _note;
        // LD9: whether the player picked the drive end (kept across fixed grips that offer it), else the type's own.
        bool _drivePicked;

        public string CatalogId { get; private set; }
        /// <summary>LD9 (save v10): the end that drives, the player's on a type that offers the choice, else the type's own.</summary>
        public LiftDrive Drive { get; private set; }
        public LiftType Type { get; private set; }

        /// <summary>A type's model as the lists name it: "Sessellift FGQ-4", or its maker and type ("Monta gondola, 8 person").</summary>
        public static string ModelName(string catalogId)
        {
            if (OwnName(catalogId) is string own) return own;
            for (int c = 0; c < FixedIds.Length; c++)
            {
                int i = Array.IndexOf(FixedIds[c], catalogId);
                if (i >= 0) return FixedModels[c][i];
            }
            int m = Array.IndexOf(LiftCatalogIds.Makers, LiftCatalogIds.Maker(catalogId));
            return (m >= 0 ? MakerNames[m] + " " : "") + LiftType.Of(catalogId).Name.ToLowerInvariant();
        }

        // The types with models of their own, by their model names (the D6, LT41; the doubles and triples, LD1).
        static string OwnName(string id) =>
            id == LiftCatalogIds.SesselliftD6 ? "Sessellift D6" : id == LiftCatalogIds.SesselliftD4 ? "Sessellift D4"
            : id == LiftCatalogIds.SesselliftFgd2 ? "Sessellift FGD-2" : id == LiftCatalogIds.SesselliftFgt3 ? "Sessellift FGT-3"
            : id == LiftCatalogIds.MontaFg2 ? "Monta FG2" : id == LiftCatalogIds.MontaFg3 ? "Monta FG3"
            : id == LiftCatalogIds.SesselliftStern4 ? "Sessellift Stern Q-4" : id == LiftCatalogIds.SesselliftStern3 ? "Sessellift Stern T-3"
            : id == LiftCatalogIds.SesselliftStern2 ? "Sessellift Stern D-2"
            : id == LiftCatalogIds.MontaTbar ? "Monta T-bar" : id == LiftCatalogIds.MontaJbar ? "Monta J-bar"
            : LiftCatalogIds.IsBelt(id) ? (LiftCatalogIds.IsDoubleBelt(id) ? "Glidepath double carpet" : "Glidepath carpet") + (LiftCatalogIds.IsCovered(id) ? ", covered" : "")
            : null;

        /// <summary>Adds the three rows; <paramref name="changed"/> runs after a pick changes the type.</summary>
        public LiftTypeMenu(ToolWindow window, string catalogId, Action changed, string kindTip)
        {
            _window = window;
            _changed = changed;
            Kind = window.AddDropdown("kind", "Type", PickKind, kindTip);
            Carrier = window.AddDropdown("carrier", "Chair", PickCarrier, "The carrier size: smaller carriers climb further (LT14)");
            Model = window.AddDropdown("model", "Model", PickModel, "LC-02: its maker; a type without its own models is drawn with the quad's");
            DriveEnd = window.AddDropdown("drive", "Drive", PickDrive,
                "LD9: which end holds the drive; each terminal carries a head for either end, and the top's row (4, 6 or 8 sheaves) follows the summit");
            Cover = window.AddDropdown("cover", "Cover", PickCover, "C3: an open belt, or a gallery over it with an exit bay at least every 80 ft");
            CatalogId = LiftCatalogIds.Known(catalogId) ? catalogId : LiftCatalogIds.SesselliftFgq4;
            Drive = LiftCatalogIds.DefaultDrive(CatalogId);
            Type = LiftType.Of(CatalogId, Drive);
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
            carrier == "fixed-quad" ? (maker == "monta" ? LiftCatalogIds.MontaFg4 : LiftCatalogIds.SesselliftFgq4)
            : carrier == "tbar" ? LiftCatalogIds.MontaTbar : carrier == "jbar" ? LiftCatalogIds.MontaJbar
            : carrier == "carpet" ? LiftCatalogIds.GlidepathCarpet
            : LiftCatalogIds.Of(carrier, maker);

        /// <summary>The same model in another fixed carrier (a Stern stays a Stern), or null.</summary>
        static string SameModel(string id, string carrier)
        {
            int to = Array.IndexOf(FixedCarriers, carrier);
            if (to < 0) return null;
            for (int c = 0; c < FixedIds.Length; c++)
            {
                int i = Array.IndexOf(FixedIds[c], id);
                if (i >= 0) return FixedIds[to][i];
            }
            return null;
        }

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
            if (SameModel(CatalogId, KindCarriers[k][c]) is string same && LiftCatalogIds.Buildable(same)) Choose(same);
            else if (BuildableFor(KindCarriers[k][c], LiftCatalogIds.Maker(CatalogId)) is string id) Choose(id);
        }

        void PickModel(int m)
        {
            if (LiftCatalogIds.IsTow(CatalogId)) return;   // one maker's (the Monta)
            if (LiftCatalogIds.IsBelt(CatalogId)) { Choose(CarpetId(m == 1, LiftCatalogIds.IsCovered(CatalogId))); return; }
            int f = Array.IndexOf(FixedCarriers, LiftCatalogIds.Carrier(CatalogId));
            if (f >= 0) { if (m < FixedIds[f].Length) Choose(FixedIds[f][m]); }
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

        /// <summary>Picks the drive end (LD9; the menu, tests and captures); false where the type doesn't offer it or it's
        /// already chosen.</summary>
        public bool ChooseDrive(LiftDrive drive)
        {
            if (!LiftCatalogIds.DriveChoice(CatalogId) || drive == Drive) return false;
            _drivePicked = true;
            SetDrive(drive);
            _changed?.Invoke();
            return true;
        }

        void PickDrive(int i) => ChooseDrive(i == 0 ? LiftDrive.Top : LiftDrive.Bottom);

        void PickCover(int i)
        {
            if (LiftCatalogIds.IsBelt(CatalogId)) Choose(CarpetId(LiftCatalogIds.IsDoubleBelt(CatalogId), i == 1));
        }

        /// <summary>Shows a type without telling the tool (Edit lift's undo).</summary>
        public bool Set(string catalogId)
        {
            if (!LiftCatalogIds.Known(catalogId) || catalogId == CatalogId) return false;
            CatalogId = catalogId;
            // The player's drive end carries over to another fixed grip that offers the choice; else the type's own.
            Drive = _drivePicked && LiftCatalogIds.DriveChoice(catalogId) ? Drive : LiftCatalogIds.DefaultDrive(catalogId);
            Type = LiftType.Of(catalogId, Drive);
            Show();
            return true;
        }

        /// <summary>Shows a drive end without telling the tool (Edit lift's undo, a lift opened for editing).</summary>
        public void SetDrive(LiftDrive drive)
        {
            Drive = LiftCatalogIds.DriveChoice(CatalogId) ? drive : LiftCatalogIds.DefaultDrive(CatalogId);
            Type = LiftType.Of(CatalogId, Drive);
            Show();
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
            _window.SetRowLabel("carrier", k == SurfaceKind ? "Carrier" : k >= 2 ? "Cabin" : "Chair");
            int f = Array.IndexOf(FixedCarriers, carrier);
            bool belt = LiftCatalogIds.IsBelt(CatalogId);
            if (LiftCatalogIds.IsTow(CatalogId)) Model.Set(new[] { OwnName(CatalogId) }, 0, new[] { false });
            else if (belt) Model.Set(CarpetModels, LiftCatalogIds.IsDoubleBelt(CatalogId) ? 1 : 0, new[] { false, false });
            else if (f >= 0) Model.Set(FixedModels[f], Array.IndexOf(FixedIds[f], CatalogId), FixedOff);
            else
            {
                int m = Array.IndexOf(LiftCatalogIds.Makers, LiftCatalogIds.Maker(CatalogId));
                var names = new string[MakerNames.Length];
                var off = new bool[MakerNames.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    string id = LiftCatalogIds.Of(carrier, LiftCatalogIds.Makers[i]);
                    off[i] = !LiftCatalogIds.Buildable(id);
                    names[i] = OwnName(id) ?? MakerNames[i] + (off[i] ? " (comes later)" : "");
                }
                Model.Set(names, m, off, OwnName(CatalogId) ?? MakerNames[m] + " " + Type.Name.ToLowerInvariant());
            }
            bool choice = LiftCatalogIds.DriveChoice(CatalogId);
            _window.ShowOption("drive", choice);
            if (choice) DriveEnd.Set(Drives, Drive == LiftDrive.Top ? 0 : 1);
            _window.ShowOption("cover", belt);
            if (belt) Cover.Set(Covers, LiftCatalogIds.IsCovered(CatalogId) ? 1 : 0);
            if (_note == null) return;
            string spacing = belt ? (LiftCatalogIds.IsDoubleBelt(CatalogId) ? "two belts side by side" : "one belt")
                : k == 3 ? "two cabins" : $"{(k == SurfaceKind ? "bars" : k == 2 ? "cabins" : "chairs")} {ToolFormat.Length(Type.ChairSpacingMetres)} apart";
            _note.text = $"{Type.Capacity:#,0} an hour · {Type.SpeedMetresPerSecond * 196.850394:#,0} ft/min · {spacing}"
                         + (Type.Detachable ? $"\na {(k == 2 ? "cabin" : "chair")} every {Type.IntervalSeconds:0.#} s, slowing to load in the stations" : "");
        }
    }
}
