using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop.Views
{
    /// <summary>
    /// A topmost, borderless notification popup that appears at the bottom-right of the screen
    /// when an incoming call is detected while the main window is minimized to the system tray.
    /// Similar to Microsoft Teams incoming call notifications.
    /// </summary>
    public partial class IncomingCallPopupWindow : Window
    {
        private DispatcherTimer _autoDismissTimer;
        private bool _isClosing = false;

        /// <summary>
        /// The call ID associated with this incoming call popup.
        /// </summary>
        public string CallId { get; set; }

        /// <summary>
        /// The customer/caller name displayed on the popup.
        /// </summary>
        public string CallerName
        {
            get => CustomerNameText.Text;
            set => CustomerNameText.Text = value ?? "Unknown";
        }

        /// <summary>
        /// The phone number displayed on the popup.
        /// </summary>
        public string CallerPhone
        {
            get => PhoneNumberText.Text;
            set => PhoneNumberText.Text = value ?? "Unknown";
        }

        /// <summary>
        /// Raised when the user clicks Accept. Passes the CallId.
        /// </summary>
        public event EventHandler<string> CallAccepted;

        /// <summary>
        /// Raised when the user clicks Reject. Passes the CallId.
        /// </summary>
        public event EventHandler<string> CallRejected;

        public IncomingCallPopupWindow()
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
                // Use the work area (excludes taskbar) to position above the taskbar
                double workAreaWidth = SystemParameters.WorkArea.Width;
                double workAreaHeight = SystemParameters.WorkArea.Height;
                double workAreaLeft = SystemParameters.WorkArea.Left;
                double workAreaTop = SystemParameters.WorkArea.Top;

                // Position 16px from right edge and 16px above the taskbar
                this.Left = workAreaLeft + workAreaWidth - 380 - 16;
                this.Top = workAreaTop + workAreaHeight - 16; // Start off-screen for slide-up animation
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IncomingCallPopupWindow] Error positioning popup: {ex.Message}");
                // Fallback position
                this.Left = SystemParameters.PrimaryScreenWidth - 400;
                this.Top = SystemParameters.PrimaryScreenHeight - 200;
            }
        }

        /// <summary>
        /// Show the popup with a slide-up + fade-in animation and start the auto-dismiss timer.
        /// </summary>
        public void ShowWithAnimation()
        {
            try
            {
                LoggingService.Info($"[IncomingCallPopupWindow] Showing popup - CallId: {CallId}, Caller: {CallerName}, Phone: {CallerPhone}");

                // Set initial state for animation
                this.Opacity = 0;
                this.Show();

                // Calculate the final top position (16px above taskbar)
                double workAreaHeight = SystemParameters.WorkArea.Height;
                double workAreaTop = SystemParameters.WorkArea.Top;
                // Estimate popup height (card + drop shadow padding)
                double estimatedHeight = 190;
                double finalTop = workAreaTop + workAreaHeight - estimatedHeight - 16;

                // Start position: 40px below final position for slide-up effect
                this.Top = finalTop + 40;

                // Fade-in animation
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                // Slide-up animation on the window's Top property
                var slideUp = new DoubleAnimation(this.Top, finalTop, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(Window.TopProperty, slideUp);

                // Start auto-dismiss timer (30 seconds)
                _autoDismissTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                _autoDismissTimer.Tick += (s, e) =>
                {
                    _autoDismissTimer.Stop();
                    LoggingService.Info("[IncomingCallPopupWindow] Auto-dismiss timer expired (30s)");
                    CloseWithAnimation();
                };
                _autoDismissTimer.Start();

                LoggingService.Info($"[IncomingCallPopupWindow] Popup shown at Left={this.Left}, Top={finalTop}");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IncomingCallPopupWindow] Error showing popup with animation: {ex.Message}");
                // Fallback: show without animation
                try { this.Show(); } catch { }
            }
        }

        /// <summary>
        /// Close the popup with a fade-out animation.
        /// </summary>
        public void CloseWithAnimation()
        {
            if (_isClosing) return;
            _isClosing = true;

            try
            {
                // Stop auto-dismiss timer
                _autoDismissTimer?.Stop();
                _autoDismissTimer = null;

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

                LoggingService.Info("[IncomingCallPopupWindow] Closing with fade-out animation");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IncomingCallPopupWindow] Error closing with animation: {ex.Message}");
                try { this.Close(); } catch { }
            }
        }

        /// <summary>
        /// Accept button click handler.
        /// </summary>
        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info($"[IncomingCallPopupWindow] Accept clicked - CallId: {CallId}");

                // Disable buttons to prevent double-click
                AcceptButton.IsEnabled = false;
                RejectButton.IsEnabled = false;

                // Raise the event before closing
                CallAccepted?.Invoke(this, CallId);

                CloseWithAnimation();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IncomingCallPopupWindow] Error handling Accept click: {ex.Message}");
            }
        }

        /// <summary>
        /// Reject button click handler.
        /// </summary>
        private void RejectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoggingService.Info($"[IncomingCallPopupWindow] Reject clicked - CallId: {CallId}");

                // Disable buttons to prevent double-click
                AcceptButton.IsEnabled = false;
                RejectButton.IsEnabled = false;

                // Raise the event before closing
                CallRejected?.Invoke(this, CallId);

                CloseWithAnimation();
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[IncomingCallPopupWindow] Error handling Reject click: {ex.Message}");
            }
        }

        /// <summary>
        /// Override OnClosed to clean up the timer.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _autoDismissTimer?.Stop();
                _autoDismissTimer = null;
            }
            catch { }

            base.OnClosed(e);
        }
    }
}
