using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Native;
using RecoverX.Core.Services;
using RecoverX.Database;
using RecoverX.Desktop.Services;
using RecoverX.Desktop.ViewModels;

namespace RecoverX.Desktop
{
    public partial class App : Application
    {
        private ServiceProvider _provider;
        private IAppLogger _log;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _log = new FileLogger(AppPaths.LogsDirectory);
            DispatcherUnhandledException += OnUnhandled;

            try
            {
                // Database first: the saved language and theme are needed before any UI object exists.
                var db = new SqliteDatabase(AppPaths.DatabasePath);
                db.EnsureCreated();
                var settingsService = new SettingsService(db);
                var settings = settingsService.Load();
                var history = new ScanHistoryRepository(db);
                try { history.Prune(settings.HistoryMaxEntries); } catch (Exception ex) { _log.Warn("History prune skipped: " + ex.Message); }

                try { var interrupted = history.MarkInterrupted(); if (interrupted > 0) _log.Warn(interrupted + " scan(s) from a previous run did not finish; marked as interrupted."); }
                catch (Exception ex) { _log.Warn("Could not update interrupted scans: " + ex.Message); }

                var localizer = new ResourceLocalizer(_log);
                LocExtension.Current = localizer;

                var engine = new NativeEngineClient(_log);
                var services = new ServiceCollection();
                services.AddSingleton<IAppLogger>(_log);
                services.AddSingleton(db);
                services.AddSingleton<ILocalizer>(localizer);
                services.AddSingleton<ISettingsService>(settingsService);
                services.AddSingleton<IScanHistoryRepository>(history);
                services.AddSingleton<ICategoryRepository, CategoryRepository>();
                services.AddSingleton<IEngineClient>(engine);
                services.AddSingleton<IRecoveredFilesRepository, RecoveredFilesRepository>();
                services.AddSingleton<IScanLauncher, ScanLauncher>();
                services.AddSingleton<IQuickScanService, QuickScanService>();
                services.AddSingleton<IRecoveryService, RecoveryService>();
                services.AddSingleton<IDestinationValidator, DestinationValidator>();
                // Phase 1 DriveInfo detection stays as the base; the engine only enriches it.
                services.AddSingleton<IDriveService>(sp => new EnrichedDriveService(
                    new DriveService(sp.GetRequiredService<IAppLogger>()), engine, sp.GetRequiredService<IAppLogger>()));
                services.AddSingleton<IThemeService, ThemeService>();
                services.AddSingleton<IDialogService, WpfDialogService>();
                services.AddSingleton<IElevationService, ElevationService>();
                services.AddSingleton<DrivesViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<ScanViewModel>();
                services.AddSingleton<RecoveredFilesViewModel>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<AboutViewModel>();
                services.AddSingleton<MainViewModel>();
                services.AddTransient<MainWindow>();
                _provider = services.BuildServiceProvider();

                _provider.GetRequiredService<IThemeService>().Apply(settings.Theme);
                LogEnvironment(engine);

                var window = _provider.GetRequiredService<MainWindow>();
                MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                _log.Error("Startup failed", ex);
                MessageBox.Show("RecoverX could not start:\n\n" + ex.Message + "\n\nDetails were written to the log folder.",
                    "RecoverX", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_provider != null) _provider.Dispose();      // closes any open scan session
            base.OnExit(e);
        }

        private void LogEnvironment(IEngineClient engine)
        {
            _log.Info("RecoverX " + typeof(App).Assembly.GetName().Version + " | " + SystemInfo.OsDescription()
                      + " | .NET " + SystemInfo.DotNetFramework() + " | " + (SystemInfo.Is64BitProcess ? "x64" : "x86") + " process"
                      + " | admin=" + SystemInfo.IsAdministrator()
                      + " | engine=" + (engine.IsAvailable ? engine.Version : "unavailable: " + engine.UnavailableReason));
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _log.Error("Unhandled UI exception", e.Exception);
            MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message, "RecoverX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
