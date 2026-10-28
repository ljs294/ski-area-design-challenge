using MountainPlanner.Presentation.Design;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>Task P3-10, sections findings 5 and 6: a trail's drawn line, rounded at its turns and stopped short of its ends.</summary>
    public sealed class TrailLineShapeTests
    {
        static float Length(Vector2[] l)
        {
            float s = 0;
            for (int k = 1; k < l.Length; k++) s += Vector2.Distance(l[k - 1], l[k]);
            return s;
        }

        [Test]
        public void Finding6_ALineStopsFiftyFeetShortOfEachEnd()
        {
            var line = new[] { new Vector2(0, 0), new Vector2(0, -200), new Vector2(100, -300) };
            var trimmed = TrailLineShape.Trim(line);
            Assert.That(Vector2.Distance(trimmed[0], line[0]), Is.EqualTo(15.24f).Within(0.01f));
            Assert.That(Length(trimmed), Is.EqualTo(Length(line) - 2 * 15.24f).Within(0.01f));
            var shortLine = new[] { new Vector2(0, 0), new Vector2(0, -30) };
            Assert.That(Length(TrailLineShape.Trim(shortLine)), Is.EqualTo(10).Within(0.01f), "a short line keeps its middle third");
        }

        [Test]
        public void Finding5_ATurnOntoAnotherRunIsRoundedNearTheCornerOnly()
        {
            // Down 100 m, then a right-angle turn east for 100 m: the join is the corner.
            var line = new[] { new Vector2(0, 0), new Vector2(0, -50), new Vector2(0, -100), new Vector2(50, -100), new Vector2(100, -100) };
            var round = TrailLineShape.RoundJoins(line, new[] { 2 });
            Assert.That(round[0], Is.EqualTo(line[0]));
            Assert.That(round[round.Length - 1], Is.EqualTo(line[line.Length - 1]));
            float nearest = float.MaxValue;
            foreach (var p in round) nearest = Mathf.Min(nearest, Vector2.Distance(p, line[2]));
            Assert.That(nearest, Is.GreaterThan(2f), "the line no longer passes through the corner");
            foreach (var p in round)
                Assert.That(Mathf.Min(Mathf.Abs(p.x), Mathf.Abs(p.y + 100)), Is.LessThan(TrailLineShape.JoinReachMetres), "the rounding stays near the corner");
            Assert.That(TrailLineShape.RoundJoins(line, new int[0]), Is.SameAs(line), "no joins, the line itself");
        }
    }
}
