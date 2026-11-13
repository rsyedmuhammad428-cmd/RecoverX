using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Services;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    /// <summary>One file in the results list. Display text is computed on first use: only visible rows are ever rendered.</summary>
    public sealed class ResultRow : ViewModelBase
    {
        private readonly ILocalizer _l;
        private readonly Action<ResultRow, bool> _selectionChanged;
        private bool _selected;
        private string _folder, _size, _date, _ratingText, _tip;

        public ScanEntry Entry { get; }

        public ResultRow(ScanEntry e, ILocalizer l, Action<ResultRow, bool> selectionChanged)
        {
            Entry = e; _l = l; _selectionChanged = selectionChanged;
        }

        public string Name { get { return Entry.Name; } }
        public string TypeText { get { return Entry.Extension.Length > 0 ? Entry.Extension.ToUpperInvariant() : "-"; } }
        public string FolderText
        {
            get
            {
                if (_folder == null) { int i = Entry.Path.LastIndexOf('\\'); _folder = i > 0 ? Entry.Path.Substring(0, i) : "\\"; }
                return _folder;
            }
        }
        public string SizeText { get { return _size ?? (_size = ByteFormatter.Format(Entry.Size)); } }
        public string DateText
        {
            get
            {
                if (_date == null)
                {
                    DateTime? d = Entry.DisplayDate;
                    _date = d.HasValue ? d.Value.ToString("g", _l.Culture) : "-";
                    if (d.HasValue && !Entry.Modified.HasValue && Entry.ContentTime.HasValue) _date += " *";   // date from inside the file
                }
                return _date;
            }
        }
        public string RatingText { get { return _ratingText ?? (_ratingText = _l.Get(ConditionInfo.RatingKey(Entry.Rating))); } }
        public string ConditionText { get { return _l.Get(ConditionInfo.Key(Entry.Condition)); } }
        public string ConditionTip
        {
            get
            {
                if (_tip != null) return _tip;
                ScanEntry e = Entry;
                string detail;
                if (e.Origin == EntryOrigin.Snapshot)
                    detail = _l.Format(ConditionInfo.SnapshotTipKey, e.SnapshotTime.HasValue ? e.SnapshotTime.Value.ToLocalTime().ToString("g", _l.Culture) : "-");
                else if (e.IsCarved) detail = _l.Format(ConditionInfo.CarvedTipKey(e), e.ClustersInUse, e.ClustersTotal);
                else if (e.Condition == RecoveryCondition.Partial) detail = _l.Format(ConditionInfo.TipKey(e.Condition), e.ClustersInUse, e.ClustersTotal);
                else detail = _l.Get(ConditionInfo.TipKey(e.Condition));
                _tip = detail + "\n" + _l.Get("Rating_Estimate");
                return _tip;
            }
        }
        public string RatingColor
        {
            get
            {
                switch (Entry.Rating)
                {
                    case RecoveryRating.Excellent: return Entry.Condition == RecoveryCondition.Verified ? "#16A34A" : "#84CC16";
                    case RecoveryRating.Partial: return "#F59E0B";
                    case RecoveryRating.Poor: return "#EF4444";
                    default: return "#9CA3AF";
                }
            }
        }
        public bool PathUncertain { get { return (Entry.Flags & (EntryFlags.PathUncertain | EntryFlags.NameGuessed)) != 0; } }
        public string PathUncertainText
        {
            get
            {
                if ((Entry.Flags & EntryFlags.NameGuessed) != 0) return _l.Get("Scan_NameGuessed");
                return PathUncertain ? _l.Get("Scan_PathUncertain") : string.Empty;
            }
        }

        public bool IsSelected
        {
            get { return _selected; }
            set { if (SetProperty(ref _selected, value) && _selectionChanged != null) _selectionChanged(this, value); }
        }
    }

    public sealed class OutcomeRow
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public string SavedPath { get; set; }
        public bool IsProblem { get; set; }
    }

    /// <summary>A localized choice for a filter combo box.</summary>
    public sealed class Choice<T>
    {
        public Choice(T value, string text) { Value = value; Text = text; }
        public T Value { get; }
        public string Text { get; }
        public override string ToString() { return Text; }
    }

    public enum ScanPageState { Idle, Scanning, Results }

    public sealed class ScanViewModel : ViewModelBase, IDisposable
    {
        private readonly IScanService _scan;
        private readonly IRecoveryService _recovery;
        private readonly IPreviewService _preview;
        private readonly ISettingsService _settings;
        private readonly ICategoryRepository _categories;
        private readonly IScanLauncher _launcher;
        private readonly IDialogService _dialogs;
        private readonly IElevationService _elevation;
        private readonly ILocalizer _l;
        private readonly IAppLogger _log;
        private readonly DispatcherTimer _searchTimer;

        private ScanPageState _state = ScanPageState.Idle;
        private ScanMode _mode;
        private IScanSession _session, _running;
        private DriveModel _drive;
        private IList<FileCategory> _cats = new List<FileCategory>();
        private List<ResultRow> _allRows = new List<ResultRow>();
        private IReadOnlyList<ResultRow> _rows = new List<ResultRow>();
        private CancellationTokenSource _cts, _previewCts;
        private bool _busy, _recovering, _preserveFolders = true, _showAdmin, _paused, _sortDescending;
        private string _driveTitle = "", _phaseText = "", _progressText = "", _candidatesText = "", _summary = "", _error = "",
                       _destination = "", _selectedText = "", _recoveryText = "", _doneText = "", _sessionFolder, _search = "",
                       _previewTitle = "", _previewText = "", _previewMessage = "", _previewWarning = "";
        private double _progress, _recoveryProgress;
        private int _selectedCount;
        private long _selectedBytes;
        private DateTime? _dateFrom, _dateTo;
        private Choice<string> _category;
        private Choice<RatingFilter> _rating;
        private Choice<SizeBucket> _size;
        private Choice<ResultSort> _sort;
        private ResultRow _current;
        private BitmapSource _previewImage;

        public ObservableCollection<string> Notices { get; } = new ObservableCollection<string>();
        public ObservableCollection<OutcomeRow> Outcomes { get; } = new ObservableCollection<OutcomeRow>();
        public ObservableCollection<Choice<string>> CategoryChoices { get; } = new ObservableCollection<Choice<string>>();
        public IList<Choice<RatingFilter>> RatingChoices { get; }
        public IList<Choice<SizeBucket>> SizeChoices { get; }
        public IList<Choice<ResultSort>> SortChoices { get; }

        public ICommand CancelCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand ResumeCommand { get; }
        public ICommand SelectShownCommand { get; }
        public ICommand SelectExcellentCommand { get; }
        public ICommand ClearSelectionCommand { get; }
        public ICommand ResetFiltersCommand { get; }
        public ICommand RecoverCommand { get; }
        public ICommand BrowseCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand GoToDrivesCommand { get; }
        public ICommand RestartAdminCommand { get; }
        public ICommand PreviewCommand { get; }

        public ScanViewModel(IScanService scan, IRecoveryService recovery, IPreviewService preview, ISettingsService settings, ICategoryRepository categories,
                             IScanLauncher launcher, IDialogService dialogs, IElevationService elevation, ILocalizer l, IAppLogger log)
        {
            _scan = scan; _recovery = recovery; _preview = preview; _settings = settings; _categories = categories; _launcher = launcher;
            _dialogs = dialogs; _elevation = elevation; _l = l; _log = log;
            _destination = settings.Load().DefaultRecoveryDestination;

            RatingChoices = new List<Choice<RatingFilter>>
            {
                new Choice<RatingFilter>(RatingFilter.All, l.Get("Filter_AllRatings")),
                new Choice<RatingFilter>(RatingFilter.Excellent, l.Get("Rating_Excellent")),
                new Choice<RatingFilter>(RatingFilter.Partial, l.Get("Rating_Partial")),
                new Choice<RatingFilter>(RatingFilter.Poor, l.Get("Rating_Poor"))
            };
            SizeChoices = new List<Choice<SizeBucket>>
            {
                new Choice<SizeBucket>(SizeBucket.Any, l.Get("Filter_AnySize")),
                new Choice<SizeBucket>(SizeBucket.Under1Mb, l.Get("Filter_Under1Mb")),
                new Choice<SizeBucket>(SizeBucket.From1To100Mb, l.Get("Filter_1To100Mb")),
                new Choice<SizeBucket>(SizeBucket.Over100Mb, l.Get("Filter_Over100Mb"))
            };
            SortChoices = new List<Choice<ResultSort>>
            {
                new Choice<ResultSort>(ResultSort.Name, l.Get("Sort_Name")),
                new Choice<ResultSort>(ResultSort.Size, l.Get("Sort_Size")),
                new Choice<ResultSort>(ResultSort.Date, l.Get("Sort_Date")),
                new Choice<ResultSort>(ResultSort.Rating, l.Get("Sort_Rating")),
                new Choice<ResultSort>(ResultSort.Type, l.Get("Sort_Type"))
            };
            _rating = RatingChoices[0]; _size = SizeChoices[0]; _sort = SortChoices[0];

            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplyQuery(); };

            CancelCommand = new RelayCommand(_ => { var c = _cts; if (c != null) c.Cancel(); }, _ => _busy || _recovering);
            PauseCommand = new RelayCommand(_ => SetPaused(true), _ => _busy && !_paused);
            ResumeCommand = new RelayCommand(_ => SetPaused(false), _ => _busy && _paused);
            SelectShownCommand = new RelayCommand(_ => SelectShown(r => true), _ => HasResults && !_recovering);
            SelectExcellentCommand = new RelayCommand(_ => SelectShown(r => r.Entry.Rating == RecoveryRating.Excellent && r.Entry.Size > 0), _ => HasResults && !_recovering);
            ClearSelectionCommand = new RelayCommand(_ => { foreach (var r in _allRows) r.IsSelected = false; }, _ => HasResults && !_recovering);
            ResetFiltersCommand = new RelayCommand(_ => ResetFilters(), _ => HasResults);
            RecoverCommand = new RelayCommand(async _ => await RecoverAsync(), _ => HasResults && !_recovering && _selectedCount > 0);
            BrowseCommand = new RelayCommand(_ => Browse(), _ => !_recovering);
            OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => !string.IsNullOrEmpty(_sessionFolder));
            GoToDrivesCommand = new RelayCommand(_ => _launcher.RequestPicker());
            RestartAdminCommand = new RelayCommand(_ => RestartAdmin());
            PreviewCommand = new RelayCommand(p => { var r = p as ResultRow; if (r != null) CurrentRow = r; }, p => p is ResultRow);

            _launcher.ScanRequested += r => { var ignored = StartScanAsync(r.Drive, r.Mode); };
        }

        // ---------------- state exposed to the view ----------------
        public bool IsIdle { get { return _state == ScanPageState.Idle; } }
        public bool IsScanning { get { return _state == ScanPageState.Scanning; } }
        public bool HasResults { get { return _state == ScanPageState.Results; } }
        public bool IsRecovering { get { return _recovering; } }
        public bool IsPaused { get { return _paused; } }
        public bool CanEditSelection { get { return HasResults && !_recovering; } }
        public bool ShowAdminButton { get { return _showAdmin; } }
        public bool HasError { get { return _error.Length > 0; } }
        public bool HasNotices { get { return Notices.Count > 0; } }
        public bool HasOutcomes { get { return Outcomes.Count > 0; } }
        public bool HasDoneText { get { return _doneText.Length > 0; } }
        public bool CanOpenFolder { get { return !string.IsNullOrEmpty(_sessionFolder); } }
        public bool IsDeep { get { return _mode == ScanMode.Deep; } }
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
        public string DestinationHint { get { return _l.Format("Scan_DestinationHint", _drive == null ? "-" : _drive.Letter); } }
        public IReadOnlyList<ResultRow> Rows { get { return _rows; } private set { SetProperty(ref _rows, value); OnPropertyChanged(nameof(ShownText)); } }
        public string ShownText { get { return _l.Format("Filter_Shown", _rows.Count, _allRows.Count); } }

        // ---------------- search, filters, sort ----------------
        public string SearchText
        {
            get { return _search; }
            set { if (SetProperty(ref _search, value ?? string.Empty)) { _searchTimer.Stop(); _searchTimer.Start(); } }
        }
        public Choice<string> SelectedCategory { get { return _category; } set { if (SetProperty(ref _category, value)) ApplyQuery(); } }
        public Choice<RatingFilter> SelectedRating { get { return _rating; } set { if (SetProperty(ref _rating, value)) ApplyQuery(); } }
        public Choice<SizeBucket> SelectedSize { get { return _size; } set { if (SetProperty(ref _size, value)) ApplyQuery(); } }
        public Choice<ResultSort> SelectedSort { get { return _sort; } set { if (SetProperty(ref _sort, value)) ApplyQuery(); } }
        public bool SortDescending { get { return _sortDescending; } set { if (SetProperty(ref _sortDescending, value)) ApplyQuery(); } }
        public DateTime? DateFrom { get { return _dateFrom; } set { if (SetProperty(ref _dateFrom, value)) ApplyQuery(); } }
        public DateTime? DateTo { get { return _dateTo; } set { if (SetProperty(ref _dateTo, value)) ApplyQuery(); } }

        // ---------------- preview ----------------
        public ResultRow CurrentRow { get { return _current; } set { if (SetProperty(ref _current, value)) { var ignored = LoadPreviewAsync(value); } } }
        public BitmapSource PreviewImage { get { return _previewImage; } private set { SetProperty(ref _previewImage, value); OnPropertyChanged(nameof(HasPreviewImage)); OnPropertyChanged(nameof(ShowPreviewHint)); } }
        public bool ShowPreviewHint { get { return !HasPreviewImage && !HasPreviewText && !HasPreviewMessage; } }
        public bool HasPreviewImage { get { return _previewImage != null; } }
        public string PreviewTitle { get { return _previewTitle; } private set { SetProperty(ref _previewTitle, value); } }
        public string PreviewText { get { return _previewText; } private set { SetProperty(ref _previewText, value ?? string.Empty); OnPropertyChanged(nameof(HasPreviewText)); OnPropertyChanged(nameof(ShowPreviewHint)); } }
        public bool HasPreviewText { get { return _previewText.Length > 0; } }
        public string PreviewMessage { get { return _previewMessage; } private set { SetProperty(ref _previewMessage, value ?? string.Empty); OnPropertyChanged(nameof(HasPreviewMessage)); OnPropertyChanged(nameof(ShowPreviewHint)); } }
        public bool HasPreviewMessage { get { return _previewMessage.Length > 0; } }
        public string PreviewWarning { get { return _previewWarning; } private set { SetProperty(ref _previewWarning, value ?? string.Empty); OnPropertyChanged(nameof(HasPreviewWarning)); } }
        public bool HasPreviewWarning { get { return _previewWarning.Length > 0; } }

        private void SetState(ScanPageState s)
        {
            _state = s;
            OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(IsScanning)); OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(CanEditSelection));
        }

        private void SetBusy(bool busy, bool recovering)
        {
            _busy = busy; _recovering = recovering;
            OnPropertyChanged(nameof(IsRecovering)); OnPropertyChanged(nameof(CanEditSelection));
            CommandManager.InvalidateRequerySuggested();
        }

        // ---------------- scanning ----------------
        private async Task StartScanAsync(DriveModel drive, ScanMode mode)
        {
            if (_busy || _recovering) return;
            _drive = drive; _mode = mode;
            OnPropertyChanged(nameof(IsDeep));
            DriveTitle = _l.Format(mode == ScanMode.Deep ? "Deep_Scanning" : mode == ScanMode.PreviousVersions ? "Snap_Scanning" : "Scan_Scanning", drive.Letter);
            OnPropertyChanged(nameof(DestinationHint));
            bool ok = mode == ScanMode.Deep
                ? _dialogs.Confirm(_l.Get("Deep_Confirm_Title"), _l.Format("Deep_Confirm_Body", drive.Letter, drive.TotalDisplay))
                : mode == ScanMode.PreviousVersions
                    ? _dialogs.Confirm(_l.Get("Snap_Confirm_Title"), _l.Format("Snap_Confirm_Body", drive.Letter))
                    : _dialogs.Confirm(_l.Get("Scan_Confirm_Title"), _l.Format("Scan_Confirm_Body", drive.Letter));
            if (!ok) return;

            DisposeSession();
            ResetResults();
            ErrorText = string.Empty; _showAdmin = false; OnPropertyChanged(nameof(ShowAdminButton));
            _paused = false; OnPropertyChanged(nameof(IsPaused));
            SetState(ScanPageState.Scanning);
            SetBusy(true, false);
            Progress = 0; ProgressText = string.Empty; CandidatesText = string.Empty; PhaseText = _l.Get("Scan_Phase_Opening");
            _cts = new CancellationTokenSource();
            var progress = new Progress<ScanProgress>(UpdateProgress);
            AppSettings settings = _settings.Load();

            ScanOutcome outcome;
            try { outcome = await _scan.RunAsync(drive, mode, settings, progress, _cts.Token, s => _running = s); }
            catch (Exception ex)
            {
                _log.Error("Scan failed unexpectedly", ex);
                outcome = new ScanOutcome { Mode = mode, Status = EngineStatus.Internal, Detail = ex.Message };
            }
            finally { _running = null; _paused = false; OnPropertyChanged(nameof(IsPaused)); SetBusy(false, false); }

            if (!outcome.HasResults)
            {
                ErrorText = ScanError(outcome, drive);
                _showAdmin = outcome.Status == EngineStatus.AccessDenied && !_elevation.IsElevated;
                OnPropertyChanged(nameof(ShowAdminButton));
                SetState(ScanPageState.Idle);
                return;
            }

            _session = outcome.Session;
            _cats = _categories.GetAll();
            FilterResult filtered = ScanFilter.Apply(outcome.Entries, _cats, settings);
            _allRows = filtered.Shown.Select(e => new ResultRow(e, _l, OnRowSelection)).ToList();
            BuildCategoryChoices();
            BuildNotices(outcome, filtered, settings);
            SetState(ScanPageState.Results);
            ApplyQuery();
            UpdateSummary(outcome.Entries.Count);
            UpdateSelection();
        }

        private void SetPaused(bool paused)
        {
            var s = _running;
            if (s == null) return;
            try { s.SetPaused(paused); } catch (ObjectDisposedException) { return; }
            _paused = paused;
            OnPropertyChanged(nameof(IsPaused));
            PhaseText = _l.Get(paused ? "Scan_Paused" : "Scan_Phase_Scanning");
            CommandManager.InvalidateRequerySuggested();
        }

        private void UpdateProgress(ScanProgress p)
        {
            if (p.Paused || _paused) PhaseText = _l.Get("Scan_Paused");
            else if (p.Mode == ScanMode.PreviousVersions) PhaseText = _l.Get(p.IsDone ? "Scan_Phase_Done" : "Snap_Phase");
            else if (p.Mode == ScanMode.Deep) PhaseText = _l.Get(p.Phase == ScanPhase.Done ? "Scan_Phase_Done" : p.Phase == ScanPhase.Opening ? "Scan_Phase_Opening" : "Deep_Phase_Scanning");
            else switch (p.Phase)
            {
                case ScanPhase.Scanning: PhaseText = _l.Get("Scan_Phase_Scanning"); break;
                case ScanPhase.Validating: PhaseText = _l.Get("Scan_Phase_Validating"); break;
                case ScanPhase.Paths: PhaseText = _l.Get("Scan_Phase_Paths"); break;
                case ScanPhase.Done: PhaseText = _l.Get("Scan_Phase_Done"); break;
                default: PhaseText = _l.Get("Scan_Phase_Opening"); break;
            }
            Progress = p.Fraction;
            if (p.Total > 0)
                ProgressText = p.CountsBytes
                    ? _l.Format("Deep_Progress", ByteFormatter.Format(p.Done), ByteFormatter.Format(p.Total), (int)(p.Fraction * 100))
                    : p.Mode == ScanMode.PreviousVersions ? _l.Format("Snap_Progress", p.Done, p.Total) : _l.Format("Scan_Progress", p.Done, p.Total);
            else ProgressText = string.Empty;
            CandidatesText = p.Candidates > 0 ? _l.Format(p.Mode == ScanMode.Deep ? "Deep_Found" : p.Mode == ScanMode.PreviousVersions ? "Snap_Found" : "Scan_Candidates", p.Candidates) : string.Empty;
        }

        private string ScanError(ScanOutcome o, DriveModel d)
        {
            switch (o.Status)
            {
                case EngineStatus.AccessDenied: return _l.Format("Scan_Err_Denied", d.Letter);
                case EngineStatus.NotNtfs: return _l.Format("Scan_Err_NotNtfs", d.Letter);
                case EngineStatus.NotFat: return _l.Format("Scan_Err_NotFat", d.Letter);
                case EngineStatus.Corrupt: return _l.Format("Scan_Err_Corrupt", d.Letter);
                case EngineStatus.NotFound: return _l.Format(o.Mode == ScanMode.PreviousVersions ? "Snap_None" : "Scan_Err_NotFound", d.Letter);
                case EngineStatus.Unsupported: return _l.Format(o.Mode == ScanMode.PreviousVersions ? "Snap_NotSupported" : "Scan_Err_Generic", o.Mode == ScanMode.PreviousVersions ? (object)d.Letter : (int)o.Status);
                case EngineStatus.Unavailable: return _l.Format("Scan_Err_Engine", o.Detail);
                default: return _l.Format("Scan_Failed", string.IsNullOrEmpty(o.Detail) ? _l.Format("Scan_Err_Generic", (int)o.Status) : o.Detail);
            }
        }

        private void BuildNotices(ScanOutcome outcome, FilterResult filtered, AppSettings settings)
        {
            Notices.Clear();
            ScanProgress fp = outcome.FinalProgress ?? new ScanProgress();
            if (outcome.Cancelled) Notices.Add(_l.Get("Scan_Cancelled"));
            if (outcome.Mode == ScanMode.PreviousVersions)
            {
                var snaps = outcome.Session as SnapshotScanSession;
                Notices.Add(_l.Format("Snap_Info", snaps == null ? 0 : snaps.SnapshotCount));
                if (fp.BadRecords > 0) Notices.Add(_l.Format("Snap_Unreadable", fp.BadRecords));
            }
            else if (outcome.Mode == ScanMode.Deep)
            {
                bool ntfs = ScanService.SupportsQuickScan(_drive);
                if (settings.DeepScanIncludeUsedSpace) Notices.Add(_l.Get("Deep_AllSpace"));
                else if (fp.BitmapUnavailable) Notices.Add(_l.Get(ntfs ? "Deep_NoMapNtfs" : "Deep_NoMap"));
                if (fp.ReadErrors > 0) Notices.Add(_l.Format("Deep_ReadErrors", fp.ReadErrors));
                Notices.Add(_l.Get("Deep_Contiguous"));
            }
            else
            {
                if (fp.BitmapUnavailable) Notices.Add(_l.Get("Scan_BitmapMissing"));
                if (ScanService.IsFatFamily(_drive) && !string.Equals(_drive.FileSystem, "exFAT", StringComparison.OrdinalIgnoreCase))
                    Notices.Add(_l.Get("Fat_Contiguous"));
                if (fp.MftPartial) Notices.Add(_l.Get("Scan_MftPartial"));
                if (fp.BadRecords > 0) Notices.Add(_l.Format("Scan_BadRecords", fp.BadRecords));
            }
            if (fp.Truncated) Notices.Add(_l.Format("Scan_Truncated", outcome.Entries.Count));
            string warn = DriveClassifier.WarningKey(_drive);
            if (warn != null) Notices.Add(_l.Get(warn));
            if (filtered.HiddenBySettings > 0) Notices.Add(_l.Format("Scan_Hidden_Filters", filtered.HiddenBySettings));
            OnPropertyChanged(nameof(HasNotices));
        }

        private void UpdateSummary(int foundTotal)
        {
            int excellent = 0, partial = 0, poor = 0, other = 0;
            foreach (var r in _allRows)
                switch (r.Entry.Rating)
                {
                    case RecoveryRating.Excellent: excellent++; break;
                    case RecoveryRating.Partial: partial++; break;
                    case RecoveryRating.Poor: poor++; break;
                    default: other++; break;
                }
            string key = _mode == ScanMode.Deep ? "Deep_Summary" : _mode == ScanMode.PreviousVersions ? "Snap_Summary" : "Scan_Summary_All";
            var lines = new List<string> { _l.Format(key, foundTotal, _drive == null ? "" : _drive.Letter) };
            lines.Add(_l.Format("Scan_Summary_Ratings", excellent, partial, poor, other));
            lines.Add(_l.Get("Rating_Estimate"));
            Summary = string.Join("\n", lines);
        }

        private void ResetResults()
        {
            CancelPreview();
            foreach (var r in _allRows) r.IsSelected = false;
            _allRows = new List<ResultRow>();
            Rows = new List<ResultRow>();
            Notices.Clear(); Outcomes.Clear(); CategoryChoices.Clear();
            Summary = string.Empty; DoneText = string.Empty; RecoveryText = string.Empty; RecoveryProgress = 0; _sessionFolder = null;
            _selectedCount = 0; _selectedBytes = 0;
            _current = null; OnPropertyChanged(nameof(CurrentRow)); ClearPreview();
            OnPropertyChanged(nameof(HasNotices)); OnPropertyChanged(nameof(HasOutcomes)); OnPropertyChanged(nameof(CanOpenFolder));
            UpdateSelection();
        }

        private void BuildCategoryChoices()
        {
            CategoryChoices.Clear();
            CategoryChoices.Add(new Choice<string>(null, _l.Get("Filter_AllTypes")));
            foreach (var c in _cats.Where(c => c.Enabled).OrderBy(c => c.SortOrder)) CategoryChoices.Add(new Choice<string>(c.Id, _l.Get("Cat_" + c.Id)));
            _category = CategoryChoices[0];
            OnPropertyChanged(nameof(SelectedCategory));
        }

        private void ResetFilters()
        {
            _search = string.Empty; OnPropertyChanged(nameof(SearchText));
            _category = CategoryChoices.Count > 0 ? CategoryChoices[0] : null; OnPropertyChanged(nameof(SelectedCategory));
            _rating = RatingChoices[0]; OnPropertyChanged(nameof(SelectedRating));
            _size = SizeChoices[0]; OnPropertyChanged(nameof(SelectedSize));
            _dateFrom = null; _dateTo = null; OnPropertyChanged(nameof(DateFrom)); OnPropertyChanged(nameof(DateTo));
            ApplyQuery();
        }

        private void ApplyQuery()
        {
            if (!HasResults) return;
            var q = new ResultQuery
            {
                Search = _search, CategoryId = _category == null ? null : _category.Value,
                Rating = _rating == null ? RatingFilter.All : _rating.Value, Size = _size == null ? SizeBucket.Any : _size.Value,
                From = _dateFrom, To = _dateTo, SortBy = _sort == null ? ResultSort.Name : _sort.Value, Descending = _sortDescending
            };
            var byEntry = new Dictionary<ScanEntry, ResultRow>(_allRows.Count);
            foreach (var r in _allRows) byEntry[r.Entry] = r;
            List<ScanEntry> shown = ResultQuery.Apply(_allRows.Select(r => r.Entry), q, _cats);
            Rows = shown.Select(e => byEntry[e]).ToList();
        }

        // ---------------- selection (kept across filter changes) ----------------
        private void OnRowSelection(ResultRow row, bool selected)
        {
            _selectedCount += selected ? 1 : -1;
            _selectedBytes += selected ? row.Entry.Size : -row.Entry.Size;
            UpdateSelection();
        }

        private void SelectShown(Func<ResultRow, bool> pick)
        {
            foreach (var r in _rows) if (pick(r)) r.IsSelected = true;
        }

        private void UpdateSelection()
        {
            SelectedText = _l.Format("Scan_Selected", _selectedCount, ByteFormatter.Format(_selectedBytes));
            CommandManager.InvalidateRequerySuggested();
        }

        // ---------------- preview ----------------
        private void CancelPreview()
        {
            var c = _previewCts; _previewCts = null;
            if (c != null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
        }

        private void ClearPreview()
        {
            PreviewImage = null; PreviewText = string.Empty; PreviewMessage = string.Empty; PreviewWarning = string.Empty;
            PreviewTitle = string.Empty;
        }

        private async Task LoadPreviewAsync(ResultRow row)
        {
            CancelPreview();
            ClearPreview();
            if (row == null || _session == null) return;
            PreviewTitle = row.Name;
            if (!_preview.CanPreview(row.Entry)) { PreviewMessage = _l.Get("Preview_NoData"); return; }
            if (row.Entry.Rating != RecoveryRating.Excellent) PreviewWarning = _l.Get("Preview_DamagedWarning");
            var cts = new CancellationTokenSource();
            _previewCts = cts;
            PreviewMessage = _l.Get("Preview_Loading");
            try
            {
                PreviewContent c = await _preview.LoadAsync(_session, row.Entry, _settings.Load(), cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(row, _current)) return;
                PreviewMessage = string.Empty;
                if (c.TypeMismatch) PreviewWarning = _l.Format("Preview_TypeMismatch", row.Entry.Extension.ToUpperInvariant());
                switch (c.Kind)
                {
                    case PreviewKind.Image:
                        BitmapSource img = await Task.Run(() => Decode(c.Data), cts.Token);
                        if (cts.IsCancellationRequested || !ReferenceEquals(row, _current)) return;
                        if (img != null) PreviewImage = img; else PreviewMessage = _l.Get("Preview_DecodeFailed");
                        break;
                    case PreviewKind.Text:
                        PreviewText = c.Text + (c.Truncated ? "\n\n" + _l.Get("Preview_TextTruncated") : string.Empty);
                        if (c.Text.Length == 0) PreviewMessage = _l.Get("Preview_Empty");
                        break;
                    case PreviewKind.Pdf: PreviewText = PdfText(c.Pdf); break;
                    case PreviewKind.Hex:
                        PreviewText = _l.Get("Preview_HexHeader") + "\n\n" + c.Text + (c.Truncated ? "\n" + _l.Get("Preview_HexTruncated") : string.Empty);
                        break;
                    case PreviewKind.Erased: PreviewMessage = _l.Format("Preview_Erased", c.Detail); break;
                    case PreviewKind.Disabled: PreviewMessage = _l.Get("Preview_Disabled"); break;
                    case PreviewKind.TooLarge: PreviewMessage = _l.Format("Preview_TooLarge", c.Detail); break;
                    case PreviewKind.NoData: PreviewMessage = _l.Get("Preview_NoData"); break;
                    case PreviewKind.ReadFailed: PreviewMessage = _l.Format("Preview_ReadFailed", c.Detail); break;
                    default: PreviewMessage = _l.Get("Preview_NotSupported"); break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Warn("Preview failed: " + ex.Message);
                if (ReferenceEquals(row, _current)) PreviewMessage = _l.Format("Preview_ReadFailed", ex.Message);
            }
        }

        private string PdfText(PdfSummary p)
        {
            var lines = new List<string>
            {
                _l.Format("Preview_Pdf_Version", p.Version),
                _l.Format("Preview_Pdf_Pages", p.Pages > 0 ? p.Pages.ToString(_l.Culture) : "?")
            };
            if (!string.IsNullOrEmpty(p.Title)) lines.Add(_l.Format("Preview_Pdf_Title", p.Title));
            if (!string.IsNullOrEmpty(p.Author)) lines.Add(_l.Format("Preview_Pdf_Author", p.Author));
            if (!string.IsNullOrEmpty(p.Producer)) lines.Add(_l.Format("Preview_Pdf_Producer", p.Producer));
            if (p.EncryptedOrEncoded) lines.Add(_l.Get("Preview_Pdf_Encrypted"));
            lines.Add(string.Empty);
            lines.Add(p.TextSample.Length > 0 ? _l.Get("Preview_Pdf_TextHeader") + "\n" + p.TextSample : _l.Get("Preview_Pdf_NoText"));
            return string.Join("\n", lines);
        }

        private const long MaxPreviewPixels = 100L * 1000 * 1000;   // 100 megapixels, well above any camera photo

        /// <summary>Decodes on a worker thread; large images are scaled down. Damaged data returns null instead of throwing.</summary>
        private static BitmapSource Decode(byte[] data)
        {
            try
            {
                int width;
                using (var probe = new MemoryStream(data, false))
                {
                    var dec = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    width = dec.Frames[0].PixelWidth;
                    // Recovered data is untrusted: a damaged or crafted header can declare enormous dimensions ("decompression bomb").
                    if ((long)width * dec.Frames[0].PixelHeight > MaxPreviewPixels) return null;
                }
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (width > 1200) bi.DecodePixelWidth = 1200;
                bi.StreamSource = new MemoryStream(data, false);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception ex) when (ex is NotSupportedException || ex is FileFormatException || ex is ArgumentException || ex is InvalidOperationException
                                       || ex is OverflowException || ex is IOException || ex is System.Runtime.InteropServices.COMException || ex is IndexOutOfRangeException)
            {
                return null;
            }
        }

        // ---------------- recovery ----------------
        private void Browse()
        {
            string picked = _dialogs.PickFolder(_l.Get("Scan_Browse_Title"), Destination);
            if (!string.IsNullOrEmpty(picked)) Destination = picked;
        }

        private async Task RecoverAsync()
        {
            var chosen = _allRows.Where(r => r.IsSelected).Select(r => r.Entry).ToList();
            if (chosen.Count == 0 || _session == null || _drive == null) return;
            string dest = (Destination ?? string.Empty).Trim();
            if (dest.Length == 0) { DoneText = _l.Get("Recover_Err_Dest"); return; }

            long total = chosen.Sum(e => e.Size);
            int risky = chosen.Count(e => e.Rating == RecoveryRating.Partial || e.Rating == RecoveryRating.Poor);
            string body = _l.Format("Recover_Confirm_Body", chosen.Count, ByteFormatter.Format(total), dest, _drive.Letter);
            if (risky > 0) body += "\n\n" + _l.Format("Recover_Confirm_Risky", risky);
            if (!_dialogs.Confirm(_l.Get("Recover_Confirm_Title"), body)) return;

            CancelPreview();
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
            CancelPreview();
            var s = _session; _session = null;
            if (s != null) { try { s.Dispose(); } catch (Exception ex) { _log.Warn("Closing scan session: " + ex.Message); } }
        }

        public void Dispose()
        {
            var c = _cts; if (c != null) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
            var running = _running; if (running != null) { try { running.Dispose(); } catch (Exception) { } }
            DisposeSession();
        }
    }
}
