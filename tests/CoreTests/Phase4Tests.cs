using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Native;
using RecoverX.Core.Services;

namespace RecoverX.CoreTests
{
    // ---------------- ratings ----------------
    [TestClass]
    public class RatingTests
    {
        [TestMethod]
        public void Conditions_map_to_the_three_ratings_and_unusable_files_are_never_rated_recoverable()
        {
            Assert.AreEqual(RecoveryRating.Excellent, ConditionInfo.Rating(RecoveryCondition.Verified));
            Assert.AreEqual(RecoveryRating.Excellent, ConditionInfo.Rating(RecoveryCondition.Likely));
            Assert.AreEqual(RecoveryRating.Partial, ConditionInfo.Rating(RecoveryCondition.Partial));
            Assert.AreEqual(RecoveryRating.Poor, ConditionInfo.Rating(RecoveryCondition.Lost));
            foreach (var c in new[] { RecoveryCondition.Unknown, RecoveryCondition.Unsupported, RecoveryCondition.Empty })
                Assert.AreEqual(RecoveryRating.NotRecoverable, ConditionInfo.Rating(c));
            Assert.AreEqual(RecoveryRating.Excellent, new ScanEntry { Condition = RecoveryCondition.Verified }.Rating);
        }

        [TestMethod]
        public void Carved_detail_describes_the_evidence_actually_checked()
        {
            var e = new ScanEntry { Origin = EntryOrigin.Signature, Condition = RecoveryCondition.Verified, Evidence = CarveEvidence.ChecksumOk };
            Assert.AreEqual("CarveTip_Checksum", ConditionInfo.CarvedTipKey(e));
            e.Evidence = CarveEvidence.ChecksumOk | CarveEvidence.InUse;
            Assert.AreEqual("CarveTip_InUseChecksum", ConditionInfo.CarvedTipKey(e));
            e.Condition = RecoveryCondition.Partial; e.Evidence = CarveEvidence.InUse;
            Assert.AreEqual("CarveTip_InUse", ConditionInfo.CarvedTipKey(e));
            e.Evidence = CarveEvidence.Truncated;
            Assert.AreEqual("CarveTip_Truncated", ConditionInfo.CarvedTipKey(e));
            e.Condition = RecoveryCondition.Likely; e.Evidence = CarveEvidence.EndFound;
            Assert.AreEqual("CarveTip_Likely", ConditionInfo.CarvedTipKey(e));
        }

        [TestMethod]
        public void Carved_names_are_safe_and_unique_per_offset()
        {
            Assert.AreEqual("\\Deep Scan\\JPG\\f0000000128.jpg", CarvedNames.PathFor("jpg", 65536));
            Assert.AreEqual("\\Deep Scan\\DOCX\\f0000000001.docx", CarvedNames.PathFor("docx", 512));
            Assert.AreEqual("\\Deep Scan\\X\\f0000000000.x", CarvedNames.PathFor("..\\x", 0));     // path characters dropped
            Assert.AreEqual("\\Deep Scan\\BIN\\f0000000000.bin", CarvedNames.PathFor(null, 0));
            Assert.AreNotEqual(CarvedNames.PathFor("jpg", 512), CarvedNames.PathFor("jpg", 1024));
            // The recovery path builder keeps the result inside the session folder.
            string p = RecoveryPathBuilder.BuildUniquePath("C:\\out", CarvedNames.PathFor("jpg", 65536), true, _ => false);
            StringAssert.StartsWith(p, "C:\\out\\Deep Scan\\JPG\\");
        }

        [TestMethod]
        public void Display_date_uses_file_time_first_then_content_time_without_converting_camera_time()
        {
            var camera = new DateTime(2024, 5, 17, 14, 30, 0, DateTimeKind.Unspecified);
            var carved = new ScanEntry { Path = "\\Deep Scan\\JPG\\f1.jpg", ContentTime = camera, ContentTimeIsLocal = true };
            Assert.AreEqual(camera, carved.DisplayDate);
            var utc = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(utc.ToLocalTime(), new ScanEntry { Path = "\\a.mp4", ContentTime = utc }.DisplayDate);
            var modified = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(modified.ToLocalTime(), new ScanEntry { Path = "\\a.jpg", Modified = modified, ContentTime = camera }.DisplayDate);
            Assert.IsNull(new ScanEntry { Path = "\\a" }.DisplayDate);
            Assert.AreEqual("jpg", new ScanEntry { Path = "\\x\\A.JPG" }.Extension);
            Assert.AreEqual("", new ScanEntry { Path = "\\x\\noext" }.Extension);
        }
    }

