using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Services;

namespace RecoverX.CoreTests
{
    // ---------------- fakes ----------------
    internal sealed class FakeSession : IScanSession
    {
        public Dictionary<int, byte[]> Data = new Dictionary<int, byte[]>();
        public List<ScanEntry> Entries = new List<ScanEntry>();
        public EngineStatus RunStatus = EngineStatus.Ok;
        public EngineStatus ReadStatus = EngineStatus.Ok;
        public int FailReadAfterBytes = -1;          // simulate an I/O error part-way through a file
        public int ShortBy = 0;                      // return fewer bytes than the file size
        public Action<int> OnRead;
        public bool Disposed;
        public int ReadCalls;
        public ScanProgress Progress = new ScanProgress { Phase = ScanPhase.Done, IsDone = true };

        public ScanMode SessionMode = ScanMode.Quick;
        public bool? PausedState;
        public ScanMode Mode { get { return SessionMode; } }
        public void SetPaused(bool paused) { PausedState = paused; }
        public ScanProgress GetProgress() { return Progress; }
        public EngineStatus Run(CancellationToken c) { return c.IsCancellationRequested ? EngineStatus.Cancelled : RunStatus; }
        public IReadOnlyList<ScanEntry> GetResults() { return Entries; }
        public EngineStatus ReadFile(int index, long offset, byte[] buffer, int count, out int read)
        {
            ReadCalls++; read = 0;
            if (OnRead != null) OnRead(index);
            if (ReadStatus != EngineStatus.Ok) return ReadStatus;
            byte[] d = Data[index];
            if (FailReadAfterBytes >= 0 && offset >= FailReadAfterBytes) return EngineStatus.IoError;
            long limit = Math.Max(0, d.Length - ShortBy);
            if (offset >= limit) return EngineStatus.Ok;
            read = (int)Math.Min(count, limit - offset);
            Array.Copy(d, offset, buffer, 0, read);
            return EngineStatus.Ok;
        }
        public void Dispose() { Disposed = true; }
    }

    internal sealed class FakeValidator : IDestinationValidator
    {
        public DestinationVerdict Verdict = DestinationVerdict.Safe;
        public DestinationVerdict Validate(string s, string d) { return Verdict; }
    }

    internal sealed class FakeRecoveredRepo : IRecoveredFilesRepository
    {
        public List<RecoveredFileRecord> Rows = new List<RecoveredFileRecord>();
        public long Add(RecoveredFileRecord r) { Rows.Add(r); return Rows.Count; }
        public IReadOnlyList<RecoveredFileRecord> GetRecent(int n) { return Rows; }
        public long Count() { return Rows.Count; }
        public int ClearAll() { int n = Rows.Count; Rows.Clear(); return n; }
    }

    internal sealed class FakeHistory : IScanHistoryRepository
    {
        public List<ScanSession> Sessions = new List<ScanSession>();
        public IReadOnlyList<ScanSession> GetRecent(int n) { return Sessions; }
        public ScanStats GetStats() { return new ScanStats(); }
        public long AddSession(ScanSession s) { Sessions.Add(s); s.Id = Sessions.Count; return s.Id; }
        public int Prune(int k) { return 0; }
        public int ClearAll() { return 0; }
        public void UpdateSession(long id, DateTime f, string status, long files) { var s = Sessions[(int)id - 1]; s.Status = status; s.FilesFound = files; s.FinishedUtc = f; }
        public int MarkInterrupted() { return 0; }
    }

    internal static class Tmp
    {
        public static string NewDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "rx-p3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }
        public static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    }

