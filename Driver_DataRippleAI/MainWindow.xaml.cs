using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using DataRippleAIDesktop.Models;
using DataRippleAIDesktop.Services;
using DataRippleAIDesktop.Views;
using DataRippleAIDesktop.HelperControls;
using static DataRippleAIDesktop.Models.EnumsInfo;
using Color = System.Windows.Media.Color;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using NAudio.CoreAudioApi;

namespace DataRippleAIDesktop
{
    public partial class MainWindow : Window
    {
        // Static instance tracking to prevent duplicates
        private static MainWindow _instance = null;
        private static readonly object _instanceLock = new object();
        
        // Services
        private TokenRefreshService _tokenRefreshService = null;
        private NetworkStatsService _networkStatsService = null;
        private BackendApiHealthCheckService _backendApiHealthCheck = null;
        
        private VoiceSessionPage VoiceSessionPage { get; set; } = null;
        private bool IsCurretlySelected { get; set; } = false;
        private CurrentPage _CurrentPage { get; set; }
        // Timer field removed - no longer needed
        
        // System Tray
        private NotifyIcon _notifyIcon = null;
        private bool _isClosing = false;
        private ToolStripMenuItem _micDeviceMenu = null;
        private ToolStripMenuItem _speakerDeviceMenu = null;
        private System.Windows.Threading.DispatcherTimer _audioStatusTimer = null;
        
        // Inter-process communication for showing notifications from new instances
        private static System.Threading.EventWaitHandle _showNotificationEvent = null;
        private System.Windows.Threading.DispatcherTimer _notificationListenerTimer = null;

        public MainWindow()
        {
            // Prevent multiple instances
            lock (_instanceLock)
            {
                if (_instance != null)
                {
                    LoggingService.Warn("[MainWindow] Another MainWindow instance already exists - disposing this one");
                    // Dispose any existing tray icon from previous instance
                    if (_instance._notifyIcon != null)
                    {
                        try
                        {
                            _instance._notifyIcon.Visible = false;
                            _instance._notifyIcon.Dispose();
                            _instance._notifyIcon = null;
                        }
                        catch { }
                    }
                }
                _instance = this;
            }
            
            InitializeComponent();
            
            // Initialize system tray icon FIRST
            InitializeSystemTray();
            
            // Start hidden (tray only) - window will be shown when user clicks tray icon
            // Set these BEFORE Loaded event to prevent window from showing
            this.WindowState = WindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Visibility = Visibility.Hidden; // Use Visibility.Hidden instead of Hide() to prevent showing
            
            // Subscribe to window loaded event
            this.Loaded += MainWindow_Loaded;
            
            // Subscribe to window closing event for proper cleanup
            this.Closing += MainWindow_Closing;
            
            // Subscribe to window state changed to intercept minimize and hide to tray
            this.StateChanged += MainWindow_StateChanged;
            
            InitializeServices();
            CheckLoggedInUser();
            
            // Initialize call button state to "Start Call"
            SetCallButtonState(false);
        }

        /// <summary>
        /// Initialize system tray icon with context menu
        /// </summary>
        private void InitializeSystemTray()
        {
            try
            {
                // Prevent duplicate tray icons - dispose existing one if it exists
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                
                // Create NotifyIcon
                _notifyIcon = new NotifyIcon();
                
                // Try multiple locations to load the icon
                System.Drawing.Icon? appIcon = null;
                
                // Method 1: Try loading from executable's embedded icon (ApplicationIcon)
                try
                {
                    // Get the icon from the application executable
                    string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[MainWindow] Could not extract icon from executable: {ex.Message}");
                }
                
                // Method 2: Try loading from output directory
                if (appIcon == null)
                {
                    string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");
                    if (File.Exists(iconPath))
                    {
                        try
                        {
                            appIcon = new System.Drawing.Icon(iconPath);
                            LoggingService.Info($"[MainWindow] Loaded tray icon from: {iconPath}");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Warn($"[MainWindow] Could not load icon from {iconPath}: {ex.Message}");
                        }
                    }
                }
                
                // Method 3: Try loading from source directory (for development)
                if (appIcon == null)
                {
                    string sourceIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "logo.ico");
                    if (File.Exists(sourceIconPath))
                    {
                        try
                        {
                            appIcon = new System.Drawing.Icon(sourceIconPath);
                            LoggingService.Info($"[MainWindow] Loaded tray icon from source: {sourceIconPath}");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Warn($"[MainWindow] Could not load icon from source: {ex.Message}");
                        }
                    }
                }
                
                // Fallback to default system icon if all methods fail
                if (appIcon != null)
                {
                    _notifyIcon.Icon = appIcon;
                    LoggingService.Info("[MainWindow] Tray icon set successfully");
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                    LoggingService.Warn("[MainWindow] Using default system icon for tray (logo.ico not found)");
                }
                
                _notifyIcon.Text = "DataRippleAI";
                _notifyIcon.Visible = true;
                
                // Create context menu
                ContextMenuStrip contextMenu = new ContextMenuStrip();
                
                // Show/Expand menu item
                ToolStripMenuItem showMenuItem = new ToolStripMenuItem("Show/Expand");
                showMenuItem.Click += (s, e) => ShowWindow();
                contextMenu.Items.Add(showMenuItem);
                
                // Separator
                contextMenu.Items.Add(new ToolStripSeparator());
                
                // Audio Device Selection Section
                // Microphone selection submenu
                _micDeviceMenu = new ToolStripMenuItem("Microphone");
                UpdateMicrophoneDeviceMenu(_micDeviceMenu);
                contextMenu.Items.Add(_micDeviceMenu);
                
                // Speaker selection submenu
                _speakerDeviceMenu = new ToolStripMenuItem("Speaker");
                UpdateSpeakerDeviceMenu(_speakerDeviceMenu);
                contextMenu.Items.Add(_speakerDeviceMenu);
                
                // Separator
                contextMenu.Items.Add(new ToolStripSeparator());
                
                // Exit menu item
                ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("Exit");
                exitMenuItem.Click += (s, e) => ExitApplication();
                contextMenu.Items.Add(exitMenuItem);
                
                _notifyIcon.ContextMenuStrip = contextMenu;
                
                // Start audio status monitoring timer
                StartAudioStatusMonitoring();
                
                // Double-click on tray icon to show window
                _notifyIcon.DoubleClick += (s, e) => ShowWindow();
                
                // Single click to show window (optional - can be removed if double-click is preferred)
                _notifyIcon.Click += (s, e) =>
                {
                    if (e is MouseEventArgs mouseArgs && mouseArgs.Button == MouseButtons.Left)
                    {
                        ShowWindow();
                    }
                };
                
                LoggingService.Info("[MainWindow] System tray icon initialized");
                
                // Initialize inter-process communication for notifications
                InitializeNotificationListener();
                
                // Show startup notification after tray icon is ready
                // Use DispatcherTimer to ensure icon is fully registered with Windows
                System.Windows.Threading.DispatcherTimer notificationTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2) // Wait 2 seconds for tray icon to be ready
                };
                notificationTimer.Tick += (s, e) =>
                {
                    notificationTimer.Stop();
                    try
                    {
                        if (_notifyIcon != null && _notifyIcon.Visible)
                        {
                            LoggingService.Info($"[MainWindow] Showing startup notification. NotifyIcon visible: {_notifyIcon.Visible}");
                            
                            // Show notification
                            ShowTrayNotification("DataRippleAI is running", "The application is running in the system tray. Click the icon to open.", ToolTipIcon.Info);
                            
                            LoggingService.Info("[MainWindow] Startup notification triggered");
                        }
                        else
                        {
                            LoggingService.Warn($"[MainWindow] Tray icon not ready: null={_notifyIcon == null}, visible={_notifyIcon?.Visible ?? false}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[MainWindow] Error showing startup notification: {ex.Message}\n{ex.StackTrace}");
                    }
                };
                notificationTimer.Start();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error initializing system tray: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Initialize listener for inter-process notification requests
        /// </summary>
        private void InitializeNotificationListener()
        {
            try
            {
                // Create or open a named event for inter-process communication
                const string eventName = "DataRippleAI_ShowNotification_Event";
                _showNotificationEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, eventName);
                
                // Start a timer to periodically check for notification requests
                _notificationListenerTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500) // Check every 500ms
                };
                _notificationListenerTimer.Tick += (s, e) =>
                {
                    try
                    {
                        // Check if event was signaled (new instance trying to start)
                        if (_showNotificationEvent != null && _showNotificationEvent.WaitOne(0))
                        {
                            // Show notification that another instance tried to start
                            ShowTrayNotification("DataRippleAI is already running", "The application is already running in the system tray. Click the icon to open.", ToolTipIcon.Info);
                            
                            // Also try to bring window to front
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (this.Visibility == Visibility.Hidden)
                                    {
                                        ShowWindow();
                                    }
                                    else
                                    {
                                        this.Activate();
                                        this.WindowState = WindowState.Maximized;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LoggingService.Warn($"[MainWindow] Could not bring window to front: {ex.Message}");
                                }
                            }), System.Windows.Threading.DispatcherPriority.Normal);
                            
