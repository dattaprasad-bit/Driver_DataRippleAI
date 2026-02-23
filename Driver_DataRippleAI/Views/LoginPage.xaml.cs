using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.WebView2.Core;
using DataRippleAIDesktop.Services;
using DataRippleAIDesktop;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Web;

namespace DataRippleAIDesktop.Views
{
    public partial class LoginPage : UserControl
    {
        
        private readonly IConfiguration _configuration;
        public event Action LoginSuccessful;
        public event Action LoginCancelled;

        public LoginPage(IConfiguration configuration)
        {
            try
            {
                LoggingService.Info("[LoginPage] Starting LoginPage initialization...");
                InitializeComponent();
                LoggingService.Info("[LoginPage] InitializeComponent completed");
                
                _configuration = configuration;
                LoggingService.Info("[LoginPage] Configuration assigned");
                
                // Load saved credentials if "Remember me" was previously checked
                LoadSavedCredentials();
                
                // Check if user is already logged in and update UI accordingly
                LoggingService.Info("[LoginPage] Checking login status...");
                CheckLoginStatus();
                LoggingService.Info("[LoginPage] LoginPage initialization completed successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Error during initialization: {ex.Message}");
                LoggingService.Info($"[LoginPage] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Load saved credentials if "Remember me" was previously checked
        /// </summary>
        private void LoadSavedCredentials()
        {
            try
            {
                var (email, password, rememberMe) = SecureTokenStorage.RetrieveCredentials();
                
                if (rememberMe && !string.IsNullOrWhiteSpace(email))
                {
                    // Load saved email
                    if (txtEmail != null)
                    {
                        txtEmail.Text = email;
                        // Remove placeholder text styling if present
                        if (txtEmail.Text == "abc@xyx.com")
                        {
                            txtEmail.Text = email;
                        }
                    }
                    
                    // Load saved password
                    if (!string.IsNullOrWhiteSpace(password))
                    {
                        if (pwdPassword != null)
                        {
                            pwdPassword.Password = password;
                        }
                    }
                    
                    // Check the "Remember me" checkbox
                    if (chkRememberMe != null)
                    {
                        chkRememberMe.IsChecked = true;
                    }
                    
                    LoggingService.Info("[LoginPage] Saved credentials loaded (Remember me was checked)");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[LoginPage] Error loading saved credentials: {ex.Message}");
            }
        }

        private void CheckLoginStatus()
        {
            var userDetails = SecureTokenStorage.RetrieveUserDetails();
            
            if (userDetails != null)
            {
                // Populate runtime globals from stored session for auto-connect after restart
                TryPopulateGlobalsFromStoredSession(userDetails);
                // User is logged in, show user info panel
                // Access properties from dynamic object safely
                string userName = userDetails.Name?.ToString() ?? "User";
                string userEmail = userDetails.Email?.ToString() ?? "user@example.com";
                ShowUserInfoPanel(userName, userEmail);
            }
            else
            {
                // User is not logged in
                ShowLoginButton();
            }
        }

        private void ShowUserInfoPanel(string userName, string userEmail)
        {
            UserInfoPanel.Visibility = Visibility.Visible;
            EmailPanel.Visibility = Visibility.Collapsed;
            PasswordPanel.Visibility = Visibility.Collapsed;
            btnSkipLogin.Visibility = Visibility.Collapsed;
            btnClientSignIn.Visibility = Visibility.Collapsed;
            
            txtUserName.Text = $"Welcome, {userName}";
            txtUserEmail.Text = userEmail;
        }

        private void ShowLoginButton()
        {
            UserInfoPanel.Visibility = Visibility.Collapsed;
            EmailPanel.Visibility = Visibility.Visible;
            PasswordPanel.Visibility = Visibility.Visible;
            btnSkipLogin.Visibility = Visibility.Visible;
            btnClientSignIn.Visibility = Visibility.Visible;
        }

        

        

        

        

        private void SetLoadingState(bool isLoading)
        {
            
            btnSkipLogin.IsEnabled = !isLoading;
            btnClientSignIn.IsEnabled = !isLoading;
            LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            txtErrorMessage.Text = message;
            ErrorBorder.Visibility = Visibility.Visible;
            StatusBorder.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Public method to show error message (can be called from MainWindow)
        /// </summary>
        public void ShowErrorMessage(string message)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                ShowError(message);
            }));
        }

        private void ShowSuccess(string message)
        {
            txtStatusMessage.Text = message;
            StatusBorder.Visibility = Visibility.Visible;
            ErrorBorder.Visibility = Visibility.Collapsed;
        }

        private void HideMessages()
        {
            ErrorBorder.Visibility = Visibility.Collapsed;
            StatusBorder.Visibility = Visibility.Collapsed;
        }

        private void btnContinue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // User wants to continue to the app
                // Since we already have valid tokens, we can trigger the login successful event
                var userDetails = SecureTokenStorage.RetrieveUserDetails();
                if (userDetails != null)
                {
                    // Ensure globals are populated (in case we reached here without CheckLoginStatus)
                    TryPopulateGlobalsFromStoredSession(userDetails);

                    // Apply dev mode selection from checkbox
                    ApplyDevModeSelection();

                    ShowSuccess("Continuing to app...");

                    // Trigger the event since user is already logged in
                    LoginSuccessful?.Invoke();
                }
                else
                {
                    // Try to get WebSocket URL from appsettings if not in stored session
                    var demoWs = _configuration["ClientIntegration:demoFrontWebSocket"] ?? string.Empty;
                    if (!string.IsNullOrEmpty(demoWs))
                    {
                        var roleParam = demoWs.Contains("?") ? "&role=Driver" : "?role=Driver";
                        var tokenParam = string.IsNullOrEmpty(Globals.BackendAccessToken) ? string.Empty : "&token=" + Uri.EscapeDataString(Globals.BackendAccessToken);
                        Globals.FrontendSocketUrl = demoWs + roleParam + tokenParam;
                        LoggingService.Info($"[LoginPage] Using WebSocket URL from appsettings for continue with user");

                        // Apply dev mode selection from checkbox
                        ApplyDevModeSelection();

                        ShowSuccess("Continuing to app...");
                        LoginSuccessful?.Invoke();
                    }
                    else
                    {
                        ShowError("No user details found. Please log in again.");
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError($"Error continuing to app: {ex.Message}");
            }
        }

        private async void btnLogout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Show loading state
                SetLoadingState(true);
                HideMessages();

                // Clear stored tokens
                SecureTokenStorage.ClearAllTokens();
                
                // Update UI to show login button
                ShowLoginButton();
                SetLoadingState(false);
                
                ShowSuccess("Logged out successfully!");
                
                // Trigger login cancelled event to refresh the page
                LoginCancelled?.Invoke();
            }
            catch (Exception ex)
            {
                ShowError($"Error during logout: {ex.Message}");
                SetLoadingState(false);
            }
        }

