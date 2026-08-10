using System;
using System.IO;
using System.Threading;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Color = Microsoft.Maui.Graphics.Color;

namespace VelocityShare.Mobile
{
    public partial class MainPage : ContentPage
    {
        private readonly FileSyncClient _syncClient;
        private string _myPeerId = "";
        private bool _isSyncing = false;
        private int _logEventCount = 0;
        private int _filesSynced = 0;
        private long _dataSentBytes = 0;
        private DateTime _syncStartTime;
        private Timer? _uptimeTimer;

        public MainPage()
        {
            InitializeComponent();
            _syncClient = new FileSyncClient();

            // Set up event listeners
            _syncClient.OnLog += LogToConsole;
            _syncClient.OnStatusChanged += UpdateStatus;
            _syncClient.OnFileSynced += OnFileSynced;

            // Generate unique random peer ID for this mobile session
            Random rnd = new Random();
            _myPeerId = $"peer_mob_{rnd.Next(100000, 999999)}";
            MyPeerIdLabel.Text = _myPeerId;

            // Configure default cross-platform local sync folder path
            string defaultSyncPath = Path.Combine(FileSystem.AppDataDirectory, "Sync");
            LocalPathEntry.Text = defaultSyncPath;
        }

        private async void OnToggleSyncClicked(object? sender, EventArgs e)
        {
            if (!_isSyncing)
            {
                string serverUrl = ServerUrlEntry.Text?.Trim() ?? "";
                string localPath = LocalPathEntry.Text?.Trim() ?? "";
                string targetPeerId = TargetPeerIdEntry.Text?.Trim() ?? "";

                if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(localPath) || string.IsNullOrEmpty(targetPeerId))
                {
                    await this.DisplayAlertAsync("Validation Error",
                        "Please fill in all configuration fields before starting sync.",
                        "OK");
                    return;
                }

                try
                {
                    ToggleSyncBtn.IsEnabled = false;
                    ToggleSyncBtn.Text = "CONNECTING...";
                    LogToConsole("[App] Activating folder synchronization...");

                    await _syncClient.StartAsync(localPath, serverUrl, _myPeerId, targetPeerId);

                    _isSyncing = true;
                    _syncStartTime = DateTime.UtcNow;
                    _filesSynced = 0;
                    _dataSentBytes = 0;

                    // Start uptime timer
                    _uptimeTimer = new Timer(UpdateUptime, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

                    ToggleSyncBtn.Text = "STOP FOLDER SYNC";
                    ToggleSyncBtn.BackgroundColor = Color.FromArgb("#ef4444");
                    ToggleSyncBtn.TextColor = Colors.White;
                    ToggleSyncBtn.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    LogToConsole($"[Error] Failed to start: {ex.Message}");
                    await this.DisplayAlertAsync("Sync Error",
                        $"Could not start synchronization engine:\n{ex.Message}",
                        "OK");
                    ToggleSyncBtn.Text = "START FOLDER SYNC";
                    ToggleSyncBtn.IsEnabled = true;
                }
            }
            else
            {
                try
                {
                    ToggleSyncBtn.IsEnabled = false;
                    ToggleSyncBtn.Text = "STOPPING...";
                    LogToConsole("[App] Deactivating folder synchronization...");

                    _uptimeTimer?.Dispose();
                    _uptimeTimer = null;

                    await _syncClient.StopAsync();

                    _isSyncing = false;
                    ToggleSyncBtn.Text = "START FOLDER SYNC";
                    ToggleSyncBtn.BackgroundColor = Color.FromArgb("#00ff66");
                    ToggleSyncBtn.TextColor = Color.FromArgb("#0a0c12");
                    ToggleSyncBtn.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    LogToConsole($"[Error] Failed to stop cleanly: {ex.Message}");
                }
                finally
                {
                    ToggleSyncBtn.IsEnabled = true;
                }
            }
        }

