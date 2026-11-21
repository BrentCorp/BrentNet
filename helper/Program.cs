using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        CrashLogger.Initialize();

        Application.ThreadException += (_, e) =>
        {
            CrashLogger.LogException("Application.ThreadException", e.Exception);

            MessageBox.Show(
                "BrentNET Client encountered an error and saved a crash report.\n\nYou can send it from Report Bug.",
                "BrentNET Client Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                CrashLogger.LogException("AppDomain.CurrentDomain.UnhandledException", ex);
            }
            else
            {
                CrashLogger.LogText("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown fatal exception.");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.LogException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        var app = new BrentNetTrayApp();
        Application.Run(app);
    }
}

public sealed class BrentNetTrayApp : ApplicationContext
{
    private const string UpdateManifestUrl = "https://brentnet.net/update.json";
    private const string RemoteRoutesManifestUrl = "https://brentnet.net/routes.json";
    private const string BugReportEmail = "brent@brentnet.net";

    private static readonly TimeSpan StartupUpdateDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromHours(6);

    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private readonly NotifyIcon _trayIcon;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly object _routesLock = new();
    private readonly object _settingsLock = new();
    private readonly AppSettings _settings;

    private ToolStripMenuItem? _pauseMenuItem;
    private ToolStripMenuItem? _checkUpdatesMenuItem;
    private Dictionary<string, string> _routes = new();
    private HttpListener? _listener;

    private volatile bool _isPaused;
    private volatile bool _isCheckingForUpdates;
    private volatile bool _isSyncingRoutes;

    private DateTime? _lastUpdateCheckUtc;
    private DateTime? _lastRouteSyncUtc;
    private string _routeSource = "Local routes.json";

