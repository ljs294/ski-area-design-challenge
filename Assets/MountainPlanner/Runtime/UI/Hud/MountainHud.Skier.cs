using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Task 4.13, the skier card (GU-02's block, on screen): who the skier is, what they're doing now, their day so far and
    /// their last runs, with Follow, Next skier, Next in this line and Close. It sits where the lift and trail cards sit
    /// and replaces them (one card at a time). The app fills it from the newest snapshot at most four times a second, and
    /// fills the header at once from the dot that was clicked, so it never opens blank.
    /// </summary>
    public sealed partial class MountainHud
    {
        /// <summary>One fill of the skier card, as words (the app's from the snapshot).</summary>
        public struct SkierCard
        {
            public uint Serial;
            /// <summary>0 green, 1 blue, 2 black, 3 expert (the dot's colour).</summary>
            public int Band;
            public string Title, Kind, Now, Today, Mood, Why;
            public string[] Runs;
            public int RunCount;
            public bool Queued, Following;
        }

        /// <summary>The card's Follow (pressed again: stop), Next skier, Next in this line, and its close (✕ or Esc).</summary>
        public event Action<bool> SkierFollowChosen;
        public event Action SkierNextChosen, SkierNextInLineChosen, SkierCardClosed;

        VisualElement _skierCard, _skierCardKv, _skierCardRuns, _skierCardDot;
        Label _skierCardName, _skierCardKind;
        Button _skierCardFollow, _skierCardNextLine;
        uint _skierCardSerial;
        bool _skierCardFollowing;

        static readonly Color[] SkierBandColours =
        {
            new Color(0.20f, 0.66f, 0.29f), new Color(0.16f, 0.43f, 0.85f), new Color(0.10f, 0.10f, 0.11f), new Color(0.86f, 0.17f, 0.17f),
        };

        /// <summary>Whether the skier card is open, and for whom.</summary>
        public uint SkierCardSerial => _skierCardSerial;

        void WireSkierCard()
        {
            _skierCard = new VisualElement { name = "skier-card" };
            _skierCard.AddToClassList("mp-win");
            _skierCard.AddToClassList("an-card");
            _skierCard.AddToClassList("hidden");
            var head = new VisualElement();
            head.AddToClassList("mp-head");
            head.AddToClassList("mp-head--kind");
            var (nameLine, kind) = CardTitles(head, "skier-card-kind");
            _skierCardKind = kind;
            _skierCardDot = new VisualElement { name = "skier-card-dot" };
            _skierCardDot.style.width = 12; _skierCardDot.style.height = 12;
            _skierCardDot.style.borderTopLeftRadius = 6; _skierCardDot.style.borderTopRightRadius = 6;
            _skierCardDot.style.borderBottomLeftRadius = 6; _skierCardDot.style.borderBottomRightRadius = 6;
            _skierCardDot.style.borderTopWidth = 1; _skierCardDot.style.borderBottomWidth = 1;
            _skierCardDot.style.borderLeftWidth = 1; _skierCardDot.style.borderRightWidth = 1;
            var rim = new Color(1, 1, 1, 0.85f);
            _skierCardDot.style.borderTopColor = rim; _skierCardDot.style.borderBottomColor = rim;
            _skierCardDot.style.borderLeftColor = rim; _skierCardDot.style.borderRightColor = rim;
            _skierCardDot.style.marginRight = 6;
            _skierCardDot.style.alignSelf = Align.Center;
            nameLine.Add(_skierCardDot);
            _skierCardName = new Label { name = "skier-card-name" };
            _skierCardName.AddToClassList("mp-title");
            nameLine.Add(_skierCardName);
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            var x = new Button(CloseSkierCard) { name = "skier-card-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            _skierCard.Add(head);
            _skierCardKv = new VisualElement();
            _skierCardKv.AddToClassList("tw-kv");
            _skierCard.Add(_skierCardKv);
            _skierCardRuns = new VisualElement { name = "skier-card-runs" };
            _skierCardRuns.AddToClassList("tw-kv");
            _skierCard.Add(_skierCardRuns);
            var acts = new VisualElement();
            acts.AddToClassList("tw-btns");
            acts.AddToClassList("an-card__acts");
            _skierCardFollow = new Button(() => SkierFollowChosen?.Invoke(!_skierCardFollowing)) { name = "skier-card-follow", text = "Follow", tooltip = "Keep the camera on this skier: orbit and zoom as usual, pan to let go" };
            _skierCardFollow.AddToClassList("mp-ghost");
            var next = new Button(() => SkierNextChosen?.Invoke()) { name = "skier-card-next", text = "Next skier" };
            next.AddToClassList("mp-ghost");
            _skierCardNextLine = new Button(() => SkierNextInLineChosen?.Invoke()) { name = "skier-card-next-line", text = "Next in this line" };
            _skierCardNextLine.AddToClassList("mp-ghost");
            acts.Add(_skierCardFollow);
            acts.Add(next);
            acts.Add(_skierCardNextLine);
            _skierCard.Add(acts);
            _root.Q("hud").Add(_skierCard);
        }

        /// <summary>Opens (or refills) the skier card; any lift, trail or Area card closes first.</summary>
        public void ShowSkierCard(in SkierCard c)
        {
            if (_skierCard == null) return;
            if (_skierCardSerial == 0)
            {
                CloseLiftCardIfOpen();
                CloseTrailCardIfOpen();
            }
            _skierCardSerial = c.Serial;
            _skierCardFollowing = c.Following;
            _skierCardDot.style.backgroundColor = SkierBandColours[Mathf.Clamp(c.Band, 0, 3)];
            _skierCardName.text = c.Title ?? "Skier";
            _skierCardKind.text = c.Kind ?? "";
            _skierCardKv.Clear();
            void Kv(VisualElement into, string k, string v)
            {
                if (string.IsNullOrEmpty(v)) return;
                var row = new VisualElement();
                row.AddToClassList("tw-kv__row");
                var kl = new Label(k);
                kl.AddToClassList("tw-kv__k");
                var vl = new Label(v);
                vl.AddToClassList("tw-kv__v");
                row.Add(kl);
                row.Add(vl);
                into.Add(row);
            }
            Kv(_skierCardKv, "Now", c.Now);
            Kv(_skierCardKv, "Today", c.Today);
            Kv(_skierCardKv, "Mood", c.Mood);
            Kv(_skierCardKv, "Why", c.Why);
            _skierCardRuns.Clear();
            for (int i = 0; i < c.RunCount && c.Runs != null && i < c.Runs.Length; i++) Kv(_skierCardRuns, i == 0 ? "Last runs" : "", c.Runs[i]);
            _skierCardFollow.text = c.Following ? "Stop following" : "Follow";
            _skierCardNextLine.EnableInClassList("hidden", !c.Queued);
            if (_skierCard.ClassListContains("hidden"))
            {
                _skierCard.RemoveFromClassList("hidden");
                CardsChanged(false);
            }
        }

        public void CloseSkierCard()
        {
            if (_skierCardSerial == 0) return;
            _skierCardSerial = 0;
            _skierCardFollowing = false;
            _skierCard?.AddToClassList("hidden");
            SkierCardClosed?.Invoke();
            CardsChanged(true);
        }

        /// <summary>Esc: closes the skier card (true when it did).</summary>
        public bool CloseSkierCardIfOpen()
        {
            if (_skierCardSerial == 0) return false;
            CloseSkierCard();
            return true;
        }
    }
}
