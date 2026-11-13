using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Services;

namespace RecoverX.CoreTests
{
    internal sealed class FakeShadow : IShadowCopyService
    {
        public ScanOpenResult Result = new ScanOpenResult { Status = EngineStatus.NotFound };
        public int Calls;
        public ScanOpenResult OpenSnapshotScan(DriveModel d) { Calls++; return Result; }
    }

    // ---------------- restore points (Previous Versions) ----------------
    [TestClass]
    public class SnapshotScanTests
    {
        private string _root;
        [TestInitialize] public void Init() { _root = Path.Combine(Path.GetTempPath(), "rx_snap_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
        [TestCleanup] public void Done() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private string Put(string dir, string rel, string content, DateTime? mtime = null)
        {
            string p = Path.Combine(_root, dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            if (mtime.HasValue) File.SetLastWriteTimeUtc(p, mtime.Value);
            return p;
        }

        private SnapshotSource Snap(string dir, DateTime when) { return new SnapshotSource { Root = Path.Combine(_root, dir) + "\\", CreatedUtc = when }; }

        [TestMethod]
        public void Files_in_a_restore_point_but_gone_from_the_drive_are_found_and_read_back_exactly()
        {
            var t1 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
            Put("live", "Docs\\kept.txt", "still here");
            Put("snapA", "Docs\\kept.txt", "older copy of a kept file");
            Put("snapA", "Docs\\deleted.txt", "this file was deleted", t1);
            Put("snapA", "Photos\\Holiday\\beach.jpg", "jpeg bytes");                 // whole folder gone from the drive
            Put("snapA", "Windows\\System32\\x.dll", "system");                        // skipped at the root
            Put("snapA", "Users\\me\\AppData\\Local\\cache.bin", "cache");             // AppData skipped anywhere
            Put("snapA", "Users\\me\\Documents\\notes.md", "notes");
            Directory.CreateDirectory(Path.Combine(_root, "live", "Users", "me", "Documents"));

            var s = new SnapshotScanSession(Path.Combine(_root, "live"), new List<SnapshotSource> { Snap("snapA", t1) });
            Assert.AreEqual(ScanMode.PreviousVersions, s.Mode);
            Assert.AreEqual(EngineStatus.Ok, s.Run(CancellationToken.None));
            var r = s.GetResults();
            CollectionAssert.AreEquivalent(new[] { "\\Docs\\deleted.txt", "\\Photos\\Holiday\\beach.jpg", "\\Users\\me\\Documents\\notes.md" }, r.Select(e => e.Path).ToArray());
            foreach (var e in r)
            {
                Assert.AreEqual(EntryOrigin.Snapshot, e.Origin);
                Assert.AreEqual(RecoveryRating.Excellent, e.Rating);
                Assert.AreEqual(t1, e.SnapshotTime);
            }
            ScanEntry del = r.Single(e => e.Path == "\\Docs\\deleted.txt");
            Assert.AreEqual(t1, del.Modified);
            var buf = new byte[1000]; int n;
            Assert.AreEqual(EngineStatus.Ok, s.ReadFile(del.Index, 0, buf, buf.Length, out n));
            Assert.AreEqual("this file was deleted", Encoding.UTF8.GetString(buf, 0, n));
            Assert.AreEqual(EngineStatus.Ok, s.ReadFile(del.Index, 5, buf, 4, out n));
            Assert.AreEqual("file", Encoding.UTF8.GetString(buf, 0, n));
            Assert.AreEqual(EngineStatus.InvalidArgument, s.ReadFile(99, 0, buf, 4, out n));
            Assert.IsTrue(s.GetProgress().IsDone && s.GetProgress().Candidates == 3);
            s.Dispose();
        }

        [TestMethod]
        public void The_newest_restore_point_wins_and_a_file_is_listed_once()
        {
            DateTime older = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), newer = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            Directory.CreateDirectory(Path.Combine(_root, "live"));
            Put("old", "a.txt", "old version");
            Put("new", "a.txt", "new version");
            Put("old", "only_old.txt", "x");
            var s = new SnapshotScanSession(Path.Combine(_root, "live"), new List<SnapshotSource> { Snap("new", newer), Snap("old", older) });
            Assert.AreEqual(EngineStatus.Ok, s.Run(CancellationToken.None));
            var r = s.GetResults();
            Assert.AreEqual(2, r.Count);
            ScanEntry a = r.Single(e => e.Path == "\\a.txt");
            Assert.AreEqual(newer, a.SnapshotTime);
            var buf = new byte[64]; int n;
            s.ReadFile(a.Index, 0, buf, buf.Length, out n);
            Assert.AreEqual("new version", Encoding.UTF8.GetString(buf, 0, n));
            s.Dispose();
        }

        [TestMethod]
        public void Restore_point_search_can_be_paused_and_cancelled()
        {
            Directory.CreateDirectory(Path.Combine(_root, "live"));
            for (int i = 0; i < 50; i++) Put("snap", "d" + i + "\\f.txt", "x");
            var s = new SnapshotScanSession(Path.Combine(_root, "live"), new List<SnapshotSource> { Snap("snap", DateTime.UtcNow) });
            s.SetPaused(true);
            var cts = new CancellationTokenSource();
            Task<EngineStatus> run = Task.Run(() => s.Run(cts.Token));
            Thread.Sleep(200);
            Assert.IsFalse(run.IsCompleted);                                        // paused: no progress
            Assert.AreEqual(0, s.GetProgress().Candidates);
            cts.Cancel();
            Assert.AreEqual(EngineStatus.Cancelled, run.Result);
            var s2 = new SnapshotScanSession(Path.Combine(_root, "live"), new List<SnapshotSource> { Snap("snap", DateTime.UtcNow) });
            s2.SetPaused(true);
            run = Task.Run(() => s2.Run(CancellationToken.None));
            Thread.Sleep(100);
            s2.SetPaused(false);
            Assert.AreEqual(EngineStatus.Ok, run.Result);
            Assert.AreEqual(50, s2.GetResults().Count);
        }

        [TestMethod]
        public void Restore_point_mode_needs_an_ntfs_drive_and_records_history()
        {
            var shadow = new FakeShadow();
            var hist = new FakeHistory();
            var svc = new ScanService(new FakeEngine { Available = false }, hist, new NullLogger(), shadow);
            var fat = new DriveModel { Letter = "F:", RootPath = "F:\\", FileSystem = "FAT32" };
            Assert.AreEqual(EngineStatus.Unsupported, svc.RunAsync(fat, ScanMode.PreviousVersions, new AppSettings(), null, CancellationToken.None).Result.Status);
            Assert.AreEqual(0, shadow.Calls);
            var ntfs = new DriveModel { Letter = "D:", RootPath = "D:\\", FileSystem = "NTFS" };
            Assert.AreEqual(EngineStatus.NotFound, svc.RunAsync(ntfs, ScanMode.PreviousVersions, new AppSettings(), null, CancellationToken.None).Result.Status);   // no restore points
            Directory.CreateDirectory(Path.Combine(_root, "live"));
            Put("snap", "lost.txt", "x");
            shadow.Result = new ScanOpenResult { Status = EngineStatus.Ok, Session = new SnapshotScanSession(Path.Combine(_root, "live"), new List<SnapshotSource> { Snap("snap", DateTime.UtcNow) }) };
            ScanOutcome o = svc.RunAsync(ntfs, ScanMode.PreviousVersions, new AppSettings(), null, CancellationToken.None).Result;   // works without the native engine
            Assert.AreEqual(EngineStatus.Ok, o.Status);
            Assert.AreEqual(1, o.Entries.Count);
            Assert.AreEqual("Snapshot", hist.Sessions.Last().ScanType);
            o.Session.Dispose();
        }
    }

    // ---------------- FAT/exFAT entry flags in the UI layer ----------------
    [TestClass]
    public class FatEntryTests
    {
        [TestMethod]
        public void Fat_flags_map_from_the_engine_values()
        {
            Assert.AreEqual(512, (int)EntryFlags.NameGuessed);
            Assert.AreEqual(1024, (int)EntryFlags.ChainFollowed);
            Assert.AreEqual(10, (int)EngineStatus.NotFat);
        }
    }
}
