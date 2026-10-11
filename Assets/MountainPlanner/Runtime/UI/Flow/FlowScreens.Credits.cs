using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// S9 Credits (task P2-07; the accepted mockup's #demo=credits): a window like Settings. The head names the game and
    /// the studio; each section is a heading with a note on the right, then sources in rows, each with its licence on the
    /// right and a line under it (the product and the areas that use it). Licence texts swaps the page for the full texts.
    /// The window opens from the title and from the in-game menu; Esc or Close closes it.
    /// </summary>
    public sealed partial class FlowScreens
    {
        ScrollView _creditsBody;
        Label _creditsTitle, _creditsSubtitle, _creditsFoot;
        Button _creditsLicences, _creditsClose;
        CreditsPage _creditsPage;
        bool _creditsLicencePage;

        /// <summary>True while Credits shows the licence texts (tests).</summary>
        public bool CreditsShowingLicences => _creditsLicencePage && IsShown(_credits);

        void WireCredits()
        {
            _creditsBody = _root.Q<ScrollView>("credits-body");
            _creditsTitle = _root.Q<Label>("credits-title");
            _creditsSubtitle = _root.Q<Label>("credits-subtitle");
            _creditsFoot = _root.Q<Label>("credits-offline");
            _creditsLicences = _root.Q<Button>("credits-licences");
            _creditsClose = _root.Q<Button>("credits-close");
            _creditsClose.clicked += CloseOverlay;
            _root.Q<Button>("credits-x").clicked += CloseOverlay;
            _creditsLicences.clicked += () => ShowCreditsPage(!_creditsLicencePage);
        }

        /// <summary>Opens Credits on its first page.</summary>
        public void ShowCredits(CreditsPage page)
        {
            _creditsPage = page ?? new CreditsPage();
            ShowCreditsPage(false);
            Show(_credits, true);
            UiFocus.OpenModal(_credits, _creditsClose);
        }

        /// <summary>The sources page, or the licence texts (the Licence texts button; Back returns).</summary>
        public void ShowCreditsPage(bool licences)
        {
            _creditsLicencePage = licences;
            var page = _creditsPage ?? new CreditsPage();
            _creditsBody.Clear();
            SetText(_creditsTitle, licences ? "Licence texts" : "Credits");
            SetText(_creditsSubtitle, licences ? "Overpass · Json.NET" : "");
            Show(_creditsSubtitle, licences);
            SetText(_creditsLicences, licences ? "Back" : "Licence texts");
            SetText(_creditsFoot, page.Foot);
            if (licences) _creditsBody.Add(Text(page.Licences, "credits-ofl", "mono"));
            else
            {
                var hero = new VisualElement { name = "credits-hero" };
                hero.AddToClassList("credits-hero");
                hero.Add(Text(page.Game, "credits-game"));
                var by = new VisualElement();
                by.AddToClassList("credits-by");
                by.Add(Text("Made by", "credits-by-text"));
                by.Add(Text(page.Studio, "credits-studio"));
                by.Add(Text("· " + page.Tagline, "credits-by-text"));
                hero.Add(by);
                _creditsBody.Add(hero);
                foreach (var section in page.Sections) _creditsBody.Add(Section(section));
            }
            _creditsBody.scrollOffset = UnityEngine.Vector2.zero;
        }

        static VisualElement Section(CreditsSection section)
        {
            var box = new VisualElement { name = section.Name };
            var head = new VisualElement();
            head.AddToClassList("credits-sub");
            head.Add(Text(section.Title, "credits-sub-title"));
            head.Add(Text(section.Note, "credits-sub-note"));
            box.Add(head);
            for (int i = 0; i < section.Rows.Count; i++)
            {
                var r = section.Rows[i];
                var row = new VisualElement();
                row.AddToClassList("credits-src");
                if (i == 0) row.AddToClassList("credits-src--first");
                var top = new VisualElement();
                top.AddToClassList("credits-src-top");
                // "<b>Elevation</b> · U.S. Geological Survey, …": rich text, so the label wraps as one line of words.
                var who = Text(r.What.Length > 0 ? $"<b>{Escape(r.What)}</b> · {Escape(r.Who)}" : Escape(r.Who), "credits-who");
                who.enableRichText = true;
                top.Add(who);
                top.Add(Text(r.Licence, "credits-lic", "mono"));
                row.Add(top);
                if (r.Detail.Length > 0) row.Add(Text(r.Detail, "credits-detail"));
                box.Add(row);
            }
            if (section.Rows.Count == 0 && section.Empty.Length > 0) box.Add(Text(section.Empty, "credits-empty"));
            if (section.Names.Length > 0) box.Add(Text(section.Names, "credits-names"));
            return box;
        }

        /// <summary>Keeps a source's own angle brackets from reading as rich-text tags.</summary>
        static string Escape(string text) => string.IsNullOrEmpty(text) ? "" : text.Replace("<", "<noparse><</noparse>");
    }
}
