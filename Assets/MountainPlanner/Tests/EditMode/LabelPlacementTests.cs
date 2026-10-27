using System.Collections.Generic;
using MountainPlanner.UI;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // NT7 (owner, 2026-10-26): names on the map never overlap, and a run's or a lift's name reads left to right along it.
    public sealed class LabelPlacementTests
    {
        static readonly Vector2 Name = new Vector2(50, 10);   // half a 100 x 20 px name

        [Test]
        public void NT7_BoxesOverlapOnlyWhenTheyTouch()
        {
            var a = new LabelBox(Vector2.zero, Name, 0);
            Assert.IsTrue(LabelBox.Overlap(a, new LabelBox(new Vector2(90, 0), Name, 0)), "side by side, 10 px into each other");
            Assert.IsFalse(LabelBox.Overlap(a, new LabelBox(new Vector2(110, 0), Name, 0)), "10 px apart");
            Assert.IsFalse(LabelBox.Overlap(a, new LabelBox(new Vector2(0, 25), Name, 0)), "one above the other");
            Assert.IsTrue(LabelBox.Overlap(a, new LabelBox(new Vector2(0, 15), Name, 0)));
        }

        [Test]
        public void NT7_SlantedNamesAreTestedAsSlanted()
        {
            // Two names slanted 45° side by side along their own line clear each other, though their upright bounding
            // boxes would overlap; crossing at the same point they don't.
            var a = new LabelBox(Vector2.zero, Name, 45);
            var along = new LabelBox(new Vector2(80, 80), Name, 45);
            Assert.IsFalse(LabelBox.Overlap(a, along), "end to end along the slant");
            var beside = new LabelBox(new Vector2(-20, 20), Name, 45);
            Assert.IsFalse(LabelBox.Overlap(a, beside), "a name's height off the slant, side by side");
            Assert.IsTrue(LabelBox.Overlap(a, new LabelBox(Vector2.zero, Name, -45)), "crossing");
        }

        [Test]
        public void NT7_AFreeSpotIsOneNoPlacedNameTouches()
        {
            var taken = new List<LabelBox> { new LabelBox(Vector2.zero, Name, 0), new LabelBox(new Vector2(0, 100), Name, 0) };
            Assert.IsFalse(LabelPlacement.Free(taken, new LabelBox(new Vector2(0, 90), Name, 0)));
            Assert.IsTrue(LabelPlacement.Free(taken, new LabelBox(new Vector2(0, 50), Name, 0)));
            Assert.IsTrue(LabelPlacement.Free(new List<LabelBox>(), new LabelBox(Vector2.zero, Name, 0)));
        }

        [Test]
        public void NT7_NamesReadLeftToRight()
        {
            // Panel pixels, y down: a line rising to the right slants anticlockwise (negative), whichever end comes first.
            Assert.That(LandmarkLabelOverlay.Slant(new Vector2(0, 0), new Vector2(100, -100)), Is.EqualTo(-45).Within(0.01f));
            Assert.That(LandmarkLabelOverlay.Slant(new Vector2(100, -100), new Vector2(0, 0)), Is.EqualTo(-45).Within(0.01f));
            Assert.That(LandmarkLabelOverlay.Slant(new Vector2(0, 0), new Vector2(-100, 0)), Is.EqualTo(0).Within(0.01f));
            Assert.That(LandmarkLabelOverlay.Slant(new Vector2(0, 0), new Vector2(0, 100)), Is.EqualTo(90).Within(0.01f));
        }
    }
}
