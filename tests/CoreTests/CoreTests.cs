using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Native;
using RecoverX.Core.Services;
using RecoverX.Database;

namespace RecoverX.CoreTests
{
    internal sealed class NullLogger : IAppLogger
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m, Exception ex = null) { }
    }

    [TestClass]
    public class ByteFormatterTests
    {
        [TestMethod]
        public void Formats_bytes_and_larger_units()
        {
            Assert.AreEqual("0 B", ByteFormatter.Format(0));
            StringAssert.Contains(ByteFormatter.Format(1024L * 1024 * 1024), "GB");
        }
    }

    [TestClass]
    public class DriveServiceTests
    {
        [TestMethod]
        public void GetDrives_does_not_throw_and_reports_only_fixed_or_removable()
        {
            var drives = new DriveService(new NullLogger()).GetDrives();
            Assert.IsTrue(drives.All(d => d.Kind == DriveKind.Fixed || d.Kind == DriveKind.Removable));
        }
    }

    // ---------------- Phase 2: drive classification and enrichment ----------------
    [TestClass]
    public class DriveClassifierTests
    {
        private static DriveModel Drive(BusType bus, MediaType media = MediaType.Unknown, bool? trim = null, bool? removable = null, DriveKind kind = DriveKind.Fixed)
        {
            return new DriveModel { Bus = bus, Media = media, TrimEnabled = trim, RemovableMedia = removable, Kind = kind };
        }

        [TestMethod]
        public void Usb_is_external_and_card_buses_are_cards()
        {
            Assert.AreEqual("Loc_External", DriveClassifier.LocationKey(Drive(BusType.Usb)));
            Assert.AreEqual("Loc_Card", DriveClassifier.LocationKey(Drive(BusType.Sd)));
            Assert.AreEqual("Loc_Card", DriveClassifier.LocationKey(Drive(BusType.Mmc)));
        }

        [TestMethod]
        public void Internal_buses_are_internal_unless_flagged_removable()
        {
            Assert.AreEqual("Loc_Internal", DriveClassifier.LocationKey(Drive(BusType.Nvme)));
            Assert.AreEqual("Loc_Internal", DriveClassifier.LocationKey(Drive(BusType.Sata, removable: false)));
            Assert.AreEqual("Loc_Removable", DriveClassifier.LocationKey(Drive(BusType.Sata, removable: true)));
        }

        [TestMethod]
        public void Unknown_bus_falls_back_to_the_drive_kind()
        {
            Assert.AreEqual("Loc_Removable", DriveClassifier.LocationKey(Drive(BusType.Unknown, kind: DriveKind.Removable)));
            Assert.AreEqual("Loc_Unknown", DriveClassifier.LocationKey(Drive(BusType.Unknown)));
        }

        [TestMethod]
        public void Ssd_warnings_depend_on_trim_and_hdd_has_none()
        {
            Assert.AreEqual("Warn_Trim", DriveClassifier.WarningKey(Drive(BusType.Sata, MediaType.Ssd, trim: true)));
            Assert.AreEqual("Warn_SsdNoTrim", DriveClassifier.WarningKey(Drive(BusType.Sata, MediaType.Ssd, trim: false)));
            Assert.AreEqual("Warn_SsdNoTrim", DriveClassifier.WarningKey(Drive(BusType.Sata, MediaType.Ssd, trim: null)));
            Assert.IsNull(DriveClassifier.WarningKey(Drive(BusType.Sata, MediaType.Hdd, trim: true)));
            Assert.IsNull(DriveClassifier.WarningKey(Drive(BusType.Unknown)));
        }

        [TestMethod]
        public void Apply_marks_detection_source_by_what_was_queried()
        {
            var d = new DriveModel();
            DriveClassifier.Apply(d, new VolumeHardwareInfo { Bus = BusType.Usb, Media = MediaType.Ssd, DeviceQueried = true, MediaQueried = true });
            Assert.AreEqual(DetectionSource.Native, d.Source);
            Assert.AreEqual(MediaType.Ssd, d.Media);

            d = new DriveModel();
            DriveClassifier.Apply(d, new VolumeHardwareInfo { Bus = BusType.Sata, Media = MediaType.Hdd, DeviceQueried = true, MediaQueried = false });
            Assert.AreEqual(DetectionSource.NativePartial, d.Source);
            Assert.AreEqual(MediaType.Unknown, d.Media);          // never trust a media type that was not actually queried

            d = new DriveModel();
            DriveClassifier.Apply(d, new VolumeHardwareInfo { DeviceQueried = false });
            Assert.AreEqual(DetectionSource.Basic, d.Source);
        }

        [TestMethod]
        public void Every_enum_value_maps_to_a_resource_key()
        {
            foreach (BusType b in Enum.GetValues(typeof(BusType))) Assert.IsTrue(DriveClassifier.BusKey(b).StartsWith("Bus_"));
            foreach (MediaType m in Enum.GetValues(typeof(MediaType))) Assert.IsTrue(DriveClassifier.MediaKey(m).StartsWith("Media_"));
        }
    }

    internal sealed class FakeDrives : IDriveService
    {
        public IReadOnlyList<DriveModel> GetDrives()
        {
            return new List<DriveModel> {
                new DriveModel { Letter = "C:", RootPath = "C:\\", Kind = DriveKind.Fixed, IsReady = true },
                new DriveModel { Letter = "E:", RootPath = "E:\\", Kind = DriveKind.Removable, IsReady = true } };
        }
    }

    internal sealed class FakeEngine : IEngineClient
    {
        public bool Available = true; public bool Throw; public IReadOnlyList<VolumeHardwareInfo> Volumes = new List<VolumeHardwareInfo>();
        public bool IsAvailable { get { return Available; } }
        public string UnavailableReason { get { return Available ? "" : "fake"; } }
        public string Version { get { return "fake"; } }
        public string LoadedPath { get { return ""; } }
        public IReadOnlyList<VolumeHardwareInfo> EnumerateVolumes() { if (Throw) throw new EngineException("boom"); return Volumes; }
        public VolumeProbeResult ProbeVolume(string root) { return new VolumeProbeResult { Status = EngineStatus.Ok }; }
        public DestinationVerdict ValidateDestination(string s, string d) { return DestinationVerdict.SameVolume; }
        public HeaderCheck NextHeader = HeaderCheck.NotChecked;
        public ScanOpenResult OpenResult = new ScanOpenResult { Status = EngineStatus.Unavailable };
        public ScanOpenResult OpenNtfsScan(string root, ScanOptions o) { NtfsOpens++; return OpenResult; }
        public ScanOpenResult DeepOpenResult = new ScanOpenResult { Status = EngineStatus.Unavailable };
        public DeepScanOptions LastDeepOptions;
        public ScanOpenResult OpenDeepScan(string root, DeepScanOptions o) { LastDeepOptions = o; return DeepOpenResult; }
        public ScanOpenResult FatOpenResult = new ScanOpenResult { Status = EngineStatus.Unavailable };
        public int FatOpens, NtfsOpens;
        public ScanOpenResult OpenFatScan(string root, ScanOptions o) { FatOpens++; return FatOpenResult; }
        public HeaderCheck CheckHeader(string name, byte[] data, int length) { return NextHeader; }
    }

    [TestClass]
    public class EnrichedDriveServiceTests
    {
        [TestMethod]
        public void Adds_hardware_details_by_drive_letter_case_insensitively()
        {
            var engine = new FakeEngine { Volumes = new List<VolumeHardwareInfo> {
                new VolumeHardwareInfo { Letter = "e:", Bus = BusType.Usb, Media = MediaType.Ssd, Vendor = "Acme", Product = "Stick", DeviceQueried = true, MediaQueried = true } } };
            var drives = new EnrichedDriveService(new FakeDrives(), engine, new NullLogger()).GetDrives();
            var e = drives.Single(d => d.Letter == "E:");
            Assert.AreEqual(BusType.Usb, e.Bus);
            Assert.AreEqual("Acme Stick", e.DeviceName);
            Assert.AreEqual(DetectionSource.Basic, drives.Single(d => d.Letter == "C:").Source);   // C: not reported: untouched
        }

        [TestMethod]
        public void Phase1_result_is_returned_when_engine_is_unavailable_or_fails()
        {
            var off = new FakeEngine { Available = false };
            Assert.AreEqual(2, new EnrichedDriveService(new FakeDrives(), off, new NullLogger()).GetDrives().Count);
            var bad = new FakeEngine { Throw = true };
            var drives = new EnrichedDriveService(new FakeDrives(), bad, new NullLogger()).GetDrives();
            Assert.AreEqual(2, drives.Count);
            Assert.IsTrue(drives.All(d => d.Source == DetectionSource.Basic));
        }
    }

    // ---------------- Phase 2: native interop (managed side) ----------------
    [TestClass]
    public class NativeInteropTests
    {
        [TestMethod]
        public void Managed_struct_sizes_match_the_native_windows_layout()
        {
            // These numbers are asserted at compile time against rx_engine.h by the MinGW build (see docs/RECOVERY_ENGINE.md).
            Assert.AreEqual(216, Marshal.SizeOf(typeof(RxVolumeInfo)));
            Assert.AreEqual(32, Marshal.SizeOf(typeof(RxProbeResult)));
        }

        [TestMethod]
        public void Field_offsets_match_the_native_header()
        {
            Assert.AreEqual(0, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "StructSize"));
            Assert.AreEqual(8, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "BusType"));
            Assert.AreEqual(16, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "RemovableMedia"));
            Assert.AreEqual(24, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "Flags"));
            Assert.AreEqual(32, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "Root"));
            Assert.AreEqual(40, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "Vendor"));
            Assert.AreEqual(120, (int)Marshal.OffsetOf(typeof(RxVolumeInfo), "Product"));
            Assert.AreEqual(16, (int)Marshal.OffsetOf(typeof(RxProbeResult), "TotalBytes"));
            Assert.AreEqual(24, (int)Marshal.OffsetOf(typeof(RxProbeResult), "BytesRead"));
        }

        [TestMethod]
        public void Phase3_struct_sizes_and_offsets_match_the_native_header()
        {
            Assert.AreEqual(16, Marshal.SizeOf(typeof(RxScanOptions)));
            Assert.AreEqual(32, Marshal.SizeOf(typeof(RxScanProgress)));
            Assert.AreEqual(1120, Marshal.SizeOf(typeof(RxFileEntry)));
            Assert.AreEqual(4, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Flags"));
            Assert.AreEqual(8, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Condition"));
            Assert.AreEqual(12, (int)Marshal.OffsetOf(typeof(RxFileEntry), "HeaderCheck"));
            Assert.AreEqual(16, (int)Marshal.OffsetOf(typeof(RxFileEntry), "MftNumber"));
            Assert.AreEqual(24, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Size"));
            Assert.AreEqual(32, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Created"));
            Assert.AreEqual(40, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Modified"));
            Assert.AreEqual(48, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Accessed"));
            Assert.AreEqual(56, (int)Marshal.OffsetOf(typeof(RxFileEntry), "RecordChanged"));
            Assert.AreEqual(64, (int)Marshal.OffsetOf(typeof(RxFileEntry), "ClustersTotal"));
            Assert.AreEqual(68, (int)Marshal.OffsetOf(typeof(RxFileEntry), "ClustersInUse"));
            Assert.AreEqual(72, (int)Marshal.OffsetOf(typeof(RxFileEntry), "RecordSeq"));
            Assert.AreEqual(80, (int)Marshal.OffsetOf(typeof(RxFileEntry), "Path"));
            Assert.AreEqual(4, (int)Marshal.OffsetOf(typeof(RxScanProgress), "Phase"));
            Assert.AreEqual(20, (int)Marshal.OffsetOf(typeof(RxScanProgress), "Flags"));
            Assert.AreEqual(28, (int)Marshal.OffsetOf(typeof(RxScanProgress), "BadRecords"));
        }

        [TestMethod]
        public void Missing_engine_dll_degrades_gracefully()
        {
            // The test host has no x64\recoverx_engine.dll next to it, so loading must fail cleanly, never throw.
            var client = new NativeEngineClient(new NullLogger());
            if (client.IsAvailable) Assert.Inconclusive("An engine DLL happens to be present; the missing-DLL path was not exercised.");
            Assert.IsTrue(client.UnavailableReason.Length > 0);
            Assert.AreEqual(EngineStatus.Unavailable, client.ProbeVolume("C:").Status);
            try { client.EnumerateVolumes(); Assert.Fail("expected EngineException"); } catch (EngineException) { }
            Assert.AreEqual(EngineStatus.Unavailable, client.OpenNtfsScan("C:\\", new ScanOptions()).Status);
            Assert.AreEqual(HeaderCheck.NotChecked, client.CheckHeader("a.jpg", new byte[] { 1, 2, 3 }, 3));
        }
    }

    [TestClass]
    public class DestinationValidatorTests
    {
        [TestMethod]
        public void Same_drive_letter_is_unsafe_regardless_of_case_or_separator()
        {
            Assert.AreEqual(DestinationVerdict.SameVolume, DestinationValidator.ByDriveLetter("E:\\", "E:\\Recovered"));
            Assert.AreEqual(DestinationVerdict.SameVolume, DestinationValidator.ByDriveLetter("e:", "E:/x"));
            Assert.AreEqual(DestinationVerdict.Safe, DestinationValidator.ByDriveLetter("E:\\", "D:\\Recovered"));
        }

        [TestMethod]
        public void Network_shares_are_safe_and_relative_paths_are_unverifiable()
        {
            Assert.AreEqual(DestinationVerdict.Safe, DestinationValidator.ByDriveLetter("E:\\", "\\\\server\\share\\r"));
            Assert.AreEqual(DestinationVerdict.Unverifiable, DestinationValidator.ByDriveLetter("E:\\", "relative\\path"));
            Assert.AreEqual(DestinationVerdict.Unverifiable, DestinationValidator.ByDriveLetter("junk", "D:\\x"));
        }

        [TestMethod]
        public void Missing_folder_is_reported_before_any_volume_check()
        {
            var v = new DestinationValidator(new FakeEngine());
            Assert.AreEqual(DestinationVerdict.NotFound, v.Validate("E:\\", ""));
            Assert.AreEqual(DestinationVerdict.NotFound, v.Validate("E:\\", Path.Combine(Path.GetTempPath(), "rx-does-not-exist-" + Guid.NewGuid().ToString("N"))));
        }

        [TestMethod]
        public void Existing_folder_uses_the_engine_verdict_when_available()
        {
            Assert.AreEqual(DestinationVerdict.SameVolume, new DestinationValidator(new FakeEngine()).Validate("E:\\", Path.GetTempPath()));
        }
    }

    // ---------------- Phase 2: localization catalog, categories, settings ----------------
    [TestClass]
    public class FileCategorizerTests
    {
        [TestMethod]
        public void Classifies_by_extension_and_falls_back_to_other()
        {
            var cats = DefaultCategories.Create();
            Assert.AreEqual("Photos", FileCategorizer.Classify("holiday.JPG", cats));
            Assert.AreEqual("Documents", FileCategorizer.Classify("report.final.pdf", cats));
            Assert.AreEqual("Videos", FileCategorizer.Classify(".mp4", cats));
            Assert.AreEqual("Other", FileCategorizer.Classify("data.xyz", cats));
            Assert.AreEqual("Other", FileCategorizer.Classify("noextension", cats));
        }

        [TestMethod]
        public void Disabled_categories_are_not_wanted()
        {
            var cats = DefaultCategories.Create();
            cats.Single(c => c.Id == "Photos").Enabled = false;
            Assert.IsFalse(FileCategorizer.IsWanted("a.png", cats));
            Assert.IsTrue(FileCategorizer.IsWanted("a.pdf", cats));
        }

        [TestMethod]
        public void Parses_and_normalizes_extension_lists()
        {
            List<string> e;
            Assert.IsTrue(FileCategorizer.TryParseExtensions("JPG, .png ;gif jpg", out e));
            CollectionAssert.AreEqual(new[] { "jpg", "png", "gif" }, e);
            Assert.IsFalse(FileCategorizer.TryParseExtensions("", out e));
            Assert.IsFalse(FileCategorizer.TryParseExtensions("jp g!", out e));
            Assert.IsFalse(FileCategorizer.TryParseExtensions("..\\evil", out e));
            Assert.IsFalse(FileCategorizer.TryParseExtensions("waytoolongextension", out e));
        }

        [TestMethod]
        public void Detects_an_extension_claimed_by_two_categories()
        {
            var cats = DefaultCategories.Create();
            var photos = cats.Single(c => c.Id == "Photos");
            photos.Extensions.Add("pdf");
            string owner;
            Assert.AreEqual("pdf", FileCategorizer.FindConflict(photos, cats, out owner));
            Assert.AreEqual("Documents", owner);
            string none;
            Assert.IsNull(FileCategorizer.FindConflict(cats.Single(c => c.Id == "Audio"), cats, out none));
        }
    }

    [TestClass]
    public class AppSettingsTests
    {
        [TestMethod]
        public void Sanitize_clamps_every_value_into_range()
        {
            var s = new AppSettings { Theme = "Neon", DefaultScanType = "Turbo", Language = " ", MinFileSizeKb = -5,
                                      PreviewMaxSizeMb = 99999, HistoryMaxEntries = 1, DefaultRecoveryDestination = null };
            s.Sanitize();
            Assert.AreEqual(AppSettings.LightTheme, s.Theme);
            Assert.AreEqual(AppSettings.QuickScan, s.DefaultScanType);
            Assert.AreEqual("en", s.Language);
            Assert.AreEqual(0, s.MinFileSizeKb);
            Assert.AreEqual(AppSettings.MaxPreviewMb, s.PreviewMaxSizeMb);
            Assert.AreEqual(AppSettings.MinHistoryEntries, s.HistoryMaxEntries);
            Assert.AreEqual(string.Empty, s.DefaultRecoveryDestination);
        }
    }

    // ---------------- Database (Phase 1 + Phase 2) ----------------
    [TestClass]
    public class DatabaseTests
    {
        private string _path;

        [TestInitialize] public void Init() { _path = Path.Combine(Path.GetTempPath(), "recoverx-test-" + Guid.NewGuid().ToString("N") + ".db"); }
        [TestCleanup] public void Cleanup() { try { File.Delete(_path); } catch (IOException) { } }

        private SqliteDatabase NewDb() { var db = new SqliteDatabase(_path); db.EnsureCreated(); return db; }

        [TestMethod]
        public void Settings_round_trip_and_defaults()
        {
            var svc = new SettingsService(NewDb());
            var d = svc.Load();
            Assert.AreEqual(AppSettings.LightTheme, d.Theme);
            Assert.AreEqual(AppSettings.QuickScan, d.DefaultScanType);
            Assert.IsTrue(d.PreviewEnabled);
            svc.Save(new AppSettings { Theme = AppSettings.DarkTheme, DefaultRecoveryDestination = @"D:\Recovered", Language = "en",
                DefaultScanType = AppSettings.DeepScan, MinFileSizeKb = 4, PreviewEnabled = false, PreviewMaxSizeMb = 50, HistoryMaxEntries = 77 });
            var s = svc.Load();
            Assert.AreEqual(AppSettings.DarkTheme, s.Theme);
            Assert.AreEqual(@"D:\Recovered", s.DefaultRecoveryDestination);
            Assert.AreEqual(AppSettings.DeepScan, s.DefaultScanType);
            Assert.AreEqual(4, s.MinFileSizeKb);
            Assert.IsFalse(s.PreviewEnabled);
            Assert.AreEqual(50, s.PreviewMaxSizeMb);
            Assert.AreEqual(77, s.HistoryMaxEntries);
        }

        [TestMethod]
        public void Tampered_stored_values_are_sanitized_on_load()
        {
            var db = NewDb();
            using (var c = db.Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES('theme','Neon'),('min_file_size_kb','abc'),('preview_max_mb','-3'),('history_max_entries','999999999')";
                cmd.ExecuteNonQuery();
            }
            var s = new SettingsService(db).Load();
            Assert.AreEqual(AppSettings.LightTheme, s.Theme);
            Assert.AreEqual(0, s.MinFileSizeKb);
            Assert.AreEqual(1, s.PreviewMaxSizeMb);
            Assert.AreEqual(AppSettings.MaxHistoryEntries, s.HistoryMaxEntries);
        }

        [TestMethod]
        public void Phase1_database_upgrades_in_place_without_losing_data()
        {
            // Build a database exactly as Phase 1 did (schema v1, user_version 0) with a saved theme and a history row.
            using (var c = new SQLiteConnection("Data Source=" + _path + ";Version=3;"))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        "CREATE TABLE settings(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);" +
                        "CREATE TABLE scan_sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, started_utc TEXT NOT NULL, finished_utc TEXT, drive_letter TEXT NOT NULL, volume_name TEXT, file_system TEXT, scan_type TEXT NOT NULL, status TEXT NOT NULL, files_found INTEGER NOT NULL DEFAULT 0, resumable INTEGER NOT NULL DEFAULT 0);" +
                        "INSERT INTO settings VALUES('theme','Dark');" +
                        "INSERT INTO scan_sessions(started_utc,drive_letter,scan_type,status,files_found) VALUES('2026-01-01T00:00:00.0000000Z','E:','Quick','Completed',9);";
                    cmd.ExecuteNonQuery();
                }
            }
            var db = new SqliteDatabase(_path);
            db.EnsureCreated();
            Assert.AreEqual(SqliteDatabase.CurrentSchemaVersion, db.SchemaVersion());
            Assert.AreEqual(AppSettings.DarkTheme, new SettingsService(db).Load().Theme);
            Assert.AreEqual(9, new ScanHistoryRepository(db).GetStats().TotalFilesFound);
            Assert.AreEqual(6, new CategoryRepository(db).GetAll().Count);
            Assert.AreEqual(0, new RecoveredFilesRepository(db).Count());          // Phase 3 table exists after the upgrade
            db.EnsureCreated();   // running it again must be harmless
            Assert.AreEqual(6, new CategoryRepository(db).GetAll().Count);
        }

        [TestMethod]
        public void Categories_are_seeded_and_edits_persist()
        {
            var repo = new CategoryRepository(NewDb());
            var all = repo.GetAll();
            CollectionAssert.AreEqual(new[] { "Photos", "Videos", "Documents", "Audio", "Archives", "Other" }, all.Select(c => c.Id).ToArray());
            var photos = all.Single(c => c.Id == "Photos");
            photos.Enabled = false;
            photos.Extensions = new List<string> { "jpg", "heic" };
            repo.Save(all);
            var again = repo.GetAll().Single(c => c.Id == "Photos");
            Assert.IsFalse(again.Enabled);
            CollectionAssert.AreEqual(new[] { "jpg", "heic" }, again.Extensions.ToArray());
        }

        [TestMethod]
        public void Empty_history_has_zero_stats_and_added_session_is_listed()
        {
            var repo = new ScanHistoryRepository(NewDb());
            Assert.AreEqual(0, repo.GetStats().TotalScans);
            repo.AddSession(new ScanSession { StartedUtc = DateTime.UtcNow, DriveLetter = "E:", ScanType = "Quick", Status = "Completed", FilesFound = 3 });
            Assert.AreEqual(1, repo.GetRecent(10).Count);
            Assert.AreEqual(3, repo.GetStats().TotalFilesFound);
        }

        [TestMethod]
        public void Session_updates_and_interrupted_sessions_are_recorded()
        {
            var repo = new ScanHistoryRepository(NewDb());
            long id = repo.AddSession(new ScanSession { StartedUtc = DateTime.UtcNow, DriveLetter = "E:", ScanType = "Quick", Status = "Running" });
            repo.UpdateSession(id, DateTime.UtcNow, "Completed", 42);
            var s = repo.GetRecent(5).Single();
            Assert.AreEqual("Completed", s.Status);
            Assert.AreEqual(42, s.FilesFound);
            Assert.IsTrue(s.FinishedUtc.HasValue);

            repo.AddSession(new ScanSession { StartedUtc = DateTime.UtcNow, DriveLetter = "F:", ScanType = "Quick", Status = "Running" });
            Assert.AreEqual(1, repo.MarkInterrupted());
            Assert.AreEqual(0, repo.MarkInterrupted());
            Assert.AreEqual(1, repo.GetRecent(5).Count(x => x.Status == "Interrupted"));
            Assert.AreEqual(1, repo.GetRecent(5).Count(x => x.Status == "Completed"));       // finished sessions are untouched
        }

        [TestMethod]
        public void Recovered_files_round_trip_newest_first_and_clear()
        {
            var repo = new RecoveredFilesRepository(NewDb());
            Assert.AreEqual(0, repo.Count());
            repo.Add(new RecoveredFileRecord { RecoveredUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), SourceDrive = "E:", OriginalPath = "\\a.txt", SavedPath = "D:\\r\\a.txt", Size = 10, Status = "Verified" });
            repo.Add(new RecoveredFileRecord { RecoveredUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), SourceDrive = "E:", OriginalPath = "\\b.txt", SavedPath = "D:\\r\\b.txt", Size = 20, Status = "Suspect" });
            Assert.AreEqual(2, repo.Count());
            var rows = repo.GetRecent(10);
            Assert.AreEqual("\\b.txt", rows[0].OriginalPath);
            Assert.AreEqual("Suspect", rows[0].Status);
            Assert.AreEqual(20, rows[0].Size);
            Assert.AreEqual(1, repo.GetRecent(1).Count);
            Assert.AreEqual(2, repo.ClearAll());
            Assert.AreEqual(0, repo.Count());
        }

        [TestMethod]
        public void Prune_keeps_only_the_newest_and_clear_removes_everything()
        {
            var repo = new ScanHistoryRepository(NewDb());
            for (int i = 0; i < 5; i++)
                repo.AddSession(new ScanSession { StartedUtc = new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc), DriveLetter = "E:", ScanType = "Deep", Status = "Completed", FilesFound = i });
            Assert.AreEqual(3, repo.Prune(2));
            var left = repo.GetRecent(10);
            Assert.AreEqual(2, left.Count);
            Assert.AreEqual(4, left[0].FilesFound);          // newest first
            Assert.AreEqual(2, repo.ClearAll());
            Assert.AreEqual(0, repo.GetStats().TotalScans);
        }
    }
}
