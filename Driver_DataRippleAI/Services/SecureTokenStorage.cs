using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;

namespace DataRippleAIDesktop.Services
{
    public static class SecureTokenStorage
    {
        private static readonly string DirectoryPath = Path.Combine(Environment.CurrentDirectory, "DataRippleAI_Auth");  
        private static readonly string TokenFilePath = Path.Combine(DirectoryPath, "tokens.dat");
        private static readonly string CredentialsFilePath = Path.Combine(DirectoryPath, "credentials.dat");
        
        /// <summary>
        /// Clear any existing token files - useful for clean deployments
        /// </summary>
        public static void ClearExistingTokens()
        {
            try
            {
                if (File.Exists(TokenFilePath))
                {
                    File.Delete(TokenFilePath);
                    LoggingService.Info("[SecureTokenStorage] Existing token file cleared for clean deployment");
                }
                
                if (Directory.Exists(DirectoryPath) && !Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
                {
                    Directory.Delete(DirectoryPath);
                    LoggingService.Info("[SecureTokenStorage] Empty token directory removed");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[SecureTokenStorage] Could not clear existing tokens: {ex.Message}");
            }
        }

        public static void StoreUserDetails(string accessToken, string idToken, string name, string email)
        {
            if (!Directory.Exists(DirectoryPath))
            {
                Directory.CreateDirectory(DirectoryPath);
            }
            var userDetails = new
            {
                AccessToken = accessToken,
                IdToken = idToken,
                Name = name,
                Email = email
            };

            var json = JsonConvert.SerializeObject(userDetails);
            var encryptedData = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenFilePath, encryptedData);
        }

        /// <summary>
        /// Preferred method: persist full session details including refresh token and frontend WebSocket URL
        /// </summary>
        public static void StoreSessionDetails(string accessToken, string refreshToken, string name, string email, string frontendSocketUrl)
        {
            if (!Directory.Exists(DirectoryPath))
            {
                Directory.CreateDirectory(DirectoryPath);
            }

            var session = new
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                Name = name,
                Email = email,
                FrontendSocketUrl = frontendSocketUrl
            };

            var json = JsonConvert.SerializeObject(session);
            var encryptedData = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenFilePath, encryptedData);
        }

