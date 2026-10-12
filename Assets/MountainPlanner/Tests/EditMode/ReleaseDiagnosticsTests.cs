using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MountainPlanner.App;
using MountainPlanner.App.Diagnostics;
using MountainPlanner.Persistence;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-10: errors from the main thread and from workers reach the session's log, headed by the version and the
    /// machine; the session's first error is announced once; the old company's prefs are copied to Alpine Labs once.
    /// </summary>
    public sealed class ErrorReporterTests
    {
        string _folder;

        [SetUp]
        public void Install()
        {
            _folder = Path.Combine(Path.GetTempPath(), "mp-reporter-" + Guid.NewGuid().ToString("N"));
            ErrorReporter.Install(_folder);
        }

        [TearDown]
        public void Uninstall()
        {
            ErrorReporter.Uninstall();
            LogAssert.ignoreFailingMessages = false;
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        [Test]
        public void ACleanSessionWritesNothing()
        {
            Debug.Log("an ordinary line");
            Debug.LogWarning("a warning");
            Assert.IsNull(ErrorReporter.LogPath);
            Assert.IsFalse(Directory.Exists(_folder));
        }

        [Test]
        public void ExceptionsAndErrorsFromAnyThreadReachTheLog()
        {
            LogAssert.ignoreFailingMessages = true;   // set in the test: the runner resets it after SetUp
            Debug.LogException(new InvalidOperationException("main-thread boom"));
            Task.Run(() => Debug.LogError("worker-thread error")).Wait();
            Debug.LogAssertion("an assert");
            string path = ErrorReporter.LogPath;
            Assert.IsNotNull(path);
            Assert.AreEqual(3, ErrorReporter.Count);
            ErrorReporter.Uninstall();
            string text = File.ReadAllText(path);
            StringAssert.StartsWith(Application.productName + " " + BuildInfo.Current.DisplayVersion, text);
            StringAssert.Contains("GPU         " + SystemInfo.graphicsDeviceName, text);
            StringAssert.Contains("Display     ", text);
            StringAssert.Contains("Exception: InvalidOperationException: main-thread boom", text);
            StringAssert.Contains(nameof(ExceptionsAndErrorsFromAnyThreadReachTheLog), text, "with its stack trace");
            StringAssert.Contains("Error: worker-thread error", text);
            StringAssert.Contains("Assert: an assert", text);
        }

        [Test]
        public void TheUsersFolderStaysOutOfTheLog()
        {
            LogAssert.ignoreFailingMessages = true;
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Debug.LogError($@"Could not find {profile}\Pictures\a.png or {profile.Replace('\\', '/')}/b.png");
            string path = ErrorReporter.LogPath;
            ErrorReporter.Uninstall();
            string text = File.ReadAllText(path);
            StringAssert.DoesNotContain(profile, text);
            StringAssert.Contains(@"%USERPROFILE%\Pictures\a.png", text);
            StringAssert.Contains("%USERPROFILE%/b.png", text);
        }

        [Test]
        public void TheFirstErrorIsAnnouncedOnce()
        {
            LogAssert.ignoreFailingMessages = true;
            var pump = ErrorReporter.CreatePump();
            int announced = 0;
            Action<string> heard = _ => announced++;
            ErrorReporter.FirstError += heard;
            try
            {
                Debug.LogError("first");
                Debug.LogError("second");
                pump.SendMessage("Update");
                pump.SendMessage("Update");
                Assert.AreEqual(1, announced);
            }
            finally
            {
                ErrorReporter.FirstError -= heard;
                UnityEngine.Object.DestroyImmediate(pump.gameObject);
            }
        }
    }

    public sealed class PrefsMigrationTests
    {
        const int Dword = 4, Binary = 3;
        string _root;

        [SetUp]
        public void Key() => _root = @"Software\MountainPlannerTests-" + Guid.NewGuid().ToString("N");

        [TearDown]
        public void Clean() => PrefsMigration.DeleteTree(_root);

        string Old => _root + @"\Ski Area Design Challenge\Ski Area Design Challenge";
        string New => _root + @"\Alpine Labs\Ski Area Design Challenge";

        [Test]
        public void EveryOldValueIsCopiedOnceAsItIs()
        {
            byte[] folder = Encoding.UTF8.GetBytes(@"D:\Areas" + "\0");
            PrefsMigration.WriteValue(Old, "MountainPlanner.Data.LibraryFolder_h123", Binary, folder);
            PrefsMigration.WriteValue(Old, "MountainPlanner.Theme_h456", Dword, BitConverter.GetBytes(2));
            PrefsMigration.WriteValue(Old, "Screenmanager Fullscreen mode_h789", Dword, BitConverter.GetBytes(3));
            PrefsMigration.WriteValue(New, "Screenmanager Fullscreen mode_h789", Dword, BitConverter.GetBytes(1));   // already chosen here

            Assert.AreEqual(2, PrefsMigration.Copy(Old, New));
            Assert.IsTrue(PrefsMigration.ReadValue(New, "MountainPlanner.Data.LibraryFolder_h123", out int type, out byte[] data));
            Assert.AreEqual(Binary, type);
            CollectionAssert.AreEqual(folder, data);
            Assert.IsTrue(PrefsMigration.ReadValue(New, "MountainPlanner.Theme_h456", out type, out data));
            Assert.AreEqual(Dword, type);
            Assert.AreEqual(2, BitConverter.ToInt32(data, 0));
            PrefsMigration.ReadValue(New, "Screenmanager Fullscreen mode_h789", out _, out data);
            Assert.AreEqual(1, BitConverter.ToInt32(data, 0), "a value the new key has stays");
            Assert.IsTrue(PrefsMigration.ReadValue(Old, "MountainPlanner.Theme_h456", out _, out _), "the old key stays for older builds");

            PrefsMigration.WriteValue(Old, "MountainPlanner.Units_h1", Dword, BitConverter.GetBytes(1));
            Assert.AreEqual(-1, PrefsMigration.Copy(Old, New), "the marker stops a second copy");
            Assert.IsFalse(PrefsMigration.ReadValue(New, "MountainPlanner.Units_h1", out _, out _));
        }

        [Test]
        public void NothingToCopyWithoutTheOldKey()
        {
            Assert.AreEqual(-1, PrefsMigration.Copy(Old, New));
            Assert.IsFalse(PrefsMigration.ReadValue(New, PrefsMigration.Marker, out _, out _));
        }
    }
}
