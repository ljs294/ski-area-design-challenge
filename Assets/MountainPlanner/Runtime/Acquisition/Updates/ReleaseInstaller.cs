#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>Where an install is: the step ("Downloading", "Checking", "Unzipping") and bytes done of total.</summary>
    public readonly struct InstallProgress
    {
        public readonly string Step;
        public readonly long Done, Total;
        public InstallProgress(string step, long done, long total) { Step = step; Done = done; Total = total; }
        public double Fraction => Total > 0 ? Math.Min(1.0, (double)Done / Total) : 0;
    }

    /// <summary>
    /// Installs one release into a <see cref="VersionStore"/> (task 4.23, UP5):
    /// 1. reads the release's .sha256 (and GitHub's own digest, when it gives one: the two must agree);
    /// 2. downloads the zip to <c>downloads\&lt;zip&gt;.partial</c>, resuming a dropped download where it stopped;
    /// 3. checks its SHA-256 (a mismatch deletes it);
    /// 4. unzips into <c>versions\&lt;v&gt;.partial\</c>, every entry kept inside that folder, then renames it to
    ///    <c>versions\&lt;v&gt;\</c> and writes the marker last.
    /// Whatever fails, the version already installed is untouched and still plays. The program writes every file itself,
    /// so nothing it installs carries the Mark of the Web; it never removes one either.
    /// </summary>
    public static class ReleaseInstaller
    {
        /// <summary>Spare room asked for on top of the zip and its unzipped files.</summary>
        public const long SpareBytes = 200L << 20;

        public static async Task<GameVersion> InstallAsync(IReleaseFeed feed, ReleaseInfo release, VersionStore store,
                                                           IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            var v = release.Version;
            if (store.Installed().Contains(v)) return v;
            Directory.CreateDirectory(store.Downloads);
            Directory.CreateDirectory(store.Versions);
            // The zip, then about as much again unzipped (the game's files barely compress).
            RequireSpace(store.Root, release.Zip.Size * 2 + SpareBytes);

            progress?.Report(new InstallProgress("Checking", 0, 0));
            string expected = ShaFile.Parse(await feed.ReadTextAsync(release.Sha, ct).ConfigureAwait(false), release.Zip.Name);
            if (release.Zip.Sha256.Length > 0 && release.Zip.Sha256 != expected)
                throw new UpdateException($"{release.Zip.Name}: the .sha256 file and GitHub's digest disagree; nothing was installed.");

            string zip = await DownloadAsync(feed, release, store, expected, progress, ct).ConfigureAwait(false);
            Extract(zip, v, store, progress, ct);
            TryDelete(zip);
            store.Prune(v);
            return v;
        }

        /// <summary>The verified zip's path in the downloads folder.</summary>
        public static async Task<string> DownloadAsync(IReleaseFeed feed, ReleaseInfo release, VersionStore store, string expectedSha,
                                                       IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            string final = Path.Combine(store.Downloads, release.Zip.Name);
            string partial = final + VersionStore.PartialSuffix;
            long size = release.Zip.Size;
            if (File.Exists(final) && ShaFile.Of(final) == expectedSha) return final;
            TryDelete(final);
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (size > 0 && have > size) { TryDelete(partial); have = 0; }

            if (size <= 0 || have < size)
            {
                using (var source = await feed.OpenAsync(release.Zip, have, ct).ConfigureAwait(false))
                {
                    if (!source.Resumed) have = 0;
                    long total = size > 0 ? size : (source.Remaining >= 0 ? have + source.Remaining : 0);
                    using (var file = new FileStream(partial, source.Resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        var buffer = new byte[1 << 16];
                        long done = have;
                        var lastReport = DateTime.MinValue;
                        while (true)
                        {
                            int n;
                            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                            {
                                stall.CancelAfter(GitHubFeed.StallLimit);
                                try { n = await source.Stream.ReadAsync(buffer, 0, buffer.Length, stall.Token).ConfigureAwait(false); }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    throw new UpdateException($"The download sent nothing for {GitHubFeed.StallLimit.TotalSeconds:0} s; run the update again to carry on.");
                                }
                            }
                            if (n == 0) break;
                            await file.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                            done += n;
                            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
                            {
                                lastReport = DateTime.UtcNow;
                                progress.Report(new InstallProgress("Downloading", done, total));
                            }
                        }
                        progress?.Report(new InstallProgress("Downloading", done, total));
                    }
                }
            }

            long got = new FileInfo(partial).Length;
            if (size > 0 && got < size) throw new UpdateException($"The download stopped at {got >> 20} of {size >> 20} MB; run the update again to carry on.");
            progress?.Report(new InstallProgress("Checking", 0, 0));
            string actual = ShaFile.Of(partial);
            if (actual != expectedSha)
            {
                TryDelete(partial);
                throw new UpdateException($"{release.Zip.Name} didn't match its SHA-256 (got {actual.Substring(0, 12)}…, expected {expectedSha.Substring(0, 12)}…); it was deleted and nothing was installed.");
            }
            File.Move(partial, final);
            return final;
        }

        /// <summary>
        /// Unzips a verified release zip into <c>versions\&lt;v&gt;\</c> (its one top folder, SkiAreaDesignChallenge-0.3.2,
        /// stripped). Throws <see cref="UpdateException"/> for an entry outside that folder or a zip without the game.
        /// </summary>
        public static void Extract(string zip, GameVersion v, VersionStore store, IProgress<InstallProgress>? progress, CancellationToken ct)
        {
            string top = ReleaseInfo.TopFolder(v) + "/";
            string target = store.FolderOf(v) + VersionStore.PartialSuffix;
            TryDeleteDirectory(target);
            Directory.CreateDirectory(target);
            try
            {
                using (var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    long total = archive.Entries.Sum(e => e.Length);
                    RequireSpace(store.Root, total + SpareBytes);
                    long done = 0;
                    var buffer = new byte[1 << 16];
                    var lastReport = DateTime.MinValue;
                    foreach (var entry in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = entry.FullName.Replace('\\', '/');
                        if (!name.StartsWith(top, StringComparison.Ordinal))
                            throw new UpdateException($"The zip holds {name}, outside {top}; nothing was installed.");
                        string relative = name.Substring(top.Length);
                        if (relative.Length == 0) continue;
                        string dest = Path.GetFullPath(Path.Combine(target, relative));
                        if (!VersionStore.IsInside(target, dest) || dest.TrimEnd('\\', '/').Length <= target.TrimEnd('\\', '/').Length)
                            throw new UpdateException($"The zip entry {name} points outside the install; nothing was installed.");
                        if (name.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(dest); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        using (var input = entry.Open())
                        using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            int n;
                            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, n);
                                done += n;
                            }
                        }
                        if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
                        {
                            lastReport = DateTime.UtcNow;
                            progress.Report(new InstallProgress("Unzipping", done, total));
                        }
                    }
                    progress?.Report(new InstallProgress("Unzipping", done, total));
                }
                if (!File.Exists(Path.Combine(target, VersionStore.GameExe)))
                    throw new UpdateException($"The zip has no {VersionStore.GameExe}; nothing was installed.");
                string final = store.FolderOf(v);
                TryDeleteDirectory(final);
                Directory.Move(target, final);
                File.WriteAllText(Path.Combine(final, VersionStore.Marker), "");
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                TryDeleteDirectory(target);
                if (e is UpdateException) throw;
                if (e is InvalidDataException) throw new UpdateException($"The zip is damaged ({e.Message}); nothing was installed.", e);
                throw new UpdateException($"Unzipping failed ({e.Message}); nothing was installed.", e);
            }
            catch (OperationCanceledException)
            {
                TryDeleteDirectory(target);
                throw;
            }
        }

        static void RequireSpace(string root, long bytes)
        {
            long free;
            try
            {
                string? drive = Path.GetPathRoot(Path.GetFullPath(root));
                if (string.IsNullOrEmpty(drive)) return;
                free = new DriveInfo(drive).AvailableFreeSpace;
            }
            catch (Exception) { return; }
            if (free < bytes)
                throw new UpdateException($"The update needs about {(bytes >> 20) + 1} MB free on {Path.GetPathRoot(root)}; there are {free >> 20} MB. Free some space and try again.");
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
    }
}