        public static dynamic RetrieveUserDetails()
        {
            try
            {
                if (File.Exists(TokenFilePath))
                {
                    var encryptedData = File.ReadAllBytes(TokenFilePath);
                    var decryptedData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
                    var json = Encoding.UTF8.GetString(decryptedData);

                    return JsonConvert.DeserializeObject(json);
                }
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                LoggingService.Warn($"[SecureTokenStorage] Cannot decrypt stored user data (likely from different machine/user): {ex.Message}");
                LoggingService.Info("[SecureTokenStorage] Clearing invalid token file and continuing...");
                
                // Delete the invalid token file so it doesn't cause issues again
                try
                {
                    File.Delete(TokenFilePath);
                    LoggingService.Info("[SecureTokenStorage] Invalid token file deleted successfully");
                }
                catch (Exception deleteEx)
                {
                    LoggingService.Warn($"[SecureTokenStorage] Could not delete invalid token file: {deleteEx.Message}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[SecureTokenStorage] Unexpected error retrieving user details: {ex.Message}");
            }

            return null;
        }

   
        public static void DeleteToken()
        {
            try
            {
                if (File.Exists(TokenFilePath))
                {
                    File.Delete(TokenFilePath);
                    System.Diagnostics.Debug.WriteLine("[SecureTokenStorage] Token file deleted successfully");
                }
            }
            catch (Exception ex)
            {
                // Log the error but don't throw to prevent app crash
                System.Diagnostics.Debug.WriteLine($"Error deleting token file: {ex.Message}");
            }
        }

        public static void ClearAllTokens()
        {
            try
            {
                // Delete the token file
                DeleteToken();
                
                // Also try to delete the entire directory if it's empty
                if (Directory.Exists(DirectoryPath))
                {
                    try
                    {
                        Directory.Delete(DirectoryPath, false);
                        System.Diagnostics.Debug.WriteLine("[SecureTokenStorage] DataRippleAI directory deleted successfully");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SecureTokenStorage] Could not delete directory: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecureTokenStorage] Error clearing tokens: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates if a JWT token is expired based on its expiration claim
        /// </summary>
        /// <param name="token">JWT token string</param>
        /// <param name="maxAgeHours">Maximum age in hours from configuration (optional, for additional validation)</param>
        /// <returns>True if token is valid and not expired, False otherwise</returns>
        public static bool IsTokenValid(string token, int? maxAgeHours = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    LoggingService.Info("[SecureTokenStorage] Token is null or empty");
                    return false;
                }

                // Try to decode the JWT token
                var handler = new JwtSecurityTokenHandler();
                
                // Check if token can be read (without validation - we just want to read claims)
                if (!handler.CanReadToken(token))
                {
                    LoggingService.Info("[SecureTokenStorage] Token cannot be read - invalid format");
                    return false;
                }

                // Read token without validation (we only care about expiration claim)
                var jsonToken = handler.ReadJwtToken(token);
                
                // Check expiration claim (exp)
                if (jsonToken.ValidTo != DateTime.MinValue)
                {
                    var expirationTime = jsonToken.ValidTo;
                    var currentTime = DateTime.UtcNow;
                    
                    // Add 5 minute buffer to account for clock skew
                    var bufferTime = expirationTime.AddMinutes(-5);
                    
                    if (currentTime >= bufferTime)
                    {
                        LoggingService.Info($"[SecureTokenStorage] Token expired. Expiration: {expirationTime:yyyy-MM-dd HH:mm:ss} UTC, Current: {currentTime:yyyy-MM-dd HH:mm:ss} UTC");
                        return false;
                    }
                    
                    LoggingService.Info($"[SecureTokenStorage] Token is valid. Expires at: {expirationTime:yyyy-MM-dd HH:mm:ss} UTC");
                }
                else
                {
                    // No expiration claim found - check maxAgeHours if provided
                    if (maxAgeHours.HasValue)
                    {
                        LoggingService.Info("[SecureTokenStorage] Token has no expiration claim, cannot validate expiration");
                        return false;
                    }
                    else
                    {
                        // If no expiration claim and no maxAgeHours, assume valid (for backward compatibility)
                        LoggingService.Info("[SecureTokenStorage] Token has no expiration claim, assuming valid");
                        return true;
                    }
                }

                // Additional validation: Check if token age exceeds maxAgeHours (if configured)
                if (maxAgeHours.HasValue && jsonToken.ValidFrom != DateTime.MinValue)
                {
                    var tokenAge = DateTime.UtcNow - jsonToken.ValidFrom;
                    if (tokenAge.TotalHours > maxAgeHours.Value)
                    {
                        LoggingService.Info($"[SecureTokenStorage] Token age ({tokenAge.TotalHours:F2} hours) exceeds maximum allowed ({maxAgeHours.Value} hours)");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[SecureTokenStorage] Error validating token: {ex.Message}");
                // If we can't validate, assume invalid for security
                return false;
            }
        }

        /// <summary>
        /// Store login credentials (email and password) securely for "Remember me" functionality
        /// </summary>
        public static void StoreCredentials(string email, string password)
        {
            try
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    Directory.CreateDirectory(DirectoryPath);
                }

                var credentials = new
                {
                    Email = email,
                    Password = password,
                    RememberMe = true
                };

                var json = JsonConvert.SerializeObject(credentials);
                var encryptedData = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(CredentialsFilePath, encryptedData);
                LoggingService.Info("[SecureTokenStorage] Credentials stored successfully");
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[SecureTokenStorage] Error storing credentials: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieve stored login credentials for "Remember me" functionality
        /// </summary>
        public static (string email, string password, bool rememberMe) RetrieveCredentials()
        {
            try
            {
                if (File.Exists(CredentialsFilePath))
                {
                    var encryptedData = File.ReadAllBytes(CredentialsFilePath);
                    var decryptedData = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
                    var json = Encoding.UTF8.GetString(decryptedData);
                    var credentials = JsonConvert.DeserializeObject<dynamic>(json);

                    string email = credentials?.Email?.ToString() ?? string.Empty;
                    string password = credentials?.Password?.ToString() ?? string.Empty;
                    bool rememberMe = credentials?.RememberMe?.ToString().ToLower() == "true";

                    return (email, password, rememberMe);
                }
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                LoggingService.Warn($"[SecureTokenStorage] Cannot decrypt stored credentials (likely from different machine/user): {ex.Message}");
                // Delete invalid credentials file
                try
                {
                    if (File.Exists(CredentialsFilePath))
                    {
                        File.Delete(CredentialsFilePath);
                        LoggingService.Info("[SecureTokenStorage] Invalid credentials file deleted");
                    }
                }
                catch (Exception deleteEx)
                {
                    LoggingService.Warn($"[SecureTokenStorage] Could not delete invalid credentials file: {deleteEx.Message}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Error($"[SecureTokenStorage] Unexpected error retrieving credentials: {ex.Message}");
            }

            return (string.Empty, string.Empty, false);
        }

        /// <summary>
        /// Delete stored credentials (when "Remember me" is unchecked)
        /// </summary>
        public static void DeleteCredentials()
        {
            try
            {
                if (File.Exists(CredentialsFilePath))
                {
                    File.Delete(CredentialsFilePath);
                    LoggingService.Info("[SecureTokenStorage] Credentials file deleted successfully");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"[SecureTokenStorage] Error deleting credentials file: {ex.Message}");
            }
        }
    }
}