    // ---------------- pure logic ----------------
    [TestClass]
    public class RecoveryPathBuilderTests
    {
        [TestMethod]
        public void Invalid_characters_and_trailing_dots_are_replaced()
        {
            Assert.AreEqual("a_b_c.txt", RecoveryPathBuilder.SanitizeComponent("a:b*c.txt"));
            Assert.AreEqual("name", RecoveryPathBuilder.SanitizeComponent("name. . "));
            Assert.AreEqual("_", RecoveryPathBuilder.SanitizeComponent(".."));
            Assert.AreEqual("_", RecoveryPathBuilder.SanitizeComponent(""));
            Assert.AreEqual("_", RecoveryPathBuilder.SanitizeComponent(null));
            Assert.AreEqual("x_y", RecoveryPathBuilder.SanitizeComponent("x\u0001y"));
            Assert.AreEqual("r\u00e9sum\u00e9_\u00f1.txt", RecoveryPathBuilder.SanitizeComponent("r\u00e9sum\u00e9_\u00f1.txt"));          // legal Unicode is kept
            Assert.AreEqual("\u65e5\u672c\u8a9e.txt", RecoveryPathBuilder.SanitizeComponent("\u65e5\u672c\u8a9e.txt"));
        }

        [TestMethod]
        public void Reserved_device_names_are_defused()
        {
            Assert.AreEqual("_CON", RecoveryPathBuilder.SanitizeComponent("CON"));
            Assert.AreEqual("_nul.txt", RecoveryPathBuilder.SanitizeComponent("nul.txt"));
            Assert.AreEqual("_COM1.log", RecoveryPathBuilder.SanitizeComponent("COM1.log"));
            Assert.AreEqual("CONSOLE.txt", RecoveryPathBuilder.SanitizeComponent("CONSOLE.txt"));
        }

        [TestMethod]
        public void Long_names_are_shortened_but_keep_their_extension()
        {
            string n = RecoveryPathBuilder.SanitizeComponent(new string('a', 300) + ".jpeg");
            Assert.AreEqual(RecoveryPathBuilder.MaxComponentLength, n.Length);
            Assert.IsTrue(n.EndsWith(".jpeg"));
        }

        [TestMethod]
        public void Parent_directory_components_can_never_escape_the_recovery_folder()
        {
            string root = Path.Combine(Path.GetTempPath(), "rxroot");
            foreach (string evil in new[] { "\\..\\..\\evil.txt", "\\a\\..\\..\\b.txt", "\\..", "\\C:\\Windows\\x.dll", "\\\\server\\share\\x.txt", "/../../etc/passwd" })
            {
                string p = RecoveryPathBuilder.BuildUniquePath(root, evil, true, f => false);
                Assert.IsTrue(Path.GetFullPath(p).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar), evil + " -> " + p);
            }
        }

        [TestMethod]
        public void Folder_structure_is_kept_or_flattened_on_request()
        {
            string root = Path.Combine(Path.GetTempPath(), "rxroot");
            Assert.AreEqual(Path.Combine(root, "docs", "a.txt"), RecoveryPathBuilder.BuildUniquePath(root, "\\docs\\a.txt", true, f => false));
            Assert.AreEqual(Path.Combine(root, "a.txt"), RecoveryPathBuilder.BuildUniquePath(root, "\\docs\\a.txt", false, f => false));
        }

        [TestMethod]
        public void Existing_files_are_never_overwritten()
        {
            string root = Path.Combine(Path.GetTempPath(), "rxroot");
            var taken = new HashSet<string> { Path.Combine(root, "a.txt"), Path.Combine(root, "a (2).txt") };
            Assert.AreEqual(Path.Combine(root, "a (3).txt"), RecoveryPathBuilder.BuildUniquePath(root, "\\a.txt", true, taken.Contains));
        }

