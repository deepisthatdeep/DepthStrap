namespace Bloxstrap
{
    // https://stackoverflow.com/a/53873141/11852173

    public class Logger
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private FileStream? _filestream;

        public readonly List<string> History = new();
        public bool Initialized = false;
        public bool NoWriteMode = false;
        public string? FileLocation;

        public string AsDocument { get { lock (History) return String.Join('\n', History); } }

        public void Initialize(bool useTempDir = false)
        {
            const string LOG_IDENT = "Logger::Initialize";

            string directory = useTempDir ? Path.Combine(Paths.TempLogs) : Path.Combine(Paths.Base, "Logs");
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            // Bootstrapper, post-launch helper and watcher routinely start in the same second.
            // Diagnostic files must never double as an application-instance lock.
            string filename = $"{App.ProjectName}_{timestamp}_{Environment.ProcessId}_{Guid.NewGuid():N}.log";
            string location = Path.Combine(directory, filename);

            WriteLine(LOG_IDENT, $"Initializing at {location}");

            if (Initialized)
            {
                WriteLine(LOG_IDENT, "Failed to initialize because logger is already initialized");
                return;
            }

            if (File.Exists(location))
            {
                WriteLine(LOG_IDENT, "Failed to initialize because log file already exists");
                return;
            }

            try
            {
                Directory.CreateDirectory(directory);
                _filestream = File.Open(location, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException)
            {
                NoWriteMode = true;
                WriteLine(LOG_IDENT, "Diagnostic storage could not be initialized; continuing with in-memory diagnostics.");
                return;
            }
            catch (UnauthorizedAccessException)
            {
                if (NoWriteMode)
                    return;

                WriteLine(LOG_IDENT, $"Failed to initialize because Bloxstrap cannot write to {directory}");

                Frontend.ShowMessageBox(
                    String.Format(Strings.Logger_NoWriteMode, directory), 
                    System.Windows.MessageBoxImage.Warning, 
                    System.Windows.MessageBoxButton.OK
                );

                NoWriteMode = true;

                return;
            }
            

            Initialized = true;

            string history;
            lock (History) history = string.Join("\r\n", History);
            if (history.Length > 0) WriteToLog(history);

            WriteLine(LOG_IDENT, "Finished initializing!");

            FileLocation = location;

            // Retention owns only generated diagnostic filenames. Other files in
            // Logs may be user exports or notes and must never be swept by age.
            try
            {
                if (!Paths.Initialized || !Directory.Exists(Paths.Logs)) return;
                string pattern = $@"\A{Regex.Escape(App.ProjectName)}_\d{{8}}T\d{{6}}Z(?:_\d+_[a-fA-F0-9]{{32}})?\.log\z";
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (FileInfo log in new DirectoryInfo(Paths.Logs).EnumerateFiles("*.log"))
                {
                    if (!Regex.IsMatch(log.Name, pattern) || log.LastWriteTimeUtc > cutoff ||
                        (log.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    try
                    {
                        log.Delete();
                        WriteLine(LOG_IDENT, $"Cleaned up old diagnostic log '{log.Name}'");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        WriteLine(LOG_IDENT, "An old diagnostic log could not be deleted; it was retained.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteLine(LOG_IDENT, "Diagnostic retention could not finish; startup continues.");
            }
        }

        private void WriteLine(string message)
        {
            string timestamp = DateTime.UtcNow.ToString("s") + "Z";
            string outcon = $"{timestamp} {DiagnosticPrivacy.Redact(message)}";
            string outlog = outcon.Replace(Paths.UserProfile, "%UserProfile%", StringComparison.InvariantCultureIgnoreCase);

            Debug.WriteLine(outcon);
            WriteToLog(outlog);

            lock (History) History.Add(outlog);
        }

        public void WriteLine(string identifier, string message) => WriteLine($"[{identifier}] {message}");

        public void WriteException(string identifier, Exception ex)
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            string hresult = "0x" + ex.HResult.ToString("X8");

            WriteLine($"[{identifier}] ({hresult}) {ex}");

            Thread.CurrentThread.CurrentUICulture = Locale.CurrentCulture;
        }

        private void WriteToLog(string message)
        {
            if (!Initialized || NoWriteMode)
                return;

            // Startup helpers can exit immediately, and the WPF dispatcher may
            // already be stopping. Finish each serialized write before returning.
            _semaphore.Wait();
            try
            {
                if (!Initialized || NoWriteMode) return;
                _filestream!.Write(Encoding.UTF8.GetBytes($"{message}\r\n"));
                _filestream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Losing diagnostic storage must not crash the app. History still
                // retains the message for the exception window or a copied report.
                NoWriteMode = true;
                Initialized = false;
                Debug.WriteLine($"Diagnostic log unavailable: {ex.GetType().Name}");
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}