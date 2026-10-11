using System.Collections.Generic;

namespace MountainPlanner.UI.Flow
{
    /// <summary>One credited source in the credits window (S9): what it gives, who, the licence on the right, and a line under it.</summary>
    public readonly struct CreditRow
    {
        public readonly string What, Who, Licence, Detail;
        public CreditRow(string what, string who, string licence, string detail = "")
        {
            What = what ?? ""; Who = who ?? ""; Licence = licence ?? ""; Detail = detail ?? "";
        }
    }

    /// <summary>A group of credits under a heading, with a note on the right ("3 areas in the library"), rows, and a plain list of names.</summary>
    public sealed class CreditsSection
    {
        public string Name = "";
        public string Title = "";
        public string Note = "";
        public readonly List<CreditRow> Rows = new List<CreditRow>();
        /// <summary>Names in one wrapped paragraph (the texture authors), or empty.</summary>
        public string Names = "";
        /// <summary>A line shown when there are no rows ("Download an area to see the data it uses.").</summary>
        public string Empty = "";
    }

    /// <summary>
    /// What the credits window (S9, task P2-07; mockup #demo=credits) shows: the game and the studio, then sections, then
    /// the licence texts behind a button. The app fills it from the packages' manifests and the game's own sources.
    /// </summary>
    public sealed class CreditsPage
    {
        public string Game = "Ski Area Design Challenge";
        public string Studio = "Alpine Labs";
        public string Tagline = "a ski resort designer on real mountains";
        public readonly List<CreditsSection> Sections = new List<CreditsSection>();
        /// <summary>The licence texts page (the fonts' OFL, Json.NET's MIT notices).</summary>
        public string Licences = "";
        /// <summary>The line in the foot: where the credits come from, and that the game is offline when it is.</summary>
        public string Foot = "";
    }
}