        [TestMethod]
        public void Very_deep_paths_fall_back_to_a_flat_name_within_the_limit()
        {
            string root = Path.Combine(Path.GetTempPath(), "rxroot");
            string deep = "\\" + string.Join("\\", Enumerable.Repeat(new string('d', 90), 6)) + "\\file.bin";
            string p = RecoveryPathBuilder.BuildUniquePath(root, deep, true, f => false);
            Assert.IsTrue(p.Length <= RecoveryPathBuilder.MaxFullPathLength);
            Assert.AreEqual("file.bin", Path.GetFileName(p));
        }
    }

    [TestClass]
    public class ScanFilterTests
    {
        private static ScanEntry E(string path, long size) { return new ScanEntry { Path = path, Size = size }; }

        [TestMethod]
        public void Settings_hide_disabled_categories_and_small_files()
        {
            var cats = DefaultCategories.Create();
            cats.Single(c => c.Id == "Videos").Enabled = false;
            var s = new AppSettings { MinFileSizeKb = 4 };
            var all = new[] { E("\\a.jpg", 10000), E("\\tiny.jpg", 100), E("\\movie.mp4", 5000000), E("\\x.pdf", 4096), E("\\blob.xyz", 9000) };
            var r = ScanFilter.Apply(all, cats, s);
            CollectionAssert.AreEqual(new[] { "a.jpg", "x.pdf", "blob.xyz" }, r.Shown.Select(e => e.Name).ToArray());
            Assert.AreEqual(2, r.HiddenBySettings);
        }

        [TestMethod]
        public void Default_settings_hide_nothing()
        {
            var r = ScanFilter.Apply(new[] { E("\\a.jpg", 0), E("\\b", 1) }, DefaultCategories.Create(), new AppSettings());
            Assert.AreEqual(2, r.Shown.Count);
            Assert.AreEqual(0, r.HiddenBySettings);
        }

        [TestMethod]
        public void Every_condition_has_resource_keys_and_only_good_conditions_are_likely_recoverable()
        {
            foreach (RecoveryCondition c in Enum.GetValues(typeof(RecoveryCondition)))
            {
                Assert.IsTrue(ConditionInfo.Key(c).StartsWith("Cond_"));
                Assert.IsTrue(ConditionInfo.TipKey(c).StartsWith("CondTip_"));
            }
            Assert.IsTrue(new ScanEntry { Condition = RecoveryCondition.Verified }.IsLikelyRecoverable);
            Assert.IsTrue(new ScanEntry { Condition = RecoveryCondition.Likely }.IsLikelyRecoverable);
            Assert.IsFalse(new ScanEntry { Condition = RecoveryCondition.Partial }.IsLikelyRecoverable);
            Assert.IsFalse(new ScanEntry { Condition = RecoveryCondition.Lost }.IsLikelyRecoverable);
            Assert.IsFalse(new ScanEntry { Condition = RecoveryCondition.Unknown }.IsLikelyRecoverable);
            Assert.IsFalse(new ScanEntry { Condition = RecoveryCondition.Unsupported }.CanAttemptRecovery);
            Assert.AreEqual("name.txt", new ScanEntry { Path = "\\a\\b\\name.txt" }.Name);
        }
    }

    // ---------------- scan runner (Quick mode) ----------------
    [TestClass]
    public class ScanServiceQuickTests
    {
        private static DriveModel Ntfs() { return new DriveModel { Letter = "E:", RootPath = "E:\\", FileSystem = "NTFS", IsReady = true }; }

        [TestMethod]
        public void Unsupported_file_systems_and_missing_engine_are_reported_without_touching_history()
        {
            var hist = new FakeHistory();
            var svc = new ScanService(new FakeEngine { Available = true }, hist, new NullLogger());
            var fat = new DriveModel { Letter = "F:", RootPath = "F:\\", FileSystem = "ReFS" };
            Assert.AreEqual(EngineStatus.NotNtfs, svc.RunAsync(fat, ScanMode.Quick, new AppSettings(), null, CancellationToken.None).Result.Status);
            var noEngine = new ScanService(new FakeEngine { Available = false }, hist, new NullLogger());
            Assert.AreEqual(EngineStatus.Unavailable, noEngine.RunAsync(Ntfs(), ScanMode.Quick, new AppSettings(), null, CancellationToken.None).Result.Status);
            Assert.AreEqual(0, hist.Sessions.Count);
        }

        [TestMethod]
        public void Open_failures_such_as_access_denied_are_passed_through()
        {
            var eng = new FakeEngine { Available = true, OpenResult = new ScanOpenResult { Status = EngineStatus.AccessDenied } };
            var o = new ScanService(eng, new FakeHistory(), new NullLogger()).RunAsync(Ntfs(), ScanMode.Quick, new AppSettings(), null, CancellationToken.None).Result;
            Assert.AreEqual(EngineStatus.AccessDenied, o.Status);
            Assert.IsFalse(o.HasResults);
        }

        [TestMethod]
        public void Successful_scan_keeps_the_session_and_records_a_completed_history_entry()
        {
            var session = new FakeSession { Entries = new List<ScanEntry> { new ScanEntry { Path = "\\a.txt", Size = 5 }, new ScanEntry { Path = "\\b.txt", Size = 6 } } };
            var eng = new FakeEngine { Available = true, OpenResult = new ScanOpenResult { Status = EngineStatus.Ok, Session = session } };
            var hist = new FakeHistory();
            var o = new ScanService(eng, hist, new NullLogger()).RunAsync(Ntfs(), ScanMode.Quick, new AppSettings(), null, CancellationToken.None).Result;
            Assert.AreEqual(EngineStatus.Ok, o.Status);
            Assert.AreEqual(2, o.Entries.Count);
            Assert.IsFalse(session.Disposed);                         // kept open for recovery
            Assert.AreEqual("Completed", hist.Sessions.Single().Status);
            Assert.AreEqual(2, hist.Sessions.Single().FilesFound);
            Assert.AreEqual("Quick", hist.Sessions.Single().ScanType);
        }

        [TestMethod]
        public void Cancelled_scan_keeps_partial_results_and_is_recorded_as_cancelled()
        {
            var session = new FakeSession { Entries = new List<ScanEntry> { new ScanEntry { Path = "\\a.txt" } } };
            var eng = new FakeEngine { Available = true, OpenResult = new ScanOpenResult { Status = EngineStatus.Ok, Session = session } };
            var hist = new FakeHistory();
            var cts = new CancellationTokenSource(); cts.Cancel();
            var o = new ScanService(eng, hist, new NullLogger()).RunAsync(Ntfs(), ScanMode.Quick, new AppSettings(), null, cts.Token).Result;
            Assert.IsTrue(o.Cancelled);
            Assert.IsTrue(o.HasResults);
            Assert.AreEqual("Cancelled", hist.Sessions.Single().Status);
        }

        [TestMethod]
        public void Failed_scan_closes_the_session_and_is_recorded_as_failed()
        {
            var session = new FakeSession { RunStatus = EngineStatus.IoError };
            var eng = new FakeEngine { Available = true, OpenResult = new ScanOpenResult { Status = EngineStatus.Ok, Session = session } };
            var hist = new FakeHistory();
            var o = new ScanService(eng, hist, new NullLogger()).RunAsync(Ntfs(), ScanMode.Quick, new AppSettings(), null, CancellationToken.None).Result;
            Assert.AreEqual(EngineStatus.IoError, o.Status);
            Assert.IsFalse(o.HasResults);
            Assert.IsTrue(session.Disposed);
            Assert.AreEqual("Failed", hist.Sessions.Single().Status);
        }
    }

    // ---------------- recovery ----------------
    [TestClass]
    public class RecoveryServiceTests
    {
        private string _dir;
        [TestInitialize] public void Init() { _dir = Tmp.NewDir(); }
        [TestCleanup] public void Cleanup() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private RecoveryService Svc(FakeEngine eng, FakeValidator val, FakeRecoveredRepo repo)
        {
            return new RecoveryService(eng, val, repo, new NullLogger(), () => new DateTime(2026, 11, 8, 10, 30, 0));
        }

        private static RecoveryRequest Req(string dest, bool keep = true) { return new RecoveryRequest { SourceRoot = "E:\\", DestinationDirectory = dest, PreserveFolders = keep }; }

        private static ScanEntry Entry(int idx, string path, int size, RecoveryCondition c = RecoveryCondition.Verified)
        {
            return new ScanEntry { Index = idx, Path = path, Size = size, Condition = c, Modified = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), Created = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        }

        [TestMethod]
        public void Recovers_content_byte_for_byte_with_folders_and_timestamps()
        {
            var big = Tmp.Bytes(2_500_000, 1);                 // spans several 1 MB chunks
            var small = Tmp.Bytes(120, 2);
            var session = new FakeSession { Data = { { 0, big }, { 1, small } } };
            var items = new List<ScanEntry> { Entry(0, "\\pics\\big.bin", big.Length), Entry(1, "\\docs\\small.txt", small.Length) };
            var repo = new FakeRecoveredRepo();
            var sum = Svc(new FakeEngine { NextHeader = HeaderCheck.Match }, new FakeValidator(), repo).RecoverAsync(session, items, Req(_dir), null, CancellationToken.None).Result;

            Assert.IsNull(sum.Error);
            Assert.AreEqual(2, sum.Succeeded);
            Assert.IsTrue(sum.SessionDirectory.StartsWith(Path.Combine(_dir, "RecoverX_E_20261108_103000")));
            string p0 = Path.Combine(sum.SessionDirectory, "pics", "big.bin");
            CollectionAssert.AreEqual(big, File.ReadAllBytes(p0));
            CollectionAssert.AreEqual(small, File.ReadAllBytes(Path.Combine(sum.SessionDirectory, "docs", "small.txt")));
            Assert.AreEqual(new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc(p0));   // original timestamps restored
            Assert.AreEqual(RecoveryOutcome.Verified, sum.Items[0].Outcome);
            Assert.AreEqual(2, repo.Rows.Count);
            Assert.AreEqual("Verified", repo.Rows[0].Status);
        }

        [TestMethod]
        public void Destination_on_the_scanned_drive_is_refused_before_anything_is_created_or_read()
        {
            var session = new FakeSession { Data = { { 0, new byte[10] } } };
            var sum = Svc(new FakeEngine(), new FakeValidator { Verdict = DestinationVerdict.SameVolume }, new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\a.txt", 10) }, Req(_dir), null, CancellationToken.None).Result;
            Assert.AreEqual("Recover_Err_SameVolume", sum.Error);
            Assert.AreEqual("E:", sum.ErrorDetail);
            Assert.AreEqual(0, session.ReadCalls);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(_dir).Length);
        }

        [TestMethod]
        public void Missing_unverifiable_or_empty_destinations_are_refused()
        {
            var session = new FakeSession();
            var items = new List<ScanEntry> { Entry(0, "\\a.txt", 10) };
            Assert.AreEqual("Recover_Err_Dest", Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo()).RecoverAsync(session, items, Req(""), null, CancellationToken.None).Result.Error);
            Assert.AreEqual("Recover_Err_DestInvalid", Svc(new FakeEngine(), new FakeValidator { Verdict = DestinationVerdict.NotFound }, new FakeRecoveredRepo()).RecoverAsync(session, items, Req(_dir), null, CancellationToken.None).Result.Error);
            Assert.AreEqual("Recover_Err_Unverifiable", Svc(new FakeEngine(), new FakeValidator { Verdict = DestinationVerdict.Unverifiable }, new FakeRecoveredRepo()).RecoverAsync(session, items, Req(_dir), null, CancellationToken.None).Result.Error);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(_dir).Length);
        }

        [TestMethod]
        public void Not_enough_free_space_is_detected_up_front_and_nothing_is_created()
        {
            var huge = new ScanEntry { Index = 0, Path = "\\huge.bin", Size = long.MaxValue / 4, Condition = RecoveryCondition.Verified };   // far more than any disk
            var session = new FakeSession();
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { huge }, Req(_dir), null, CancellationToken.None).Result;
            if (sum.Error == null) Assert.Inconclusive("The platform could not report free space for the temp folder.");
            Assert.AreEqual("Recover_Err_Space", sum.Error);
            Assert.IsTrue(sum.NeededBytes > sum.AvailableBytes);
            Assert.AreEqual(0, session.ReadCalls);
            Assert.AreEqual(0, Directory.GetFileSystemEntries(_dir).Length);
        }

        [TestMethod]
        public void Files_with_the_same_path_never_overwrite_each_other()
        {
            var session = new FakeSession { Data = { { 0, Tmp.Bytes(50, 3) }, { 1, Tmp.Bytes(60, 4) } } };
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\x\\same.txt", 50), Entry(1, "\\x\\same.txt", 60) }, Req(_dir), null, CancellationToken.None).Result;
            var files = Directory.GetFiles(sum.SessionDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(new[] { "same (2).txt", "same.txt" }, files);
            Assert.AreEqual(50, new FileInfo(Path.Combine(sum.SessionDirectory, "x", "same.txt")).Length);
            Assert.AreEqual(60, new FileInfo(Path.Combine(sum.SessionDirectory, "x", "same (2).txt")).Length);
        }

        [TestMethod]
        public void Hostile_paths_stay_inside_the_recovery_folder()
        {
            var session = new FakeSession { Data = { { 0, Tmp.Bytes(5, 5) }, { 1, Tmp.Bytes(5, 6) } } };
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\..\\..\\evil.txt", 5), Entry(1, "\\a\\..\\..\\b.txt", 5) }, Req(_dir), null, CancellationToken.None).Result;
            Assert.AreEqual(2, sum.Succeeded);
            foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                Assert.IsTrue(Path.GetFullPath(f).StartsWith(Path.GetFullPath(sum.SessionDirectory) + Path.DirectorySeparatorChar), f);
            Assert.AreEqual(2, Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Length);           // nothing written outside
        }

        [TestMethod]
        public void A_read_error_part_way_removes_the_partial_file_and_continues()
        {
            var session = new FakeSession { Data = { { 0, Tmp.Bytes(3_000_000, 7) }, { 1, Tmp.Bytes(100, 8) } }, FailReadAfterBytes = 1_500_000 };
            session.OnRead = i => { if (i == 1) session.FailReadAfterBytes = -1; };
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\bad.bin", 3_000_000), Entry(1, "\\ok.bin", 100) }, Req(_dir), null, CancellationToken.None).Result;
            Assert.AreEqual(RecoveryOutcome.Failed, sum.Items[0].Outcome);
            Assert.AreEqual("Reason_ReadError", sum.Items[0].ReasonKey);
            Assert.IsFalse(File.Exists(Path.Combine(sum.SessionDirectory, "bad.bin")));        // no half-written file left behind
            Assert.AreEqual(RecoveryOutcome.Unverified, sum.Items[1].Outcome);
            Assert.AreEqual(1, sum.Failed);
        }

        [TestMethod]
        public void A_short_read_is_a_failure_not_a_silently_truncated_file()
        {
            var session = new FakeSession { Data = { { 0, Tmp.Bytes(1000, 9) } }, ShortBy = 10 };
            var repo = new FakeRecoveredRepo();
            var sum = Svc(new FakeEngine(), new FakeValidator(), repo).RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\t.bin", 1000) }, Req(_dir), null, CancellationToken.None).Result;
            Assert.AreEqual(RecoveryOutcome.Failed, sum.Items[0].Outcome);
            Assert.AreEqual("Reason_ShortRead", sum.Items[0].ReasonKey);
            Assert.IsFalse(File.Exists(Path.Combine(sum.SessionDirectory, "t.bin")));
            Assert.AreEqual(0, repo.Rows.Count);                                                // failures are not listed as recovered
        }

        [TestMethod]
        public void Record_reused_since_the_scan_is_reported_clearly()
        {
            var session = new FakeSession { Data = { { 0, new byte[10] } }, ReadStatus = EngineStatus.NotFound };
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo()).RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\a.txt", 10) }, Req(_dir), null, CancellationToken.None).Result;
            Assert.AreEqual("Reason_RecordReused", sum.Items[0].ReasonKey);
        }

        [TestMethod]
        public void Cancellation_stops_between_files_and_keeps_completed_ones()
        {
            var cts = new CancellationTokenSource();
            var session = new FakeSession { Data = { { 0, Tmp.Bytes(10, 1) }, { 1, Tmp.Bytes(3_000_000, 2) }, { 2, Tmp.Bytes(10, 3) } } };
            session.OnRead = i => { if (i == 1) cts.Cancel(); };
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\one.txt", 10), Entry(1, "\\two.bin", 3_000_000), Entry(2, "\\three.txt", 10) }, Req(_dir), null, cts.Token).Result;
            Assert.IsTrue(sum.Cancelled);
            Assert.AreEqual(RecoveryOutcome.Unverified, sum.Items[0].Outcome);
            Assert.AreEqual(RecoveryOutcome.Cancelled, sum.Items[1].Outcome);
            Assert.AreEqual(2, sum.Items.Count);                                                // third file never started
            Assert.IsTrue(File.Exists(Path.Combine(sum.SessionDirectory, "one.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(sum.SessionDirectory, "two.bin")));         // partial file removed
        }

        [TestMethod]
        public void Outcome_reflects_the_evidence_and_never_overstates_it()
        {
            var data = Tmp.Bytes(600, 11);
            Func<RecoveryCondition, HeaderCheck, RecoveryItemResult> run = (cond, hdr) =>
            {
                var session = new FakeSession { Data = { { 0, data } } };
                var d = Tmp.NewDir();
                try
                {
                    return Svc(new FakeEngine { NextHeader = hdr }, new FakeValidator(), new FakeRecoveredRepo())
                        .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\f.jpg", data.Length, cond) }, Req(d), null, CancellationToken.None).Result.Items[0];
                }
                finally { try { Directory.Delete(d, true); } catch (IOException) { } }
            };
            Assert.AreEqual(RecoveryOutcome.Verified, run(RecoveryCondition.Verified, HeaderCheck.Match).Outcome);
            Assert.AreEqual(RecoveryOutcome.Unverified, run(RecoveryCondition.Likely, HeaderCheck.NoSignature).Outcome);
            Assert.AreEqual(RecoveryOutcome.Unverified, run(RecoveryCondition.Likely, HeaderCheck.NotChecked).Outcome);
            var mism = run(RecoveryCondition.Verified, HeaderCheck.Mismatch);
            Assert.AreEqual(RecoveryOutcome.Suspect, mism.Outcome);
            Assert.AreEqual("Reason_HeaderMismatch", mism.ReasonKey);
            Assert.AreEqual("Reason_PartlyOverwritten", run(RecoveryCondition.Partial, HeaderCheck.Match).ReasonKey);   // even a matching header cannot rescue a partly-overwritten file
            Assert.AreEqual("Reason_Overwritten", run(RecoveryCondition.Lost, HeaderCheck.Match).ReasonKey);
        }

        [TestMethod]
        public void Unsupported_files_are_skipped_and_empty_files_are_recreated()
        {
            var session = new FakeSession { Data = { { 1, new byte[0] } } };
            var sum = Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\packed.txt", 500, RecoveryCondition.Unsupported), Entry(1, "\\empty.dat", 0, RecoveryCondition.Empty) }, Req(_dir), null, CancellationToken.None).Result;
            Assert.AreEqual(RecoveryOutcome.Unsupported, sum.Items[0].Outcome);
            Assert.IsFalse(File.Exists(Path.Combine(sum.SessionDirectory, "packed.txt")));
            Assert.AreEqual(RecoveryOutcome.Unverified, sum.Items[1].Outcome);
            Assert.AreEqual(0, new FileInfo(Path.Combine(sum.SessionDirectory, "empty.dat")).Length);
        }

        [TestMethod]
        public void Progress_reports_every_file()
        {
            var session = new FakeSession { Data = { { 0, Tmp.Bytes(10, 1) }, { 1, Tmp.Bytes(10, 2) } } };
            var seen = new List<int>();
            var prog = new SyncProgress<RecoveryProgress>(p => seen.Add(p.FileNumber));
            Svc(new FakeEngine(), new FakeValidator(), new FakeRecoveredRepo())
                .RecoverAsync(session, new List<ScanEntry> { Entry(0, "\\a", 10), Entry(1, "\\b", 10) }, Req(_dir), prog, CancellationToken.None).Wait();
            CollectionAssert.AreEqual(new[] { 1, 1, 2, 2 }, seen.ToArray());       // each file reports at start and on completion of its data
        }
    }

    internal sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) { _a = a; }
        public void Report(T v) { _a(v); }
    }
}