                            LoggingService.Info("[MainWindow] Notification shown because another instance tried to start");
                        }
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"[MainWindow] Error checking notification event: {ex.Message}");
                    }
                };
                _notificationListenerTimer.Start();
                
                LoggingService.Info("[MainWindow] Notification listener initialized");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error initializing notification listener: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Signal the existing instance to show a notification
        /// This is called from a new instance that's about to exit
        /// </summary>
        public static void SignalShowNotification()
        {
            try
            {
                const string eventName = "DataRippleAI_ShowNotification_Event";
                using (var eventHandle = System.Threading.EventWaitHandle.OpenExisting(eventName))
                {
                    eventHandle.Set(); // Signal the existing instance
                    LoggingService.Info("[App] Signaled existing instance to show notification");
                }
            }
            catch (System.Threading.WaitHandleCannotBeOpenedException)
            {
                // Event doesn't exist yet (no existing instance), which is fine
                LoggingService.Info("[App] No existing instance found to signal");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[App] Error signaling existing instance: {ex.Message}");
            }
        }
        
        private Window _notificationWindow = null;
        
        /// <summary>
        /// Show a custom popup notification near the system tray icon
        /// This creates a custom WPF window that appears as a popup, bypassing Windows Action Center
        /// </summary>
        private void ShowTrayNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
        {
            try
            {
                if (_notifyIcon == null)
                {
                    LoggingService.Warn("[MainWindow] Cannot show notification: NotifyIcon is null");
                    return;
                }
                
                // Ensure we're on the UI thread
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => ShowTrayNotification(title, message, icon)));
                    return;
                }
                
                // Close any existing notification window
                if (_notificationWindow != null)
                {
                    try
                    {
                        _notificationWindow.Close();
                    }
                    catch { }
                    _notificationWindow = null;
                }
                
                // Update tooltip (NotifyIcon.Text limit is 127 chars)
                string tooltipText = $"{title}\n{message}";
                if (tooltipText.Length > 127) tooltipText = tooltipText.Substring(0, 124) + "...";
                _notifyIcon.Text = tooltipText;
                
                // Get system tray location
                System.Drawing.Point trayLocation = GetTrayLocation();
                
                // Calculate notification position (bottom-right of screen, above taskbar)
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;
                double notificationWidth = 320;
                double notificationHeight = 100;
                
                double left = screenWidth - notificationWidth - 20; // 20px from right edge
                double top = screenHeight - notificationHeight - 80; // 80px above bottom (above taskbar)
                
                LoggingService.Info($"[MainWindow] Notification position: Left={left}, Top={top}, Screen={screenWidth}x{screenHeight}");
                
                // Create custom notification window
                _notificationWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = System.Windows.Media.Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Left = left,
                    Top = top,
                };
                
                // Create content for notification
                Border border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x13, 0x23, 0x40)), // #132340
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xFF)), // #00D4FF
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(15, 12, 15, 12),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black,
                        Direction = 315,
                        ShadowDepth = 5,
                        Opacity = 0.5,
                        BlurRadius = 10
                    }
                };
                
                StackPanel panel = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal
                };
                
                // Icon
                TextBlock iconBlock = new TextBlock
                {
                    Text = "🔔",
                    FontSize = 20,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(iconBlock);
                
                // Text content
                StackPanel textPanel = new StackPanel();
                TextBlock titleBlock = new TextBlock
                {
                    Text = title,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                TextBlock messageBlock = new TextBlock
                {
                    Text = message,
                    FontSize = 12,
                    Foreground = System.Windows.Media.Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280
                };
                textPanel.Children.Add(titleBlock);
                textPanel.Children.Add(messageBlock);
                panel.Children.Add(textPanel);
                
                border.Child = panel;
                _notificationWindow.Content = border;
                
                // Show window with fade-in animation
                _notificationWindow.Opacity = 0;
                _notificationWindow.Visibility = Visibility.Visible;
                _notificationWindow.Show();
                _notificationWindow.Activate();
                _notificationWindow.Focus();
                
                // Force window to be on top
                _notificationWindow.Topmost = true;
                
                // Animate fade-in
                DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
                _notificationWindow.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                
                LoggingService.Info($"[MainWindow] Notification window shown at position: Left={_notificationWindow.Left}, Top={_notificationWindow.Top}, Visible={_notificationWindow.Visibility}, Topmost={_notificationWindow.Topmost}");
                
                // Auto-close after delay
                int timeout = title.Contains("running") ? 5000 : 4000;
                System.Windows.Threading.DispatcherTimer closeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(timeout)
                };
                closeTimer.Tick += (s, e) =>
                {
                    closeTimer.Stop();
                    CloseNotificationWindow();
                };
                closeTimer.Start();
                
                // Make window clickable to close
                _notificationWindow.MouseDown += (s, e) => CloseNotificationWindow();
                
                LoggingService.Info($"[MainWindow] ✅ Custom notification popup shown: {title} - {message}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error showing custom notification: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Get the system tray icon location on screen
        /// </summary>
        private System.Drawing.Point GetTrayLocation()
        {
            try
            {
                // Get taskbar position
                System.Drawing.Rectangle taskbarRect = GetTaskbarBounds();
                
                // Tray is typically in bottom-right (or bottom-left, top-right, top-left depending on taskbar position)
                return new System.Drawing.Point(
                    taskbarRect.Right - 50,
                    taskbarRect.Bottom - 50
                );
            }
            catch
            {
                // Fallback to screen bottom-right
                return new System.Drawing.Point(
                    (int)SystemParameters.PrimaryScreenWidth - 350,
                    (int)SystemParameters.PrimaryScreenHeight - 100
                );
            }
        }
        
        /// <summary>
        /// Get taskbar bounds
        /// </summary>
        private System.Drawing.Rectangle GetTaskbarBounds()
        {
            System.Drawing.Rectangle taskbar = new System.Drawing.Rectangle();
            System.IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
            if (taskbarHandle != IntPtr.Zero)
            {
                GetWindowRect(taskbarHandle, out RECT rect);
                taskbar = new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }
            return taskbar;
        }
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
        
        /// <summary>
        /// Close the notification window with fade-out animation
        /// </summary>
        private void CloseNotificationWindow()
        {
            if (_notificationWindow != null)
            {
                try
                {
                    DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                    fadeOut.Completed += (s, e) =>
                    {
                        try
                        {
                            _notificationWindow?.Close();
                            _notificationWindow = null;
                        }
                        catch { }
                    };
                    _notificationWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                catch
                {
                    try
                    {
                        _notificationWindow?.Close();
                        _notificationWindow = null;
                    }
                    catch { }
                }
            }
        }
        
        /// <summary>
        /// Show and restore the main window
        /// </summary>
        private void ShowWindow()
        {
            try
            {
                // Make window visible and restore from minimized state
                this.Visibility = Visibility.Visible;
                this.ShowInTaskbar = true;
                
                if (this.WindowState == WindowState.Minimized)
                {
                    this.WindowState = WindowState.Normal;
                }
                
                this.Show();
                this.Activate();
            this.WindowState = WindowState.Maximized;
                this.BringIntoView();
                this.Focus();
                
                LoggingService.Info("[MainWindow] Window shown from system tray");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error showing window: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Hide window to system tray
        /// </summary>
        private void HideToTray()
        {
            try
            {
                this.Hide();
                this.ShowInTaskbar = false;
                LoggingService.Info("[MainWindow] Window hidden to system tray");
                
                // Show notification that app is minimized to tray
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        ShowTrayNotification("DataRippleAI minimized", "The application is running in the system tray. Click the icon to restore.", ToolTipIcon.Info);
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Error($"[MainWindow] Error showing minimize notification: {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error hiding window to tray: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Exit application completely
        /// </summary>
        private void ExitApplication()
        {
            try
            {
                _isClosing = true;
                
                // Stop notification listener
                if (_notificationListenerTimer != null)
                {
                    _notificationListenerTimer.Stop();
                    _notificationListenerTimer = null;
                }
                
                // Dispose event handle
                if (_showNotificationEvent != null)
                {
                    _showNotificationEvent.Dispose();
                    _showNotificationEvent = null;
                }
                
                // Clear static instance
                lock (_instanceLock)
                {
                    if (_instance == this)
                    {
                        _instance = null;
                    }
                }
                
                // Dispose tray icon
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                
                // Shutdown application
                System.Windows.Application.Current.Shutdown();
                
                LoggingService.Info("[MainWindow] Application exited");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error exiting application: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update microphone device menu with available devices
        /// </summary>
        private void UpdateMicrophoneDeviceMenu(ToolStripMenuItem parentMenu)
        {
            try
            {
                parentMenu.DropDownItems.Clear();
                
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                
                // Get currently selected device from settings
                string currentDevice = GetCurrentMicrophoneDevice();
                
                if (devices.Count == 0)
                {
                    ToolStripMenuItem noDeviceItem = new ToolStripMenuItem("No microphones found");
                    noDeviceItem.Enabled = false;
                    parentMenu.DropDownItems.Add(noDeviceItem);
                }
                else
                {
                    foreach (var device in devices)
                    {
                        ToolStripMenuItem deviceItem = new ToolStripMenuItem(device.FriendlyName);
                        deviceItem.Tag = device;
                        deviceItem.Click += (s, e) => OnMicrophoneDeviceSelected(device);
                        
                        // Mark current device with checkmark
                        if (device.FriendlyName == currentDevice)
                        {
                            deviceItem.Checked = true;
                        }
                        
                        parentMenu.DropDownItems.Add(deviceItem);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error updating microphone device menu: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update speaker device menu with available devices
        /// </summary>
        private void UpdateSpeakerDeviceMenu(ToolStripMenuItem parentMenu)
        {
            try
            {
                parentMenu.DropDownItems.Clear();
                
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                
                // Get currently selected device from settings
                string currentDevice = GetCurrentSpeakerDevice();
                
                if (devices.Count == 0)
                {
                    ToolStripMenuItem noDeviceItem = new ToolStripMenuItem("No speakers found");
                    noDeviceItem.Enabled = false;
                    parentMenu.DropDownItems.Add(noDeviceItem);
                }
                else
                {
                    foreach (var device in devices)
                    {
                        ToolStripMenuItem deviceItem = new ToolStripMenuItem(device.FriendlyName);
                        deviceItem.Tag = device;
                        deviceItem.Click += (s, e) => OnSpeakerDeviceSelected(device);
                        
                        // Mark current device with checkmark
                        if (device.FriendlyName == currentDevice)
                        {
                            deviceItem.Checked = true;
                        }
                        
                        parentMenu.DropDownItems.Add(deviceItem);
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error updating speaker device menu: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get current microphone device from settings
        /// </summary>
        private string GetCurrentMicrophoneDevice()
        {
            try
            {
                if (Globals.ConfigurationInfo != null)
                {
                    var audioDevicesSection = Globals.ConfigurationInfo.GetSection("AudioDevices");
                    if (audioDevicesSection.Exists())
                    {
                        return audioDevicesSection["SelectedMicrophoneDevice"] ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error getting current microphone device: {ex.Message}");
            }
            return "";
        }
        
        /// <summary>
        /// Get current speaker device from settings
        /// </summary>
        private string GetCurrentSpeakerDevice()
        {
            try
            {
                if (Globals.ConfigurationInfo != null)
                {
                    var audioDevicesSection = Globals.ConfigurationInfo.GetSection("AudioDevices");
                    if (audioDevicesSection.Exists())
                    {
                        return audioDevicesSection["SelectedSpeakerDevice"] ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error getting current speaker device: {ex.Message}");
            }
            return "";
        }
        
        /// <summary>
        /// Handle microphone device selection from tray menu
        /// </summary>
        private void OnMicrophoneDeviceSelected(MMDevice device)
        {
            try
            {
                LoggingService.Info($"[MainWindow] Microphone device selected from tray: {device.FriendlyName}");
                
                // Update settings
                UpdateAudioDeviceSettings("SelectedMicrophoneDevice", device.FriendlyName, "MicrophoneDeviceIndex", GetMicrophoneDeviceIndex(device));
                
                // Update AudioRecorderVisualizer if available
                if (VoiceSessionPage != null && VoiceSessionPage.GetAudioRecorderVisualizer() != null)
                {
                    var audioRecorder = VoiceSessionPage.GetAudioRecorderVisualizer();
                    int deviceIndex = GetMicrophoneDeviceIndex(device);
                    audioRecorder.SetMicrophoneDevice(deviceIndex, device.FriendlyName);
                }
                
                // Update menu to show new selection
                UpdateMicrophoneDeviceMenu(_micDeviceMenu);
                
                // Show notification
                ShowTrayNotification("Microphone Changed", $"Microphone set to: {device.FriendlyName}", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error selecting microphone device: {ex.Message}");
                ShowTrayNotification("Error", $"Failed to change microphone: {ex.Message}", ToolTipIcon.Error);
            }
        }
        
        /// <summary>
        /// Handle speaker device selection from tray menu
        /// </summary>
        private void OnSpeakerDeviceSelected(MMDevice device)
        {
            try
            {
                LoggingService.Info($"[MainWindow] Speaker device selected from tray: {device.FriendlyName}");
                
                // Update settings
                UpdateAudioDeviceSettings("SelectedSpeakerDevice", device.FriendlyName, "SpeakerDeviceIndex", GetSpeakerDeviceIndex(device));
                
                // Update AudioRecorderVisualizer if available
                if (VoiceSessionPage != null && VoiceSessionPage.GetAudioRecorderVisualizer() != null)
                {
                    var audioRecorder = VoiceSessionPage.GetAudioRecorderVisualizer();
                    int deviceIndex = GetSpeakerDeviceIndex(device);
                    audioRecorder.SetSpeakerDevice(deviceIndex, device.FriendlyName);
                }
                
                // Update menu to show new selection
                UpdateSpeakerDeviceMenu(_speakerDeviceMenu);
                
                // Show notification
                ShowTrayNotification("Speaker Changed", $"Speaker set to: {device.FriendlyName}", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error selecting speaker device: {ex.Message}");
                ShowTrayNotification("Error", $"Failed to change speaker: {ex.Message}", ToolTipIcon.Error);
            }
        }
        
        /// <summary>
        /// Get microphone device index from MMDevice
        /// </summary>
        private int GetMicrophoneDeviceIndex(MMDevice device)
        {
            try
            {
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                return devices.IndexOf(device);
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Get speaker device index from MMDevice
        /// </summary>
        private int GetSpeakerDeviceIndex(MMDevice device)
        {
            try
            {
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                return devices.IndexOf(device);
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Update audio device settings in appsettings.json
        /// </summary>
        private void UpdateAudioDeviceSettings(string deviceNameKey, string deviceName, string deviceIndexKey, int deviceIndex)
        {
            try
            {
                string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                
                if (File.Exists(configFilePath))
                {
                    string jsonContent = File.ReadAllText(configFilePath);
                    var config = Newtonsoft.Json.Linq.JObject.Parse(jsonContent);
                    
                    // Ensure AudioDevices section exists
                    if (config["AudioDevices"] == null)
                    {
                        config["AudioDevices"] = new Newtonsoft.Json.Linq.JObject();
                    }
                    
                    var audioDevicesSection = (Newtonsoft.Json.Linq.JObject)config["AudioDevices"];
                    audioDevicesSection[deviceNameKey] = deviceName;
                    audioDevicesSection[deviceIndexKey] = deviceIndex;
                    
                    // Write back to file
                    File.WriteAllText(configFilePath, config.ToString(Newtonsoft.Json.Formatting.Indented));
                    
                    // Reload configuration
                    if (Globals.ConfigurationInfo != null)
                    {
                        var builder = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                        Globals.ConfigurationInfo = builder.Build();
                    }
                    
                    LoggingService.Info($"[MainWindow] Updated audio device settings: {deviceNameKey}={deviceName}, {deviceIndexKey}={deviceIndex}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error updating audio device settings: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Start monitoring audio device status
        /// </summary>
        private void StartAudioStatusMonitoring()
        {
            try
            {
                _audioStatusTimer = new System.Windows.Threading.DispatcherTimer();
                _audioStatusTimer.Interval = TimeSpan.FromSeconds(2); // Check every 2 seconds
                _audioStatusTimer.Tick += AudioStatusTimer_Tick;
                _audioStatusTimer.Start();
                
                LoggingService.Info("[MainWindow] Audio status monitoring started");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error starting audio status monitoring: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Monitor audio device status and show alerts
        /// </summary>
        private void AudioStatusTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                CheckAudioDeviceStatus();
                UpdateTrayTooltip();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error in audio status monitoring: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check audio device status and show alerts if needed
        /// </summary>
        // Track notification state to prevent repeated notifications every 2 seconds
        private bool _micDisconnectedNotified = false;
        private bool _micMutedNotified = false;
        private bool _speakerDisconnectedNotified = false;

        private void CheckAudioDeviceStatus()
        {
            try
            {
                MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();

                // Check microphone
                string currentMic = GetCurrentMicrophoneDevice();
                if (!string.IsNullOrEmpty(currentMic))
                {
                    var micDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                    var currentMicDevice = micDevices.FirstOrDefault(d => d.FriendlyName == currentMic);

                    if (currentMicDevice == null)
                    {
                        // Microphone disconnected - only notify once
                        if (!_micDisconnectedNotified)
                        {
                            _micDisconnectedNotified = true;
                            ShowTrayNotification("Microphone Disconnected", $"Microphone '{currentMic}' is no longer available", ToolTipIcon.Warning);
                            LoggingService.Warn($"[MainWindow] Microphone '{currentMic}' is disconnected");
                        }
                    }
                    else
                    {
                        // Microphone is connected again - reset disconnected flag
                        _micDisconnectedNotified = false;

                        // Check if microphone is muted
                        try
                        {
                            if (currentMicDevice.AudioEndpointVolume.Mute)
                            {
                                if (!_micMutedNotified)
                                {
                                    _micMutedNotified = true;
                                    ShowTrayNotification("Microphone Muted", $"Microphone '{currentMic}' is muted", ToolTipIcon.Warning);
                                }
                            }
                            else
                            {
                                // Microphone unmuted - reset flag
                                _micMutedNotified = false;
                            }
                        }
                        catch (Exception micEx)
                        {
                            LoggingService.Debug($"[MainWindow] Error checking mic mute state: {micEx.Message}");
                        }
                    }
                }

                // Check speaker
                string currentSpeaker = GetCurrentSpeakerDevice();
                if (!string.IsNullOrEmpty(currentSpeaker))
                {
                    var speakerDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                    var currentSpeakerDevice = speakerDevices.FirstOrDefault(d => d.FriendlyName == currentSpeaker);

                    if (currentSpeakerDevice == null)
                    {
                        // Speaker disconnected - only notify once
                        if (!_speakerDisconnectedNotified)
                        {
                            _speakerDisconnectedNotified = true;
                            ShowTrayNotification("Speaker Disconnected", $"Speaker '{currentSpeaker}' is no longer available", ToolTipIcon.Warning);
                            LoggingService.Warn($"[MainWindow] Speaker '{currentSpeaker}' is disconnected");
                        }
                    }
                    else
                    {
                        // Speaker connected again - reset flag
                        _speakerDisconnectedNotified = false;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error checking audio device status: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update tray icon tooltip with current status
        /// </summary>
        private void UpdateTrayTooltip()
        {
            try
            {
                string micDevice = GetCurrentMicrophoneDevice();
                string speakerDevice = GetCurrentSpeakerDevice();
                
                string tooltip = "DataRippleAI";
                if (!string.IsNullOrEmpty(micDevice) || !string.IsNullOrEmpty(speakerDevice))
                {
                    tooltip += "\n";
                    if (!string.IsNullOrEmpty(micDevice))
                    {
                        tooltip += $"Mic: {micDevice}\n";
                    }
                    if (!string.IsNullOrEmpty(speakerDevice))
                    {
                        tooltip += $"Speaker: {speakerDevice}";
                    }
                }
                
                // NotifyIcon.Text has a 127-character limit; truncate to prevent ArgumentOutOfRangeException
                if (tooltip.Length > 127)
                {
                    tooltip = tooltip.Substring(0, 124) + "...";
                }
                _notifyIcon.Text = tooltip;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error updating tray tooltip: {ex.Message}");
            }
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoggingService.Info("[MainWindow] MainWindow_Loaded event fired");
            
            // Ensure window stays hidden on startup (tray only mode)
            // Only show if explicitly requested via ShowWindow()
            if (this.Visibility != Visibility.Visible)
            {
                // Keep window hidden - it should only be shown when user clicks tray icon
                this.WindowState = WindowState.Minimized;
                this.ShowInTaskbar = false;
                LoggingService.Info("[MainWindow] Window kept hidden in tray mode");
                return;
            }
            
            // Window is being shown (user clicked tray icon), so maximize it
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.Topmost = false;
            this.Activate();
            this.Focus();
            
            // Ensure network stats indicator is initialized
            if (NetworkStatsIndicator != null && _networkStatsService != null)
            {
                NetworkStatsIndicator.Initialize(_networkStatsService);
            }
        }

        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // If user is closing window (not exiting), minimize to tray instead
            if (!_isClosing)
            {
                e.Cancel = true; // Cancel the close event
                HideToTray(); // Hide to tray instead
                return;
            }
            
            // Only cleanup if actually exiting
            try
            {
                LoggingService.Info("[MainWindow] Application closing - cleaning up resources...");
                
                // Stop audio status monitoring timer
                if (_audioStatusTimer != null)
                {
                    _audioStatusTimer.Stop();
                    _audioStatusTimer = null;
                }
                
                // Dispose system tray icon
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _notifyIcon = null;
                }
                
                // Stop backend API health check
                StopBackendApiHealthCheck();

                // Dispose token refresh service
                if (_tokenRefreshService != null)
                {
                    LoggingService.Info("[MainWindow] Disposing TokenRefreshService...");
                    _tokenRefreshService.Dispose();
                    _tokenRefreshService = null;
                }
                
                // Dispose any active voice session
                if (VoiceSessionPage != null)
                {
                    LoggingService.Info("[MainWindow] Disposing VoiceSessionPage...");
                    VoiceSessionPage.DisposeVoiceSessionPage();
                    VoiceSessionPage = null;
                }
                
                // Dispose network stats service
                if (_networkStatsService != null)
                {
                    LoggingService.Info("[MainWindow] Disposing NetworkStatsService...");
                    _networkStatsService.Dispose();
                    _networkStatsService = null;
                    Globals.NetworkStatsService = null;
                }
                
                // AuthService removed
                
                // Clear global configuration
                Globals.ConfigurationInfo = null;
                
                LoggingService.Info("[MainWindow] Application cleanup completed");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[MainWindow] Error during application cleanup: {ex.Message}");
            }
        }

        // EnsureWindowMaximized method removed - no longer needed

        private void InitializeServices()
        {
            try
            {
                var configuration = LoadConfiguration();
                Globals.ConfigurationInfo = configuration;

                // Initialize network stats service only if not already created (prevents leaking on re-navigation)
                if (_networkStatsService == null)
                {
                    _networkStatsService = new NetworkStatsService();
                    Globals.NetworkStatsService = _networkStatsService;
                }

                // Initialize network stats indicator
                if (NetworkStatsIndicator != null)
                {
                    NetworkStatsIndicator.Initialize(_networkStatsService);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[MainWindow] ? Error initializing services: {ex.Message}");
                System.Windows.MessageBox.Show($"Configuration error: {ex.Message}\n\nPlease ensure appsettings.json is in the application directory.",
                              "Configuration Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                throw;
            }
        }

        #region Backend API Health Check

        /// <summary>
        /// Initializes and starts the backend API health check service.
        /// Reads the backend base URL from configuration and begins periodic
        /// reachability checks (every 10 seconds by default).
        /// Updates the BE WS indicator in the title bar.
        /// </summary>
        private void StartBackendApiHealthCheck()
        {
            try
            {
                var config = Globals.ConfigurationInfo;
                var backendBaseUrl = config?.GetSection("Backend")?["BaseUrl"];

                if (string.IsNullOrWhiteSpace(backendBaseUrl))
                {
                    LoggingService.Warn("[MainWindow] Backend:BaseUrl not configured - backend API health check will not start");
                    return;
                }

                // Dispose any existing instance (e.g., if called multiple times)
                if (_backendApiHealthCheck != null)
                {
                    _backendApiHealthCheck.StateChanged -= OnBackendApiHealthStateChanged;
                    _backendApiHealthCheck.Stop();
                    _backendApiHealthCheck.Dispose();
                    _backendApiHealthCheck = null;
                }

                _backendApiHealthCheck = new BackendApiHealthCheckService(backendBaseUrl);
                _backendApiHealthCheck.StateChanged += OnBackendApiHealthStateChanged;
                _backendApiHealthCheck.Start();

                LoggingService.Info("[MainWindow] Backend API health check started");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error starting backend API health check: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops and disposes the backend API health check service.
        /// </summary>
        private void StopBackendApiHealthCheck()
        {
            try
            {
                if (_backendApiHealthCheck != null)
                {
                    _backendApiHealthCheck.StateChanged -= OnBackendApiHealthStateChanged;
                    _backendApiHealthCheck.Stop();
                    _backendApiHealthCheck.Dispose();
                    _backendApiHealthCheck = null;
                    LoggingService.Info("[MainWindow] Backend API health check stopped and disposed");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error stopping backend API health check: {ex.Message}");
            }
        }

        /// <summary>
        /// Callback fired by BackendApiHealthCheckService when the reachability state changes.
        /// This fires on a background thread, so we marshal the UI update to the Dispatcher.
        /// </summary>
        private void OnBackendApiHealthStateChanged(BackendApiHealthCheckService.ReachabilityState newState)
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    UpdateBackendApiHealthIndicator(newState);
                });
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"[MainWindow] Error dispatching backend API health state change: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the Backend API status indicator ellipse based on the health-check reachability state.
        /// Green = reachable, Orange = checking, Gray = unreachable or unknown.
        /// Must be called on the UI (Dispatcher) thread.
        /// </summary>
        private void UpdateBackendApiHealthIndicator(BackendApiHealthCheckService.ReachabilityState state)
        {
            try
            {
                string color;
                switch (state)
                {
                    case BackendApiHealthCheckService.ReachabilityState.Reachable:
                        color = "#27AE60"; // Green
                        break;
                    case BackendApiHealthCheckService.ReachabilityState.Checking:
                        color = "#F39C12"; // Orange/Amber
                        break;
                    case BackendApiHealthCheckService.ReachabilityState.Unreachable:
                    case BackendApiHealthCheckService.ReachabilityState.Unknown:
                    default:
                        color = "#808080"; // Gray
                        break;
                }

                NetworkStatsIndicator?.UpdateBackendWsStatus(color);
            }
            catch (Exception ex)
            {
                LoggingService.Debug($"[MainWindow] Error updating backend API health indicator: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Updates the window title based on the current AppMode (set by DevMode checkbox on login page).
        /// </summary>
        public void UpdateWindowTitleForMode()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (Globals.IsDemoMode)
                    {
                        this.Title = "DataRipple AI - Demo";
                    }
                    else
                    {
                        this.Title = "DataRipple AI";
                    }
                    LoggingService.Info($"[MainWindow] Window title updated to: {this.Title}");
                });
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error updating window title: {ex.Message}");
            }
        }

        private IConfiguration LoadConfiguration()
        {
            // Use application base directory instead of current working directory
            string folderPath = AppDomain.CurrentDomain.BaseDirectory;
            string jsonFilePath = Path.Combine(folderPath, "appsettings.json");
            
            LoggingService.Info($"[MainWindow] ?? Application base directory: {folderPath}");
            LoggingService.Info($"[MainWindow] ?? Looking for configuration at: {jsonFilePath}");
            
            // Check if appsettings.json exists in project directory
            if (!File.Exists(jsonFilePath))
            {
                LoggingService.Info($"[MainWindow] ? Configuration file not found at: {jsonFilePath}");
                LoggingService.Info($"[MainWindow] ?? Directory contents:");
                try
                {
                    var files = Directory.GetFiles(folderPath, "*.json");
                    foreach (var file in files)
                    {
                        LoggingService.Info($"[MainWindow] ?? Found JSON file: {file}");
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[MainWindow] ? Error listing directory contents: {ex.Message}");
                }
                throw new FileNotFoundException($"Configuration file not found at: {jsonFilePath}");
            }

            LoggingService.Info($"[Debug] Configuration file path: {jsonFilePath}");

            var configurationBuilder = new ConfigurationBuilder()
                .SetBasePath(folderPath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

            var configuration = configurationBuilder.Build();

            return configuration;
        }

        private void CheckLoggedInUser()
        {
            LoggingService.Info("[MainWindow] Fresh application launch - clearing stored session tokens to require login");

            // On every fresh launch, clear stored session tokens so the user must log in again.
            // This does NOT affect stored credentials (email/password for "Remember me" pre-fill).
            // Tray restore is unaffected because it uses ShowWindow() which does not call this method.
            SecureTokenStorage.ClearAllTokens();

            // Always show login page on fresh launch
            ShowLoginPage();
        }

        private void ShowLoginPage(string initialMessage = null)
        {
            LoggingService.Info("[MainWindow] Showing login page...");
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    LoggingService.Info("[MainWindow] Loading configuration for login page...");
                    var configuration = LoadConfiguration();
                    LoggingService.Info("[MainWindow] Configuration loaded successfully");
                    
                    LoggingService.Info("[MainWindow] Creating LoginPage instance...");
                    var loginPage = new LoginPage(configuration);
                    LoggingService.Info("[MainWindow] LoginPage instance created successfully");
                
                    // Show initial message if provided (e.g., session expired)
                    if (!string.IsNullOrEmpty(initialMessage))
                    {
                        loginPage.ShowErrorMessage(initialMessage);
                        LoggingService.Info($"[MainWindow] Showing initial message on login page: {initialMessage}");
                    }
                
                    // Subscribe to login events
                    loginPage.LoginSuccessful += () =>
                    {
                        LoggingService.Info("[MainWindow] Login successful, navigating to main app...");
                        // Login successful, go to main application
                        
                        // Start token refresh service
                        StartTokenRefreshService();
                        
                        // Ensure window is maximized after login
                        // EnsureWindowMaximized call removed
                        
                        GoToMainApplication();
                    };
                    
                    loginPage.LoginCancelled += () =>
                    {
                        LoggingService.Info("[MainWindow] Login cancelled, showing login page again...");
                        // Login cancelled, show login page again
                        ShowLoginPage();
                    };
                    
                    // Load the LoginPage into the DockPanel
                    LoggingService.Info("[MainWindow] Clearing main frame and adding login page...");
                    MainFrameDockPanel.Children.Clear();
                    MainFrameDockPanel.Children.Add(loginPage);
                    
                    _CurrentPage = CurrentPage.Login;
                    AdjustUI(_CurrentPage);
                    
                    txtPageWelcome.Text = "Please sign in to continue";
                    
                    // Hide bottom bar, sidebar, user avatar, and BE WS indicator during login
                    FrameBottomBar.Visibility = Visibility.Collapsed;
                    LeftSidebar.Visibility = Visibility.Collapsed;
                    UserAvatarButton.Visibility = Visibility.Collapsed;
                    NetworkStatsIndicator?.HideBackendWsStatus();
                    UserAvatarPopup.IsOpen = false;
                    LoggingService.Info("[MainWindow] Login page setup completed successfully");
                }
                catch (Exception ex)
                {
                    LoggingService.Info($"[MainWindow] Error showing login page: {ex.Message}");
                    System.Windows.MessageBox.Show($"Error loading login page: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }));
        }

        private void GoToMainApplication()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                // Ensure window is maximized when going to main application
                // EnsureWindowMaximized call removed

                FrameBottomBar.Visibility = Visibility.Visible;
                LeftSidebar.Visibility = Visibility.Visible;

                // Show user avatar in title bar and populate user info
                UserAvatarButton.Visibility = Visibility.Visible;
                NetworkStatsIndicator?.ShowBackendWsStatus();
                UpdateUserAvatarInfo();

                InitializeServices(); // Initialize services

                // Start backend API health check for the title bar indicator
                StartBackendApiHealthCheck();

                // Update window title based on AppMode (set by DevMode checkbox on login page)
                UpdateWindowTitleForMode();

                // Go to VoiceSessionPage
                GoToVoiceSessionPage();
            }));
        }

        private void GoToCreateLiveCallPage()
        {
            // Go to main application
            GoToMainApplication();
        }

        private void GoToConfigurationSettingsPage()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                // Always show the tabbed settings view with two tabs:
                // Tab 1: Server Connection Settings
                // Tab 2: Devices (audio device selection)
                var tabbedSettings = new TabbedSettingsPage();
                tabbedSettings.NavigateBackRequested += () => GoToMainApplication();

                MainFrameDockPanel.Children.Clear();
                MainFrameDockPanel.Children.Add(tabbedSettings);

                _CurrentPage = CurrentPage.ConfigurationSettings;
                AdjustUI(_CurrentPage);

                // Keep sidebar visible
                LeftSidebar.Visibility = Visibility.Visible;
                FrameBottomBar.Visibility = Visibility.Visible;

                // Show back button in title bar, hide avatar
                ShowTitleBarBackButton();

                txtPageWelcome.Text = "Settings";
                LoggingService.Info("[MainWindow] Navigated to Tabbed Settings page");
            }));
        }

        private void GoToConversationHistoryPage()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var historyPage = new ConversationHistoryPage();

                // Load the ConversationHistoryPage into the DockPanel
                MainFrameDockPanel.Children.Clear();
                MainFrameDockPanel.Children.Add(historyPage);

                _CurrentPage = CurrentPage.History;
                AdjustUI(_CurrentPage);

                // Keep sidebar visible
                LeftSidebar.Visibility = Visibility.Visible;
                FrameBottomBar.Visibility = Visibility.Visible;

                // Show back button in title bar, hide avatar
                ShowTitleBarBackButton();

                txtPageWelcome.Text = "Conversation History";
                LoggingService.Info("[MainWindow] Navigated to Conversation History page");
            }));
        }

        public void GoToVoiceSessionPageFromHistory()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                LoggingService.Info("[MainWindow] Navigating back to Voice Session page from History");

                // Reuse existing VoiceSessionPage instance to preserve call logs
                GoToVoiceSessionPage();

                LoggingService.Info("[MainWindow] Navigated back to Voice Session page (call logs preserved)");
            }));
        }

        /// <summary>
        /// Synchronous navigation to VoiceSessionPage when no prior instance exists.
        /// Used by CallControl_Click to ensure the page is fully loaded before
        /// triggering the incoming call overlay.
        /// </summary>
        private void EnsureVoiceSessionPageSync()
        {
            FrameBottomBar.Visibility = Visibility.Visible;
            LeftSidebar.Visibility = Visibility.Visible;

            // Show user avatar in title bar and populate user info
            UserAvatarButton.Visibility = Visibility.Visible;
            NetworkStatsIndicator?.ShowBackendWsStatus();
            UpdateUserAvatarInfo();

            InitializeServices();
            StartBackendApiHealthCheck();
            UpdateWindowTitleForMode();

            // Create and display VoiceSessionPage synchronously
            GoToVoiceSessionPage();
        }

        /// <summary>
        /// Synchronous navigation from Settings/History to VoiceSessionPage.
        /// Reuses the existing instance to preserve call logs, all on the
        /// current dispatcher frame so the page is ready for immediate use.
        /// </summary>
        private void NavigateToVoiceSessionPageSync()
        {
            LoggingService.Info("[MainWindow] Navigating back to Voice Session page synchronously");

            // Reuse existing VoiceSessionPage instance to preserve call logs
            GoToVoiceSessionPage();

            LoggingService.Info("[MainWindow] Navigated back to Voice Session page synchronously (call logs preserved)");
        }

        private void AdjustUI(CurrentPage currentPage)
        {
            // Reset sidebar icon highlights
            ResetSidebarHighlights();

            switch (currentPage)
            {
                case CurrentPage.ConfigurationSettings:
                    // Configuration settings page - highlight settings icon in sidebar
                    if (!_isCallActive)
                    {
                        _isInitializing = false;
                        _isDisposing = false;
                    }
                    SidebarSettingsButtonBorder.Background = new SolidColorBrush(Color.FromRgb(26, 41, 66)); // #1A2942 highlight
                    break;

                case CurrentPage.DeviceSettings:
                    // Device settings panel (Production mode) - same UI treatment as ConfigurationSettings
                    if (!_isCallActive)
                    {
                        _isInitializing = false;
                        _isDisposing = false;
                    }
                    SidebarSettingsButtonBorder.Background = new SolidColorBrush(Color.FromRgb(26, 41, 66));
                    break;

                case CurrentPage.History:
                    // History page - highlight history icon and show refresh button
                    LoggingService.Info("[MainWindow] History page - disabling call button");
                    if (!_isCallActive)
                    {
                        _isInitializing = false;
                        _isDisposing = false;
                    }
                    SidebarHistoryButtonBorder.Background = new SolidColorBrush(Color.FromRgb(26, 41, 66));
                    break;

                case CurrentPage.Logout:
                    if (!_isCallActive)
                    {
                        _isInitializing = false;
                        _isDisposing = false;
                    }
                    break;

                case CurrentPage.Login:
                    // Reset all sidebar state on login page
                    _isInitializing = false;
                    _isDisposing = false;
                    _isCallActive = false;
                    break;

                case CurrentPage.LiveCall:
                    // LiveCall page - highlight call icon
                    LoggingService.Info("[MainWindow] LiveCall page - updating call button state");
                    break;

                default:
                    if (!_isCallActive)
                    {
                        _isInitializing = false;
                        _isDisposing = false;
                    }
                    break;
            }

            // Update call button state based on current page and call state
            SetCallButtonState(_isCallActive);
        }

        /// <summary>
        /// Reset all sidebar icon background highlights to transparent
        /// </summary>
        private void ResetSidebarHighlights()
        {
            SidebarSettingsButtonBorder.Background = System.Windows.Media.Brushes.Transparent;
            SidebarHistoryButtonBorder.Background = System.Windows.Media.Brushes.Transparent;
        }

        private void GoToVoiceSessionPage()
        {
            FrameBottomBar.Visibility = Visibility.Visible;
            LeftSidebar.Visibility = Visibility.Visible;

            // Hide back button, show avatar when returning to Voice Session
            HideTitleBarBackButton();

            // Reuse existing VoiceSessionPage instance if available to preserve call logs.
            // Only create a new instance if one does not exist yet.
            if (VoiceSessionPage == null)
            {
                LoggingService.Info("[MainWindow] Creating new VoiceSessionPage instance");
                VoiceSessionPage = new VoiceSessionPage()
                {
                    DataContext = null // No ViewModel needed for LiveKit testing
                };
                VoiceSessionPage.NavigateToLiveCallRequested += GoToCreateLiveCallPage;
                VoiceSessionPage.DisposalCompleted += OnVoiceSessionDisposalCompleted;
                VoiceSessionPage.OnVoiceSessionCallStarted += OnVoiceSessionCallStarted;
                VoiceSessionPage.OnVoiceSessionCallRejected += OnVoiceSessionCallRejected;
                VoiceSessionPage.OnVoiceSessionCallStartFailed += OnVoiceSessionCallStartFailed;
            }
            else
            {
                LoggingService.Info("[MainWindow] Reusing cached VoiceSessionPage instance (call logs preserved)");
            }

            // Load the VoiceSessionPage into the DockPanel with Fill docking
            MainFrameDockPanel.Children.Clear();
            MainFrameDockPanel.Children.Add(VoiceSessionPage);
            // No need to set Dock - LastChildFill will handle it

            _CurrentPage = CurrentPage.LiveCall;
            AdjustUI(_CurrentPage);

            // Update header text to show current page
            txtPageWelcome.Text = "Call Log";

            // Only reset call button state when creating a fresh page (no active call)
            if (!_isCallActive)
            {
                SetCallButtonState(false);

                // Force button text update to ensure it shows correctly
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!_isCallActive)
                    {
                        SetCallButtonState(false);
                        LoggingService.Info("[MainWindow] Call button state reset to 'Start Call'");
                    }
                }));
            }
        }


        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Start moving the window when the title bar is clicked
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

     

        /// <summary>
        /// Sidebar logo click - navigate to main application (voice session page)
        /// </summary>
        private void SidebarLogo_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (IsCallActive() || _isInitializing || _isDisposing)
            {
                LoggingService.Info("[MainWindow] Logo click blocked - call is active or transitioning");
                return;
            }
            GoToMainApplication();
        }

        /// <summary>
        /// Sidebar settings click (MouseButtonEventArgs overload)
        /// </summary>
        private void btnSettings_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            btnSettings_Click(sender, (RoutedEventArgs)e);
        }

        /// <summary>
        /// Sidebar history click (MouseButtonEventArgs overload)
        /// </summary>
        private void btnHistory_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            btnHistory_Click(sender, (RoutedEventArgs)e);
        }

        /// <summary>
        /// User avatar click - toggles the dropdown popup
        /// </summary>
        private void UserAvatar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            UserAvatarPopup.IsOpen = !UserAvatarPopup.IsOpen;
        }

        /// <summary>
        /// Title bar back button click - navigates back to the Voice Session page
        /// from Settings or History pages.
        /// </summary>
        private void TitleBarBackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[MainWindow] Title bar back button clicked - navigating back to Voice Session page");

                if (_CurrentPage == CurrentPage.ConfigurationSettings || _CurrentPage == CurrentPage.DeviceSettings)
                {
                    GoToMainApplication();
                }
                else if (_CurrentPage == CurrentPage.History)
                {
                    GoToVoiceSessionPageFromHistory();
                }
                else
                {
                    GoToMainApplication();
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error in title bar back button: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the title bar back button (replaces the logo).
        /// Called when navigating to Settings or History pages.
        /// The user avatar remains visible so the user can still access
        /// the account dropdown from any authenticated page.
        /// </summary>
        private void ShowTitleBarBackButton()
        {
            TitleBarBackButton.Visibility = Visibility.Visible;
            TitleBarLogo.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Hides the title bar back button and restores the logo.
        /// Called when navigating back to the Voice Session page.
        /// </summary>
        private void HideTitleBarBackButton()
        {
            TitleBarBackButton.Visibility = Visibility.Collapsed;
            TitleBarLogo.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Dropdown "Log out" button click
        /// </summary>
        private void DropdownLogout_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            UserAvatarPopup.IsOpen = false;
            btnLogout_Click(sender, (RoutedEventArgs)e);
        }

        /// <summary>
        /// Dropdown logout hover enter - highlight background
        /// </summary>
        private void DropdownLogout_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(34, 50, 74)); // #22324A
            }
        }

        /// <summary>
        /// Dropdown logout hover leave - reset background
        /// </summary>
        private void DropdownLogout_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = System.Windows.Media.Brushes.Transparent;
            }
        }

        /// <summary>
        /// Compute user initials from full name (e.g., "John Doe" -> "JD", "test1" -> "T")
        /// </summary>
        private string GetUserInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return "U";

            var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[parts.Length - 1][0])}";
            }
            else if (parts.Length == 1 && parts[0].Length > 0)
            {
                return char.ToUpper(parts[0][0]).ToString();
            }
            return "U";
        }

        /// <summary>
        /// Update the user avatar circle and dropdown with current user information
        /// </summary>
        private void UpdateUserAvatarInfo()
        {
            try
            {
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                string userName = userDetails?.Name?.ToString() ?? "User";
                string userEmail = userDetails?.Email?.ToString() ?? "user@example.com";
                string initials = GetUserInitials(userName);

                txtUserInitials.Text = initials;
                txtDropdownInitials.Text = initials;
                txtDropdownUserName.Text = userName;
                txtDropdownUserEmail.Text = userEmail;

                LoggingService.Info($"[MainWindow] User avatar updated: {userName} ({userEmail})");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[MainWindow] Could not update user avatar info: {ex.Message}");
                txtUserInitials.Text = "U";
                txtDropdownInitials.Text = "U";
                txtDropdownUserName.Text = "User";
                txtDropdownUserEmail.Text = "";
            }
        }

        /// <summary>
        /// Sidebar call control click (MouseButtonEventArgs overload)
        /// </summary>
        private void CallControl_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            CallControl_Click(sender, (RoutedEventArgs)e);
        }

        private void btnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Check if call is active or transitioning
            if (IsCallActive() || _isInitializing || _isDisposing)
            {
                System.Windows.MessageBox.Show("Please end the current call before accessing settings.", 
                                "Call In Progress", 
                                System.Windows.MessageBoxButton.OK, 
                                System.Windows.MessageBoxImage.Warning);
                LoggingService.Info("[MainWindow] ⚠️ Settings access blocked - call is active or transitioning");
                return;
            }
            
            GoToConfigurationSettingsPage();
        }

        private void btnHistory_Click(object sender, RoutedEventArgs e)
        {
            // Check if call is active or transitioning
            if (IsCallActive() || _isInitializing || _isDisposing)
            {
                System.Windows.MessageBox.Show("Please end the current call before viewing conversation history.", 
                                "Call In Progress", 
                                System.Windows.MessageBoxButton.OK, 
                                System.Windows.MessageBoxImage.Warning);
                LoggingService.Info("[MainWindow] ⚠️ History access blocked - call is active or transitioning");
                return;
            }
            
            GoToConversationHistoryPage();
        }

        private async void btnLogout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if call is active or transitioning
                if (IsCallActive() || _isInitializing || _isDisposing)
                {
                    System.Windows.MessageBox.Show("Please end the current call before logging out.", 
                                    "Call In Progress", 
                                    System.Windows.MessageBoxButton.OK, 
                                    System.Windows.MessageBoxImage.Warning);
                    LoggingService.Info("[MainWindow] ⚠️ Logout blocked - call is active or transitioning");
                    return;
                }
                
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                if (userDetails != null)
                {
                    System.Windows.MessageBoxResult messageBoxResult = System.Windows.MessageBox.Show($"Are you sure you want to Logout as {userDetails.Email}", "Logout", System.Windows.MessageBoxButton.YesNo);
                    if (messageBoxResult == System.Windows.MessageBoxResult.Yes)
                    {
                        // Clear stored tokens
                        SecureTokenStorage.ClearAllTokens();
                        
                        // Verify tokens are cleared
                        var verifyUserDetails = SecureTokenStorage.RetrieveUserDetails();
                        if (verifyUserDetails == null)
                        {
                            LoggingService.Info("[MainWindow] Tokens successfully cleared");
                        }
                        else
                        {
                            LoggingService.Info("[MainWindow] WARNING: Tokens still exist after clearing!");
                        }
                        
                        // Reset UI state
                        await ResetToLoginState();

                        // Show success message
                        System.Windows.MessageBox.Show("Logged out successfully!", "Logout", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                    else
                    {
                        AdjustUI(_CurrentPage); //restore to previous selected btn...
                    }
                }
                else
                {
                    // No user details found, just clear tokens and reset
                    SecureTokenStorage.ClearAllTokens();
                    
                    // Verify tokens are cleared
                    var verifyUserDetails = SecureTokenStorage.RetrieveUserDetails();
                    if (verifyUserDetails == null)
                    {
                        LoggingService.Info("[MainWindow] Tokens successfully cleared");
                    }
                    else
                    {
                        LoggingService.Info("[MainWindow] WARNING: Tokens still exist after clearing!");
                    }
                    
                    await ResetToLoginState();
                    System.Windows.MessageBox.Show("Logged out successfully!", "Logout", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error during logout: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        private async Task ClearWebView2CacheAsync()
        {
            try
            {
                LoggingService.Info("[MainWindow] Starting WebView2 cache clearing...");
                
                // Method 1: Clear WebView2 cache directory manually (both .WebView2 and DataRippleAI.exe.WebView2)
                string[] webView2CacheDirs = {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".WebView2"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DataRippleAI.exe.WebView2")
                };

                foreach (string webView2CacheDir in webView2CacheDirs)
                {
                    if (Directory.Exists(webView2CacheDir))
                    {
                        try
                        {
                            Directory.Delete(webView2CacheDir, true);
                            LoggingService.Info($"[MainWindow] Deleted WebView2 cache directory: {webView2CacheDir}");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Info($"[MainWindow] Could not delete WebView2 cache directory {webView2CacheDir}: {ex.Message}");
                        }
                    }
                }

                // Method 2: Clear WebView2 cache in user data folder
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                    "DataRippleAI", "WebView2");
                if (Directory.Exists(userDataFolder))
                {
                    try
                    {
                        Directory.Delete(userDataFolder, true);
                        LoggingService.Info($"[MainWindow] Deleted WebView2 user data folder: {userDataFolder}");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Info($"[MainWindow] Could not delete WebView2 user data folder: {ex.Message}");
                    }
                }

                // Method 3: Clear any Auth0 related cache files
                string[] auth0CachePaths = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Auth0"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Auth0"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WebView2"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "WebView2")
                };

                foreach (string cachePath in auth0CachePaths)
                {
                    if (Directory.Exists(cachePath))
                    {
                        try
                        {
                            Directory.Delete(cachePath, true);
                            LoggingService.Info($"[MainWindow] Deleted cache directory: {cachePath}");
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Info($"[MainWindow] Could not delete cache directory {cachePath}: {ex.Message}");
                        }
                    }
                }

                LoggingService.Info("[MainWindow] WebView2 cache clearing completed");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[MainWindow] Error clearing WebView2 cache: {ex.Message}");
            }
        }

        private void ClearDebugAndTempFiles()
        {
            try
            {
                // Clear any potential debug/temp files in common locations
                string[] tempPaths = {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataRippleAI"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataRippleAI"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")
                };

                foreach (string tempPath in tempPaths)
                {
                    if (Directory.Exists(tempPath))
                    {
                        try
                        {
                            // Look for any files that might contain login data
                            var files = Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories)
                                .Where(f => f.Contains("auth") || f.Contains("token") || f.Contains("login") || f.Contains("user"))
                                .ToArray();

                            foreach (var file in files)
                            {
                                try
                                {
                                    File.Delete(file);
                                    LoggingService.Info($"[MainWindow] Deleted temp file: {file}");
                                }
                                catch (Exception fileEx)
                                {
                                    LoggingService.Info($"[MainWindow] Could not delete temp file {file}: {fileEx.Message}");
                                }
                            }
                        }
                        catch (Exception dirEx)
                        {
                            LoggingService.Info($"[MainWindow] Error processing temp directory {tempPath}: {dirEx.Message}");
                        }
                    }
                }

                LoggingService.Info("[MainWindow] Debug and temp files cleared");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[MainWindow] Error clearing debug/temp files: {ex.Message}");
            }
        }

    
        private async Task ResetToLoginState()
        {
            try
            {
                LoggingService.Info("[MainWindow] Starting complete application reset...");
                
                // 0. Stop token refresh service
                if (_tokenRefreshService != null)
                {
                    LoggingService.Info("[MainWindow] Stopping TokenRefreshService...");
                    _tokenRefreshService.Dispose();
                    _tokenRefreshService = null;
                }
                
                // 1. Dispose any active voice session
                if (VoiceSessionPage != null)
                {
                    LoggingService.Info("[MainWindow] Disposing VoiceSessionPage...");
                    VoiceSessionPage.DisposeVoiceSessionPage();
                    VoiceSessionPage = null;
                }

                // 2. Reset all class state variables
                IsCurretlySelected = false;
                _CurrentPage = CurrentPage.Login;

                // 2.5. Clear configuration state
                try
                {
                    // Clear the global configuration
                    Globals.ConfigurationInfo = null;
                    LoggingService.Info("[MainWindow] Global configuration cleared");
                    
                    // Reinitialize configuration
                    var configuration = LoadConfiguration();
                    
                }
                catch (Exception authEx)
                {
                    LoggingService.Info($"[MainWindow] Error reinitializing configuration: {authEx.Message}");
                }

                // 3. Reset call button state
                SetCallButtonState(false);

                // 3.5. Stop backend API health check
                StopBackendApiHealthCheck();

                // 4. Hide bottom bar, sidebar, user avatar, and BE WS indicator
                FrameBottomBar.Visibility = Visibility.Collapsed;
                LeftSidebar.Visibility = Visibility.Collapsed;
                UserAvatarButton.Visibility = Visibility.Collapsed;
                NetworkStatsIndicator?.HideBackendWsStatus();
                UserAvatarPopup.IsOpen = false;

                // 5. Clear main frame completely
                MainFrameDockPanel.Children.Clear();

                // 6. Clear WebView2 cache and cookies
                await ClearWebView2CacheAsync();

                // 7. Clear any debug/temp files
                ClearDebugAndTempFiles();

                // 7. Force garbage collection to clean up disposed objects
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // 7. Show login page directly (don't check tokens again)
                LoggingService.Info("[MainWindow] Showing login page...");
                ShowLoginPage();

                LoggingService.Info("[MainWindow] Complete application reset successful");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[MainWindow] Error resetting to login state: {ex.Message}");
                System.Windows.MessageBox.Show($"Error during logout: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }



        private async void CallControl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Prevent any action if currently disposing, initializing, or processing another call control
                if (_isDisposing || _isInitializing || _isProcessingCallControl)
                {
                    LoggingService.Info($"[MainWindow] Call control clicked while busy - ignoring (disposing: {_isDisposing}, initializing: {_isInitializing}, processing: {_isProcessingCallControl})");
                    return;
                }
                
                _isProcessingCallControl = true;
                LoggingService.Info("[MainWindow] Call control processing started");
                
                if (IsCallActive())
                {
                    // End the current call
                    if (VoiceSessionPage != null)
                    {
                        // Set disposing state and update UI
                        _isDisposing = true;
                        SetCallButtonState(false); // This will show "Stopping..." and disable button
                        LoggingService.Info("[MainWindow] Starting call disposal process...");
                        
                        // Start disposal - the OnVoiceSessionDisposalCompleted event will handle completion
                        await VoiceSessionPage.StopTranscription();
                        LoggingService.Info("[MainWindow] StopTranscription call completed - disposal event should have fired");
                    }
                }
                else
                {
                    // Start a new call
                    // Set initializing state and update button immediately
                    _isInitializing = true;
                    SetCallButtonState(false); // This will show "Starting..." and disable button
                    LoggingService.Info("[MainWindow] Starting call - button updated to 'Starting...'");
                    
                    // First, ensure we're on the VoiceSessionPage (navigate if needed)
                    // Navigation must be synchronous so VoiceSessionPage is ready
                    // before TriggerIncomingCall() is called below.
                    if (VoiceSessionPage == null)
                    {
                        LoggingService.Info("[MainWindow] Creating new Voice Session page for call start");
                        EnsureVoiceSessionPageSync();
                    }
                    else if (!MainFrameDockPanel.Children.Contains(VoiceSessionPage))
                    {
                        // VoiceSessionPage exists but we're on Settings/History page.
                        // Navigate synchronously so the page and its IncomingCallPanel
                        // are visible before the incoming call overlay is triggered.
                        LoggingService.Info("[MainWindow] Navigating to Voice Session page synchronously for call start");
                        NavigateToVoiceSessionPageSync();
                    }
                    else
                    {
                        LoggingService.Info("[MainWindow] Already on Voice Session page - no navigation needed");
                    }
                    
                    if (VoiceSessionPage != null)
                    {
                        // Trigger incoming call with ringing
                        LoggingService.Info("[MainWindow] Triggering incoming call with ringing...");
                        
                        await VoiceSessionPage.TriggerIncomingCall();
                        
                        // Keep initializing state TRUE during ringing to prevent double-clicking Start Call
                        // The OnVoiceSessionCallStarted event will reset it if answered
                        // The OnVoiceSessionCallRejected event will reset it if rejected
                        // Button remains disabled during ringing phase
                        LoggingService.Info("[MainWindow] Incoming call triggered - button remains disabled during ringing");
                    }
                    else
                    {
                        // Failed to get VoiceSessionPage, reset initializing state
                        _isInitializing = false;
                        SetCallButtonState(false);
                        LoggingService.Error("[MainWindow] Failed to get VoiceSessionPage for call start");
                    }
                }
            }
            catch (Exception ex)
            {
                // On error, reset all states
                _isInitializing = false;
                _isDisposing = false;
                
                // Trigger disposal completed to reset UI state
                OnVoiceSessionDisposalCompleted();
                
                LoggingService.Info($"[MainWindow] Error controlling call: {ex.Message}");
                System.Windows.MessageBox.Show($"Error controlling call: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                _isProcessingCallControl = false;
                LoggingService.Info("[MainWindow] Call control processing completed");
            }
        }

        private bool _isCallActive = false;
        private bool _isDisposing = false;
        private bool _isInitializing = false; // Track when call is being initialized
        private bool _isProcessingCallControl = false;

        private bool IsCallActive()
        {
            return _isCallActive;
        }
        
        private bool IsDisposing()
        {
            return _isDisposing;
        }
        
        private void OnVoiceSessionDisposalCompleted()
        {
            // This is called when VoiceSessionPage has completed disposal
            Dispatcher.Invoke(() =>
            {
                _isDisposing = false;
                _isInitializing = false; // Reset initializing state as well
                _isCallActive = false;
                SetCallButtonState(false); // Re-enable "Start Call" button
                LoggingService.Info("[MainWindow] VoiceSession disposal completed - Start Call button re-enabled");
                LoggingService.Info($"[MainWindow] State after disposal: disposing={_isDisposing}, initializing={_isInitializing}, callActive={_isCallActive}, processing={_isProcessingCallControl}");
            });
        }

        private void OnVoiceSessionCallStarted(object sender, EventArgs e)
        {
            // This is called when incoming call is answered and call starts
            Dispatcher.Invoke(() =>
            {
                _isInitializing = false;
                _isCallActive = true;
                SetCallButtonState(true); // Set to "End Call"
                LoggingService.Info("[MainWindow] Call started after answering - button updated to 'End Call'");
            });
        }

        private void OnVoiceSessionCallRejected(object sender, EventArgs e)
        {
            // This is called when incoming call is rejected/canceled
            Dispatcher.Invoke(() =>
            {
                _isInitializing = false;
                _isCallActive = false;
                SetCallButtonState(false); // Re-enable "Start Call" button
                LoggingService.Info("[MainWindow] Call rejected - button updated to 'Start Call'");
            });
        }

        private void OnVoiceSessionCallStartFailed(object sender, string errorMessage)
        {
            // This is called when call start fails
            Dispatcher.Invoke(() =>
            {
                _isInitializing = false;
                _isCallActive = false;
                SetCallButtonState(false); // Re-enable "Start Call" button
                LoggingService.Info($"[MainWindow] Call start failed: {errorMessage} - button updated to 'Start Call'");
            });
        }

        private void StartTokenRefreshService()
        {
            try
            {
                // Dispose existing service if any
                if (_tokenRefreshService != null)
                {
                    LoggingService.Info("[MainWindow] Disposing existing TokenRefreshService...");
                    _tokenRefreshService.Dispose();
                    _tokenRefreshService = null;
                }

                // Check if user is logged in
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                if (userDetails == null)
                {
                    LoggingService.Warn("[MainWindow] Cannot start TokenRefreshService - no user logged in");
                    return;
                }

                // Create and start token refresh service
                LoggingService.Info("[MainWindow] Creating and starting TokenRefreshService...");
                
                // Ensure we have a valid configuration
                var configuration = Globals.ConfigurationInfo ?? LoadConfiguration();
                _tokenRefreshService = new TokenRefreshService(configuration);
                
                // Subscribe to events
                _tokenRefreshService.TokenRefreshSuccess += (sender, message) =>
                {
                    LoggingService.Info($"[MainWindow] Token refresh success: {message}");
                };
                
                _tokenRefreshService.TokenRefreshFailed += (sender, message) =>
                {
                    LoggingService.Error($"[MainWindow] Token refresh failed: {message}");
                    // Consider showing a notification to the user or logging them out
                };
                
                // Start the refresh timer
                _tokenRefreshService.StartRefreshTimer();
                
                LoggingService.Info("[MainWindow] TokenRefreshService started successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error starting TokenRefreshService: {ex.Message}");
            }
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void btnMinimize_Click(object sender, RoutedEventArgs e)
        {
            // Hide to tray instead of minimizing to taskbar
            HideToTray();
        }
        
        /// <summary>
        /// Handle window state changes - intercept minimize and hide to tray instead
        /// </summary>
        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            try
            {
                // If window is being minimized by user (visible and shown in taskbar), hide to tray instead
                if (this.WindowState == WindowState.Minimized && 
                    this.Visibility == Visibility.Visible && 
                    this.ShowInTaskbar == true)
                {
                    // Cancel the minimize and hide to tray instead
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        HideToTray();
                    }), System.Windows.Threading.DispatcherPriority.Normal);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[MainWindow] Error in StateChanged handler: {ex.Message}");
            }
        }

        private void btnMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        private void SetCallButtonState(bool isCallActive)
        {
            // Check if we're on the LiveCall page - if not, disable the button
            bool isOnLiveCallPage = (_CurrentPage == CurrentPage.LiveCall);

            if (_isDisposing)
            {
                // Currently disposing - show orange icon and disable sidebar interactions
                SidebarCallIcon.Fill = new SolidColorBrush(Colors.Orange);
                SidebarCallButtonBorder.IsEnabled = false;
                SidebarCallButtonBorder.Opacity = 0.5;
                SidebarCallButtonBorder.ToolTip = "Stopping...";
                txtInstructions.Text = "Ending call, please wait...";

                // Disable navigation sidebar buttons during call disposal
                SetSidebarNavigationEnabled(false);

                LoggingService.Info("[MainWindow] Sidebar call icon updated to orange (disposal in progress)");
            }
            else if (_isInitializing)
            {
                // Currently initializing - show orange icon and disable sidebar interactions
                SidebarCallIcon.Fill = new SolidColorBrush(Colors.Orange);
                SidebarCallButtonBorder.IsEnabled = false;
                SidebarCallButtonBorder.Opacity = 0.5;
                SidebarCallButtonBorder.ToolTip = "Starting...";
                txtInstructions.Text = "Starting call, please wait...";

                // Disable navigation sidebar buttons during call initialization
                SetSidebarNavigationEnabled(false);

                LoggingService.Info("[MainWindow] Sidebar call icon updated to orange (initialization in progress)");
            }
            else if (isCallActive)
            {
                // Call is active - show red "End Call" icon
                SidebarCallIcon.Fill = new SolidColorBrush(Colors.Red);
                SidebarCallButtonBorder.IsEnabled = isOnLiveCallPage;
                SidebarCallButtonBorder.Opacity = isOnLiveCallPage ? 1.0 : 0.5;
                SidebarCallButtonBorder.ToolTip = "End Call";
                txtInstructions.Text = isOnLiveCallPage ? "Call is active - End Call when finished" : "Navigate to Call page to end call";

                // Disable navigation sidebar buttons during active call
                SetSidebarNavigationEnabled(false);

                LoggingService.Info($"[MainWindow] Sidebar call icon updated to red/End Call (enabled: {isOnLiveCallPage})");
            }
            else
            {
                // Call is not active - show green "Start Call" icon, always enabled
                SidebarCallIcon.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));   // #4CAF50 green
                SidebarCallButtonBorder.IsEnabled = true;
                SidebarCallButtonBorder.Opacity = 1.0;
                SidebarCallButtonBorder.ToolTip = "Start Call";
                txtInstructions.Text = "Press Start Call to continue";

                // Enable navigation sidebar buttons when call is not active
                SetSidebarNavigationEnabled(true);

                LoggingService.Info($"[MainWindow] Sidebar call icon updated to green/Start Call (enabled: true, onLiveCallPage: {isOnLiveCallPage})");
            }
        }

        /// <summary>
        /// Enable or disable the sidebar navigation buttons (Settings, History)
        /// </summary>
        private void SetSidebarNavigationEnabled(bool enabled)
        {
            SidebarSettingsButtonBorder.IsEnabled = enabled;
            SidebarSettingsButtonBorder.Opacity = enabled ? 1.0 : 0.5;
            SidebarHistoryButtonBorder.IsEnabled = enabled;
            SidebarHistoryButtonBorder.Opacity = enabled ? 1.0 : 0.5;
            // Avatar button in title bar
            UserAvatarButton.IsEnabled = enabled;
            UserAvatarButton.Opacity = enabled ? 1.0 : 0.5;
        }
    }
}
