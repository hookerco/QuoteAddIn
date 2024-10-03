using System;
using System.IO;

namespace QBRequestLibrary
{
    public class Logger
    {
        private readonly string _logFilePath;

        public Logger(string logFilePath)
        {
            _logFilePath = logFilePath;

            // Ensure the log file directory exists
            if (!Directory.Exists(Path.GetDirectoryName(logFilePath)))
                Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));
        }

        public Logger() : this(DefaultLogFilePath())
        {
        }

        public static string DefaultLogFilePath()
        {
            return Path.Combine(@"Z:\COLTON TEST\QBUtility Beta Install\", "logs", Environment.UserName + ".txt");
        }

        // Log general information
        public void LogInfo(string message)
        {
            Log("INFO", message);
        }

        // Log transactions
        public void LogTransaction(string transactionDetails)
        {
            Log("TRANSACTION", transactionDetails);
        }

        // Log errors
        public void LogError(string errorMessage, Exception ex = null)
        {
            string fullMessage = errorMessage;
            if (ex != null)
            {
                fullMessage += $"\nException: {ex.Message}\nStackTrace: {ex.StackTrace}";
            }
            Log("ERROR", fullMessage);
        }

        // Private method to handle writing to the file
        private void Log(string logType, string message)
        {
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logType}] {message}";

            try
            {
                // Append the log message to the file
                using (StreamWriter writer = new StreamWriter(_logFilePath, true))
                {
                    writer.WriteLine(logMessage);
                }
            }
            catch (Exception ex)
            {
                // If logging fails, write the error to the console as a fallback
                Console.Error.WriteLine($"Logging failed: {ex.Message}");
            }
        }
    }
}