        private async void btnSkipLogin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Show loading state briefly for visual feedback
                SetLoadingState(true);
                HideMessages();

                // Store temporary user details for development/testing
                string tempAccessToken = "temp-access-token-" + DateTime.Now.Ticks;
                string tempIdToken = "temp-id-token-" + DateTime.Now.Ticks;
                string tempUserName = "Test User (Temp)";
                string tempUserEmail = "testuser@temp.local";

                // Store temporary user details
                SecureTokenStorage.StoreSessionDetails(tempAccessToken, tempIdToken, tempUserName, tempUserEmail, Globals.FrontendSocketUrl ?? "");

                // Mirror runtime state used by real login
                Globals.BackendAccessToken = tempAccessToken;
                Globals.BackendRefreshToken = tempIdToken;
                // Prefer demo front-end WebSocket for skip-login
                var defaultWs = _configuration["ClientIntegration:demoFrontWebSocket"] ?? string.Empty;
                if (!string.IsNullOrEmpty(defaultWs))
                {
                    // For demo WS, do not attach a token
                    Globals.FrontendSocketUrl = defaultWs.Contains("?") ? defaultWs + "&role=Driver" : defaultWs + "?role=Driver";
                }

                // Apply dev mode selection from checkbox
                ApplyDevModeSelection();

                ShowSuccess("Login skipped! Redirecting...");
                await Task.Delay(1000); // Brief delay to show success message