    public BrentNetTrayApp()
    {
        _settings = AppSettings.Load();

        CrashLogger.LogInfo("BrentNET Client starting.");

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "BrentNET Client",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        ShowMaintenanceBalloon(
            "BrentNET Client",
            "BrentNET is starting...",
            ToolTipIcon.Info
        );

        Task.Run(() => StartBrentNetAsync(_shutdown.Token));
        Task.Run(() => CheckForUpdatesOnStartupAsync(_shutdown.Token));
        Task.Run(() => PeriodicMaintenanceAsync(_shutdown.Token));
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _pauseMenuItem = new ToolStripMenuItem("Pause BrentNET", null, (_, _) =>
        {
            TogglePause();
        })
        {
            CheckOnClick = false
        };

        menu.Items.Add(_pauseMenuItem);

        _checkUpdatesMenuItem = new ToolStripMenuItem("Check for Updates", null, async (_, _) =>
        {
            await CheckForUpdatesAsync(showInteractiveMessages: true);
        });

        menu.Items.Add(_checkUpdatesMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open net.brent", null, (_, _) =>
        {
            OpenUrl("http://net.brent");
        });

        menu.Items.Add("Open corp.brent", null, (_, _) =>
        {
            OpenUrl("http://corp.brent");
        });

        menu.Items.Add("Open home.brent", null, (_, _) =>
        {
            OpenUrl("http://home.brent");
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Sync Routes Now", null, async (_, _) =>
        {
            await SyncRemoteRoutesAsync(showInteractiveMessages: true);
        });

        menu.Items.Add("Status...", null, (_, _) =>
        {
            ShowStatusWindow();
        });

        menu.Items.Add("Report Bug...", null, (_, _) =>
        {
            ShowBugReportWindow();
        });

        menu.Items.Add("Open Logs Folder", null, (_, _) =>
        {
            OpenFolder(AppPaths.LogsDirectoryPath);
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Uninstall BrentNET Client...", null, (_, _) =>
        {
            UninstallBrentNetClient();
        });

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit", null, (_, _) =>
        {
            ExitThread();
        });

        return menu;
    }

    private async Task CheckForUpdatesOnStartupAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(StartupUpdateDelay, token);

            if (!token.IsCancellationRequested)
            {
                await CheckForUpdatesAsync(showInteractiveMessages: false);
            }
        }
        catch (TaskCanceledException)
        {
            // App is shutting down.
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Startup update check failed.", ex);
        }
    }

    private async Task PeriodicMaintenanceAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PeriodicCheckInterval, token);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await CheckForUpdatesAsync(showInteractiveMessages: false);
                await SyncRemoteRoutesAsync(showInteractiveMessages: false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                CrashLogger.LogException("Periodic maintenance failed.", ex);
            }
        }
    }

    private async Task CheckForUpdatesAsync(bool showInteractiveMessages)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        _lastUpdateCheckUtc = DateTime.UtcNow;

        if (_checkUpdatesMenuItem is not null)
        {
            _checkUpdatesMenuItem.Enabled = false;
            _checkUpdatesMenuItem.Text = "Checking for Updates...";
        }

        try
        {
            using var response = await _httpClient.GetAsync(UpdateManifestUrl, _shutdown.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(_shutdown.Token);
            var updateInfo = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (updateInfo is null || string.IsNullOrWhiteSpace(updateInfo.Version) || string.IsNullOrWhiteSpace(updateInfo.InstallerUrl))
            {
                throw new InvalidOperationException("Update manifest is missing version or installerUrl.");
            }

            var currentVersion = GetCurrentVersion();
            var latestVersion = Version.Parse(updateInfo.Version.Trim());

            if (latestVersion <= currentVersion)
            {
                if (showInteractiveMessages)
                {
                    MessageBox.Show(
                        $"BrentNET Client is already up to date.\n\nCurrent version: {currentVersion}",
                        "BrentNET Client Updates",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }

                return;
            }

            if (!showInteractiveMessages)
            {
                NotifyUpdateAvailableOnce(latestVersion);
                return;
            }

            var notes = string.IsNullOrWhiteSpace(updateInfo.Notes)
                ? "No release notes provided."
                : updateInfo.Notes.Trim();

            var choice = MessageBox.Show(
                $"A new BrentNET Client update is available.\n\nCurrent: {currentVersion}\nLatest: {latestVersion}\n\n{notes}\n\nDownload and install now?",
                "BrentNET Client Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information
            );

            if (choice != DialogResult.Yes)
            {
                return;
            }

            await DownloadAndLaunchInstallerAsync(updateInfo);
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Could not check for updates.", ex);

            if (showInteractiveMessages)
            {
                ShowError($"Could not check for updates.\n\n{ex.Message}");
            }
        }
        finally
        {
            _isCheckingForUpdates = false;

            if (_checkUpdatesMenuItem is not null)
            {
                _checkUpdatesMenuItem.Enabled = true;
                _checkUpdatesMenuItem.Text = "Check for Updates";
            }
        }
    }

    private void NotifyUpdateAvailableOnce(Version latestVersion)
    {
        lock (_settingsLock)
        {
            if (!_settings.UpdateAlertsEnabled)
            {
                return;
            }

            var latestVersionText = latestVersion.ToString();

            if (string.Equals(_settings.LastNotifiedVersion, latestVersionText, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _trayIcon.ShowBalloonTip(
                5000,
                "BrentNET Update Available",
                $"Version {latestVersionText} is available. Open the tray menu and click Check for Updates to install it.",
                ToolTipIcon.Info
            );

            _settings.LastNotifiedVersion = latestVersionText;
            _settings.Save();
        }
    }

    private async Task DownloadAndLaunchInstallerAsync(UpdateManifest updateInfo)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), "BrentNETClientUpdates");
        Directory.CreateDirectory(tempFolder);

        var fileName = string.IsNullOrWhiteSpace(updateInfo.FileName)
            ? $"BrentNETClientSetup-v{updateInfo.Version}.exe"
            : updateInfo.FileName.Trim();

        var installerPath = Path.Combine(tempFolder, fileName);

        ShowMaintenanceBalloon(
            "BrentNET Client",
            "Downloading update...",
            ToolTipIcon.Info
        );

        await using (var installerStream = await _httpClient.GetStreamAsync(updateInfo.InstallerUrl, _shutdown.Token))
        await using (var fileStream = File.Create(installerPath))
        {
            await installerStream.CopyToAsync(fileStream, _shutdown.Token);
        }

        if (!string.IsNullOrWhiteSpace(updateInfo.Sha256))
        {
            var actualHash = await CalculateSha256Async(installerPath);
            var expectedHash = updateInfo.Sha256.Trim();

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(installerPath);
                }
                catch
                {
                    // Ignore cleanup failure.
                }

                throw new InvalidOperationException("Downloaded update failed SHA-256 verification.");
            }
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
        };

        Process.Start(processInfo);

        ShowMaintenanceBalloon(
            "BrentNET Client",
            "Update installer launched. BrentNET Client is closing so it can be replaced.",
            ToolTipIcon.Info
        );

        ExitThread();
    }

    private async Task SyncRemoteRoutesAsync(bool showInteractiveMessages)
    {
        if (_isSyncingRoutes)
        {
            return;
        }

        _isSyncingRoutes = true;

        try
        {
            using var response = await _httpClient.GetAsync(RemoteRoutesManifestUrl, _shutdown.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(_shutdown.Token);
            var remoteRoutes = ParseRoutesJson(json);

            if (remoteRoutes.Count == 0)
            {
                throw new InvalidOperationException("Remote route manifest did not contain any usable routes.");
            }

            var routesChanged = RoutesChanged(remoteRoutes);

            ApplyRoutes(remoteRoutes, "BrentNET.net/routes.json", showRouteBalloon: showInteractiveMessages);
            _lastRouteSyncUtc = DateTime.UtcNow;

            if (showInteractiveMessages)
            {
                MessageBox.Show(
                    $"BrentNET routes synced successfully.\n\nRoutes loaded: {remoteRoutes.Count}",
                    "BrentNET Route Sync",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            else if (routesChanged)
            {
                ShowRouteBalloon(
                    "BrentNET Routes Updated",
                    $"Remote routes changed. Routes loaded: {remoteRoutes.Count}",
                    ToolTipIcon.Info
                );
            }
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Could not sync remote routes.", ex);

            if (showInteractiveMessages)
            {
                ShowError($"Could not sync remote routes.\n\n{ex.Message}");
            }
        }
        finally
        {
            _isSyncingRoutes = false;
        }
    }

    private bool RoutesChanged(Dictionary<string, string> newRoutes)
    {
        lock (_routesLock)
        {
            if (_routes.Count != newRoutes.Count)
            {
                return true;
            }

            foreach (var pair in newRoutes)
            {
                if (!_routes.TryGetValue(pair.Key, out var existingTarget))
                {
                    return true;
                }

                if (!string.Equals(existingTarget, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void ApplyRoutes(Dictionary<string, string> routes, string routeSource, bool showRouteBalloon = true)
    {
        lock (_routesLock)
        {
            _routes = routes;
            _routeSource = routeSource;
        }

        if (!_isPaused)
        {
            HostsManager.InstallRoutes(routes.Keys);
        }

        if (showRouteBalloon)
        {
            ShowRouteBalloon(
                "BrentNET Client",
                $"Routes loaded: {routes.Count}",
                ToolTipIcon.Info
            );
        }
    }

    private void ShowStatusWindow()
    {
        using var form = new Form
        {
            Text = "BrentNET Client Status",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = true,
            MinimizeBox = false,
            Width = 590,
            Height = 625,
            MinimumSize = new Size(540, 560),
            AutoScaleMode = AutoScaleMode.Dpi
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(18, 16, 18, 16)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "BrentNET Client Status",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12, FontStyle.Bold)
        };

        var statusBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = BuildStatusText(),
            TabStop = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            MinimumSize = new Size(0, 150)
        };

        var actionButtonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 12),
            Anchor = AnchorStyles.None
        };

        var checkUpdatesButton = new Button
        {
            Text = "Check Updates",
            Width = 130,
            Height = 34,
            Margin = new Padding(0, 0, 12, 0)
        };

        var syncRoutesButton = new Button
        {
            Text = "Sync Routes",
            Width = 130,
            Height = 34,
            Margin = new Padding(0, 0, 12, 0)
        };

        var reportBugButton = new Button
        {
            Text = "Report Bug",
            Width = 130,
            Height = 34,
            Margin = new Padding(0)
        };

        actionButtonsPanel.Controls.Add(checkUpdatesButton);
        actionButtonsPanel.Controls.Add(syncRoutesButton);
        actionButtonsPanel.Controls.Add(reportBugButton);

        var settingsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 14)
        };

        var updateAlertsCheckBox = new CheckBox
        {
            Text = "Notify when updates are available",
            AutoSize = true,
            Checked = GetUpdateAlertsEnabled(),
            Margin = new Padding(0, 0, 0, 6)
        };

        var routeAlertsCheckBox = new CheckBox
        {
            Text = "Notify when remote routes change",
            AutoSize = true,
            Checked = GetRouteAlertsEnabled(),
            Margin = new Padding(0, 0, 0, 6)
        };

        var maintenanceAlertsCheckBox = new CheckBox
        {
            Text = "Notify on BrentNET maintenance events",
            AutoSize = true,
            Checked = GetMaintenanceAlertsEnabled(),
            Margin = new Padding(0)
        };

        settingsPanel.Controls.Add(updateAlertsCheckBox);
        settingsPanel.Controls.Add(routeAlertsCheckBox);
        settingsPanel.Controls.Add(maintenanceAlertsCheckBox);

        var bottomButtonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Anchor = AnchorStyles.None
        };

        var applyButton = new Button
        {
            Text = "Apply",
            Width = 150,
            Height = 34,
            Enabled = false,
            Margin = new Padding(0, 0, 16, 0)
        };

        var closeButton = new Button
        {
            Text = "Close",
            Width = 150,
            Height = 34,
            Margin = new Padding(0)
        };

        bottomButtonsPanel.Controls.Add(applyButton);
        bottomButtonsPanel.Controls.Add(closeButton);

        void CenterPanel(object? _, EventArgs __)
        {
            actionButtonsPanel.Left = Math.Max(0, (root.ClientSize.Width - actionButtonsPanel.Width) / 2);
            bottomButtonsPanel.Left = Math.Max(0, (root.ClientSize.Width - bottomButtonsPanel.Width) / 2);
        }

        void MarkSettingsChanged(object? _, EventArgs __)
        {
            applyButton.Enabled = true;
        }

        updateAlertsCheckBox.CheckedChanged += MarkSettingsChanged;
        routeAlertsCheckBox.CheckedChanged += MarkSettingsChanged;
        maintenanceAlertsCheckBox.CheckedChanged += MarkSettingsChanged;

        checkUpdatesButton.Click += async (_, _) =>
        {
            checkUpdatesButton.Enabled = false;
            syncRoutesButton.Enabled = false;
            reportBugButton.Enabled = false;
            applyButton.Enabled = false;

            try
            {
                await CheckForUpdatesAsync(showInteractiveMessages: true);
                statusBox.Text = BuildStatusText();
                ClearStatusSelection(statusBox);
            }
            finally
            {
                checkUpdatesButton.Enabled = true;
                syncRoutesButton.Enabled = true;
                reportBugButton.Enabled = true;
                applyButton.Enabled = SettingsDifferFromUi(updateAlertsCheckBox, routeAlertsCheckBox, maintenanceAlertsCheckBox);
                closeButton.Focus();
            }
        };

        syncRoutesButton.Click += async (_, _) =>
        {
            checkUpdatesButton.Enabled = false;
            syncRoutesButton.Enabled = false;
            reportBugButton.Enabled = false;
            applyButton.Enabled = false;

            try
            {
                await SyncRemoteRoutesAsync(showInteractiveMessages: true);
                statusBox.Text = BuildStatusText();
                ClearStatusSelection(statusBox);
            }
            finally
            {
                checkUpdatesButton.Enabled = true;
                syncRoutesButton.Enabled = true;
                reportBugButton.Enabled = true;
                applyButton.Enabled = SettingsDifferFromUi(updateAlertsCheckBox, routeAlertsCheckBox, maintenanceAlertsCheckBox);
                closeButton.Focus();
            }
        };

        reportBugButton.Click += (_, _) =>
        {
            ShowBugReportWindow();
        };

        applyButton.Click += (_, _) =>
        {
            SaveSettingsFromUi(updateAlertsCheckBox, routeAlertsCheckBox, maintenanceAlertsCheckBox);
            statusBox.Text = BuildStatusText();
            ClearStatusSelection(statusBox);
            applyButton.Enabled = false;
            closeButton.Focus();
        };

        closeButton.Click += (_, _) => form.Close();

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(statusBox, 0, 1);
        root.Controls.Add(actionButtonsPanel, 0, 2);
        root.Controls.Add(settingsPanel, 0, 3);
        root.Controls.Add(bottomButtonsPanel, 0, 4);

        form.Controls.Add(root);

        root.Resize += CenterPanel;
        actionButtonsPanel.SizeChanged += CenterPanel;
        bottomButtonsPanel.SizeChanged += CenterPanel;

        form.Shown += (_, _) =>
        {
            CenterPanel(null, EventArgs.Empty);
            ClearStatusSelection(statusBox);
            closeButton.Focus();
        };

        form.ShowDialog();
    }

    private void ShowBugReportWindow()
    {
        var attachedScreenshots = new List<string>();

        using var form = new Form
        {
            Text = "Report BrentNET Bug",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = true,
            MinimizeBox = false,
            Width = 720,
            Height = 650,
            MinimumSize = new Size(620, 560),
            AutoScaleMode = AutoScaleMode.Dpi
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(18, 16, 18, 16)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Report BrentNET Bug",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12, FontStyle.Bold)
        };

        var instructions = new Label
        {
            Text =
                "Describe what happened below. For screenshots, press Win+Shift+S, save the image, then click Add Screenshot(s).\r\n" +
                "BrentNET will create a report bundle you can review before sending.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };

        var commentBox = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            PlaceholderText = "What happened? What were you doing? What did you expect to happen?"
        };

        var screenshotLabel = new Label
        {
            Text = "Screenshots / Attachments:",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9, FontStyle.Bold)
        };

        var screenshotList = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        var privacyNote = new Label
        {
            Text =
                "Privacy note: this report includes app diagnostics and any screenshots you choose to attach. Review before sending.",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10)
        };

        var buttonsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };

        var addScreenshotsButton = new Button
        {
            Text = "Add Screenshot(s)",
            Width = 140,
            Height = 34,
            Margin = new Padding(0, 0, 10, 8)
        };

        var saveBundleButton = new Button
        {
            Text = "Save Report Bundle",
            Width = 155,
            Height = 34,
            Margin = new Padding(0, 0, 10, 8)
        };

        var createEmailButton = new Button
        {
            Text = "Create Email",
            Width = 120,
            Height = 34,
            Margin = new Padding(0, 0, 10, 8)
        };

        var copyReportButton = new Button
        {
            Text = "Copy Report",
            Width = 120,
            Height = 34,
            Margin = new Padding(0, 0, 10, 8)
        };

        var openLogsButton = new Button
        {
            Text = "Open Logs Folder",
            Width = 135,
            Height = 34,
            Margin = new Padding(0, 0, 10, 8)
        };

        var closeButton = new Button
        {
            Text = "Close",
            Width = 110,
            Height = 34,
            Margin = new Padding(0, 0, 0, 8)
        };

        buttonsPanel.Controls.Add(addScreenshotsButton);
        buttonsPanel.Controls.Add(saveBundleButton);
        buttonsPanel.Controls.Add(createEmailButton);
        buttonsPanel.Controls.Add(copyReportButton);
        buttonsPanel.Controls.Add(openLogsButton);
        buttonsPanel.Controls.Add(closeButton);

        addScreenshotsButton.Click += (_, _) =>
        {
            try
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Add Screenshot(s)",
                    Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*",
                    Multiselect = true,
                    CheckFileExists = true,
                    CheckPathExists = true,
                    RestoreDirectory = true
                };

                if (dialog.ShowDialog(form) != DialogResult.OK)
                {
                    return;
                }

                foreach (var file in dialog.FileNames)
                {
                    if (attachedScreenshots.Contains(file, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    attachedScreenshots.Add(file);
                    screenshotList.Items.Add(file);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.LogException("Add Screenshot dialog failed.", ex);

                MessageBox.Show(
                    $"Could not open screenshot picker.\n\n{ex.Message}",
                    "BrentNET Bug Reporter",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        };

        saveBundleButton.Click += (_, _) =>
        {
            try
            {
                var bundlePath = SaveBugReportBundle(commentBox.Text, attachedScreenshots);

                MessageBox.Show(
                    $"Bug report bundle saved.\n\n{bundlePath}",
                    "BrentNET Bug Report",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );

                OpenFolder(bundlePath);
            }
            catch (Exception ex)
            {
                CrashLogger.LogException("Could not save bug report bundle.", ex);
                ShowError($"Could not save bug report bundle.\n\n{ex.Message}");
            }
        };

        createEmailButton.Click += (_, _) =>
        {
            try
            {
                var bundlePath = SaveBugReportBundle(commentBox.Text, attachedScreenshots);
                CreateBugReportEmail(commentBox.Text, bundlePath);

                MessageBox.Show(
                    "An email draft has been opened.\n\nPlease attach the bug report bundle folder contents before sending.",
                    "BrentNET Bug Report",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                CrashLogger.LogException("Could not create bug report email.", ex);
                ShowError($"Could not create bug report email.\n\n{ex.Message}");
            }
        };

        copyReportButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(BuildBugReportText(commentBox.Text, attachedScreenshots));

                MessageBox.Show(
                    "Bug report copied to clipboard.",
                    "BrentNET Bug Report",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                CrashLogger.LogException("Could not copy bug report.", ex);
                ShowError($"Could not copy bug report.\n\n{ex.Message}");
            }
        };

        openLogsButton.Click += (_, _) =>
        {
            OpenFolder(AppPaths.LogsDirectoryPath);
        };

        closeButton.Click += (_, _) => form.Close();

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(instructions, 0, 1);
        root.Controls.Add(commentBox, 0, 2);
        root.Controls.Add(screenshotLabel, 0, 3);
        root.Controls.Add(screenshotList, 0, 4);
        root.Controls.Add(privacyNote, 0, 5);
        root.Controls.Add(buttonsPanel, 0, 6);

        form.Controls.Add(root);
        form.ShowDialog();
    }

    private string SaveBugReportBundle(string comments, IReadOnlyList<string> attachedScreenshots)
    {
        Directory.CreateDirectory(AppPaths.BugReportsDirectoryPath);

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
        var bundlePath = Path.Combine(AppPaths.BugReportsDirectoryPath, $"BrentNET-BugReport-{timestamp}");

        Directory.CreateDirectory(bundlePath);

        var reportPath = Path.Combine(bundlePath, "bug-report.txt");
        File.WriteAllText(reportPath, BuildBugReportText(comments, attachedScreenshots));

        var screenshotsPath = Path.Combine(bundlePath, "screenshots");
        Directory.CreateDirectory(screenshotsPath);

        for (var i = 0; i < attachedScreenshots.Count; i++)
        {
            var sourcePath = attachedScreenshots[i];

            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var extension = Path.GetExtension(sourcePath);

            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var destinationPath = Path.Combine(screenshotsPath, $"screenshot-{i + 1}{extension}");

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        var latestCrashPath = CrashLogger.GetLatestCrashReportPath();

        if (!string.IsNullOrWhiteSpace(latestCrashPath) && File.Exists(latestCrashPath))
        {
            File.Copy(latestCrashPath, Path.Combine(bundlePath, "latest-crash.txt"), overwrite: true);
        }

        return bundlePath;
    }

    private void CreateBugReportEmail(string comments, string bundlePath)
    {
        var subject = $"BrentNET Client Bug Report - v{GetCurrentVersion()}";

        var body =
            "Hi Brent,\r\n\r\n" +
            "I created a BrentNET Client bug report.\r\n\r\n" +
            $"Report bundle folder:\r\n{bundlePath}\r\n\r\n" +
            "Please attach the files from that folder, especially bug-report.txt and any screenshots.\r\n\r\n" +
            "Summary:\r\n" +
            TrimForEmail(comments, 1200);

        var uri =
            "mailto:" + Uri.EscapeDataString(BugReportEmail) +
            "?subject=" + Uri.EscapeDataString(subject) +
            "&body=" + Uri.EscapeDataString(body);

        Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true
        });
    }

    private string BuildBugReportText(string comments, IReadOnlyList<string> attachedScreenshots)
    {
        var sb = new StringBuilder();

        sb.AppendLine("BrentNET Client Bug Report");
        sb.AppendLine("=========================");
        sb.AppendLine();
        sb.AppendLine($"Generated Local Time: {DateTime.Now:yyyy-MM-dd h:mm:ss tt}");
        sb.AppendLine($"Generated UTC Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine();

        sb.AppendLine("User Comments");
        sb.AppendLine("-------------");
        sb.AppendLine(string.IsNullOrWhiteSpace(comments) ? "(No comments provided.)" : comments.Trim());
        sb.AppendLine();

        sb.AppendLine("Attached Screenshots");
        sb.AppendLine("--------------------");

        if (attachedScreenshots.Count == 0)
        {
            sb.AppendLine("(No screenshots attached.)");
        }
        else
        {
            foreach (var screenshot in attachedScreenshots)
            {
                sb.AppendLine(screenshot);
            }
        }

        sb.AppendLine();
        sb.AppendLine(BuildDiagnosticText());

        return sb.ToString();
    }

    private string BuildStatusText()
    {
        Dictionary<string, string> routesSnapshot;
        string routeSourceSnapshot;
        bool updateAlertsEnabled;
        bool routeAlertsEnabled;
        bool maintenanceAlertsEnabled;
        string lastNotifiedVersion;

        lock (_routesLock)
        {
            routesSnapshot = new Dictionary<string, string>(_routes, StringComparer.OrdinalIgnoreCase);
            routeSourceSnapshot = _routeSource;
        }

        lock (_settingsLock)
        {
            updateAlertsEnabled = _settings.UpdateAlertsEnabled;
            routeAlertsEnabled = _settings.RouteAlertsEnabled;
            maintenanceAlertsEnabled = _settings.MaintenanceAlertsEnabled;
            lastNotifiedVersion = string.IsNullOrWhiteSpace(_settings.LastNotifiedVersion)
                ? "Never"
                : _settings.LastNotifiedVersion.Trim();
        }

        return
            $"Version: {GetCurrentVersion()}\r\n" +
            $"Status: {(_isPaused ? "Paused" : "Running")}\r\n" +
            $"Routes Loaded: {routesSnapshot.Count}\r\n" +
            $"Route Source: {routeSourceSnapshot}\r\n" +
            $"Last Route Sync: {FormatLocalTime(_lastRouteSyncUtc)}\r\n" +
            $"Last Update Check: {FormatLocalTime(_lastUpdateCheckUtc)}\r\n" +
            $"Update Alerts: {FormatEnabled(updateAlertsEnabled)}\r\n" +
            $"Route Alerts: {FormatEnabled(routeAlertsEnabled)}\r\n" +
            $"Maintenance Alerts: {FormatEnabled(maintenanceAlertsEnabled)}\r\n" +
            $"Last Notified Version: {lastNotifiedVersion}\r\n" +
            $"Settings File: {AppSettings.SettingsFilePath}\r\n" +
            $"Logs Folder: {AppPaths.LogsDirectoryPath}\r\n" +
            $"Bug Reports Folder: {AppPaths.BugReportsDirectoryPath}\r\n" +
            $"Hosts File: {HostsManager.GetHostsPath()}\r\n" +
            $"Update Manifest: {UpdateManifestUrl}\r\n" +
            $"Route Manifest: {RemoteRoutesManifestUrl}";
    }

    private string BuildDiagnosticText()
    {
        Dictionary<string, string> routesSnapshot;
        string routeSourceSnapshot;
        bool updateAlertsEnabled;
        bool routeAlertsEnabled;
        bool maintenanceAlertsEnabled;
        string lastNotifiedVersion;

        lock (_routesLock)
        {
            routesSnapshot = new Dictionary<string, string>(_routes, StringComparer.OrdinalIgnoreCase);
            routeSourceSnapshot = _routeSource;
        }

        lock (_settingsLock)
        {
            updateAlertsEnabled = _settings.UpdateAlertsEnabled;
            routeAlertsEnabled = _settings.RouteAlertsEnabled;
            maintenanceAlertsEnabled = _settings.MaintenanceAlertsEnabled;
            lastNotifiedVersion = string.IsNullOrWhiteSpace(_settings.LastNotifiedVersion)
                ? "Never"
                : _settings.LastNotifiedVersion.Trim();
        }

        var primaryScreen = Screen.PrimaryScreen;
        var bounds = primaryScreen?.Bounds;
        var workingArea = primaryScreen?.WorkingArea;

        var sb = new StringBuilder();

        sb.AppendLine("Diagnostics");
        sb.AppendLine("-----------");
        sb.AppendLine($"Application Version: {GetCurrentVersion()}");
        sb.AppendLine($"Application Base Directory: {AppContext.BaseDirectory}");
        sb.AppendLine($"Process ID: {Environment.ProcessId}");
        sb.AppendLine($"Process Architecture: {RuntimeInformationHelper.ProcessArchitecture}");
        sb.AppendLine($"OS Architecture: {RuntimeInformationHelper.OSArchitecture}");
        sb.AppendLine($"Running as Administrator: {FormatEnabled(IsRunningAsAdministrator())}");
        sb.AppendLine($"App Started UTC: {_startedUtc:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"App Uptime: {DateTime.UtcNow - _startedUtc}");
        sb.AppendLine();

        sb.AppendLine("Windows / Machine");
        sb.AppendLine("-----------------");
        sb.AppendLine($"OS Version: {Environment.OSVersion}");
        sb.AppendLine($"Machine Name: {Environment.MachineName}");
        sb.AppendLine($"User Domain: {Environment.UserDomainName}");
        sb.AppendLine($"User Name: {Environment.UserName}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
        sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
        sb.AppendLine($"System Directory: {Environment.SystemDirectory}");
        sb.AppendLine();

        sb.AppendLine("Display");
        sb.AppendLine("-------");
        sb.AppendLine($"Primary Screen Bounds: {FormatRectangle(bounds)}");
        sb.AppendLine($"Primary Screen Working Area: {FormatRectangle(workingArea)}");
        sb.AppendLine($"DPI: {GetCurrentDpiInfo()}");
        sb.AppendLine();

        sb.AppendLine("BrentNET State");
        sb.AppendLine("--------------");
        sb.AppendLine($"Status: {(_isPaused ? "Paused" : "Running")}");
        sb.AppendLine($"Routes Loaded: {routesSnapshot.Count}");
        sb.AppendLine($"Route Source: {routeSourceSnapshot}");
        sb.AppendLine($"Last Route Sync: {FormatLocalTime(_lastRouteSyncUtc)}");
        sb.AppendLine($"Last Update Check: {FormatLocalTime(_lastUpdateCheckUtc)}");
        sb.AppendLine($"Update Alerts: {FormatEnabled(updateAlertsEnabled)}");
        sb.AppendLine($"Route Alerts: {FormatEnabled(routeAlertsEnabled)}");
        sb.AppendLine($"Maintenance Alerts: {FormatEnabled(maintenanceAlertsEnabled)}");
        sb.AppendLine($"Last Notified Version: {lastNotifiedVersion}");
        sb.AppendLine();

        sb.AppendLine("Paths / URLs");
        sb.AppendLine("------------");
        sb.AppendLine($"Settings File: {AppSettings.SettingsFilePath}");
        sb.AppendLine($"Logs Folder: {AppPaths.LogsDirectoryPath}");
        sb.AppendLine($"Bug Reports Folder: {AppPaths.BugReportsDirectoryPath}");
        sb.AppendLine($"Hosts File: {HostsManager.GetHostsPath()}");
        sb.AppendLine($"Update Manifest: {UpdateManifestUrl}");
        sb.AppendLine($"Route Manifest: {RemoteRoutesManifestUrl}");
        sb.AppendLine();

        sb.AppendLine("Routes");
        sb.AppendLine("------");

        if (routesSnapshot.Count == 0)
        {
            sb.AppendLine("(No routes loaded.)");
        }
        else
        {
            foreach (var route in routesSnapshot.OrderBy(pair => pair.Key))
            {
                sb.AppendLine($"{route.Key} -> {route.Value}");
            }
        }

        sb.AppendLine();

        var latestCrashPath = CrashLogger.GetLatestCrashReportPath();

        sb.AppendLine("Crash Reports");
        sb.AppendLine("-------------");
        sb.AppendLine($"Latest Crash Report: {(string.IsNullOrWhiteSpace(latestCrashPath) ? "None" : latestCrashPath)}");

        return sb.ToString();
    }

    private bool SettingsDifferFromUi(CheckBox updateAlertsCheckBox, CheckBox routeAlertsCheckBox, CheckBox maintenanceAlertsCheckBox)
    {
        lock (_settingsLock)
        {
            return _settings.UpdateAlertsEnabled != updateAlertsCheckBox.Checked ||
                   _settings.RouteAlertsEnabled != routeAlertsCheckBox.Checked ||
                   _settings.MaintenanceAlertsEnabled != maintenanceAlertsCheckBox.Checked;
        }
    }

    private void SaveSettingsFromUi(CheckBox updateAlertsCheckBox, CheckBox routeAlertsCheckBox, CheckBox maintenanceAlertsCheckBox)
    {
        lock (_settingsLock)
        {
            _settings.UpdateAlertsEnabled = updateAlertsCheckBox.Checked;
            _settings.RouteAlertsEnabled = routeAlertsCheckBox.Checked;
            _settings.MaintenanceAlertsEnabled = maintenanceAlertsCheckBox.Checked;
            _settings.Save();
        }
    }

    private bool GetUpdateAlertsEnabled()
    {
        lock (_settingsLock)
        {
            return _settings.UpdateAlertsEnabled;
        }
    }

    private bool GetRouteAlertsEnabled()
    {
        lock (_settingsLock)
        {
            return _settings.RouteAlertsEnabled;
        }
    }

    private bool GetMaintenanceAlertsEnabled()
    {
        lock (_settingsLock)
        {
            return _settings.MaintenanceAlertsEnabled;
        }
    }

    private void ShowRouteBalloon(string title, string message, ToolTipIcon icon)
    {
        lock (_settingsLock)
        {
            if (!_settings.RouteAlertsEnabled)
            {
                return;
            }
        }

        _trayIcon.ShowBalloonTip(3000, title, message, icon);
    }

    private void ShowMaintenanceBalloon(string title, string message, ToolTipIcon icon)
    {
        lock (_settingsLock)
        {
            if (!_settings.MaintenanceAlertsEnabled)
            {
                return;
            }
        }

        _trayIcon.ShowBalloonTip(3000, title, message, icon);
    }

    private static string TrimForEmail(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(No comments provided.)";
        }

        var clean = text.Trim();

        if (clean.Length <= maxLength)
        {
            return clean;
        }

        return clean[..maxLength] + "...";
    }

    private static string FormatRectangle(Rectangle? rectangle)
    {
        if (rectangle is null)
        {
            return "Unknown";
        }

        return $"X={rectangle.Value.X}, Y={rectangle.Value.Y}, Width={rectangle.Value.Width}, Height={rectangle.Value.Height}";
    }

    private static string GetCurrentDpiInfo()
    {
        try
        {
            using var form = new Form();
            using var graphics = form.CreateGraphics();

            return $"{graphics.DpiX:0} x {graphics.DpiY:0}";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);

            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatEnabled(bool value)
    {
        return value ? "Enabled" : "Disabled";
    }

    private static void ClearStatusSelection(TextBox statusBox)
    {
        statusBox.SelectionStart = 0;
        statusBox.SelectionLength = 0;
    }

    private static Dictionary<string, string> ParseRoutesJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            var manifest = JsonSerializer.Deserialize<RouteManifest>(json, options);

            if (manifest?.Routes is not null && manifest.Routes.Count > 0)
            {
                return manifest.Routes
                    .Where(pair =>
                        !string.IsNullOrWhiteSpace(pair.Key) &&
                        pair.Value is not null &&
                        string.Equals(pair.Value.Type, "redirect", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(pair.Value.Target))
                    .ToDictionary(
                        pair => pair.Key.Trim().ToLowerInvariant(),
                        pair => pair.Value.Target.Trim(),
                        StringComparer.OrdinalIgnoreCase
                    );
            }
        }
        catch
        {
            // Fall back to legacy dictionary format below.
        }

        var legacyRoutes = JsonSerializer.Deserialize<Dictionary<string, string>>(json, options);

        return legacyRoutes?
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => pair.Value.Trim(),
                StringComparer.OrdinalIgnoreCase
            ) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void UninstallBrentNetClient()
    {
        var choice = MessageBox.Show(
            "This will remove BrentNET Client from this computer.\n\nBrentNET routing will be stopped and the managed .brent entries will be removed from the Windows hosts file before uninstalling.\n\nContinue?",
            "Uninstall BrentNET Client",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning
        );

        if (choice != DialogResult.Yes)
        {
            return;
        }

        try
        {
            HostsManager.RemoveRoutes();

            var uninstallerPath = Path.Combine(AppContext.BaseDirectory, "unins000.exe");

            if (!File.Exists(uninstallerPath))
            {
                ShowError($"BrentNET Client uninstaller was not found.\n\nExpected location:\n{uninstallerPath}");
                return;
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = uninstallerPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SILENT /NORESTART"
            };

            Process.Start(processInfo);

            ShowMaintenanceBalloon(
                "BrentNET Client",
                "Uninstaller launched. BrentNET Client is closing.",
                ToolTipIcon.Info
            );

            ExitThread();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Could not uninstall BrentNET Client.", ex);
            ShowError($"Could not uninstall BrentNET Client.\n\n{ex.Message}");
        }
    }

    private static async Task<string> CalculateSha256Async(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }

    private static string FormatLocalTime(DateTime? utcTime)
    {
        if (utcTime is null)
        {
            return "Never";
        }

        return utcTime.Value
            .ToLocalTime()
            .ToString("yyyy-MM-dd h:mm:ss tt");
    }

    private void TogglePause()
    {
        if (_isPaused)
        {
            ResumeBrentNet();
        }
        else
        {
            PauseBrentNet();
        }
    }

    private void PauseBrentNet()
    {
        try
        {
            HostsManager.RemoveRoutes();

            _isPaused = true;

            if (_pauseMenuItem is not null)
            {
                _pauseMenuItem.Text = "Resume BrentNET";
                _pauseMenuItem.Checked = true;
            }

            _trayIcon.Text = "BrentNET Client - Paused";

            ShowMaintenanceBalloon(
                "BrentNET Client",
                "BrentNET routing is paused.",
                ToolTipIcon.Info
            );
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Could not pause BrentNET.", ex);
            ShowError($"Could not pause BrentNET.\n\n{ex.Message}");
        }
    }

    private void ResumeBrentNet()
    {
        try
        {
            Dictionary<string, string> routesSnapshot;

            lock (_routesLock)
            {
                routesSnapshot = new Dictionary<string, string>(_routes, StringComparer.OrdinalIgnoreCase);
            }

            HostsManager.InstallRoutes(routesSnapshot.Keys);

            _isPaused = false;

            if (_pauseMenuItem is not null)
            {
                _pauseMenuItem.Text = "Pause BrentNET";
                _pauseMenuItem.Checked = false;
            }

            _trayIcon.Text = "BrentNET Client";

            ShowMaintenanceBalloon(
                "BrentNET Client",
                $"BrentNET routing resumed with {routesSnapshot.Count} routes.",
                ToolTipIcon.Info
            );
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Could not resume BrentNET.", ex);
            ShowError($"Could not resume BrentNET.\n\n{ex.Message}");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            MessageBox.Show(
                $"Could not open {url}",
                "BrentNET Client",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private static void OpenFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            CrashLogger.LogException($"Could not open folder: {folderPath}", ex);

            MessageBox.Show(
                $"Could not open folder:\n{folderPath}",
                "BrentNET Client",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private async Task StartBrentNetAsync(CancellationToken token)
    {
        try
        {
            var routesPath = Path.Combine(AppContext.BaseDirectory, "routes.json");

            if (!File.Exists(routesPath))
            {
                ShowError($"routes.json not found.\nExpected location:\n{routesPath}");
                return;
            }

            var localRoutes = ParseRoutesJson(File.ReadAllText(routesPath));
            ApplyRoutes(localRoutes, "Local routes.json", showRouteBalloon: true);

            await SyncRemoteRoutesAsync(showInteractiveMessages: false);

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://*:80/");
            _listener.Start();

            CrashLogger.LogInfo("HTTP listener started.");

            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpListenerException ex)
                {
                    CrashLogger.LogException("HTTP listener stopped.", ex);
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleRequestAsync(context);
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.LogException("Request handling failed.", ex);
                    }
                }, token);
            }
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("StartBrentNetAsync failed.", ex);
            ShowError(ex.Message);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (_isPaused)
        {
            response.StatusCode = 503;
            response.ContentType = "text/plain";

            using var writer = new StreamWriter(response.OutputStream);
            await writer.WriteAsync("BrentNET Client is currently paused.");

            return;
        }

        var host = request.UserHostName?.Trim().ToLowerInvariant() ?? string.Empty;

        if (host.Contains(":"))
        {
            host = host.Split(':')[0];
        }

        Dictionary<string, string> routesSnapshot;

        lock (_routesLock)
        {
            routesSnapshot = new Dictionary<string, string>(_routes, StringComparer.OrdinalIgnoreCase);
        }

        if (!routesSnapshot.TryGetValue(host, out var destination))
        {
            response.StatusCode = 404;
            response.ContentType = "text/plain";

            using var writer = new StreamWriter(response.OutputStream);
            await writer.WriteAsync($"No BrentNET route found for '{host}'");

            return;
        }

        response.StatusCode = 302;
        response.RedirectLocation = destination;
        response.Close();
    }

    private void ShowError(string message)
    {
        _trayIcon.ShowBalloonTip(
            5000,
            "BrentNET Client Error",
            message,
            ToolTipIcon.Error
        );

        MessageBox.Show(
            message,
            "BrentNET Client Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error
        );
    }

    protected override void ExitThreadCore()
    {
        CrashLogger.LogInfo("BrentNET Client shutting down.");

        _shutdown.Cancel();
        _httpClient.Dispose();

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Listener shutdown cleanup failed.", ex);
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();

        base.ExitThreadCore();
    }
}

public static class AppPaths
{
    public static string AppDataDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BrentNET Helper"
    );

    public static string LogsDirectoryPath => Path.Combine(AppDataDirectoryPath, "Logs");

    public static string BugReportsDirectoryPath => Path.Combine(AppDataDirectoryPath, "BugReports");
}

public sealed class AppSettings
{
    public static string SettingsDirectoryPath => AppPaths.AppDataDirectoryPath;

    public static string SettingsFilePath => Path.Combine(SettingsDirectoryPath, "settings.json");

    [JsonPropertyName("updateAlertsEnabled")]
    public bool UpdateAlertsEnabled { get; set; } = false;

    [JsonPropertyName("routeAlertsEnabled")]
    public bool RouteAlertsEnabled { get; set; } = false;

    [JsonPropertyName("maintenanceAlertsEnabled")]
    public bool MaintenanceAlertsEnabled { get; set; } = false;

    [JsonPropertyName("lastNotifiedVersion")]
    public string LastNotifiedVersion { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectoryPath);

            if (!File.Exists(SettingsFilePath))
            {
                var settings = new AppSettings();
                settings.Save();
                return settings;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Could not load settings.", ex);
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectoryPath);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(SettingsFilePath, json);
    }
}

public static class CrashLogger
{
    private static readonly object LockObject = new();

    public static void Initialize()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectoryPath);
        Directory.CreateDirectory(AppPaths.BugReportsDirectoryPath);

        LogInfo("Crash logger initialized.");
    }

    public static void LogInfo(string message)
    {
        WriteLog("app", "INFO", message, null);
    }

    public static void LogText(string source, string text)
    {
        WriteLog("crash", "ERROR", $"{source}\r\n\r\n{text}", null);
    }

    public static void LogException(string source, Exception exception)
    {
        WriteLog("crash", "ERROR", source, exception);
    }

    public static string? GetLatestCrashReportPath()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectoryPath);

            return Directory
                .GetFiles(AppPaths.LogsDirectoryPath, "crash-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLog(string prefix, string level, string message, Exception? exception)
    {
        lock (LockObject)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectoryPath);

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss-fff");
                var fileName = prefix == "crash"
                    ? $"crash-{timestamp}.txt"
                    : $"app-{DateTime.Now:yyyy-MM-dd}.txt";

                var path = Path.Combine(AppPaths.LogsDirectoryPath, fileName);

                var sb = new StringBuilder();

                sb.AppendLine($"Timestamp Local: {DateTime.Now:yyyy-MM-dd h:mm:ss.fff tt}");
                sb.AppendLine($"Timestamp UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
                sb.AppendLine($"Level: {level}");
                sb.AppendLine($"Source: {message}");
                sb.AppendLine($"App Version: {GetAppVersion()}");
                sb.AppendLine($"OS Version: {Environment.OSVersion}");
                sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
                sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
                sb.AppendLine();

                if (exception is not null)
                {
                    AppendException(sb, exception);
                }

                if (prefix == "app")
                {
                    File.AppendAllText(path, sb.ToString() + Environment.NewLine);
                }
                else
                {
                    File.WriteAllText(path, sb.ToString());
                    File.WriteAllText(Path.Combine(AppPaths.LogsDirectoryPath, "latest-crash.txt"), sb.ToString());
                }
            }
            catch
            {
                // Last-resort logging failure. Do not throw from logger.
            }
        }
    }

    private static void AppendException(StringBuilder sb, Exception exception)
    {
        var current = exception;
        var depth = 0;

        while (current is not null)
        {
            sb.AppendLine($"Exception Depth: {depth}");
            sb.AppendLine($"Type: {current.GetType().FullName}");
            sb.AppendLine($"Message: {current.Message}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(current.StackTrace ?? "(No stack trace.)");
            sb.AppendLine();

            current = current.InnerException;
            depth++;
        }
    }

    private static Version GetAppVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
    }
}

public static class RuntimeInformationHelper
{
    public static string ProcessArchitecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();

    public static string OSArchitecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
}

public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;
    public string InstallerUrl { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? Sha256 { get; set; }
    public string? Notes { get; set; }
}

public sealed class RouteManifest
{
    public int Version { get; set; }
    public string? Updated { get; set; }
    public Dictionary<string, RouteEntry> Routes { get; set; } = new();
}

public sealed class RouteEntry
{
    public string Type { get; set; } = "redirect";
    public string Target { get; set; } = string.Empty;
}