using NLog;
using System;

namespace DataRippleAIDesktop.Services
{
    /// <summary>
    /// Centralized logging service using NLog
    /// </summary>
    public static class LoggingService
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Log a debug message
        /// </summary>
        public static void Debug(string message, params object[] args)
        {
            _logger.Debug(message, args);
        }

        /// <summary>
        /// Log an info message
        /// </summary>
        public static void Info(string message, params object[] args)
        {
            _logger.Info(message, args);
        }

        /// <summary>
        /// Log a warning message
        /// </summary>
        public static void Warn(string message, params object[] args)
        {
            _logger.Warn(message, args);
        }

        /// <summary>
        /// Log an error message
        /// </summary>
        public static void Error(string message, params object[] args)
        {
            _logger.Error(message, args);
        }

        /// <summary>
        /// Log an error with exception
        /// </summary>
        public static void Error(Exception exception, string message, params object[] args)
        {
            _logger.Error(exception, message, args);
        }

        /// <summary>
        /// Log a fatal message
        /// </summary>
        public static void Fatal(string message, params object[] args)
        {
            _logger.Fatal(message, args);
        }

        /// <summary>
        /// Log a fatal message with exception
        /// </summary>
        public static void Fatal(Exception exception, string message, params object[] args)
        {
            _logger.Fatal(exception, message, args);
        }

        /// <summary>
        /// Get a logger for a specific class
        /// </summary>
        public static Logger GetLogger<T>()
        {
            return LogManager.GetLogger(typeof(T).Name);
        }

        /// <summary>
        /// Get a logger for a specific class name
        /// </summary>
        public static Logger GetLogger(string name)
        {
            return LogManager.GetLogger(name);
        }
    }
}
