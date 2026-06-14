using System;
using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;

namespace VelocityShare.Mobile
{
    public partial class MainPage : ContentPage
    {
        private readonly FileSyncClient _syncClient;
        private string _myPeerId = "";
        private bool _isSyncing = false;

        public MainPage()
        {
            InitializeComponent();
            _syncClient = new FileSyncClient();
            
            // Set up event listeners
            _syncClient.OnLog += LogToConsole;
            _syncClient.OnStatusChanged += UpdateStatus;

            // Generate unique random peer ID for this mobile session
            Random rnd = new Random();
            _myPeerId = $"peer_mob_{rnd.Next(100000, 999999)}";
            MyPeerIdLabel.Text = _myPeerId;

            // Configure default cross-platform local sync folder path
            string defaultSyncPath = Path.Combine(FileSystem.AppDataDirectory, "Sync");
            LocalPathEntry.Text = defaultSyncPath;
        }

        private async void OnToggleSyncClicked(object sender, EventArgs e)
        {
            if (!_isSyncing)
            {
                string serverUrl = ServerUrlEntry.Text.Trim();
                string localPath = LocalPathEntry.Text.Trim();
                string targetPeerId = TargetPeerIdEntry.Text.Trim();

                if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(localPath) || string.IsNullOrEmpty(targetPeerId))
                {
                    await DisplayAlert("Validation Error", "Please fill in all configuration fields.", "OK");
                    return;
                }

                try
                {
                    ToggleSyncBtn.IsEnabled = false;
                    LogToConsole("[App] Activating folder synchronization...");
                    
                    await _syncClient.StartAsync(localPath, serverUrl, _myPeerId, targetPeerId);
                    
                    _isSyncing = true;
                    ToggleSyncBtn.Text = "STOP FOLDER SYNC";
                    ToggleSyncBtn.BackgroundColor = Color.FromArgb("#ff3366");
                    ToggleSyncBtn.TextColor = Colors.White;
                }
                catch (Exception ex)
                {
                    LogToConsole($"[App Error] Failed to start: {ex.Message}");
                    await DisplayAlert("Sync Error", $"Could not start synchronization engine: {ex.Message}", "OK");
                }
                finally
                {
                    ToggleSyncBtn.IsEnabled = true;
                }
            }
            else
            {
                try
                {
                    ToggleSyncBtn.IsEnabled = false;
                    LogToConsole("[App] Deactivating folder synchronization...");
                    
                    await _syncClient.StopAsync();
                    
                    _isSyncing = false;
                    ToggleSyncBtn.Text = "START FOLDER SYNC";
                    ToggleSyncBtn.BackgroundColor = Color.FromArgb("#00ff66");
                    ToggleSyncBtn.TextColor = Color.FromArgb("#080a10");
                }
                catch (Exception ex)
                {
                    LogToConsole($"[App Error] Failed to stop cleanly: {ex.Message}");
                }
                finally
                {
                    ToggleSyncBtn.IsEnabled = true;
                }
            }
        }

        private void LogToConsole(string message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                LogLabel.Text += $"{DateTime.Now:HH:mm:ss} - {message}\n";
                // Auto-scroll logs
                _ = LogScroll.ScrollToAsync(0, LogLabel.Height, true);
            });
        }

        private void UpdateStatus(string status)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusBadge.Text = status;
                if (status == "ACTIVE")
                {
                    StatusBadge.TextColor = Color.FromArgb("#00ff66");
                }
                else if (status == "CONNECTING")
                {
                    StatusBadge.TextColor = Color.FromArgb("#ffbb00");
                }
                else
                {
                    StatusBadge.TextColor = Color.FromArgb("#ff3366");
                }
            });
        }

        protected override async void OnDisappearing()
        {
            base.OnDisappearing();
            if (_isSyncing)
            {
                await _syncClient.StopAsync();
            }
        }
    }
}
