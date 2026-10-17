using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Flow;
using NUnit.Framework;
using UnityEngine.InputSystem;

namespace MountainPlanner.Tests
{
    /// <summary>Task P3-04: the Save key and Load Area's save lines.</summary>
    public sealed class SaveUiTests
    {
        [Test]
        public void SaveIsCtrlSAndRebindable()
        {
            Assert.That(KeyBindings.Default(GameAction.Save, 0), Is.EqualTo(new KeyChord(Key.S, KeyMods.Ctrl)));
            Assert.That(KeyBindings.Caption(KeyBindings.Default(GameAction.Save, 0)), Is.EqualTo("Ctrl S"));
            Assert.That(KeyBindings.Actions.Length, Is.EqualTo(Enum.GetValues(typeof(GameAction)).Length), "every action has its defaults (SH-07)");
            var chord = new KeyChord(Key.S, KeyMods.Ctrl);
            for (int a = 0; a < KeyBindings.Actions.Length; a++)
            {
                if ((GameAction)a == GameAction.Save) continue;
                Assert.That(KeyBindings.Actions[a].First, Is.Not.EqualTo(chord), $"{(GameAction)a} doesn't share Ctrl+S");
                Assert.That(KeyBindings.Actions[a].Second, Is.Not.EqualTo(chord), $"{(GameAction)a} doesn't share Ctrl+S");
            }
        }

        [Test]
        public void LoadAreaRowsCarryTheirSavesAndCounts()
        {
            var area = new LibraryEntry { PackageId = "abc", Name = "Jackson Hole", Folder = "x" };
            var saves = new List<SaveSummary>
            {
                new SaveSummary { Folder = "x/autosave", Name = "Autosave", Autosave = true, SavedUtc = "2026-10-16T11:57:00Z", Trails = 5, Lifts = 1 },
                new SaveSummary { Folder = "x/save-001", Name = "Teton plan", SavedUtc = "2026-10-16T09:00:00Z", Trails = 1, Lifts = 2 },
            };
            var vm = LibraryViewModel.Build(new[] { area }, Array.Empty<PendingDownload>(), new RecentResorts(), LibrarySort.Name,
                new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), saves: e => e.PackageId == "abc" ? saves : new List<SaveSummary>());
            var row = vm.Rows.Single();
            Assert.That(row.Saves, Is.EqualTo(saves));
            Assert.That(LibraryViewModel.SaveCount(row.Saves.Count), Is.EqualTo("2 saves"));
            Assert.That(LibraryViewModel.SaveCount(1), Is.EqualTo("1 save"));
            Assert.That(LibraryViewModel.SaveCount(0), Is.EqualTo("No saves"));
            var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            Assert.That(LibraryViewModel.SavedWhen(saves[0].SavedUtc, now), Is.EqualTo("3 min ago"));
            Assert.That(LibraryViewModel.SavedWhen(saves[1].SavedUtc, now), Is.EqualTo("3 h ago"));
            Assert.That(LibraryViewModel.SavedWhen("2026-10-15T20:00:00Z", now), Is.EqualTo("yesterday"));
            Assert.That(LibraryViewModel.Counts(saves[1]), Is.EqualTo("1 trail · 2 lifts"));
        }

        [Test]
        public void ASaveFromANewerVersionCantBeOpened()
        {
            var area = new LibraryRow { Entry = new LibraryEntry { PackageId = "abc", Folder = "x" } };
            var newer = new LibraryRow { Entry = area.Entry, Area = area, Save = new SaveSummary { Name = "Later", Refusal = "newer" } };
            var fine = new LibraryRow { Entry = area.Entry, Area = area, Save = new SaveSummary { Name = "Now" } };
            Assert.That(newer.CanOpen, Is.False);
            Assert.That(fine.CanOpen, Is.True);
            Assert.That(new LibraryRow { Entry = area.Entry, Area = area, IsNewDesign = true }.CanOpen, Is.True);
        }

        [Test]
        public void AutosaveChoicesAreTheMockups()
        {
            Assert.That(GameplayPreferences.AutosaveChoices, Is.EqualTo(new[] { 0, 5, 10, 15, 30 }));
            Assert.That(GameplayPreferences.AutosaveDefault, Is.EqualTo(10), "decision SV2");
        }
    }
}
