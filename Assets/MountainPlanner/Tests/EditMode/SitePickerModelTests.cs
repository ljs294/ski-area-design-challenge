using System;
using MountainPlanner.Domain.Geo;
using MountainPlanner.UI.Picker;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 13: the picker's view model (0.4 S3): size steps, placement, the name rule and when Download is on.
    public sealed class SitePickerModelTests
    {
        static readonly AlbersPoint JacksonHole = Albers6350.Forward(new GeoPoint(43.593, -110.848));

        [Test]
        public void TheSizeStepsInTenthsBetweenTwoAndFiveKm()
        {
            var m = new SitePickerModel();
            Assert.That(m.SizeKm, Is.EqualTo(4.0));
            m.StepSize(1);
            Assert.That(m.SizeKm, Is.EqualTo(4.1));
            m.StepSize(10);   // Shift+arrow: 1 km
            Assert.That(m.SizeKm, Is.EqualTo(5.0), "clamped at 5 km");
            m.SetSize(2.04);
            Assert.That(m.SizeKm, Is.EqualTo(2.0));
            m.StepSize(-1);
            Assert.That(m.SizeKm, Is.EqualTo(2.0), "clamped at 2 km");
            Assert.That(m.SizeLabel, Is.EqualTo("2.0 km"));
        }

        [Test]
        public void ResizingKeepsTheCentreAndStaysExact()
        {
            var m = new SitePickerModel();
            m.PlaceAt(JacksonHole);
            var centre = m.Square.Value.Centre;
            m.SetSize(3.7);
            Assert.That(m.Square.Value.Centre, Is.EqualTo(centre));
            Assert.That(m.Square.Value.Core.Width, Is.EqualTo(3700));
        }

        [Test]
        public void DownloadNeedsASquareAndAName()
        {
            var m = new SitePickerModel();
            Assert.That(m.CanDownload, Is.False);
            m.PlaceAt(JacksonHole);
            Assert.That(m.CanDownload, Is.False, "no name yet");
            m.Suggest("Teton Village");
            Assert.That(m.CanDownload, Is.True);
            m.TypeName("   ");
            Assert.That(m.CanDownload, Is.False, "a blank name");
            m.TypeName("Jackson Hole");
            m.SetOffline(true);
            Assert.That(m.CanDownload, Is.False, "offline");
            m.SetOffline(false);
            var site = m.Choose();
            Assert.That(site.Name, Is.EqualTo("Jackson Hole"));
            Assert.That(site.Square.Core, Is.EqualTo(m.Square.Value.Core));
        }

        [Test]
        public void ASuggestionNeverReplacesATypedName()
        {
            var m = new SitePickerModel();
            m.Suggest("Teton Village");
            Assert.That(m.NameIsSuggestion, Is.True);
            m.TypeName("My Hill");
            m.Suggest("Pierce County");
            Assert.That(m.Name, Is.EqualTo("My Hill"));
            m.TypeName("");
            m.Suggest("Crystal Mountain");
            Assert.That(m.Name, Is.EqualTo("Crystal Mountain"), "clearing the field lets suggestions back in");
        }

        [Test]
        public void ArrowNudgesMoveTheSquareInMetres()
        {
            var m = new SitePickerModel();
            m.Nudge(100, 0);
            Assert.That(m.Square.HasValue, Is.False, "nothing to nudge yet");
            m.PlaceAt(JacksonHole);
            var before = m.Square.Value.Centre;
            m.Nudge(100, -1000);
            Assert.That(m.Square.Value.Centre, Is.EqualTo(new AlbersPoint(before.X + 100, before.Y - 1000)));
        }

        [Test]
        public void MovingTheSquareClearsTheOldEstimate()
        {
            var m = new SitePickerModel();
            m.PlaceAt(JacksonHole);
            m.SetEstimate(new SiteEstimate(100, new double[] { 1, 0, 0, 0 }, 1, 1, false), "Terrain about 100");
            m.Nudge(100, 0);
            Assert.That(m.Estimate.HasValue, Is.False);
            Assert.That(m.EstimateLine, Is.Empty);
        }

        [Test]
        public void ChoosingTooEarlyThrows()
        {
            Assert.Throws<InvalidOperationException>(() => new SitePickerModel().Choose());
        }

        [Test]
        public void ShareOfABoxInsideTiles()
        {
            var box = new AlbersBox(0, 0, 1000, 1000);
            Assert.That(SitePicker.Share(box, new[] { new AlbersBox(-500, -500, 500, 2000) }), Is.EqualTo(0.5));
            Assert.That(SitePicker.Share(box, Array.Empty<AlbersBox>()), Is.EqualTo(0));
        }
    }
}