                // Trigger the event
                LoginSuccessful?.Invoke();
            }
            catch (Exception ex)
            {
                ShowError($"Error during skip login: {ex.Message}");
                SetLoadingState(false);
            }
        }

        private async void btnClientSignIn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetLoadingState(true);
                HideMessages();

                // Validate credentials before calling API
                ResetFieldValidation();
                if (!ValidateCredentials(out string validationError))
                {
                    ShowError(validationError);
                    SetLoadingState(false);
                    return;
                }

                var backendBase = _configuration["Backend:BaseUrl"] ?? "https://ghostagent-dev.dataripple.ai/api/";
                var authPath = _configuration["Backend:AuthLoginPath"] ?? "/auth/login";
                var loginUrl = backendBase.TrimEnd('/') + authPath;
                var email = string.IsNullOrWhiteSpace(txtEmail?.Text) ? (_configuration["ClientIntegration:AuthEmail"] ?? "test@test.com") : txtEmail.Text.Trim();
                var passwordInput = GetCurrentPassword();
                var password = string.IsNullOrWhiteSpace(passwordInput) ? (_configuration["ClientIntegration:AuthPassword"] ?? "Password123!") : passwordInput;

                using (var httpClient = HttpClientFactory.CreateApiHttpClient())
                {
                    var payload = new { email = email, password = password };
                    var json = JsonConvert.SerializeObject(payload);
                    var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(loginUrl, content);
                    var responseText = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        ShowError($"Login failed: {response.StatusCode} {responseText}");
                        SetLoadingState(false);
                        return;
                    }

                    dynamic data = JsonConvert.DeserializeObject(responseText);
                    string accessToken = data?.data?.token?.access_token ?? string.Empty;
                    string refreshToken = data?.data?.token?.refresh_token ?? string.Empty;
                    string socketUrl = data?.data?.token?.socket_url ?? string.Empty;
                    string firstName = data?.data?.user?.first_name ?? "User";
                    string lastName = data?.data?.user?.last_name ?? "";
                    string userEmail = data?.data?.user?.email ?? email;

                    if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(socketUrl))
                    {
                        ShowError("Invalid login response");
                        SetLoadingState(false);
                        return;
                    }

                    SecureTokenStorage.StoreSessionDetails(accessToken, refreshToken, ($"{firstName} {lastName}").Trim(), userEmail, socketUrl);

                    // Save credentials if "Remember me" is checked
                    if (chkRememberMe != null && chkRememberMe.IsChecked == true)
                    {
                        SecureTokenStorage.StoreCredentials(email, password);
                        LoggingService.Info("[LoginPage] Credentials saved (Remember me checked)");
                    }
                    else
                    {
                        // Clear saved credentials if checkbox is unchecked
                        SecureTokenStorage.DeleteCredentials();
                        LoggingService.Info("[LoginPage] Credentials cleared (Remember me unchecked)");
                    }

                    Globals.BackendAccessToken = accessToken;
                    Globals.BackendRefreshToken = refreshToken;
                    // Prefer passing token in query for WS auth; keep role=Driver
                    var roleParam = socketUrl.Contains("?") ? "&role=Driver" : "?role=Driver";
                    var tokenParam = "&token=" + System.Uri.EscapeDataString(accessToken);
                    Globals.FrontendSocketUrl = socketUrl + roleParam + tokenParam;

                    // Apply dev mode selection from checkbox
                    ApplyDevModeSelection();

                    ShowSuccess("Login successful! Connecting to dashboard...");
                    // Auto-enable frontend integration by setting runtime socket
                    // Main flow will see Globals.FrontendSocketUrl and connect
                    await Task.Delay(600);
                    LoginSuccessful?.Invoke();
                }
            }
            catch (Exception ex)
            {
                ShowError($"Login error: {ex.Message}");
                LoggingService.Info($"[LoginPage] Error in btnClientSignIn_Click: {ex.Message}");
            }
            finally
            {
                SetLoadingState(false);
            }
        }

        private void ResetFieldValidation()
        {
            try
            {
                // Reset email field
                if (lblEmail != null) lblEmail.Foreground = new SolidColorBrush(Colors.White);
                if (borderEmail != null)
                {
                    borderEmail.BorderThickness = new Thickness(0);
                    borderEmail.BorderBrush = null;
                }
                if (txtEmailError != null)
                {
                    txtEmailError.Text = string.Empty;
                    txtEmailError.Visibility = Visibility.Collapsed;
                }

                // Reset password field
                if (lblPassword != null) lblPassword.Foreground = new SolidColorBrush(Colors.White);
                if (borderPassword != null)
                {
                    borderPassword.BorderThickness = new Thickness(0);
                    borderPassword.BorderBrush = null;
                }
                if (txtPasswordError != null)
                {
                    txtPasswordError.Text = string.Empty;
                    txtPasswordError.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private bool ValidateCredentials(out string error)
        {
            error = string.Empty;
            var email = txtEmail?.Text?.Trim() ?? string.Empty;
            var password = GetCurrentPassword();
            bool isValid = true;

            // Reset all fields first
            ResetFieldValidation();

            // Validate email
            if (string.IsNullOrWhiteSpace(email) || email == "abc@xyx.com")
            {
                ShowFieldError("Email", "Invalid input", borderEmail, lblEmail, txtEmailError);
                isValid = false;
            }
            else
            {
                var emailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
                if (!Regex.IsMatch(email, emailPattern))
                {
                    ShowFieldError("Email", "Invalid input", borderEmail, lblEmail, txtEmailError);
                    isValid = false;
                }
            }

            // Validate password
            if (string.IsNullOrWhiteSpace(password))
            {
                ShowFieldError("Password", "Password must be at least 6 characters.", borderPassword, lblPassword, txtPasswordError);
                isValid = false;
            }
            else if (password.Length < 6)
            {
                ShowFieldError("Password", "Password must be at least 6 characters.", borderPassword, lblPassword, txtPasswordError);
                isValid = false;
            }

            if (!isValid)
            {
                error = "Please correct the errors above.";
            }

            return isValid;
        }

        private void ShowFieldError(string fieldName, string errorMessage, Border border, TextBlock label, TextBlock errorTextBlock)
        {
            try
            {
                // Set label to red
                if (label != null)
                {
                    label.Foreground = new SolidColorBrush(Colors.Red);
                }

                // Set border to red with thickness
                if (border != null)
                {
                    border.BorderThickness = new Thickness(1);
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB8, 0xB8)); // Light blue border as in image
                }

                // Show error message
                if (errorTextBlock != null)
                {
                    errorTextBlock.Text = errorMessage;
                    errorTextBlock.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private string GetCurrentPassword()
        {
            // If visible textbox is shown, use its value; otherwise use PasswordBox
            if (txtPasswordVisible != null && txtPasswordVisible.Visibility == Visibility.Visible)
            {
                return txtPasswordVisible.Text ?? string.Empty;
            }
            return pwdPassword?.Password ?? string.Empty;
        }

        private void btnTogglePassword_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (txtPasswordVisible.Visibility == Visibility.Visible)
                {
                    // Switch to hidden mode
                    pwdPassword.Password = txtPasswordVisible.Text;
                    txtPasswordVisible.Visibility = Visibility.Collapsed;
                    pwdPassword.Visibility = Visibility.Visible;
                }
                else
                {
                    // Switch to visible mode
                    txtPasswordVisible.Text = pwdPassword.Password;
                    txtPasswordVisible.Visibility = Visibility.Visible;
                    pwdPassword.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void txtEmail_GotFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                // Clear placeholder text when user focuses on the email field
                if (txtEmail.Text == "abc@xyx.com")
                {
                    txtEmail.Text = string.Empty;
                }
            }
            catch { }
        }

        private void txtEmail_LostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                // Restore placeholder text if field is empty
                if (string.IsNullOrWhiteSpace(txtEmail.Text))
                {
                    txtEmail.Text = "abc@xyx.com";
                }
            }
            catch { }
        }

        private void TryPopulateGlobalsFromStoredSession(dynamic userDetails)
        {
            try
            {
                string access = userDetails?.AccessToken?.ToString() ?? string.Empty;
                string refresh = userDetails?.RefreshToken?.ToString() ?? (userDetails?.IdToken?.ToString() ?? string.Empty);
                string storedSocket = userDetails?.FrontendSocketUrl?.ToString() ?? string.Empty;

                if (!string.IsNullOrEmpty(access))
                {
                    Globals.BackendAccessToken = access;
                    Globals.BackendRefreshToken = refresh;
                }

                // Prefer WebSocket from appsettings if present; else use stored
                var demoWs = _configuration["ClientIntegration:demoFrontWebSocket"] ?? string.Empty;
                var ws = string.IsNullOrEmpty(demoWs) ? storedSocket : demoWs;
                if (!string.IsNullOrEmpty(ws))
                {
                    var roleParam = ws.Contains("?") ? "&role=Driver" : "?role=Driver";
                    var tokenParam = string.IsNullOrEmpty(access) ? string.Empty : "&token=" + Uri.EscapeDataString(access);
                    Globals.FrontendSocketUrl = ws + roleParam + tokenParam;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Could not populate globals from stored session: {ex.Message}");
            }
        }

        private async Task InitializeWebViewAsync()
        {
            try
            {
                // Ensure WebView2 is initialized
                if (clientWebView.CoreWebView2 == null)
                {
                    LoggingService.Info("[LoginPage] Initializing WebView2...");
                    
                    // Set WebView2 user data folder to current directory
                    string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    string webView2UserDataFolder = Path.Combine(currentDirectory, "DataRippleAI.exe.WebView2");
                    
                    LoggingService.Info($"[LoginPage] WebView2 user data folder: {webView2UserDataFolder}");
                    
                    // Create CoreWebView2Environment with custom user data folder
                    var environment = await CoreWebView2Environment.CreateAsync(null, webView2UserDataFolder);
                    await clientWebView.EnsureCoreWebView2Async(environment);
                    
                    // Configure WebView2 settings
                    clientWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = true;
                    clientWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = true;
                    clientWebView.CoreWebView2.Settings.AreDevToolsEnabled = true; // Enable for debugging
                    
                    LoggingService.Info("[LoginPage] WebView2 initialized successfully with custom user data folder");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Error initializing WebView2: {ex.Message}");
                throw;
            }
        }

        private async Task NavigateWebViewAsync(string url)
        {
            try
            {
                // Ensure WebView2 is initialized before navigation
                if (clientWebView.CoreWebView2 == null)
                {
                    // Use the same initialization method to ensure consistent user data folder
                    await InitializeWebViewAsync();
                }
                
                // Use the correct CoreWebView2 navigation method
                clientWebView.CoreWebView2.Navigate(url);
                
                LoggingService.Info($"[LoginPage] WebView navigating to: {url}");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Error navigating WebView: {ex.Message}");
                throw;
            }
        }

        private void btnCloseWebView_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Use the cleanup method
                CleanupAuth0WebView();
                
                // Show message that login was cancelled
                ShowError("Login cancelled. You can try again or skip login.");
                
                LoggingService.Info("[LoginPage] WebView closed by user - state reset");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Error closing WebView: {ex.Message}");
                // Always reset loading state even if there's an error
                SetLoadingState(false);
            }
        }
        
        private void CleanupAuth0WebView()
        {
            try
            {
                // Clean up Auth0 navigation event handler if it exists
                if (clientWebView?.CoreWebView2 != null)
                {
                    // Removed Auth0 handler
                    LoggingService.Info("[LoginPage] Auth0 navigation handler unsubscribed");
                }
                
                // Hide the WebView overlay
                WebViewOverlay.Visibility = Visibility.Collapsed;
                
                // Reset loading state to allow user to try again
                SetLoadingState(false);
                
                LoggingService.Info("[LoginPage] Auth0 WebView cleanup completed");
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Error during Auth0 WebView cleanup: {ex.Message}");
                // Always reset loading state even if there's an error
                SetLoadingState(false);
            }
        }

        private void ClientWebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (e.IsSuccess)
                {
                    LoggingService.Info($"[LoginPage] WebView navigation completed successfully to: {clientWebView.Source}");
                    
                    // TODO: Check if this is the callback URL with authentication token
                    // For now, just log the URL
                    string currentUrl = clientWebView.Source?.ToString() ?? "";
                    
                    // Example: If this was real client auth, you might check for callback URLs like:
                    // if (currentUrl.Contains("callback") && currentUrl.Contains("token="))
                    // {
                    //     ExtractTokenFromUrl(currentUrl);
                    //     WebViewOverlay.Visibility = Visibility.Collapsed;
                    // }
                }
                else
                {
                    LoggingService.Info($"[LoginPage] WebView navigation failed: {e.WebErrorStatus}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Info($"[LoginPage] Error in NavigationCompleted: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets Globals.AppMode based on the DevMode checkbox state.
        /// Called before navigating away from login.
        /// </summary>
        private void ApplyDevModeSelection()
        {
            try
            {
                bool isDevMode = chkDevMode?.IsChecked == true;
                Globals.AppMode = isDevMode ? "Demo" : "Production";

                if (Globals.IsDemoMode)
                {
                    Globals.EnableVerboseLogging = true;
                    LoggingService.Info("[LoginPage] Dev Mode enabled - AppMode set to Demo, verbose logging enabled");
                }
                else
                {
                    Globals.EnableVerboseLogging = false;
                    LoggingService.Info("[LoginPage] Production Mode - AppMode set to Production");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[LoginPage] Error applying dev mode selection: {ex.Message}");
                Globals.AppMode = "Production";
            }
        }

        /// <summary>
        /// Handle "Remember me" checkbox checked event
        /// </summary>
        private void chkRememberMe_Checked(object sender, RoutedEventArgs e)
        {
            // Checkbox is checked - credentials will be saved on successful login
            // No action needed here, handled in login method
        }

        /// <summary>
        /// Handle "Remember me" checkbox unchecked event - clear saved credentials immediately
        /// </summary>
        private void chkRememberMe_Unchecked(object sender, RoutedEventArgs e)
        {
            try
            {
                // Clear saved credentials when user unchecks the box
                SecureTokenStorage.DeleteCredentials();
                LoggingService.Info("[LoginPage] Credentials cleared (Remember me unchecked by user)");
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[LoginPage] Error clearing credentials: {ex.Message}");
            }
        }
    }
}
