using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.07 B2: what a trail's card and its row in Analysis › Trails say about its snow and why it's open or closed.
    /// The simulation reports per *run* (an edge's trail); the player's trail is a *line* that may cover part of a run or
    /// several runs, so a line adds up the runs it holds, each weighted by how much of the line it is: the worst label,
    /// the longest walk, the cover by length, the lowest rating, the longest part's surface. Pure: the app fills it from
    /// the snapshot at a bounded rate.
    /// </summary>
    public sealed class TrailCardModel
    {
        /// <summary>No run with snow yet: the snow rows read "—".</summary>
        public bool HasSnow { get; private set; }
        public TrailSnowLabel Label { get; private set; }
        public int WalkFt { get; private set; }
        /// <summary>The covered share, %, weighted by length.</summary>
        public int CoverPct => _weight == 0 ? 0 : (int)((_coverSum + _weight / 2) / _weight);
        /// <summary>SN-21's word: 0 Poor, 1 Fair, 2 Good, 3 Excellent (the lowest run's).</summary>
        public int Rating { get; private set; } = 3;
        public SnowType MainType { get; private set; }
        public bool Moguls { get; private set; }
        public bool Ice { get; private set; }
        /// <summary>Its snow has every run open on Auto (a Marginal trail that was never open isn't).</summary>
        public bool SnowOpen { get; private set; } = true;
        /// <summary>The least open run's word from the lifts' reach and the resort's hours.</summary>
        public OperatingStatus LiftStatus { get; private set; } = OperatingStatus.Open;

        long _weight, _coverSum, _mainWeight;
        bool _any;

        /// <summary>Moves when anything the card or the list shows does (the HUD redraws only then).</summary>
        public long Signature => HasSnow
            ? 1 + (long)Label * 3 + (long)Rating * 17 + (long)MainType * 131 + (Moguls ? 2_000 : 0) + (Ice ? 4_000 : 0)
              + (SnowOpen ? 8_000 : 0) + (long)CoverPct * 16_000 + (long)WalkFt * 2_000_000 + (long)LiftStatus * 1_000_000_000_000
            : -1 - (long)LiftStatus;

        /// <summary>Back to nothing (a pooled model, one per line, refilled each time).</summary>
        public void Clear()
        {
            HasSnow = false; Label = TrailSnowLabel.None; WalkFt = 0; Rating = 3; MainType = SnowType.None;
            Moguls = Ice = false; SnowOpen = true; LiftStatus = OperatingStatus.Open;
            _weight = _coverSum = _mainWeight = 0; _any = false;
        }

        /// <summary>
        /// One run's part of the line: <paramref name="weightMm"/> of it (at least 1). <paramref name="snow"/> is the
        /// snapshot's <see cref="SimSnapshot.TrailSnow"/> byte.
        /// </summary>
        public void Add(TrailSnowLabel label, int walkFt, int coverPct, int rating, byte snow, OperatingStatus liftStatus, long weightMm)
        {
            weightMm = Math.Max(1, weightMm);
            LiftStatus = !_any ? liftStatus : LessOpen(LiftStatus, liftStatus);
            _any = true;
            if (label == TrailSnowLabel.None) return;   // no trail snow on this run yet
            if ((snow & SimSnapshot.TrailSnowOpen) == 0) SnowOpen = false;
            if (!HasSnow || Worse(label, Label)) Label = label;
            HasSnow = true;
            WalkFt = Math.Max(WalkFt, walkFt);
            Rating = Math.Min(Rating, rating);
            _weight += weightMm; _coverSum += (long)coverPct * weightMm;
            var main = (SnowType)(snow & 15);
            if (main != SnowType.None && weightMm > _mainWeight) { MainType = main; _mainWeight = weightMm; }
            if ((snow & 16) != 0) Moguls = true;
            if ((snow & 32) != 0) Ice = true;
        }

        static bool Worse(TrailSnowLabel a, TrailSnowLabel b) => Rank(a) < Rank(b);
        static int Rank(TrailSnowLabel l) => l == TrailSnowLabel.Bare ? 0 : l == TrailSnowLabel.Marginal ? 1 : 2;

        /// <summary>The less open of two words: a line is as open as its least open run.</summary>
        public static OperatingStatus LessOpen(OperatingStatus a, OperatingStatus b) => StatusRank(a) <= StatusRank(b) ? a : b;

        static int StatusRank(OperatingStatus s) => s switch
        {
            OperatingStatus.Planned => 0,
            OperatingStatus.Closed => 1,
            OperatingStatus.OnHold => 2,
            OperatingStatus.Scheduled => 3,
            _ => 4,   // Open
        };

        public static readonly string[] RatingWords = { "Poor", "Fair", "Good", "Excellent" };

        static readonly string[] TypeWords =
        {
            "", "Powder", "Packed powder", "Machine groomed", "Hard pack", "Ice", "Corn", "Frozen granular", "Loose granular",
            "Wet granular", "Wet powder", "Machine made",
        };

        /// <summary>
        /// The card's Conditions: "Covered", "Marginal", "Marginal · walk 40 ft", "Bare · walk 226 ft", or "—". A Bare
        /// trail says its walk too: one long bare stretch makes a trail Bare however much of it is covered.
        /// </summary>
        public string Conditions()
        {
            if (!HasSnow) return "—";
            return Label switch
            {
                TrailSnowLabel.Covered => "Covered",
                TrailSnowLabel.Marginal => WalkFt > 0 ? $"Marginal · walk {WalkFt} ft" : "Marginal",
                _ => WalkFt > 0 ? $"Bare · walk {WalkFt} ft" : "Bare",
            };
        }

        /// <summary>The list's Conditions: the label with the rating ("Covered · Good"), a walk instead, or "—".</summary>
        public string ListConditions()
        {
            if (!HasSnow) return "—";
            if (Label != TrailSnowLabel.Covered && WalkFt > 0) return $"{(Label == TrailSnowLabel.Bare ? "Bare" : "Marginal")} · walk {WalkFt} ft";
            if (Label == TrailSnowLabel.Bare) return "Bare";
            return $"{(Label == TrailSnowLabel.Covered ? "Covered" : "Marginal")} · {RatingWords[Math.Clamp(Rating, 0, 3)]}";
        }

        /// <summary>The list's Conditions filter: "Covered", "Marginal", "Bare" or "None".</summary>
        public string ConditionsBin() => !HasSnow ? "None" : Label == TrailSnowLabel.Bare ? "Bare" : Label == TrailSnowLabel.Marginal ? "Marginal" : "Covered";

        public string CoverText() => HasSnow ? $"{CoverPct}%" : "—";

        public string RatingText() => HasSnow && Label != TrailSnowLabel.Bare ? RatingWords[Math.Clamp(Rating, 0, 3)] : "—";

        /// <summary>The card's Snow line: "Mostly packed powder · moguls · some ice", or "—".</summary>
        public string Surface()
        {
            if (!HasSnow || MainType == SnowType.None) return "—";
            int t = (int)MainType;
            string main = t < TypeWords.Length ? TypeWords[t] : "Snow";
            string text = CoverPct >= 90 ? main : $"{main} · bare patches";
            if (Moguls) text += " · moguls";
            if (Ice) text += " · some ice";
            return text;
        }

        /// <summary>
        /// SN-20 v4 for the line, with the reason: Planned; "no snow" (Bare closes it whatever the switch); "by you" (the
        /// switch); "not ready" (Auto on a trail its snow hasn't opened); "no lift" (open by snow and switch, the resort
        /// open, and no running lift reaching it); else the lifts' and hours' word.
        /// </summary>
        public (OperatingStatus Status, string Word) Status(bool built, TrailSwitch sw, bool resortOpen)
        {
            if (!built) return (OperatingStatus.Planned, "Planned");
            if (HasSnow && Label == TrailSnowLabel.Bare) return (OperatingStatus.Closed, "Closed · no snow");
            if (sw == TrailSwitch.Closed) return (OperatingStatus.Closed, "Closed by you");
            if (sw == TrailSwitch.Auto && HasSnow && !SnowOpen) return (OperatingStatus.Closed, "Closed · not ready");
            if (LiftStatus == OperatingStatus.Closed && resortOpen) return (OperatingStatus.Closed, "Closed · no lift");
            return (LiftStatus, OperatingStatusWords.Word(LiftStatus));
        }

        /// <summary>The one line under the switch: what it does to this trail now.</summary>
        public string SwitchHint(TrailSwitch sw)
        {
            if (HasSnow && Label == TrailSnowLabel.Bare) return "Bare: closed whatever the switch, until it has snow again.";
            if (sw == TrailSwitch.Closed) return "Closed by you: guests leave it at once.";
            if (sw == TrailSwitch.Open) return "Open: stays open until it goes Bare.";
            if (!HasSnow) return "Auto: opens when its snow is Covered.";
            if (!SnowOpen) return "Auto: opens when Covered. Open opens it now.";
            return Label == TrailSnowLabel.Covered ? "Auto: open while it stays Covered or Marginal." : "Auto: stays open while Marginal.";
        }

        /// <summary>The switch's three choices, with their tooltips (the card's segmented row).</summary>
        public static readonly (TrailSwitch Value, string Label, string Tip)[] Choices =
        {
            (TrailSwitch.Auto, "Auto", "Opens when Covered and stays open while Marginal"),
            (TrailSwitch.Open, "Open", "Opens it unless it's Bare: open early, hang on late"),
            (TrailSwitch.Closed, "Closed", "Closes it now"),
        };
    }
}
