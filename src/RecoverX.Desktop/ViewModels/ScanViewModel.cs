using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Services;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    /// <summary>One file in the results list.</summary>
    public sealed class ResultRow : ViewModelBase
    {
        private bool _selected;
        public ScanEntry Entry { get; }
        public string Name { get; }
        public string FolderText { get; }
        public string SizeText { get; }
        public string ModifiedText { get; }
        public string ConditionText { get; }
        public string ConditionTip { get; }
        public string ConditionColor { get; }
        public bool PathUncertain { get; }
        public string PathUncertainText { get; }

        public ResultRow(ScanEntry e, ILocalizer l)
        {
            Entry = e;
            Name = e.Name;
            int i = e.Path.LastIndexOf('\\');
            FolderText = i > 0 ? e.Path.Substring(0, i) : "\\";
            SizeText = ByteFormatter.Format(e.Size);
            ModifiedText = e.Modified.HasValue ? e.Modified.Value.ToLocalTime().ToString("g", l.Culture) : "-";
            ConditionText = l.Get(ConditionInfo.Key(e.Condition));
            ConditionTip = e.Condition == RecoveryCondition.Partial
                ? l.Format(ConditionInfo.TipKey(e.Condition), e.ClustersInUse, e.ClustersTotal)
                : l.Get(ConditionInfo.TipKey(e.Condition));
            PathUncertain = (e.Flags & EntryFlags.PathUncertain) != 0;
            PathUncertainText = PathUncertain ? l.Get("Scan_PathUncertain") : string.Empty;
            switch (e.Condition)
            {
                case RecoveryCondition.Verified: ConditionColor = "#16A34A"; break;
                case RecoveryCondition.Likely: ConditionColor = "#84CC16"; break;
                case RecoveryCondition.Partial: ConditionColor = "#F59E0B"; break;
                case RecoveryCondition.Lost: ConditionColor = "#EF4444"; break;
                default: ConditionColor = "#9CA3AF"; break;
            }
        }

        public bool IsSelected { get { return _selected; } set { SetProperty(ref _selected, value); } }
    }

    public sealed class OutcomeRow
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public string SavedPath { get; set; }
        public bool IsProblem { get; set; }
    }

    public enum ScanPageState { Idle, Scanning, Results }

    public sealed class ScanViewModel : ViewModelBase, IDisposable
    {
        private const int DisplayCap = 20000;

        private readonly IQuickScanService _scan;
        private readonly IRecoveryService _recovery;
        private readonly ISettingsService _settings;
        private readonly ICategoryRepository _categories;
        private readonly IScanLauncher _launcher;
        private readonly IDialogService _dialogs;
        private readonly IElevationService _elevation;
        private readonly ILocalizer _l;
        private readonly IAppLogger _log;

        private ScanPageState _state = ScanPageState.Idle;
        private INtfsScanSession _session;
        private DriveModel _drive;
        private IReadOnlyList<ScanEntry> _all = new List<ScanEntry>();
        private FilterResult _filtered = new FilterResult();
        private CancellationTokenSource _cts;
        private bool _busy, _recovering, _showRecoverableOnly = true, _preserveFolders = true, _showAdmin;
        private string _driveTitle = "", _phaseText = "", _progressText = "", _candidatesText = "", _summary = "", _error = "",
                       _destination = "", _selectedText = "", _recoveryText = "", _doneText = "", _sessionFolder;
        private double _progress, _recoveryProgress;

        public ObservableCollection<ResultRow> Rows { get; } = new ObservableCollection<ResultRow>();
        public ObservableCollection<string> Notices { get; } = new ObservableCollection<string>();
        public ObservableCollection<OutcomeRow> Outcomes { get; } = new ObservableCollection<OutcomeRow>();

        public ICommand CancelCommand { get; }
        public ICommand SelectLikelyCommand { get; }
        public ICommand ClearSelectionCommand { get; }
        public ICommand RecoverCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand GoToDrivesCommand { get; }
        public ICommand RestartAdminCommand { get; }

        public ScanViewModel(IQuickScanService scan, IRecoveryService recovery, ISettingsService settings, ICategoryRepository categories,
                             IScanLauncher launcher, IDialogService dialogs, IElevationService elevation, ILocalizer l, IAppLogger log)
        {
            _scan = scan; _recovery = recovery; _settings = settings; _categories = categories; _launcher = launcher;
            _dialogs = dialogs; _elevation = elevation; _l = l; _log = log;
            _destination = settings.Load().DefaultRecoveryDestination;

            CancelCommand = new RelayCommand(_ => { var c = _cts; if (c != null) c.Cancel(); }, _ => _busy || _recovering);
            SelectLikelyCommand = new RelayCommand(_ => SelectLikely(), _ => HasResults && !_recovering);
            ClearSelectionCommand = new RelayCommand(_ => { foreach (var r in Rows) r.IsSelected = false; }, _ => HasResults && !_recovering);
            RecoverCommand = new RelayCommand(async _ => await RecoverAsync(), _ => HasResults && !_recovering && SelectedCount > 0);
            OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => !string.IsNullOrEmpty(_sessionFolder));
            GoToDrivesCommand = new RelayCommand(_ => _launcher.RequestPicker());
            RestartAdminCommand = new RelayCommand(_ => RestartAdmin());

            _launcher.ScanRequested += d => { var ignored = StartScanAsync(d); };
        }

        // ---------------- state exposed to the view ----------------
        public bool IsIdle { get { return _state == ScanPageState.Idle; } }
        public bool IsScanning { get { return _state == ScanPageState.Scanning; } }
        public bool HasResults { get { return _state == ScanPageState.Results; } }
        public bool IsRecovering { get { return _recovering; } }
        public bool CanEditSelection { get { return HasResults && !_recovering; } }
        public bool ShowAdminButton { get { return _showAdmin; } }
        public bool HasError { get { return _error.Length > 0; } }
        public bool HasNotices { get { return Notices.Count > 0; } }
        public bool HasOutcomes { get { return Outcomes.Count > 0; } }
        public bool HasDoneText { get { return _doneText.Length > 0; } }
        public bool CanOpenFolder { get { return !string.IsNullOrEmpty(_sessionFolder); } }
        public string DriveTitle { get { return _driveTitle; } private set { SetProperty(ref _driveTitle, value); } }
        public string PhaseText { get { return _phaseText; } private set { SetProperty(ref _phaseText, value); } }
        public string ProgressText { get { return _progressText; } private set { SetProperty(ref _progressText, value); } }
        public string CandidatesText { get { return _candidatesText; } private set { SetProperty(ref _candidatesText, value); } }
        public double Progress { get { return _progress; } private set { SetProperty(ref _progress, value); } }
        public string Summary { get { return _summary; } private set { SetProperty(ref _summary, value); } }
        public string ErrorText { get { return _error; } private set { if (SetProperty(ref _error, value ?? string.Empty)) OnPropertyChanged(nameof(HasError)); } }
        public string SelectedText { get { return _selectedText; } private set { SetProperty(ref _selectedText, value); } }
        public string RecoveryText { get { return _recoveryText; } private set { SetProperty(ref _recoveryText, value); } }
        public double RecoveryProgress { get { return _recoveryProgress; } private set { SetProperty(ref _recoveryProgress, value); } }
        public string DoneText { get { return _doneText; } private set { if (SetProperty(ref _doneText, value ?? string.Empty)) OnPropertyChanged(nameof(HasDoneText)); } }
        public string Destination { get { return _destination; } set { SetProperty(ref _destination, value); } }
        public bool PreserveFolders { get { return _preserveFolders; } set { SetProperty(ref _preserveFolders, value); } }
        public string DestinationHint { get { return _l.Format("Scan_DestinationHint", _drive == null ? "the scanned drive" : _drive.Letter); } }
        public bool ShowRecoverableOnly
        {
            get { return _showRecoverableOnly; }
            set { if (SetProperty(ref _showRecoverableOnly, value) && HasResults) RebuildRows(); }
        }

        public int SelectedCount { get { return Rows.Count(r => r.IsSelected); } }

        private void SetState(ScanPageState s)
        {
            _state = s;
            OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(IsScanning)); OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(CanEditSelection));
        }

        private void SetBusy(bool busy, bool recovering)
        {
            _busy = busy; _recovering = recovering;
            OnPropertyChanged(nameof(IsRecovering)); OnPropertyChanged(nameof(CanEditSelection));
        }

        // ---------------- scanning ----------------
        private async Task StartScanAsync(DriveModel drive)
        {
            if (_busy || _recovering) return;
            _drive = drive;
            DriveTitle = _l.Format("Scan_Scanning", drive.Letter);
            OnPropertyChanged(nameof(DestinationHint));
            if (!_dialogs.Confirm(_l.Get("Scan_Confirm_Title"), _l.Format("Scan_Confirm_Body", drive.Letter))) return;

            DisposeSession();
            ResetResults();
            ErrorText = string.Empty; _showAdmin = false; OnPropertyChanged(nameof(ShowAdminButton));
            SetState(ScanPageState.Scanning);
            SetBusy(true, false);
            Progress = 0; ProgressText = string.Empty; CandidatesText = string.Empty; PhaseText = _l.Get("Scan_Phase_Opening");
            _cts = new CancellationTokenSource();
            var progress = new Progress<ScanProgress>(UpdateProgress);

            QuickScanOutcome outcome;
            try { outcome = await _scan.RunAsync(drive, _settings.Load(), progress, _cts.Token); }
            catch (Exception ex)
            {
                _log.Error("Scan failed unexpectedly", ex);
                outcome = new QuickScanOutcome { Status = EngineStatus.Internal, Detail = ex.Message };
            }
            finally { SetBusy(false, false); }

            if (!outcome.HasResults)
            {
                ErrorText = ScanError(outcome, drive);
                _showAdmin = outcome.Status == EngineStatus.AccessDenied && !_elevation.IsElevated;
                OnPropertyChanged(nameof(ShowAdminButton));
                SetState(ScanPageState.Idle);
                return;
            }

            _session = outcome.Session;
            _all = outcome.Entries;
            _filtered = ScanFilter.Apply(_all, _categories.GetAll(), _settings.Load());
            Notices.Clear();
            ScanProgress fp = outcome.FinalProgress ?? new ScanProgress();
            if (outcome.Cancelled) Notices.Add(_l.Get("Scan_Cancelled"));
            if (fp.BitmapUnavailable) Notices.Add(_l.Get("Scan_BitmapMissing"));
            if (fp.MftPartial) Notices.Add(_l.Get("Scan_MftPartial"));
            if (fp.BadRecords > 0) Notices.Add(_l.Format("Scan_BadRecords", fp.BadRecords));
            if (fp.Truncated) Notices.Add(_l.Format("Scan_Truncated", _all.Count));
            if (_filtered.HiddenBySettings > 0) Notices.Add(_l.Format("Scan_Hidden_Filters", _filtered.HiddenBySettings));
            OnPropertyChanged(nameof(HasNotices));
            SetState(ScanPageState.Results);
            RebuildRows();
        }

        private void UpdateProgress(ScanProgress p)
        {
            switch (p.Phase)
            {
                case ScanPhase.Scanning: PhaseText = _l.Get("Scan_Phase_Scanning"); break;
                case ScanPhase.Validating: PhaseText = _l.Get("Scan_Phase_Validating"); break;
                case ScanPhase.Paths: PhaseText = _l.Get("Scan_Phase_Paths"); break;
                case ScanPhase.Done: PhaseText = _l.Get("Scan_Phase_Done"); break;
                default: PhaseText = _l.Get("Scan_Phase_Opening"); break;
            }
            Progress = p.Fraction;
            ProgressText = p.Total > 0 ? _l.Format("Scan_Progress", p.Done, p.Total) : string.Empty;
            CandidatesText = p.Candidates > 0 ? _l.Format("Scan_Candidates", p.Candidates) : string.Empty;
        }

        private string ScanError(QuickScanOutcome o, DriveModel d)
        {
            switch (o.Status)
            {
                case EngineStatus.AccessDenied: return _l.Format("Scan_Err_Denied", d.Letter);
                case EngineStatus.NotNtfs: return _l.Format("Scan_Err_NotNtfs", d.Letter);
                case EngineStatus.Corrupt: return _l.Format("Scan_Err_Corrupt", d.Letter);
                case EngineStatus.NotFound: return _l.Format("Scan_Err_NotFound", d.Letter);
                case EngineStatus.Unavailable: return _l.Format("Scan_Err_Engine", o.Detail);
                default: return _l.Format("Scan_Failed", string.IsNullOrEmpty(o.Detail) ? _l.Format("Scan_Err_Generic", (int)o.Status) : o.Detail);
            }
        }

        private void ResetResults()
        {
            Rows.Clear(); Notices.Clear(); Outcomes.Clear();
            Summary = string.Empty; DoneText = string.Empty; RecoveryText = string.Empty; RecoveryProgress = 0; _sessionFolder = null;
            _all = new List<ScanEntry>(); _filtered = new FilterResult();
            OnPropertyChanged(nameof(HasNotices)); OnPropertyChanged(nameof(HasOutcomes)); OnPropertyChanged(nameof(CanOpenFolder));
            UpdateSelection();
        }

        private void RebuildRows()
        {
            foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
            Rows.Clear();
            var source = _showRecoverableOnly ? _filtered.Shown.Where(e => e.IsLikelyRecoverable).ToList() : _filtered.Shown;
            int hiddenCondition = _filtered.Shown.Count - source.Count;
            foreach (var e in source.Take(DisplayCap))
            {
                var row = new ResultRow(e, _l);
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
            }

            int verified = _filtered.Shown.Count(e => e.Condition == RecoveryCondition.Verified);
            int likely = _filtered.Shown.Count(e => e.Condition == RecoveryCondition.Likely);
            int risky = _filtered.Shown.Count - verified - likely;
            var lines = new List<string> { _l.Format("Scan_Summary", _all.Count, _drive == null ? "" : _drive.Letter, Rows.Count) };
            if (verified > 0) lines.Add(_l.Format("Scan_Summary_Verified", verified));
            if (likely > 0) lines.Add(_l.Format("Scan_Summary_Likely", likely));
            if (risky > 0) lines.Add(_l.Format("Scan_Summary_Risky", risky));
            if (hiddenCondition > 0) lines.Add(_l.Format("Scan_Hidden_Condition", hiddenCondition));
            if (source.Count > DisplayCap) lines.Add(_l.Format("Scan_DisplayCap", DisplayCap));
            Summary = string.Join("\n", lines);
            UpdateSelection();
        }

        // ---------------- selection ----------------
        private void OnRowChanged(object s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "IsSelected") UpdateSelection(); }

        private void SelectLikely()
        {
            foreach (var r in Rows) r.IsSelected = r.Entry.IsLikelyRecoverable && r.Entry.Size > 0;
        }

        private void UpdateSelection()
        {
            var sel = Rows.Where(r => r.IsSelected).ToList();
            long bytes = sel.Sum(r => r.Entry.Size);
            SelectedText = _l.Format("Scan_Selected", sel.Count, ByteFormatter.Format(bytes));
            OnPropertyChanged(nameof(SelectedCount));
        }

        // ---------------- recovery ----------------
        private async Task RecoverAsync()
        {
            var chosen = Rows.Where(r => r.IsSelected).Select(r => r.Entry).ToList();
            if (chosen.Count == 0 || _session == null || _drive == null) return;
            string dest = (Destination ?? string.Empty).Trim();
            if (dest.Length == 0) { DoneText = _l.Get("Recover_Err_Dest"); return; }

            long total = chosen.Sum(e => e.Size);
            int risky = chosen.Count(e => e.Condition == RecoveryCondition.Partial || e.Condition == RecoveryCondition.Lost);
            string body = _l.Format("Recover_Confirm_Body", chosen.Count, ByteFormatter.Format(total), dest, _drive.Letter);
            if (risky > 0) body += "\n\n" + _l.Format("Recover_Confirm_Risky", risky);
            if (!_dialogs.Confirm(_l.Get("Recover_Confirm_Title"), body)) return;

            SetBusy(false, true);
            Outcomes.Clear(); DoneText = string.Empty; RecoveryProgress = 0; _sessionFolder = null;
            OnPropertyChanged(nameof(HasOutcomes)); OnPropertyChanged(nameof(CanOpenFolder));
            _cts = new CancellationTokenSource();
            var progress = new Progress<RecoveryProgress>(p =>
            {
                RecoveryText = _l.Format("Recover_Progress", p.FileNumber, p.FileCount, p.CurrentName);
                RecoveryProgress = p.BytesTotal > 0 ? Math.Min(1.0, (double)p.BytesDone / p.BytesTotal) : (double)p.FileNumber / Math.Max(1, p.FileCount);
            });

            RecoverySummary sum;
            try
            {
                sum = await _recovery.RecoverAsync(_session, chosen,
                    new RecoveryRequest { SourceRoot = _drive.RootPath, DestinationDirectory = dest, PreserveFolders = PreserveFolders }, progress, _cts.Token);
            }
            catch (Exception ex)
            {
                _log.Error("Recovery failed unexpectedly", ex);
                sum = new RecoverySummary { Error = "Recover_Err_Create", ErrorDetail = ex.Message };
            }
            finally { SetBusy(false, false); RecoveryText = string.Empty; }

            if (sum.Error != null) { DoneText = RecoveryError(sum); return; }

            foreach (var it in sum.Items)
                Outcomes.Add(new OutcomeRow { Name = it.Entry.Path, Text = OutcomeText(it), SavedPath = it.SavedPath, IsProblem = it.Outcome != RecoveryOutcome.Verified && it.Outcome != RecoveryOutcome.Unverified });
            OnPropertyChanged(nameof(HasOutcomes));
            _sessionFolder = sum.SessionDirectory;
            OnPropertyChanged(nameof(CanOpenFolder));
            DoneText = (sum.Cancelled ? _l.Get("Recover_Cancelled") + "\n" : string.Empty)
                     + _l.Format("Recover_Done", sum.Succeeded, sum.NeedAttention, sum.Failed)
                     + "\n" + _l.Format("Recover_DoneFolder", sum.SessionDirectory);
        }

        private string RecoveryError(RecoverySummary s)
        {
            switch (s.Error)
            {
                case "Recover_Err_SameVolume": return _l.Format(s.Error, s.ErrorDetail);
                case "Recover_Err_Space": return _l.Format(s.Error, ByteFormatter.Format(s.NeededBytes), ByteFormatter.Format(s.AvailableBytes));
                case "Recover_Err_Create": return _l.Format(s.Error, s.ErrorDetail);
                default: return _l.Get(s.Error);
            }
        }

        private string OutcomeText(RecoveryItemResult r)
        {
            string reason = r.ReasonKey == null ? string.Empty : _l.Get(r.ReasonKey);
            switch (r.Outcome)
            {
                case RecoveryOutcome.Verified: return _l.Get("Out_Verified");
                case RecoveryOutcome.Unverified: return _l.Get("Out_Unverified");
                case RecoveryOutcome.Suspect: return _l.Format("Out_Suspect", reason);
                case RecoveryOutcome.Unsupported: return _l.Get("Out_Unsupported");
                case RecoveryOutcome.Cancelled: return _l.Get("Out_Cancelled");
                default: return _l.Format("Out_Failed", reason.Length > 0 ? reason : r.ReasonDetail);
            }
        }

        private void OpenFolder()
        {
            try { if (!string.IsNullOrEmpty(_sessionFolder) && Directory.Exists(_sessionFolder)) Process.Start("explorer.exe", "\"" + _sessionFolder + "\""); }
            catch (Exception ex) { _log.Warn("Open folder failed: " + ex.Message); }
        }

        private void RestartAdmin()
        {
            if (!_dialogs.Confirm(_l.Get("Admin_Confirm_Title"), _l.Get("Admin_Confirm_Body"))) return;
            if (!_elevation.RestartElevated()) _dialogs.Info(_l.Get("Admin_Confirm_Title"), _l.Get("Admin_Failed"));
        }

        private void DisposeSession()
        {
            var s = _session; _session = null;
            if (s != null) { try { s.Dispose(); } catch (Exception ex) { _log.Warn("Closing scan session: " + ex.Message); } }
        }

        public void Dispose()
        {
            var c = _cts; if (c != null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
            DisposeSession();
        }
    }
}
