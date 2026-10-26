using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Reports;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task P3-14: the problem report zip, the player's names kept out of it, the issue link and the crash marker.</summary>
    public sealed class ProblemReportTests
    {
        const string User = "pat";
        const string Computer = "PATS-DESKTOP";
        const string JacksonHole = "5792676e513f5302";
        static readonly DateTime Now = new DateTime(2026, 10, 25, 14, 32, 10, DateTimeKind.Utc);
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-reports-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        static ReportPrivacy Privacy => new ReportPrivacy(User, Computer);

        [Test]
        public void TheNamesAreTakenOutAsWholeWordsInAnyCase()
        {
            string text = @"Loading C:\Users\Pat\AppData\LocalLow\x (path C:/Users/pat/Saves) on PATS-DESKTOP; pathway, patio, Pat's";
            string scrubbed = Privacy.Scrub(text);
            Assert.AreEqual(@"Loading C:\Users\%USERNAME%\AppData\LocalLow\x (path C:/Users/%USERNAME%/Saves) on %COMPUTERNAME%; pathway, patio, %USERNAME%'s", scrubbed);
            Assert.IsFalse(Privacy.Leaks(scrubbed));
            Assert.AreEqual("a b", new ReportPrivacy("a", "").Scrub("a b"), "a one-letter name would eat the text");
            Assert.AreEqual("", ReportPrivacy.None.Scrub(""));
        }

        [Test]
        public void TheZipHoldsWhatIsTickedAndListsIt()
        {
            var sources = MakeSources(out string saveFolder);
            var before = Hashes(saveFolder);
            var items = ReportBundle.Gather(sources, Now);
            CollectionAssert.AreEquivalent(ReportPart.All.Where(p => p != ReportPart.System), items.Select(i => i.Part).Distinct(),
                "every part but System has files (System and settings live in report.json)");

            string zip = Path.Combine(ReportBundle.FolderIn(_root), ReportBundle.FileNameFor(Now));
            Assert.AreEqual("report-20261025-143210.zip", Path.GetFileName(zip));
            var report = Report();
            long size = ReportBundle.Write(zip, report, items, ReportPart.All.ToList(), Privacy);
            Assert.Less(size, ReportBundle.GitHubLimit);
            Assert.IsFalse(File.Exists(zip + ".tmp"));

            var entries = Entries(zip);
            CollectionAssert.AreEquivalent(report.Files.Select(f => f.Path).Append(ProblemReport.FileName), entries.Keys);
            CollectionAssert.Contains(entries.Keys, "logs/Player.log");
            CollectionAssert.Contains(entries.Keys, "crash/crash.dmp");
            CollectionAssert.Contains(entries.Keys, "screenshot.jpg");
            Assert.AreEqual(before, Hashes(saveFolder), "the save is only read");
            foreach (var file in Directory.GetFiles(saveFolder, "*", SearchOption.AllDirectories))
            {
                string rel = "save/" + file.Substring(saveFolder.Length + 1).Replace('\\', '/');
                CollectionAssert.AreEqual(File.ReadAllBytes(file), entries[rel], $"{rel} goes in byte for byte");
            }
        }

        [Test]
        public void NoTextInTheZipNamesThePlayer()
        {
            var sources = MakeSources(out _);
            string zip = Path.Combine(_root, "r.zip");
            var report = Report();
            report.Title = "Crash when pat saves";
            report.Note = @"My saves are in C:\Users\pat\AppData";
            ReportBundle.Write(zip, report, ReportBundle.Gather(sources, Now), ReportPart.All.ToList(), Privacy);
            foreach (var entry in Entries(zip).Where(e => !e.Key.StartsWith("save/") && !e.Key.EndsWith(".dmp") && !e.Key.EndsWith(".jpg")))
                Assert.IsFalse(Privacy.Leaks(Encoding.UTF8.GetString(entry.Value)), $"{entry.Key} names the player");
            var read = ReportBundle.Read(zip);
            Assert.AreEqual("Crash when %USERNAME% saves", read.Title);
            StringAssert.Contains(@"C:\Users\%USERNAME%\AppData", read.Note);
        }

        [Test]
        public void ALongGameLogKeepsItsEnd()
        {
            var sources = MakeSources(out _);
            var items = ReportBundle.Gather(sources, Now);
            var log = items.Single(i => i.ZipPath == "logs/Player.log");
            Assert.IsTrue(log.Cut);
            Assert.AreEqual(ReportBundle.LogTail, log.Size);
            string zip = Path.Combine(_root, "r.zip");
            var report = Report();
            ReportBundle.Write(zip, report, items, ReportPart.All.ToList(), Privacy);
            string text = Encoding.UTF8.GetString(Entries(zip)["logs/Player.log"]);
            StringAssert.StartsWith("[Only the end of this log is kept", text);
            StringAssert.EndsWith("the last line\n", text);
            StringAssert.DoesNotContain("Mono path[0]", text, "the start went");
            Assert.IsTrue(report.Files.Single(f => f.Path == "logs/Player.log").Cut);
        }

        [Test]
        public void UntickedPartsStayOut()
        {
            var sources = MakeSources(out _);
            string zip = Path.Combine(_root, "r.zip");
            var report = Report();
            var parts = new List<string> { ReportPart.Screenshot };
            ReportBundle.Write(zip, report, ReportBundle.Gather(sources, Now), parts, Privacy);
            CollectionAssert.AreEquivalent(new[] { "screenshot.jpg", ProblemReport.FileName }, Entries(zip).Keys);
            var read = ReportBundle.Read(zip);
            Assert.IsEmpty(read.System, "without System and settings, report.json has neither");
            Assert.IsEmpty(read.Settings);
            Assert.AreEqual("0.3.0", read.GameVersion, "the version always goes");
            Assert.AreEqual(JacksonHole, read.Area.PackageId, "so does the area's id");
        }

        [Test]
        public void ASuggestionStartsWithOnlyThePictureAndTheSystem()
        {
            CollectionAssert.AreEqual(new[] { ReportPart.Screenshot, ReportPart.System },
                ReportPart.All.Where(p => ReportPart.OnByDefault(p, ReportKind.Suggestion)));
            CollectionAssert.AreEqual(ReportPart.All, ReportPart.All.Where(p => ReportPart.OnByDefault(p, ReportKind.Problem)));
        }

        [Test]
        public void AnOldCrashIsntOffered()
        {
            var sources = MakeSources(out _);
            string crash = Directory.GetDirectories(sources.Crashes).Single();
            Directory.SetLastWriteTimeUtc(crash, Now - TimeSpan.FromDays(20));
            Assert.IsNull(ReportBundle.NewestCrash(sources.Crashes, Now));
            Assert.IsFalse(ReportBundle.Gather(sources, Now).Any(i => i.Part == ReportPart.CrashDump));
        }

        [Test]
        public void AReportOpensAgainAndCantWriteOutsideItsFolder()
        {
            var sources = MakeSources(out string saveFolder);
            string zip = Path.Combine(_root, "r.zip");
            ReportBundle.Write(zip, Report(), ReportBundle.Gather(sources, Now), ReportPart.All.ToList(), Privacy);
            string open = Path.Combine(_root, "open");
            var report = ReportBundle.Unpack(zip, open);
            Assert.AreEqual(ReportKind.Problem, report.Kind);
            Assert.AreEqual("NVIDIA GeForce RTX 3060 Ti", report.System[SystemKeys.Gpu]);
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(saveFolder, "save.json")), File.ReadAllBytes(Path.Combine(open, "save", "save.json")));

            string evil = Path.Combine(_root, "evil.zip");
            using (var z = new ZipArchive(File.Create(evil), ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry(ProblemReport.FileName).Open())) w.Write(Report().ToJson());
                using (var w = new StreamWriter(z.CreateEntry("../outside.txt").Open())) w.Write("x");
            }
            Assert.Throws<InvalidDataException>(() => ReportBundle.Unpack(evil, Path.Combine(_root, "evil")));
            Assert.IsFalse(File.Exists(Path.Combine(_root, "outside.txt")));
        }

        [Test]
        public void TheReportIsVersioned()
        {
            string json = Report().ToJson();
            StringAssert.Contains("\"Version\": 1", json);
            Assert.AreEqual(ProblemReport.CurrentVersion, ProblemReport.Parse(json).Version);
            Assert.Throws<FormatTooNewException>(() => ProblemReport.Parse(json.Replace("\"Version\": 1", "\"Version\": 2")));
            Assert.Throws<InvalidDataException>(() => ReportBundle.Read(MakeZipWithout()));
        }

        [Test]
        public void OldReportsArePruned()
        {
            string folder = ReportBundle.FolderIn(_root);
            Directory.CreateDirectory(folder);
            for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(folder, ReportBundle.FileNameFor(Now.AddMinutes(i))), "");
            ReportBundle.Prune(folder);
            var left = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.AreEqual(ReportBundle.Keep, left.Length);
            Assert.AreEqual(ReportBundle.FileNameFor(Now.AddMinutes(2)), left[0], "the oldest two went");
        }

        [Test]
        public void TheIssueLinkCarriesOnlyTheTitleVersionAndCard()
        {
            var report = Report();
            report.Title = "Trees vanish & come back";
            report.Note = "Near the tram.";
            string link = IssueLink.For(report, "report-20261025-143210.zip");
            StringAssert.StartsWith(IssueLink.Repository + "/issues/new?template=problem.yml&title=Trees%20vanish%20%26%20come%20back", link);
            StringAssert.Contains("version=v0.3.0%20%28a1b2c3d%29", link);
            StringAssert.Contains("system=NVIDIA%20GeForce%20RTX%203060%20Ti", link);
            StringAssert.Contains("details=Near%20the%20tram.", link);
            StringAssert.DoesNotContain("Windows", link, "the link holds the card, nothing else about the PC");

            report.Kind = ReportKind.Suggestion;
            report.Note = new string('x', 5000);
            string idea = IssueLink.For(report, "");
            StringAssert.Contains("template=suggestion.yml", idea);
            StringAssert.DoesNotContain("system=", idea);
            Assert.Less(idea.Length, 2500, "a long note is cut");

            string summary = IssueLink.Summary(Report(), "r.zip");
            StringAssert.StartsWith("Problem: Trees vanish\r\nSki Area Design Challenge v0.3.0 (a1b2c3d)\r\n", summary);
            StringAssert.Contains("Area: Jackson Hole", summary);
        }

        [Test]
        public void ACrashIsOfferedOnceAtTheNextLaunch()
        {
            var marker = new CrashMarker(_root);
            marker.Begin("v0.3.0 started 14:00");
            marker.End();
            marker.Begin("v0.3.0 started 15:00");
            Assert.IsNull(marker.PendingCrash, "a clean quit isn't a crash");

            var next = new CrashMarker(_root);
            next.Begin("v0.3.0 started 16:00");   // 15:00's session never ended
            Assert.AreEqual("v0.3.0 started 15:00", next.PendingCrash);
            next.Answered();
            Assert.IsNull(next.PendingCrash);
            next.End();
            new CrashMarker(_root).Begin("v0.3.0 started 17:00");
            Assert.IsNull(new CrashMarker(_root).PendingCrash);
        }

        // ---------- helpers ----------

        static ProblemReport Report()
        {
            var r = new ProblemReport
            {
                Kind = ReportKind.Problem, Title = "Trees vanish", CreatedUtc = "2026-10-25T14:32:10Z",
                GameVersion = "0.3.0", DisplayVersion = "v0.3.0", Commit = "a1b2c3d4e5f6",
                Area = new ReportArea { PackageId = JacksonHole, Name = "Jackson Hole", SaveName = "Upper bowl", Attribution = { "USGS 3DEP" } },
            };
            r.System[SystemKeys.Gpu] = "NVIDIA GeForce RTX 3060 Ti";
            r.System[SystemKeys.Windows] = "Windows 11 (10.0.26200) 64bit";
            r.Settings["MountainPlanner.Quality"] = "High";
            return r;
        }

        ReportSources MakeSources(out string saveFolder)
        {
            string logs = Path.Combine(_root, "Logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "errors-20261024-090000.log"), $"Game v0.3.0\r\nC:\\Users\\{User}\\AppData\\Local\\SkiAreaDesignChallenge\r\n");
            File.WriteAllText(Path.Combine(logs, "errors-20261025-090000.log"), $"NullReferenceException at {Computer}\r\n");

            string game = Path.Combine(_root, "LocalLow");
            Directory.CreateDirectory(game);
            var big = new StringBuilder();
            big.Append($"Mono path[0] = 'C:/Users/{User}/game/Managed'\n");
            while (big.Length < ReportBundle.LogTail + 300_000) big.Append($"Loading tile 12 for C:/Users/{User}/AppData/LocalLow and some padding text\n");
            big.Append("the last line\n");
            File.WriteAllText(Path.Combine(game, "Player.log"), big.ToString());
            File.WriteAllText(Path.Combine(game, "Player-prev.log"), $"Initialize engine version on {Computer}\n");

            string crashes = Path.Combine(_root, "Crashes");
            string crash = Path.Combine(crashes, "Crash_2026-10-24_211203");
            Directory.CreateDirectory(crash);
            File.WriteAllBytes(Path.Combine(crash, "crash.dmp"), new byte[] { 0x4D, 0x44, 0x4D, 0x50, 1, 2, 3 });
            File.WriteAllText(Path.Combine(crash, "error.log"), $"Access violation in C:\\Users\\{User}\\game\\UnityPlayer.dll\n");
            Directory.SetLastWriteTimeUtc(crash, Now - TimeSpan.FromHours(17));

            saveFolder = Path.Combine(_root, "Saves", JacksonHole, "save-001");
            CopyFolder(Path.Combine(TestData.Folder(Path.Combine("formats", "v2-save")), "Saves", JacksonHole, "save-001"), saveFolder);

            return new ReportSources
            {
                ErrorLogs = logs, GameLogs = game, Crashes = crashes, SaveFolder = saveFolder,
                Screenshot = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9 },
            };
        }

        string MakeZipWithout()
        {
            string path = Path.Combine(_root, "notareport.zip");
            Directory.CreateDirectory(_root);
            using (var z = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry("hello.txt").Open())) w.Write("hi");
            return path;
        }

        static void CopyFolder(string from, string to)
        {
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, file.Substring(from.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
        }

        static Dictionary<string, byte[]> Entries(string zip)
        {
            using var z = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read);
            return z.Entries.ToDictionary(e => e.FullName, e =>
            {
                using var s = e.Open();
                var m = new MemoryStream();
                s.CopyTo(m);
                return m.ToArray();
            });
        }

        static string Hashes(string folder)
        {
            using var sha = SHA256.Create();
            return string.Join(",", Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f)))));
        }
    }
}