        private async void OnCopyIdClicked(object? sender, EventArgs e)
        {
            await Clipboard.SetTextAsync(_myPeerId);
            CopyIdBtn.Text = "Copied!";
            LogToConsole($"[UI] Peer ID copied to clipboard: {_myPeerId}");

            // Reset button text after 2 seconds
            await Task.Delay(2000);
            if (CopyIdBtn != null)
                CopyIdBtn.Text = "Copy ID";
        }

        private async void OnBrowseFolderClicked(object? sender, EventArgs e)
        {
            // On MAUI, folder picking is platform-specific. Show the current path
            // and let the user edit it directly for now.
            var result = await DisplayPromptAsync(
                "Sync Folder",
                "Enter the full path to the folder you want to sync:",
                initialValue: LocalPathEntry.Text,
                accept: "Select",
                cancel: "Cancel");

            if (!string.IsNullOrEmpty(result))
            {
                LocalPathEntry.Text = result;
                LogToConsole($"[UI] Sync folder set to: {result}");
            }
        }

        private void OnFileSynced(string fileName, long fileSize)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _filesSynced++;
                _dataSentBytes += fileSize;
                FilesSyncedLabel.Text = _filesSynced.ToString();
                DataSentLabel.Text = FormatFileSize(_dataSentBytes);
            });
        }

        private void UpdateUptime(object? state)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_isSyncing) return;
                var elapsed = DateTime.UtcNow - _syncStartTime;
                if (elapsed.TotalHours >= 1)
                    UptimeLabel.Text = $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
                else if (elapsed.TotalMinutes >= 1)
                    UptimeLabel.Text = $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
                else
                    UptimeLabel.Text = $"{elapsed.Seconds}s";
            });
        }

        private void UpdateStatus(string status)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                switch (status.ToUpperInvariant())
                {
                    case "ACTIVE":
                        ConnectionDot.Color = Color.FromArgb("#00ff66");
                        ConnectionLabel.Text = "Secure";
                        ConnectionLabel.TextColor = Color.FromArgb("#00ff66");
                        StatusBadge.BackgroundColor = Color.FromArgb("#001a0d");
                        StatusBadge.Stroke = Color.FromArgb("#00ff66");
                        StatusBadgeLabel.Text = "ACTIVE";
                        StatusBadgeLabel.TextColor = Color.FromArgb("#00ff66");
                        break;
                    case "CONNECTING":
                        ConnectionDot.Color = Color.FromArgb("#f59e0b");
                        ConnectionLabel.Text = "Handshake";
                        ConnectionLabel.TextColor = Color.FromArgb("#f59e0b");
                        StatusBadge.BackgroundColor = Color.FromArgb("#1a1500");
                        StatusBadge.Stroke = Color.FromArgb("#f59e0b");
                        StatusBadgeLabel.Text = "CONNECTING";
                        StatusBadgeLabel.TextColor = Color.FromArgb("#f59e0b");
                        break;
                    default:
                        ConnectionDot.Color = Color.FromArgb("#ef4444");
                        ConnectionLabel.Text = "Offline";
                        ConnectionLabel.TextColor = Color.FromArgb("#94a3b8");
                        StatusBadge.BackgroundColor = Color.FromArgb("#1a0008");
                        StatusBadge.Stroke = Color.FromArgb("#ef4444");
                        StatusBadgeLabel.Text = "INACTIVE";
                        StatusBadgeLabel.TextColor = Color.FromArgb("#ef4444");
                        UptimeLabel.Text = "--:--";
                        break;
                }
            });
        }

        private void LogToConsole(string message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _logEventCount++;
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                LogLabel.Text += $"{timestamp}  {message}\n";

                // Update event counter
                LogCountLabel.Text = $"{_logEventCount} events";

                // Color-code log entries
                if (message.Contains("[Error]") || message.Contains("Error"))
                    LogLabel.TextColor = Color.FromArgb("#ef4444");
                else if (message.Contains("[Sync Client]"))
                    LogLabel.TextColor = Color.FromArgb("#00e5ff");
                else
                    LogLabel.TextColor = Color.FromArgb("#00ff66");

                // Auto-scroll to bottom
                _ = LogScroll.ScrollToAsync(0, LogScroll.ContentSize.Height, true);
            });
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