    // ---------------- search, filters, sort ----------------
    [TestClass]
    public class ResultQueryTests
    {
        private static readonly IList<FileCategory> Cats = DefaultCategories.Create();

        private static List<ScanEntry> Sample()
        {
            DateTime d(int y, int m, int day) => new DateTime(y, m, day, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
            return new List<ScanEntry>
            {
                new ScanEntry { Index = 0, Path = "\\Photos\\Beach.jpg", Size = 3_000_000, Modified = d(2024, 6, 1), Condition = RecoveryCondition.Verified },
                new ScanEntry { Index = 1, Path = "\\Docs\\report.PDF", Size = 200_000, Modified = d(2023, 1, 10), Condition = RecoveryCondition.Partial },
                new ScanEntry { Index = 2, Path = "\\Docs\\notes.txt", Size = 500, Modified = d(2024, 6, 2), Condition = RecoveryCondition.Lost },
                new ScanEntry { Index = 3, Path = "\\Deep Scan\\MP4\\f0000000001.mp4", Size = 150_000_000, Condition = RecoveryCondition.Likely },
                new ScanEntry { Index = 4, Path = "\\Photos\\beach_2.jpg", Size = 3_000_000, Modified = d(2024, 6, 1), Condition = RecoveryCondition.Verified },
                new ScanEntry { Index = 5, Path = "\\empty.dat", Size = 0, Condition = RecoveryCondition.Empty }
            };
        }

        private static int[] Idx(IEnumerable<ScanEntry> e) { return e.Select(x => x.Index).ToArray(); }

        [TestMethod]
        public void Search_matches_name_or_folder_case_insensitively()
        {
            CollectionAssert.AreEquivalent(new[] { 0, 4 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Search = "BEACH" }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Search = "docs\\" }, Cats)));
            Assert.AreEqual(6, ResultQuery.Apply(Sample(), new ResultQuery { Search = "   " }, Cats).Count);
            Assert.AreEqual(0, ResultQuery.Apply(Sample(), new ResultQuery { Search = "zzz" }, Cats).Count);
        }

        [TestMethod]
        public void Category_rating_and_size_filters_combine()
        {
            CollectionAssert.AreEquivalent(new[] { 0, 4 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { CategoryId = "Photos" }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { CategoryId = "Documents" }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 5 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { CategoryId = "Other" }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 0, 3, 4 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Rating = RatingFilter.Excellent }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 1 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Rating = RatingFilter.Partial }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 2 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Rating = RatingFilter.Poor }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 1, 2, 5 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Size = SizeBucket.Under1Mb }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 0, 4 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Size = SizeBucket.From1To100Mb }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 3 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { Size = SizeBucket.Over100Mb }, Cats)));
            CollectionAssert.AreEquivalent(new[] { 0, 4 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { CategoryId = "Photos", Rating = RatingFilter.Excellent, Size = SizeBucket.From1To100Mb }, Cats)));
        }

        [TestMethod]
        public void Date_range_is_inclusive_and_excludes_files_without_a_date()
        {
            var q = new ResultQuery { From = new DateTime(2024, 6, 1), To = new DateTime(2024, 6, 1) };
            CollectionAssert.AreEquivalent(new[] { 0, 4 }, Idx(ResultQuery.Apply(Sample(), q, Cats)));
            q = new ResultQuery { From = new DateTime(2024, 1, 1) };
            CollectionAssert.AreEquivalent(new[] { 0, 2, 4 }, Idx(ResultQuery.Apply(Sample(), q, Cats)));
            q = new ResultQuery { To = new DateTime(2023, 12, 31) };
            CollectionAssert.AreEquivalent(new[] { 1 }, Idx(ResultQuery.Apply(Sample(), q, Cats)));
        }

        [TestMethod]
        public void Sorting_by_name_size_and_date_is_deterministic_in_both_directions()
        {
            CollectionAssert.AreEqual(new[] { 0, 4, 5, 3, 2, 1 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { SortBy = ResultSort.Name }, Cats)));
            CollectionAssert.AreEqual(new[] { 5, 2, 1, 0, 4, 3 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { SortBy = ResultSort.Size }, Cats)));
            // equal sizes keep session order even when descending
            CollectionAssert.AreEqual(new[] { 3, 0, 4, 1, 2, 5 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { SortBy = ResultSort.Size, Descending = true }, Cats)));
            // undated files sort first ascending
            CollectionAssert.AreEqual(new[] { 3, 5, 1, 0, 4, 2 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { SortBy = ResultSort.Date }, Cats)));
            CollectionAssert.AreEqual(new[] { 0, 3, 4, 1, 2, 5 }, Idx(ResultQuery.Apply(Sample(), new ResultQuery { SortBy = ResultSort.Rating }, Cats)));
        }

        [TestMethod]
        public void Apply_never_modifies_the_source_and_tolerates_nulls()
        {
            var s = Sample();
            ResultQuery.Apply(s, new ResultQuery { SortBy = ResultSort.Size, Descending = true }, Cats);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, Idx(s));
            Assert.AreEqual(0, ResultQuery.Apply(null, null, null).Count);
            Assert.AreEqual(6, ResultQuery.Apply(s, null, null).Count);
        }

        [TestMethod]
        public void Hundreds_of_thousands_of_results_filter_quickly()
        {
            var big = new List<ScanEntry>();
            for (int i = 0; i < 250000; i++) big.Add(new ScanEntry { Index = i, Path = "\\Deep Scan\\JPG\\f" + i.ToString("D10") + ".jpg", Size = i * 37 % 5000000, Condition = (RecoveryCondition)(1 + i % 4) });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ResultQuery.Apply(big, new ResultQuery { Search = "f00000012", CategoryId = "Photos", Rating = RatingFilter.Excellent, SortBy = ResultSort.Size, Descending = true }, Cats);
            sw.Stop();
            Assert.IsTrue(r.Count > 0);
            Assert.IsTrue(sw.ElapsedMilliseconds < 3000, "took " + sw.ElapsedMilliseconds + " ms");
        }
    }

    // ---------------- scan runner (Deep mode) ----------------
    [TestClass]
    public class ScanServiceDeepTests
    {
        [TestMethod]
        public void Deep_scan_runs_on_fat32_records_history_and_passes_the_used_space_setting()
        {
            var session = new FakeSession { SessionMode = ScanMode.Deep, Entries = new List<ScanEntry> { new ScanEntry { Path = "\\Deep Scan\\JPG\\f1.jpg", Origin = EntryOrigin.Signature } } };
            var eng = new FakeEngine { Available = true, DeepOpenResult = new ScanOpenResult { Status = EngineStatus.Ok, Session = session } };
            var hist = new FakeHistory();
            var fat = new DriveModel { Letter = "F:", RootPath = "F:\\", FileSystem = "FAT32", IsReady = true };
            IScanSession seen = null;
            var o = new ScanService(eng, hist, new NullLogger()).RunAsync(fat, ScanMode.Deep, new AppSettings { DeepScanIncludeUsedSpace = true }, null, CancellationToken.None, s => seen = s).Result;
            Assert.AreEqual(EngineStatus.Ok, o.Status);
            Assert.AreSame(session, seen);                                 // caller gets the live session (for pause/resume)
            Assert.AreEqual(ScanMode.Deep, o.Mode);
            Assert.AreEqual(1, o.Entries.Count);
            Assert.IsTrue(eng.LastDeepOptions.IncludeUsedSpace);
            Assert.AreEqual("Deep", hist.Sessions.Single().ScanType);
            Assert.AreEqual("Completed", hist.Sessions.Single().Status);
            // Quick Scan of the same FAT32 drive goes to the FAT/exFAT scanner, never to the NTFS one.
            var quick = new FakeSession { Entries = new List<ScanEntry> { new ScanEntry { Path = "\\photos\\beach.jpg" } } };
            eng.FatOpenResult = new ScanOpenResult { Status = EngineStatus.Ok, Session = quick };
            var q = new ScanService(eng, hist, new NullLogger()).RunAsync(fat, ScanMode.Quick, new AppSettings(), null, CancellationToken.None).Result;
            Assert.AreEqual(EngineStatus.Ok, q.Status);
            Assert.AreEqual(1, eng.FatOpens);
            Assert.AreEqual(0, eng.NtfsOpens);
            Assert.AreEqual("Quick", hist.Sessions.Last().ScanType);
            foreach (string fs in new[] { "exFAT", "FAT", "fat32" })
                Assert.IsTrue(ScanService.SupportsQuickScan(new DriveModel { FileSystem = fs }), fs);
            Assert.IsFalse(ScanService.SupportsQuickScan(new DriveModel { FileSystem = "ReFS" }));
            Assert.IsFalse(ScanService.SupportsQuickScan(new DriveModel { FileSystem = "" }));
        }

        [TestMethod]
        public void Deep_scan_open_failure_is_reported_and_not_recorded()
        {
            var eng = new FakeEngine { Available = true, DeepOpenResult = new ScanOpenResult { Status = EngineStatus.AccessDenied } };
            var hist = new FakeHistory();
            var o = new ScanService(eng, hist, new NullLogger()).RunAsync(new DriveModel { Letter = "G:", RootPath = "G:\\", FileSystem = "exFAT" }, ScanMode.Deep, new AppSettings(), null, CancellationToken.None).Result;
            Assert.AreEqual(EngineStatus.AccessDenied, o.Status);
            Assert.AreEqual(0, hist.Sessions.Count);
        }
    }

    // ---------------- preview ----------------
    [TestClass]
    public class PreviewTests
    {
        private static FakeSession One(string path, byte[] data, out ScanEntry e)
        {
            e = new ScanEntry { Index = 0, Path = path, Size = data.Length, Condition = RecoveryCondition.Verified };
            var s = new FakeSession();
            s.Data[0] = data;
            return s;
        }

        private static PreviewContent Load(FakeSession s, ScanEntry e, AppSettings set = null)
        {
            return new PreviewService().LoadAsync(s, e, set ?? new AppSettings(), CancellationToken.None).Result;
        }

        [TestMethod]
        public void Images_are_returned_as_bytes_and_respect_the_size_limit_and_switch()
        {
            ScanEntry e;
            var data = new byte[3 * 1024 * 1024]; data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF;
            var s = One("\\a.jpg", data, out e);
            var c = Load(s, e);
            Assert.AreEqual(PreviewKind.Image, c.Kind);
            CollectionAssert.AreEqual(data, c.Data);
            Assert.AreEqual(PreviewKind.TooLarge, Load(s, e, new AppSettings { PreviewMaxSizeMb = 2 }).Kind);
            Assert.AreEqual(PreviewKind.Disabled, Load(s, e, new AppSettings { PreviewEnabled = false }).Kind);
        }

        [TestMethod]
        public void Empty_files_and_read_errors_are_reported_not_thrown()
        {
            ScanEntry e;
            var s = One("\\a.mp4", new byte[0], out e);
            Assert.AreEqual(PreviewKind.NoData, Load(s, e).Kind);
            Assert.IsFalse(new PreviewService().CanPreview(e));
            s = One("\\a.png", new byte[0], out e);
            Assert.AreEqual(PreviewKind.NoData, Load(s, e).Kind);
            s = One("\\a.png", new byte[100], out e);
            s.ReadStatus = EngineStatus.NotFound;
            var c = Load(s, e);
            Assert.AreEqual(PreviewKind.ReadFailed, c.Kind);
            s = One("\\a.png", new byte[100], out e);
            s.ShortBy = 10;
            Assert.AreEqual(PreviewKind.ReadFailed, Load(s, e).Kind);
            s = One("\\a.png", new byte[100], out e);
            e.Condition = RecoveryCondition.Unsupported;
            Assert.AreEqual(PreviewKind.NoData, Load(s, e).Kind);
        }

        [TestMethod]
        public void Text_is_decoded_by_bom_utf8_or_ansi_and_limited_to_64_kb()
        {
            Assert.AreEqual("héllo", PreviewService.DecodeText(Encoding.UTF8.GetBytes("héllo")));
            Assert.AreEqual("héllo", PreviewService.DecodeText(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("héllo")).ToArray()));
            Assert.AreEqual("hi", PreviewService.DecodeText(new byte[] { 0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0 }));
            Assert.AreEqual("hi", PreviewService.DecodeText(new byte[] { 0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i' }));
            byte[] ansi = { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x6F, 0x6B };                                          // "café ok" in Windows-1252: invalid UTF-8
            Assert.AreEqual(Encoding.Default.GetString(ansi), PreviewService.DecodeText(ansi));
            Assert.AreEqual("a.b", PreviewService.DecodeText(new byte[] { (byte)'a', 0x01, (byte)'b' }));          // control chars neutralized
            // A cut in the middle of a multi-byte character must not make valid UTF-8 look like ANSI.
            byte[] euro = Encoding.UTF8.GetBytes("ab€");
            Assert.AreEqual("ab", PreviewService.DecodeText(euro.Take(euro.Length - 1).ToArray()));

            ScanEntry e;
            var big = Encoding.ASCII.GetBytes(new string('x', 100000));
            var s = One("\\log.txt", big, out e);
            var c = Load(s, e, new AppSettings { PreviewMaxSizeMb = 1 });
            Assert.AreEqual(PreviewKind.Text, c.Kind);
            Assert.AreEqual(PreviewService.TextBytes, c.Text.Length);
            Assert.IsTrue(c.Truncated);
        }

        [TestMethod]
        public void Content_decides_the_preview_not_the_extension()
        {
            ScanEntry e;
            // Source code and files without an extension are text (what a Quick Scan of a project folder finds).
            var s = One("\\proj\\app\\engine.py", Encoding.UTF8.GetBytes("import os\nprint('hello')\n"), out e);
            Assert.IsTrue(new PreviewService().CanPreview(e));
            var c = Load(s, e);
            Assert.AreEqual(PreviewKind.Text, c.Kind);
            StringAssert.Contains(c.Text, "print('hello')");
            Assert.IsFalse(c.TypeMismatch);
            s = One("\\[unknown]\\37", Encoding.ASCII.GetBytes("empty"), out e);
            Assert.AreEqual(PreviewKind.Text, Load(s, e).Kind);
            // A PNG named .dat is still shown as an image; a PDF without extension as a PDF.
            s = One("\\x.dat", new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, out e);
            Assert.AreEqual(PreviewKind.Image, Load(s, e).Kind);
            s = One("\\doc", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"), out e);
            Assert.AreEqual(PreviewKind.Pdf, Load(s, e).Kind);
        }

        [TestMethod]
        public void Erased_data_is_explained_and_binary_data_gets_a_hex_view()
        {
            ScanEntry e;
            // SSD TRIM: the deleted photo reads back as zeros.
            var s = One("\\$RCD8XJK.jpeg", new byte[858842], out e);
            Assert.AreEqual(PreviewKind.Erased, Load(s, e).Kind);
            // A ".jpeg" holding no image (a Recycle Bin $I record): hex view, flagged as not matching its name.
            var rec = new byte[112]; rec[0] = 2; rec[8] = 0x10; rec[24] = (byte)'C'; rec[26] = (byte)':';
            s = One("\\$ICD8XJK.jpeg", rec, out e);
            var c = Load(s, e);
            Assert.AreEqual(PreviewKind.Hex, c.Kind);
            Assert.IsTrue(c.TypeMismatch);
            StringAssert.StartsWith(c.Text, "00000000  02 00 00 00");
            // Binary data longer than the hex window is marked truncated; zeros only at the start are not "erased".
            var bin = new byte[10000]; new Random(3).NextBytes(bin); for (int i = 0; i < 100; i++) bin[i] = 0;
            s = One("\\blob", bin, out e);
            c = Load(s, e);
            Assert.AreEqual(PreviewKind.Hex, c.Kind);
            Assert.IsTrue(c.Truncated);
            Assert.AreEqual(PreviewService.HexBytes / 16, c.Text.Split('\n').Length - 1);
        }

        private static byte[] Pdf(bool compressed)
        {
            string content = "BT /F1 12 Tf 72 700 Td (Hello RecoverX) Tj T* [(Second) -250 ( line)] TJ ET";
            byte[] stream;
            string dict;
            if (compressed)
            {
                using (var ms = new MemoryStream())
                {
                    ms.WriteByte(0x78); ms.WriteByte(0x9C);                     // zlib header
                    using (var z = new DeflateStream(ms, CompressionMode.Compress, true)) { var b = Encoding.ASCII.GetBytes(content); z.Write(b, 0, b.Length); }
                    stream = ms.ToArray();
                }
                dict = "<< /Length " + stream.Length + " /Filter /FlateDecode >>";
            }
            else { stream = Encoding.ASCII.GetBytes(content); dict = "<< /Length " + stream.Length + " >>"; }
            var o = new MemoryStream();
            void W(string t) { var b = Encoding.GetEncoding(28591).GetBytes(t); o.Write(b, 0, b.Length); }
            W("%PDF-1.5\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >> endobj\n");
            W("3 0 obj << /Type /Page /Parent 2 0 R /Contents 4 0 R >> endobj\n5 0 obj << /Type /Page /Parent 2 0 R >> endobj\n");
            W("4 0 obj " + dict + "\nstream\n"); o.Write(stream, 0, stream.Length); W("\nendstream endobj\n");
            W("6 0 obj << /Title (Quarterly \\(draft\\)) /Author <FEFF00410069> >> endobj\ntrailer << /Root 1 0 R /Info 6 0 R >>\n%%EOF\n");
            return o.ToArray();
        }

        [TestMethod]
        public void Pdf_summary_reports_version_pages_info_and_text_from_plain_and_compressed_streams()
        {
            foreach (bool compressed in new[] { false, true })
            {
                PdfSummary p = PdfInspector.Summarize(Pdf(compressed));
                Assert.AreEqual("1.5", p.Version);
                Assert.AreEqual(2, p.Pages);
                Assert.AreEqual("Quarterly (draft)", p.Title);
                Assert.AreEqual("Ai", p.Author);                               // UTF-16BE hex string
                StringAssert.Contains(p.TextSample, "Hello RecoverX");
                StringAssert.Contains(p.TextSample, "Second line");
                Assert.IsFalse(p.EncryptedOrEncoded);
            }
        }

        [TestMethod]
        public void Pdf_inspector_survives_garbage_and_truncation()
        {
            var rnd = new Random(7);
            byte[] good = Pdf(true);
            for (int i = 0; i < 300; i++)
            {
                byte[] d = good.Take(rnd.Next(1, good.Length)).ToArray();
                for (int k = 0; k < 10; k++) d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
                PdfSummary p = PdfInspector.Summarize(d);
                Assert.IsNotNull(p.TextSample);
            }
            var junk = new byte[50000]; rnd.NextBytes(junk);
            Assert.AreEqual("?", PdfInspector.Summarize(junk).Version);
        }
    }

    // ---------------- recovery of Deep Scan results ----------------
    [TestClass]
    public class DeepRecoveryTests
    {
        [TestMethod]
        public void Carved_files_go_to_the_deep_scan_folder_and_get_the_date_recorded_inside_them()
        {
            string root = Path.Combine(Path.GetTempPath(), "rx_p4_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var data = new byte[5000]; data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF;
                var camera = new DateTime(2024, 5, 17, 14, 30, 0, DateTimeKind.Unspecified);
                var e = new ScanEntry
                {
                    Index = 0, Origin = EntryOrigin.Signature, Path = CarvedNames.PathFor("jpg", 65536), Size = data.Length,
                    Condition = RecoveryCondition.Verified, ContentTime = camera, ContentTimeIsLocal = true
                };
                var s = new FakeSession { SessionMode = ScanMode.Deep };
                s.Data[0] = data;
                var svc = new RecoveryService(new FakeEngine { NextHeader = HeaderCheck.Match }, new FakeValidator { Verdict = DestinationVerdict.Safe },
                                              new FakeRecoveredRepo(), new NullLogger(), () => new DateTime(2026, 11, 12, 10, 0, 0));
                RecoverySummary sum = svc.RecoverAsync(s, new List<ScanEntry> { e }, new RecoveryRequest { SourceRoot = "F:\\", DestinationDirectory = root }, null, CancellationToken.None).Result;
                Assert.IsNull(sum.Error);
                RecoveryItemResult r = sum.Items.Single();
                Assert.AreEqual(RecoveryOutcome.Verified, r.Outcome);
                StringAssert.Contains(r.SavedPath, Path.Combine("Deep Scan", "JPG", "f0000000128.jpg"));
                CollectionAssert.AreEqual(data, File.ReadAllBytes(r.SavedPath));
                Assert.AreEqual(camera, File.GetLastWriteTime(r.SavedPath));
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }
    }

    // ---------------- the real native engine (x64 or x86, matching the test process) ----------------
    [TestClass]
    public class NativeEngineIntegrationTests
    {
        private static NativeEngineClient Client()
        {
            var c = new NativeEngineClient(new NullLogger());
            if (!c.IsAvailable) Assert.Inconclusive("recoverx_engine.dll not staged next to the tests: " + c.UnavailableReason);
            return c;
        }

        [TestMethod]
        public void Engine_loads_with_abi_2_and_all_struct_layouts_match()
        {
            NativeEngineClient c = Client();                               // IsAvailable is false on any ABI or layout mismatch
            Console.WriteLine("process " + (Environment.Is64BitProcess ? "x64" : "x86") + ", engine " + c.Version + " from " + c.LoadedPath);
            StringAssert.Contains(c.LoadedPath, Environment.Is64BitProcess ? "\\x64\\" : "\\x86\\");
            Assert.AreEqual("1.0.0", c.Version);
            Assert.AreEqual(64, Marshal.SizeOf(typeof(RxCarveEntryProbe)));
        }

        [TestMethod]
        public void Header_check_and_failure_paths_cross_the_boundary_safely()
        {
            NativeEngineClient c = Client();
            Assert.AreEqual(HeaderCheck.Match, c.CheckHeader("a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 8));
            Assert.AreEqual(HeaderCheck.Mismatch, c.CheckHeader("a.pdf", new byte[] { 1, 2, 3, 4, 5 }, 5));
            // Not a drive letter: rejected by the engine (the shipped DLL never opens arbitrary paths).
            Assert.AreEqual(EngineStatus.InvalidArgument, c.OpenDeepScan("C:\\Windows\\notepad.exe", new DeepScanOptions()).Status);
            Assert.AreEqual(EngineStatus.InvalidArgument, c.OpenNtfsScan("\\\\.\\PhysicalDrive0", new ScanOptions()).Status);
            Assert.AreEqual(EngineStatus.InvalidArgument, c.OpenFatScan("D:\\image.img", new ScanOptions()).Status);
            Assert.AreEqual(EngineStatus.NotFound, c.OpenFatScan(FreeLetter() + ":\\", new ScanOptions()).Status);
            // A letter that does not exist.
            ScanOpenResult missing = c.OpenDeepScan(FreeLetter() + ":\\", new DeepScanOptions());
            Assert.AreEqual(EngineStatus.NotFound, missing.Status);
            Assert.IsNull(missing.Session);
        }

        private static char FreeLetter()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            for (char l = 'Z'; l >= 'D'; l--) if (!used.Contains(l)) return l;
            Assert.Inconclusive("no free drive letter");
            return 'Z';
        }

        // Same layout as the engine's RxCarveEntry (the real struct is internal to RecoverX.Core).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RxCarveEntryProbe
        {
            public uint StructSize, Type, Condition, Evidence;
            public ulong Offset, Size, ContentTime;
            public uint ClustersTotal, ClustersInUse;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Ext;
        }
    }
}
