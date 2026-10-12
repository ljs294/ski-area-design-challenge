using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task P2-10: the version comes from the release tag, and the error log a tester sends.</summary>
    public sealed class ReleaseVersionTests
    {
        [TestCase("v0.2.0-0-gabc1234", false, ExpectedResult = "0.2.0")]
        [TestCase("v0.2.0-0-gabc1234", true, ExpectedResult = "0.2.0+dev.0.abc1234.dirty")]
        [TestCase("v0.2.0-7-gabc1234", false, ExpectedResult = "0.2.0+dev.7.abc1234")]
        [TestCase("v1.10.3-12-g0123abc", true, ExpectedResult = "1.10.3+dev.12.0123abc.dirty")]
        public string DescribeGivesTheVersion(string describe, bool dirty) => ReleaseVersion.FromDescribe(describe, dirty);

        [Test]
        public void NoTagYetIsAPreviewOfZero()
        {
            Assert.AreEqual("0.0.0+dev.312.abc1234", ReleaseVersion.FromDescribe(null, false, "abc1234", 312));
            Assert.AreEqual("0.0.0+dev.312.abc1234", ReleaseVersion.FromDescribe("v0.1-40-gabc1234", false, "abc1234", 312), "v0.1 (MapLibre) isn't a release tag");
            Assert.IsFalse(ReleaseVersion.IsRelease("0.0.0+dev.312.abc1234"));
        }

        [Test]
        public void OnlyPlainThreeNumberTagsAreReleases()
        {
            Assert.IsTrue(ReleaseVersion.IsTag("v0.2.0"));
            Assert.IsFalse(ReleaseVersion.IsTag("v0.1"));
            Assert.IsFalse(ReleaseVersion.IsTag("v0.2.0-alpha"));
            Assert.IsFalse(ReleaseVersion.IsTag("unity-m1"));
            Assert.AreEqual("v0.2.0", ReleaseVersion.TagOf("0.2.0"));
            Assert.IsNull(ReleaseVersion.TagOf("0.2.0+dev.7.abc1234"));
        }
    }

    public sealed class ErrorLogTests
    {
        static readonly DateTime Start = new DateTime(2026, 10, 12, 18, 3, 11, DateTimeKind.Utc);
        string _folder;

        [SetUp]
        public void Folder()
        {
            _folder = Path.Combine(Path.GetTempPath(), "mp-errorlog-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void Clean()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        static ErrorEntry Entry(string message, double uptime = 1, string stack = "at A.B ()\nat C.D ()") =>
            new ErrorEntry("Exception", message, stack, Start.AddSeconds(uptime), uptime);

        [Test]
        public void NoErrorsLeaveNoFile()
        {
            using (var log = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "header")) Assert.IsNull(log.Path);
            Assert.IsFalse(Directory.Exists(_folder));
        }

        [Test]
        public void TheFirstErrorCreatesTheFileWithItsHeader()
        {
            string text;
            using (var log = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "Ski Area Design Challenge 0.2.0"))
            {
                Assert.IsTrue(log.Write(Entry("NullReferenceException: boom")), "the first entry says so");
                Assert.IsFalse(log.Write(Entry("InvalidOperationException: again", 2)));
                Assert.AreEqual(Path.Combine(_folder, "errors-20261012-180311.log"), log.Path);
                text = ErrorLog.ReadShared(log.Path);   // readable while the game still holds it
            }
            StringAssert.StartsWith("Ski Area Design Challenge 0.2.0", text);
            StringAssert.Contains("[2026-10-12 18:03:12Z +1.0 s] Exception: NullReferenceException: boom", text);
            StringAssert.Contains("    at C.D ()", text);
            StringAssert.Contains("InvalidOperationException: again", text);
        }

        [Test]
        public void RepeatsAreCountedNotWritten()
        {
            string path;
            using (var log = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "h"))
            {
                for (int i = 0; i < 50; i++) log.Write(Entry("same", 1 + i));
                log.Write(Entry("different", 60));
                Assert.AreEqual(51, log.Count);
                path = log.Path;
            }
            string text = File.ReadAllText(path);
            Assert.AreEqual(1, CountOf(text, "same\r\n"));
            StringAssert.Contains("(the same entry 49 more times, the last at +50.0 s)", text);
            Assert.Less(text.IndexOf("49 more times", StringComparison.Ordinal), text.IndexOf("different", StringComparison.Ordinal));
        }

        [Test]
        public void TheFileStopsAtItsCap()
        {
            string path;
            using (var log = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "h", maxBytes: 2000))
            {
                for (int i = 0; i < 200; i++) log.Write(Entry("error " + i, i));
                path = log.Path;
            }
            Assert.LessOrEqual(new FileInfo(path).Length, 2000 + 200);
            StringAssert.Contains("later errors in this session aren't recorded", File.ReadAllText(path));
        }

        [Test]
        public void OnlyTheNewestLogsAreKept()
        {
            for (int i = 0; i < 12; i++)
                using (var log = new ErrorLog(_folder, ErrorLog.Stamp(Start.AddMinutes(i)), () => "h", keep: 10))
                    log.Write(Entry("e"));
            var files = ErrorLog.Files(_folder);
            Assert.AreEqual(10, files.Count);
            StringAssert.EndsWith("errors-20261012-181411.log", files[0], "newest first");
            Assert.IsFalse(files.Any(f => f.EndsWith("errors-20261012-180311.log", StringComparison.Ordinal)), "the oldest went");
        }

        [Test]
        public void TwoSessionsInOneSecondGetTwoFiles()
        {
            using (var a = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "a"))
            using (var b = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "b"))
            {
                a.Write(Entry("x"));
                b.Write(Entry("y"));
                Assert.AreNotEqual(a.Path, b.Path);
            }
        }

        [Test]
        public void WorkerThreadsCanWriteAtOnce()
        {
            string path;
            using (var log = new ErrorLog(_folder, ErrorLog.Stamp(Start), () => "h"))
            {
                int firsts = 0;
                Parallel.For(0, 400, i => { if (log.Write(Entry("thread error " + i, i))) System.Threading.Interlocked.Increment(ref firsts); });
                Assert.AreEqual(1, firsts, "exactly one writer created the file");
                Assert.AreEqual(400, log.Count);
                path = log.Path;
            }
            Assert.AreEqual(400, CountOf(File.ReadAllText(path), "thread error "));
        }

        static int CountOf(string text, string part)
        {
            int count = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
            return count;
        }
    }
}
