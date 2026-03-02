using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.Views
{
    /// <summary>
    /// A topmost, borderless mini-window that appears during an active call or incoming ringing
    /// when the main window is minimized to the system tray.
    /// Supports two modes:
    ///   - Ringing mode: pulsing orange "Incoming Call" header with Accept/Reject buttons
    ///   - Active call mode: green "Active Call" header with Mute/End Call buttons and call timer
    /// Transitions dynamically from ringing to active when the call is accepted.
    /// </summary>
    public partial class ActiveCallPopupWindow : Window
    {
        private DispatcherTimer _durationTimer;
        private DateTime _callStartTime;
        private bool _isClosing = false;
        private bool _isMuted = false;
        private bool _isRingingMode = false;

        /// <summary>
        /// The caller/customer name displayed on the popup.
        /// </summary>
        public string CallerName
        {
            get => CallerNameText.Text;
            set => CallerNameText.Text = value ?? "Unknown";
        }

        /// <summary>
        /// The phone number displayed on the popup.
        /// </summary>
        public string CallerPhone
        {
            get => PhoneNumberText.Text;
            set
            {
                PhoneNumberText.Text = value ?? "";
                PhoneNumberText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>
        /// Gets or sets the muted state. Updates the icon accordingly.
        /// </summary>
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                UpdateMuteVisual();
            }
        }

        /// <summary>
        /// Gets whether the popup is currently in ringing mode (as opposed to active call mode).
        /// </summary>
        public bool IsRingingMode => _isRingingMode;

        /// <summary>
        /// Raised when the user clicks End Call (active call mode).
        /// </summary>
        public event EventHandler EndCallRequested;

        /// <summary>
        /// Raised when the user clicks the Expand button to restore the main window.
        /// </summary>
        public event EventHandler ExpandRequested;

        /// <summary>
        /// Raised when the user clicks the Mute/Unmute button. Passes the new muted state (true = muted).
        /// </summary>
        public event EventHandler<bool> MuteToggleRequested;

        /// <summary>
        /// Raised when the user clicks Accept Call (ringing mode).
        /// </summary>
        public event EventHandler AcceptCallRequested;

        /// <summary>
        /// Raised when the user clicks Reject Call (ringing mode).
        /// </summary>
        public event EventHandler RejectCallRequested;

        public ActiveCallPopupWindow()
        {
            InitializeComponent();
            PositionAtBottomRight();
        }

        /// <summary>
        /// Position the popup at the bottom-right corner of the primary screen, above the taskbar.
        /// </summary>
        private void PositionAtBottomRight()
        {
            try
            {
                double workAreaWidth = SystemParameters.WorkArea.Width;
                double workAreaHeight = SystemParameters.WorkArea.Height;
                double workAreaLeft = SystemParameters.WorkArea.Left;
                double workAreaTop = SystemParameters.WorkArea.Top;

                // Position 16px from right edge; start off-screen below for slide-up animation
                this.Left = workAreaLeft + workAreaWidth - 320 - 16;
                this.Top = workAreaTop + workAreaHeight; // Will be animated to final position
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error positioning popup: {ex.Message}");
                this.Left = SystemParameters.PrimaryScreenWidth - 350;
                this.Top = SystemParameters.PrimaryScreenHeight - 200;
            }
        }

        // =====================================================================
        // Mode Switching: Ringing vs Active Call
        // =====================================================================

        /// <summary>
        /// Switches the popup to ringing mode. Shows the orange "Incoming Call" header
        /// and Accept/Reject buttons. Hides the mute button and call timer.
        /// </summary>
        private void ApplyRingingMode()
        {
            try
            {
                _isRingingMode = true;

                // Header: show ringing, hide active call
                RingingHeaderPanel.Visibility = Visibility.Visible;
                ActiveCallHeaderPanel.Visibility = Visibility.Collapsed;

                // Buttons: show ringing (Accept/Reject/Expand), hide active (Mute/EndCall/Expand)
                RingingButtonsPanel.Visibility = Visibility.Visible;
                ActiveCallButtonsPanel.Visibility = Visibility.Collapsed;

                // Change the border accent to orange for ringing
                PopupBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // #FF9800

                LoggingService.Info("[ActiveCallPopupWindow] Switched to ringing mode");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error applying ringing mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Switches the popup to active call mode. Shows the green "Active Call" header
        /// with call timer and Mute/End Call buttons. Hides Accept/Reject.
        /// </summary>
        private void ApplyActiveCallMode()
        {
            try
            {
                _isRingingMode = false;

                // Header: show active call, hide ringing
                RingingHeaderPanel.Visibility = Visibility.Collapsed;
                ActiveCallHeaderPanel.Visibility = Visibility.Visible;

                // Buttons: show active (Mute/EndCall/Expand), hide ringing (Accept/Reject/Expand)
                RingingButtonsPanel.Visibility = Visibility.Collapsed;
                ActiveCallButtonsPanel.Visibility = Visibility.Visible;

                // Restore the default border color
                PopupBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(26, 58, 92)); // #1A3A5C

                LoggingService.Info("[ActiveCallPopupWindow] Switched to active call mode");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error applying active call mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Transitions the popup from ringing mode to active call mode.
        /// Called when a ringing call is accepted (from dashboard or locally).
        /// Starts the call duration timer from the provided start time.
        /// </summary>
        /// <param name="callStartTime">The time the call was accepted/started.</param>
        public void TransitionToActiveCall(DateTime callStartTime)
        {
            try
            {
                if (!_isRingingMode)
                {
                    LoggingService.Warn("[ActiveCallPopupWindow] TransitionToActiveCall called but already in active call mode");
                    return;
                }

                LoggingService.Info("[ActiveCallPopupWindow] Transitioning from ringing to active call mode");

                _callStartTime = callStartTime;

                // Switch UI to active call mode
                ApplyActiveCallMode();

                // Start the duration timer
                StartDurationTimer();
                UpdateDurationDisplay();

                LoggingService.Info("[ActiveCallPopupWindow] Transition to active call complete");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error transitioning to active call: {ex.Message}");
            }
        }

        // =====================================================================
        // Show / Close with Animation
        // =====================================================================

        /// <summary>
        /// Show the popup in active call mode with a slide-up + fade-in animation.
        /// Starts the call duration timer.
        /// </summary>
        /// <param name="callStartTime">The time the call started, used to display elapsed duration.</param>
        public void ShowWithAnimation(DateTime callStartTime)
        {
            try
            {
                _callStartTime = callStartTime;
                _isClosing = false;

                // Set to active call mode
                ApplyActiveCallMode();

                LoggingService.Info($"[ActiveCallPopupWindow] Showing active call popup - Caller: {CallerName}, Phone: {CallerPhone}");

                // Set initial state for animation
                this.Opacity = 0;
                this.Show();

                // Calculate the final top position (16px above taskbar)
                double workAreaHeight = SystemParameters.WorkArea.Height;
                double workAreaTop = SystemParameters.WorkArea.Top;
                double estimatedHeight = 160;
                double finalTop = workAreaTop + workAreaHeight - estimatedHeight - 16;

                // Start position: 40px below final position for slide-up effect
                this.Top = finalTop + 40;

                // Fade-in animation
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                // Slide-up animation
                var slideUp = new DoubleAnimation(this.Top, finalTop, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(Window.TopProperty, slideUp);

                // Start duration timer
                StartDurationTimer();

                // Immediately show current duration (in case call has been running for a while)
                UpdateDurationDisplay();

                LoggingService.Info($"[ActiveCallPopupWindow] Popup shown at Left={this.Left}, Top={finalTop}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error showing popup with animation: {ex.Message}");
                try { this.Show(); } catch { }
            }
        }

        /// <summary>
        /// Show the popup in ringing mode with a slide-up + fade-in animation.
        /// No call timer is started; instead shows "Ringing..." with Accept/Reject buttons.
        /// </summary>
        public void ShowRingingWithAnimation()
        {
            try
            {
                _isClosing = false;

                // Set to ringing mode
                ApplyRingingMode();

                LoggingService.Info($"[ActiveCallPopupWindow] Showing ringing popup - Caller: {CallerName}, Phone: {CallerPhone}");

                // Set initial state for animation
                this.Opacity = 0;
                this.Show();

                // Calculate the final top position (16px above taskbar)
                double workAreaHeight = SystemParameters.WorkArea.Height;
                double workAreaTop = SystemParameters.WorkArea.Top;
                double estimatedHeight = 160;
                double finalTop = workAreaTop + workAreaHeight - estimatedHeight - 16;

                // Start position: 40px below final position for slide-up effect
                this.Top = finalTop + 40;

                // Fade-in animation
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                // Slide-up animation
                var slideUp = new DoubleAnimation(this.Top, finalTop, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(Window.TopProperty, slideUp);

                // No duration timer in ringing mode

                LoggingService.Info($"[ActiveCallPopupWindow] Ringing popup shown at Left={this.Left}, Top={finalTop}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error showing ringing popup with animation: {ex.Message}");
                try { this.Show(); } catch { }
            }
        }

        /// <summary>
        /// Close the popup with a fade-out + slide-down animation.
        /// </summary>
        public void CloseWithAnimation()
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                StopDurationTimer();

                // Fade-out animation
                var fadeOut = new DoubleAnimation(this.Opacity, 0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (s, e) =>
                {
                    try
                    {
                        this.Close();
                    }
                    catch { }
                };
                this.BeginAnimation(UIElement.OpacityProperty, fadeOut);

                LoggingService.Info("[ActiveCallPopupWindow] Closing with fade-out animation");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error closing with animation: {ex.Message}");
                try { this.Close(); } catch { }
            }
        }

        // =====================================================================
        // Duration Timer
        // =====================================================================

        /// <summary>
        /// Starts the call duration timer (ticks every second).
        /// </summary>
        private void StartDurationTimer()
        {
            try
            {
                _durationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _durationTimer.Tick += (s, e) => UpdateDurationDisplay();
                _durationTimer.Start();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error starting duration timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the call duration timer.
        /// </summary>
        private void StopDurationTimer()
        {
            try
            {
                if (_durationTimer != null)
                {
                    _durationTimer.Stop();
                    _durationTimer = null;
                }
            }
            catch { }
        }

        /// <summary>
        /// Updates the call duration display text.
        /// </summary>
        private void UpdateDurationDisplay()
        {
            try
            {
                if (_callStartTime != default(DateTime))
                {
                    var elapsed = DateTime.Now - _callStartTime;
                    CallDurationText.Text = FormatDuration(elapsed);
                }
            }
            catch { }
        }

        /// <summary>
        /// Formats a TimeSpan duration into a human-readable string.
        /// </summary>
        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            }
            else
            {
                return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
            }
        }

        /// <summary>
        /// Updates the mute button visual to reflect the current mute state.
        /// </summary>
        private void UpdateMuteVisual()
        {
            try
            {
                if (_isMuted)
                {
                    // Muted: show muted icon with red-tinted background
                    MuteIcon.Text = "\U0001F507"; // Speaker with X (muted)
                    MuteButton.Background = new SolidColorBrush(Color.FromRgb(183, 28, 28)); // Dark red
                    MuteButton.ToolTip = "Unmute Microphone";
                }
                else
                {
                    // Unmuted: show mic icon with default background
                    MuteIcon.Text = "\U0001F399"; // Microphone
                    MuteButton.Background = new SolidColorBrush(Color.FromRgb(26, 58, 92)); // #1A3A5C
                    MuteButton.ToolTip = "Mute Microphone";
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error updating mute visual: {ex.Message}");
            }
        }

        // =====================================================================
        // Event Handlers
        // =====================================================================

        /// <summary>
        /// Mute button click handler. Toggles mute state and raises MuteToggleRequested.
        /// </summary>
        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isMuted = !_isMuted;
                UpdateMuteVisual();
                LoggingService.Info($"[ActiveCallPopupWindow] Mute toggled - IsMuted: {_isMuted}");
                MuteToggleRequested?.Invoke(this, _isMuted);
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error handling mute click: {ex.Message}");
            }
        }

        /// <summary>
        /// End Call button click handler (active call mode).
        /// </summary>
        private void EndCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[ActiveCallPopupWindow] End Call clicked");

                // Disable buttons to prevent double-click
                EndCallButton.IsEnabled = false;
                MuteButton.IsEnabled = false;
                ExpandButton.IsEnabled = false;

                EndCallRequested?.Invoke(this, EventArgs.Empty);

                // Close the popup after ending the call
                CloseWithAnimation();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error handling End Call click: {ex.Message}");
            }
        }

        /// <summary>
        /// Accept Call button click handler (ringing mode).
        /// </summary>
        private void AcceptCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[ActiveCallPopupWindow] Accept Call clicked from popup");

                // Disable ringing buttons to prevent double-click
                AcceptCallButton.IsEnabled = false;
                RejectCallButton.IsEnabled = false;

                AcceptCallRequested?.Invoke(this, EventArgs.Empty);

                // Do NOT close the popup -- MainWindow will transition it to active call mode
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error handling Accept Call click: {ex.Message}");
            }
        }

        /// <summary>
        /// Reject Call button click handler (ringing mode).
        /// </summary>
        private void RejectCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[ActiveCallPopupWindow] Reject Call clicked from popup");

                // Disable ringing buttons to prevent double-click
                AcceptCallButton.IsEnabled = false;
                RejectCallButton.IsEnabled = false;
                RingingExpandButton.IsEnabled = false;

                RejectCallRequested?.Invoke(this, EventArgs.Empty);

                // Close the popup after rejecting
                CloseWithAnimation();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error handling Reject Call click: {ex.Message}");
            }
        }

        /// <summary>
        /// Expand button click handler. Restores the main window.
        /// Works in both ringing and active call modes.
        /// </summary>
        private void ExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info("[ActiveCallPopupWindow] Expand clicked - restoring main window");
                ExpandRequested?.Invoke(this, EventArgs.Empty);

                // Close the popup since the main window is being restored
                CloseWithAnimation();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error handling Expand click: {ex.Message}");
            }
        }

        /// <summary>
        /// Allow dragging the popup by clicking and dragging anywhere on the window.
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // Only drag if the click is not on a button
                if (e.OriginalSource is FrameworkElement element)
                {
                    // Walk up the visual tree to check if we clicked on a Button
                    DependencyObject current = element;
                    while (current != null)
                    {
                        if (current is System.Windows.Controls.Button)
                            return; // Don't drag when clicking a button
                        current = VisualTreeHelper.GetParent(current);
                    }
                }

                // Clear any active animation on Top/Left so DragMove works
                this.BeginAnimation(Window.TopProperty, null);
                this.BeginAnimation(Window.LeftProperty, null);

                this.DragMove();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[ActiveCallPopupWindow] Error during drag: {ex.Message}");
            }
        }

        /// <summary>
        /// Override OnClosed to clean up the timer.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                StopDurationTimer();
            }
            catch { }

            base.OnClosed(e);
        }
    }
}
