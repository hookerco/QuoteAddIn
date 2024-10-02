using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ExcelAddIn1.SendToQb
{
    internal class QBServiceHost : IDisposable
    {
        private const string ServiceProcessName = "QuickBooksServiceHost";
        private readonly string _serviceExecutablePath;
        private Process _serviceProcess;

        public QBServiceHost()
        {
            string serviceExecutablePath = _getServiceExecutablePath();
            if (string.IsNullOrWhiteSpace(serviceExecutablePath))
                throw new ArgumentException("Service executable path cannot be null or empty.", nameof(serviceExecutablePath));

            if (!File.Exists(serviceExecutablePath))
                throw new FileNotFoundException("Service executable not found.", serviceExecutablePath);

            _serviceExecutablePath = serviceExecutablePath;
        }

        private string _getServiceExecutablePath()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var projectDirectory = Directory.GetParent(baseDirectory).Parent.Parent.FullName;
            return projectDirectory + "\\ServiceHost\\debug\\QuickBooksServiceHost.exe";
        }

        /// <summary>
        /// Starts the QuickBooksService process if it's not already running.
        /// </summary>
        public void StartService()
        {
            if (IsServiceRunning())
            {
                Debug.WriteLine("QuickBooksService is already running.");
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _serviceExecutablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                    // You can set additional properties like Arguments if needed
                };

                _serviceProcess = Process.Start(startInfo);
                if (_serviceProcess == null)
                {
                    throw new InvalidOperationException("Failed to start QuickBooksService process.");
                }

                // Read the output asynchronously
                _serviceProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Debug.WriteLine($"[Service Output]: {e.Data}");
                    }
                };
                _serviceProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Debug.WriteLine($"[Service Error]: {e.Data}");
                    }
                };

                _serviceProcess.BeginOutputReadLine();
                _serviceProcess.BeginErrorReadLine();

                // Optionally, wait for the service to initialize
                // For example, wait until a specific endpoint is available
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting QuickBooksService: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stops the QuickBooksService process if it's running.
        /// </summary>
        public void StopService()
        {
            var process = GetServiceProcess();
            if (process == null)
            {
                Debug.WriteLine("QuickBooksService is not running.");
                return;
            }

            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5000)) // Wait up to 5 seconds for graceful exit
                {
                    process.Kill();
                }
                Debug.WriteLine("QuickBooksService has been stopped.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error stopping QuickBooksService: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Checks if the QuickBooksService process is currently running.
        /// </summary>
        /// <returns>True if running; otherwise, false.</returns>
        public bool IsServiceRunning()
        {
            return GetServiceProcess() != null;
        }

        /// <summary>
        /// Retrieves the QuickBooksService process if it's running.
        /// </summary>
        /// <returns>The Process instance if running; otherwise, null.</returns>
        private Process GetServiceProcess()
        {
            return Process.GetProcessesByName(ServiceProcessName).FirstOrDefault();
        }

        /// <summary>
        /// Disposes the QBServiceHost, ensuring the service is stopped.
        /// </summary>
        public void Dispose()
        {
            StopService();
            _serviceProcess?.Dispose();
        }
    }
}
