using System;
using System.IO;
using System.Linq;
using MountainPlanner.App.Flow;
using MountainPlanner.Acquisition.Picker;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>Task P2-07: photo mode's sizes, names, focus presets and keys; the credits page the game builds.</summary>
    public sealed class PhotoCreditsTests
    {
        [Test]
        public void TwoTimesIsDoubleTheScreenWithinTheTextureLimit()
        {
            Assert.That(PhotoCapture.Size(2560, 1080, 1), Is.EqualTo(new Vector2Int(2560, 1080)));
            Assert.That(PhotoCapture.Size(2560, 1080, 2), Is.EqualTo(new Vector2Int(5120, 2160)));
            Assert.That(PhotoCapture.Size(3440, 1440, 2), Is.EqualTo(new Vector2Int(6880, 2880)));
            // 32:9 at 2× would pass the GPU's limit: it scales down to fit, keeping the shape.
            var wide = PhotoCapture.Size(10240, 2880, 2);
            Assert.That(Mathf.Max(wide.x, wide.y), Is.LessThanOrEqualTo(PhotoCapture.MaxSide));
            Assert.That(wide.x / (float)wide.y, Is.EqualTo(10240f / 2880).Within(0.01f));
        }

        [Test]
        public void PhotosAreNamedByAreaAndTheViewsTimeAndNeverOverwrite()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mp-photo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string first = PhotoCapture.FileName(folder, "Jackson Hole", 2026, 1, 15, 15 * 3600 + 30 * 60);
                Assert.That(Path.GetFileName(first), Is.EqualTo("Jackson Hole 2026-01-15 15-30-00.png"));
                File.WriteAllText(first, "");
                Assert.That(Path.GetFileName(PhotoCapture.FileName(folder, "Jackson Hole", 2026, 1, 15, 15 * 3600 + 30 * 60)), Is.EqualTo("Jackson Hole 2026-01-15 15-30-00 (2).png"));
                Assert.That(Path.GetFileName(PhotoCapture.FileName(folder, "A/B: C?", 2026, 1, 15, 0)), Is.EqualTo("AB C 2026-01-15 00-00-00.png"));
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void FocusPresetsBlurMoreForMiniatureAndNothingWhenOff()
        {
            Assert.That(PhotoFocus.Preset(PhotoFocusMode.Off), Is.EqualTo(Vector3.zero));
            var natural = PhotoFocus.Preset(PhotoFocusMode.Natural);
            var mini = PhotoFocus.Preset(PhotoFocusMode.Miniature);
            Assert.That(mini.x, Is.LessThan(natural.x), "a thinner sharp slice");
            Assert.That(mini.y, Is.LessThan(natural.y), "a steeper rise to full blur");
            Assert.That(mini.z, Is.GreaterThan(natural.z), "a wider blur");
            Assert.That(mini.z * 1080, Is.LessThan(16), "under 16 px at 1080p");
            Assert.That(Resources.Load<Shader>(PhotoFocus.ShaderResource), Is.Not.Null, "the shader ships (Resources)");
        }

        [Test]
        public void PhotoKeysAreRebindableActionsOfTheirOwn()
        {
            KeyBindings.ResetAll(remember: false);
            foreach (var a in new[] { GameAction.PhotoHideBar, GameAction.PhotoEarlier, GameAction.PhotoLater, GameAction.PhotoDayBack, GameAction.PhotoDayOn, GameAction.PhotoCapture })
                Assert.That(KeyBindings.Actions[(int)a].When, Is.EqualTo(KeyBindings.Context.Photo), a.ToString());
            Assert.That(KeyBindings.Caption(GameAction.PhotoHideBar), Is.EqualTo("H"));
            Assert.That(KeyBindings.Caption(GameAction.PhotoEarlier), Is.EqualTo("["));
            Assert.That(KeyBindings.Caption(GameAction.PhotoDayOn), Is.EqualTo("Shift ]"));
        }

        [Test]
        public void TheLicenceTextsShipTheFontsOflWord_for_word()
        {
            string licences = Resources.Load<TextAsset>(AppFlow.ResourceFolder + "Licences").text.Replace("\r", "");
            string ofl = File.ReadAllText("Assets/MountainPlanner/Art/UI/Fonts/OFL.txt").Replace("\r", "");
            Assert.That(licences, Does.Contain(ofl.Trim()));
            Assert.That(licences, Does.Contain("Copyright (c) 2007 James Newton-King"));
        }

        [Test]
        public void TheCreditsPageNamesTheStudioEverySourceAndThePickersServices()
        {
            string fixture = Path.GetFullPath("TestData/formats/v1-library");
            if (!Directory.Exists(fixture)) Assert.Ignore("TestData/formats isn't here.");
            var entries = ResortLibrary.Scan(fixture);
            var page = AppFlow.BuildCredits(entries, offline: true, licences: "OFL");
            Assert.That(page.Studio, Is.EqualTo("Alpine Labs"));
            var data = page.Sections.Single(s => s.Name == "credits-data");
            Assert.That(data.Note, Is.EqualTo("2 areas in the library"));
            foreach (var e in entries)
                foreach (string line in ResortPackage.ReadManifest(e.Folder).Attribution)
                {
                    var (what, who, licence) = CreditsReader.Split(line);
                    Assert.That(data.Rows.Any(r => r.What == what && r.Who == who && r.Licence == licence), line);
                }
            Assert.That(data.Rows.Single(r => r.What == "Elevation").Detail, Does.EndWith("All your areas"));
            // The picker's own services (its attribution constants name them).
            var picker = page.Sections.Single(s => s.Name == "credits-picker");
            Assert.That(MapTileSource.Attribution, Does.Contain("The National Map"));
            Assert.That(picker.Rows.Any(r => r.Who.Contains("The National Map")));
            Assert.That(NominatimClient.Attribution, Does.Contain("Nominatim"));
            Assert.That(picker.Rows.Any(r => r.Who.Contains("Nominatim") && r.Who.Contains("© OpenStreetMap contributors")));
            Assert.That(CoverageIndex.Attribution, Does.Contain("3DEP"));
            Assert.That(picker.Rows.Any(r => r.Who.Contains("3D Elevation Program")));
            Assert.That(page.Sections.Any(s => s.Names.Contains("Rob Tuytel")));
            Assert.That(page.Foot, Does.StartWith("Offline: "));
            Assert.That(AppFlow.BuildCredits(entries, offline: false, licences: "").Foot, Does.Not.Contain("Offline"));
        }

        [Test]
        public void AnEmptyLibrarySaysHowToGetData()
        {
            var page = AppFlow.BuildCredits(new System.Collections.Generic.List<LibraryEntry>(), false, "");
            var data = page.Sections.Single(s => s.Name == "credits-data");
            Assert.That(data.Rows, Is.Empty);
            Assert.That(data.Empty, Is.Not.Empty);
        }
    }
}
