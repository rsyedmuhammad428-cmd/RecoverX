using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;
using RecoverX.Core.Services;
using RecoverX.Desktop.Services;

namespace RecoverX.Desktop.ViewModels
{
    public sealed class CategoryEditItem : ViewModelBase
    {
        private bool _enabled;
        private string _extensionsText;
        public FileCategory Source { get; }
        public string DisplayName { get; }
        public string CatchAllNote { get; }
        public bool IsCatchAll { get { return Source.IsCatchAll; } }
        public bool IsEditable { get { return !Source.IsCatchAll; } }
        public bool Enabled { get { return _enabled; } set { SetProperty(ref _enabled, value); } }
        public string ExtensionsText { get { return _extensionsText; } set { SetProperty(ref _extensionsText, value); } }

        public CategoryEditItem(FileCategory source, ILocalizer l)
        {
            Source = source;
            DisplayName = l.Get("Cat_" + source.Id);
            CatchAllNote = l.Get("Set_Cat_CatchAll");
            _enabled = source.Enabled;
            _extensionsText = string.Join(", ", source.Extensions);
        }
    }

    public sealed class SettingsViewModel : ViewModelBase
    {
        private readonly ISettingsService _settings;
        private readonly ICategoryRepository _categories;
        private readonly IScanHistoryRepository _history;
        private readonly IThemeService _theme;
        private readonly IDriveService _drives;
        private readonly IDestinationValidator _validator;
        private readonly ILocalizer _l;
        private readonly IAppLogger _log;
        private readonly AppSettings _current;

        private string _destination, _minSizeText, _previewMaxText, _historyMaxText;
        private bool _previewEnabled;
        private string _destMessage = "", _prefsMessage = "", _catMessage = "", _logsMessage = "";

        public ICommand SaveDestinationCommand { get; }
        public ICommand SavePreferencesCommand { get; }
        public ICommand SaveCategoriesCommand { get; }
        public ICommand OpenLogsCommand { get; }

        public ObservableCollection<CategoryEditItem> Categories { get; } = new ObservableCollection<CategoryEditItem>();

        public string LogsPathLine { get { return _l.Format("Set_LogsPath", AppPaths.LogsDirectory); } }
        public string DatabasePathLine { get { return _l.Format("Set_DbPath", AppPaths.DatabasePath); } }
        public string PreviewNote { get { return _l.Get("Set_Preview_Note"); } }

        public bool IsDarkTheme { get { return _current.Theme == AppSettings.DarkTheme; } set { if (value) SetTheme(AppSettings.DarkTheme); } }
        public bool IsLightTheme { get { return _current.Theme == AppSettings.LightTheme; } set { if (value) SetTheme(AppSettings.LightTheme); } }
        public bool IsQuickDefault { get { return _current.DefaultScanType == AppSettings.QuickScan; } set { if (value) SetScanType(AppSettings.QuickScan); } }
        public bool IsDeepDefault { get { return _current.DefaultScanType == AppSettings.DeepScan; } set { if (value) SetScanType(AppSettings.DeepScan); } }

        public string Destination { get { return _destination; } set { SetProperty(ref _destination, value); } }
        public string MinFileSizeText { get { return _minSizeText; } set { SetProperty(ref _minSizeText, value); } }
        public bool PreviewEnabled { get { return _previewEnabled; } set { SetProperty(ref _previewEnabled, value); } }
        public bool DeepScanIncludeUsedSpace { get { return _deepAllSpace; } set { SetProperty(ref _deepAllSpace, value); } }
        private bool _deepAllSpace;
        public string PreviewMaxText { get { return _previewMaxText; } set { SetProperty(ref _previewMaxText, value); } }
        public string HistoryMaxText { get { return _historyMaxText; } set { SetProperty(ref _historyMaxText, value); } }

        public string DestinationMessage { get { return _destMessage; } private set { SetProperty(ref _destMessage, value); } }
        public string PreferencesMessage { get { return _prefsMessage; } private set { SetProperty(ref _prefsMessage, value); } }
        public string CategoriesMessage { get { return _catMessage; } private set { SetProperty(ref _catMessage, value); } }
        public string LogsMessage { get { return _logsMessage; } private set { SetProperty(ref _logsMessage, value); } }

        public SettingsViewModel(ISettingsService settings, ICategoryRepository categories, IScanHistoryRepository history,
                                 IThemeService theme, IDriveService drives, IDestinationValidator validator,
                                 ILocalizer l, IAppLogger log)
        {
            _settings = settings; _categories = categories; _history = history; _theme = theme;
            _drives = drives; _validator = validator; _l = l; _log = log;
            _current = settings.Load();

            _destination = _current.DefaultRecoveryDestination;
            _minSizeText = _current.MinFileSizeKb.ToString(CultureInfo.InvariantCulture);
            _previewEnabled = _current.PreviewEnabled;
            _deepAllSpace = _current.DeepScanIncludeUsedSpace;
            _previewMaxText = _current.PreviewMaxSizeMb.ToString(CultureInfo.InvariantCulture);
            _historyMaxText = _current.HistoryMaxEntries.ToString(CultureInfo.InvariantCulture);

            LoadCategories();

            SaveDestinationCommand = new RelayCommand(async _ => await SaveDestinationAsync());
            SavePreferencesCommand = new RelayCommand(_ => SavePreferences());
            SaveCategoriesCommand = new RelayCommand(_ => SaveCategories());
            OpenLogsCommand = new RelayCommand(_ => OpenLogs());
        }

