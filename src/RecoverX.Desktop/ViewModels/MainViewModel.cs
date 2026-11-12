using System.Collections.Generic;
using System.Windows.Input;
using RecoverX.Core.Interfaces;

namespace RecoverX.Desktop.ViewModels
{
    public sealed class NavItem : ViewModelBase
    {
        private bool _isSelected;
        public string Title { get; set; }
        public string Glyph { get; set; }
        public ViewModelBase Target { get; set; }       // null = not available yet
        public string DisabledReason { get; set; }
        public bool IsEnabled { get { return Target != null; } }
        public bool IsSelected { get { return _isSelected; } set { SetProperty(ref _isSelected, value); } }
    }

    public sealed class MainViewModel : ViewModelBase
    {
        private ViewModelBase _current;
        private NavItem _selected;

        public IList<NavItem> NavItems { get; }
        public ICommand NavigateCommand { get; }
        public ViewModelBase CurrentViewModel { get { return _current; } private set { SetProperty(ref _current, value); } }

        public MainViewModel(ILocalizer l, DashboardViewModel dashboard, DrivesViewModel drives, ScanViewModel scan, RecoveredFilesViewModel recovered,
                             HistoryViewModel history, SettingsViewModel settings, AboutViewModel about, Services.IScanLauncher launcher)
        {
            NavItems = new List<NavItem>
            {
                new NavItem { Title = l.Get("Nav_Dashboard"),     Glyph = "\u2302", Target = dashboard },
                new NavItem { Title = l.Get("Nav_RecoverFiles"),  Glyph = "\u21BA", Target = scan },
                new NavItem { Title = l.Get("Nav_DriveScan"),     Glyph = "\u25C9", Target = drives },
                new NavItem { Title = l.Get("Nav_RecoveredFiles"),Glyph = "\u2714", Target = recovered },
                new NavItem { Title = l.Get("Nav_ScanHistory"),   Glyph = "\u2630", Target = history },
                new NavItem { Title = l.Get("Nav_Settings"),      Glyph = "\u2699", Target = settings },
                new NavItem { Title = l.Get("Nav_About"),         Glyph = "\u2139", Target = about }
            };
            NavigateCommand = new RelayCommand(p => Navigate((NavItem)p), p => p is NavItem && ((NavItem)p).IsEnabled);
            Navigate(NavItems[0]);
            launcher.ScanRequested += d => Navigate(NavItems[1]);       // the scan page starts the scan itself
            launcher.PickerRequested += () => Navigate(NavItems[2]);
        }

        private void Navigate(NavItem item)
        {
            if (_selected != null) _selected.IsSelected = false;
            _selected = item;
            item.IsSelected = true;
            CurrentViewModel = item.Target;
            var refreshable = item.Target as IRefreshable;
            if (refreshable != null) { var ignored = refreshable.RefreshAsync(); }
        }
    }
}
