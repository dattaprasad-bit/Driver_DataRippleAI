using System.Configuration;
using System.Data;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using DataRippleAIDesktop.Services;

namespace DataRippleAIDesktop
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static MainWindow _mainWindow = null;
        private static Mutex _mutex = null;
        private const string AppMutexName = "DataRippleAI_SingleInstance_Mutex";
        
        protected override void OnStartup(StartupEventArgs e)
        {
            // Register global exception handlers to prevent silent crashes
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Check if another instance is already running
            bool createdNew;
            _mutex = new Mutex(true, AppMutexName, out createdNew);

            if (!createdNew)
            {
                // Another instance is already running
                LoggingService.Info("[App] Another instance is already running - exiting this instance");

                // Signal the existing instance to show a notification
                try
                {
                    DataRippleAIDesktop.MainWindow.SignalShowNotification();
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[App] Could not signal existing instance: {ex.Message}");
                }

                // Try to bring the existing window to front
                try
                {
                    BringExistingInstanceToFront();
                }
                catch (Exception ex)
                {
                    LoggingService.Warn($"[App] Could not bring existing instance to front: {ex.Message}");
                }

                // Exit this instance
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // CRITICAL: Initialize SSL/TLS configuration FIRST to prevent hanging
            InitializeSecuritySettings();

            // Initialize logging
            LoggingService.Info("DataRippleAI Application Starting...");
            LoggingService.Info("Logging system initialized successfully");

            // Create MainWindow manually (only one instance)
            // Window will be hidden automatically in constructor
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();
            }
        }

        /// <summary>
        /// Handle unhandled exceptions on the WPF Dispatcher (UI) thread.
        /// Logs the exception and prevents silent crashes.
        /// </summary>
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                LoggingService.Error($"[App] UNHANDLED UI EXCEPTION: {e.Exception.Message}");
                LoggingService.Error($"[App] Exception Type: {e.Exception.GetType().FullName}");
                LoggingService.Error($"[App] Stack Trace: {e.Exception.StackTrace}");
                if (e.Exception.InnerException != null)
                {
                    LoggingService.Error($"[App] Inner Exception: {e.Exception.InnerException.Message}");
                    LoggingService.Error($"[App] Inner Stack Trace: {e.Exception.InnerException.StackTrace}");
                }
            }
            catch
            {
                // Last resort - logging itself failed
            }

            // Mark as handled to prevent app crash; the error has been logged
            e.Handled = true;
        }

        /// <summary>
        /// Handle unhandled exceptions on non-UI threads.
        /// Logs the exception for diagnostics.
        /// </summary>
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var exception = e.ExceptionObject as Exception;
                LoggingService.Error($"[App] UNHANDLED DOMAIN EXCEPTION (IsTerminating={e.IsTerminating}): {exception?.Message}");
                LoggingService.Error($"[App] Exception Type: {exception?.GetType().FullName}");
                LoggingService.Error($"[App] Stack Trace: {exception?.StackTrace}");
                if (exception?.InnerException != null)
                {
                    LoggingService.Error($"[App] Inner Exception: {exception.InnerException.Message}");
                    LoggingService.Error($"[App] Inner Stack Trace: {exception.InnerException.StackTrace}");
                }
            }
            catch
            {
                // Last resort - logging itself failed
            }
        }

        /// <summary>
        /// Handle unobserved Task exceptions (fire-and-forget tasks that faulted).
        /// Logs and marks as observed to prevent process crash.
        /// </summary>
        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                LoggingService.Error($"[App] UNOBSERVED TASK EXCEPTION: {e.Exception?.Message}");
                LoggingService.Error($"[App] Exception Type: {e.Exception?.GetType().FullName}");
                if (e.Exception?.InnerExceptions != null)
                {
                    foreach (var inner in e.Exception.InnerExceptions)
                    {
                        LoggingService.Error($"[App] Inner: {inner.GetType().FullName}: {inner.Message}");
                        LoggingService.Error($"[App] Inner Stack Trace: {inner.StackTrace}");
                    }
                }
            }
            catch
            {
                // Last resort - logging itself failed
            }

            e.SetObserved();
        }
        
        protected override void OnExit(ExitEventArgs e)
        {
            // Release the mutex when application exits
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
            
            base.OnExit(e);
        }
        
        /// <summary>
        /// Try to bring existing instance window to front
        /// </summary>
        private void BringExistingInstanceToFront()
        {
            try
            {
                // Find existing MainWindow process
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                var processes = System.Diagnostics.Process.GetProcessesByName(currentProcess.ProcessName);
                
                foreach (var process in processes)
                {
                    if (process.Id != currentProcess.Id)
                    {
                        // Found another instance - try to bring its window to front
                        IntPtr hWnd = process.MainWindowHandle;
                        if (hWnd != IntPtr.Zero)
                        {
                            ShowWindow(hWnd, SW_RESTORE);
                            SetForegroundWindow(hWnd);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[App] Error bringing existing instance to front: {ex.Message}");
            }
        }
        
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        
        private const int SW_RESTORE = 9;

        /// <summary>
        /// Initialize comprehensive SSL/TLS settings for cross-platform compatibility
        /// Enhanced for Windows 10 compatibility
        /// </summary>
        private void InitializeSecuritySettings()
        {
            try
            {
                // Log Windows version for debugging
                var osVersion = Environment.OSVersion;
                LoggingService.Info($"[App] Operating System: {osVersion.VersionString}");
                LoggingService.Info($"[App] .NET Version: {Environment.Version}");
                
                // Set comprehensive TLS protocol support for different Windows versions
                // Windows 10 may not support TLS 1.3, so handle gracefully
                SecurityProtocolType supportedProtocols = SecurityProtocolType.Tls12; // Start with TLS 1.2 (widely supported)
                
                try
                {
                    // Try to add TLS 1.3 if available (Windows 11 / newer Windows 10)
                    supportedProtocols |= SecurityProtocolType.Tls13;
                    LoggingService.Info("[App] TLS 1.3 support enabled");
                }
                catch (Exception)
                {
                    LoggingService.Info("[App] TLS 1.3 not available, using TLS 1.2");
                }
                
                // Add older protocols for maximum compatibility
                supportedProtocols |= SecurityProtocolType.Tls11;
                supportedProtocols |= SecurityProtocolType.Tls;
                
                ServicePointManager.SecurityProtocol = supportedProtocols;
                LoggingService.Info($"[App] TLS Protocols enabled: {ServicePointManager.SecurityProtocol}");

                // Global certificate validation to prevent SSL/TLS hanging
                ServicePointManager.ServerCertificateValidationCallback = ValidateCertificate;

                // Windows 10 specific optimizations
                ServicePointManager.DefaultConnectionLimit = 100; // Increased for WebSocket stability
                ServicePointManager.MaxServicePointIdleTime = 20000; // 20 seconds (shorter for stability)
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false; // Reduces latency for WebSockets
                ServicePointManager.CheckCertificateRevocationList = false; // Prevent hanging on slow networks
                
                // Additional Windows 10 compatibility settings
                ServicePointManager.EnableDnsRoundRobin = true;
                
                // Windows 10 WebSocket specific settings
                try
                {
                    // Configure DNS for WebSocket connections (Windows 10 IPv6 issues)
                    // Note: IPv6 should be disabled via registry (handled by TLS configuration scripts)
                    ServicePointManager.DnsRefreshTimeout = 60000; // 1 minute DNS timeout for faster IPv4 fallback
                    LoggingService.Info("[App] DNS configured for WebSocket compatibility (IPv6 disabled via registry)");
                }
                catch (Exception ipEx)
                {
                    LoggingService.Info($"[App] Could not configure DNS settings: {ipEx.Message}");
                }
                
                LoggingService.Info("[App] SSL/TLS security settings initialized successfully for Windows 10 compatibility");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[App] CRITICAL: SSL/TLS configuration failed: {ex.Message}");
                LoggingService.Error($"[App] Stack trace: {ex.StackTrace}");
                
                // Fallback to minimal TLS 1.2 configuration
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
                    LoggingService.Info("[App] Fallback TLS 1.2 configuration applied");
                }
                catch (Exception fallbackEx)
                {
                    LoggingService.Error($"[App] Even fallback TLS configuration failed: {fallbackEx.Message}");
                }
            }
        }

        /// <summary>
        /// Custom certificate validation to handle SSL/TLS issues gracefully
        /// </summary>
        private bool ValidateCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            try
            {
                // For development/testing: log errors but allow connections
                if (sslPolicyErrors != SslPolicyErrors.None)
                {
                    LoggingService.Info($"[App] SSL Certificate validation warning: {sslPolicyErrors}");
                    
                    // Log specific SSL policy errors for debugging
                    if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
                        LoggingService.Info("[App] SSL Error: Remote certificate not available");
                    if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                        LoggingService.Info("[App] SSL Error: Remote certificate name mismatch");
                    if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                        LoggingService.Info("[App] SSL Error: Remote certificate chain errors");
                }

                // IMPORTANT: For production, implement proper certificate validation
                // For now: allow all connections to prevent hanging, but log issues
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[App] Certificate validation error: {ex.Message}");
                return true; // Allow connection to prevent hanging
            }
        }
    }
}