        // ---- appearance / scan type apply immediately ----
        private void SetTheme(string theme)
        {
            if (_current.Theme == theme) return;
            _current.Theme = theme;
            _theme.Apply(theme);
            Persist(m => PreferencesMessage = m);
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(IsLightTheme));
        }

        private void SetScanType(string type)
        {
            if (_current.DefaultScanType == type) return;
            _current.DefaultScanType = type;
            Persist(m => PreferencesMessage = m);
            OnPropertyChanged(nameof(IsQuickDefault));
            OnPropertyChanged(nameof(IsDeepDefault));
        }

        // ---- default recovery destination ----
        private async Task SaveDestinationAsync()
        {
            string path = (Destination ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                _current.DefaultRecoveryDestination = string.Empty;
                if (Persist(m => DestinationMessage = m)) DestinationMessage = _l.Get("Set_Dest_Cleared");
                return;
            }

            bool valid;
            try { valid = Path.IsPathRooted(path) && Directory.Exists(path); }
            catch (ArgumentException) { valid = false; }
            if (!valid) { DestinationMessage = _l.Get("Set_Dest_Invalid"); return; }

            _current.DefaultRecoveryDestination = path;
            if (!Persist(m => DestinationMessage = m)) return;

            // Safety check: tell the user which drives this folder must never be used with.
            string message = _l.Get("Set_Dest_Saved");
            try
            {
                var verdicts = await Task.Run(() => _drives.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new { d.Letter, V = _validator.Validate(d.RootPath, path) }).ToList());
                var same = verdicts.Where(x => x.V == DestinationVerdict.SameVolume).Select(x => x.Letter).ToList();
                if (same.Count > 0) message += " " + _l.Format("Set_Dest_Unsafe", string.Join(", ", same));
                else if (verdicts.Any(x => x.V == DestinationVerdict.Unverifiable)) message += " " + _l.Get("Set_Dest_Unverified");
            }
            catch (Exception ex) { _log.Warn("Destination safety check failed: " + ex.Message); }
            DestinationMessage = message;
        }

        // ---- scan / preview / history preferences ----
        private void SavePreferences()
        {
            int minKb = 0, prevMb = 0, histMax = 0;
            bool ok = TryInt(MinFileSizeText, 0, AppSettings.MaxMinFileSizeKb, out minKb)
                   && TryInt(PreviewMaxText, 1, AppSettings.MaxPreviewMb, out prevMb)
                   && TryInt(HistoryMaxText, AppSettings.MinHistoryEntries, AppSettings.MaxHistoryEntries, out histMax);
            if (!ok)
            {
                PreferencesMessage = _l.Format("Set_Prefs_Invalid", AppSettings.MaxMinFileSizeKb, AppSettings.MaxPreviewMb,
                                               AppSettings.MinHistoryEntries, AppSettings.MaxHistoryEntries);
                return;
            }

            _current.MinFileSizeKb = minKb;
            _current.PreviewEnabled = PreviewEnabled;
            _current.DeepScanIncludeUsedSpace = DeepScanIncludeUsedSpace;
            _current.PreviewMaxSizeMb = prevMb;
            _current.HistoryMaxEntries = histMax;
            if (!Persist(m => PreferencesMessage = m)) return;
            try { _history.Prune(histMax); } catch (Exception ex) { _log.Error("History prune failed", ex); }
            PreferencesMessage = _l.Get("Common_Saved");
        }

        private static bool TryInt(string text, int min, int max, out int value)
        {
            return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                   && value >= min && value <= max;
        }

        // ---- file categories ----
        private void LoadCategories()
        {
            Categories.Clear();
            foreach (var c in _categories.GetAll()) Categories.Add(new CategoryEditItem(c, _l));
        }

        private void SaveCategories()
        {
            var updated = new List<FileCategory>();
            foreach (var item in Categories)
            {
                var cat = new FileCategory { Id = item.Source.Id, SortOrder = item.Source.SortOrder, Enabled = item.Enabled };
                if (item.IsCatchAll) cat.Extensions = new List<string>();
                else
                {
                    List<string> exts;
                    if (!FileCategorizer.TryParseExtensions(item.ExtensionsText, out exts))
                    { CategoriesMessage = _l.Format("Set_Cat_Invalid", item.DisplayName); return; }
                    cat.Extensions = exts;
                }
                updated.Add(cat);
            }
            foreach (var cat in updated)
            {
                string ownerId;
                string clash = FileCategorizer.FindConflict(cat, updated, out ownerId);
                if (clash != null) { CategoriesMessage = _l.Format("Set_Cat_Conflict", clash, _l.Get("Cat_" + ownerId)); return; }
            }
            try
            {
                _categories.Save(updated);
                CategoriesMessage = _l.Get("Set_Cat_Saved");
                LoadCategories();   // show the normalized extension lists
            }
            catch (Exception ex) { _log.Error("Saving categories failed", ex); CategoriesMessage = _l.Get("Set_SaveFailed"); }
        }

        // ---- logs ----
        private void OpenLogs()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                Process.Start("explorer.exe", "\"" + AppPaths.LogsDirectory + "\"");
                LogsMessage = string.Empty;
            }
            catch (Exception ex) { _log.Error("Open logs failed", ex); LogsMessage = _l.Get("Set_OpenLogs_Failed"); }
        }

        private bool Persist(Action<string> report)
        {
            try { _settings.Save(_current); return true; }
            catch (Exception ex) { _log.Error("Saving settings failed", ex); report(_l.Get("Set_SaveFailed")); return false; }
        }
    }
}
