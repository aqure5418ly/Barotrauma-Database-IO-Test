using System;
using System.IO;
using System.Text;
using Barotrauma;

namespace DatabaseIOTest.Services
{
    public static class ModFileLog
    {
        private static readonly object WriteLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool _initDone;
        private static bool _enabled;
        private static string _logDirectory;

        private static void EnsureInitialized()
        {
            if (_initDone) { return; }
            _initDone = true;

            try
            {
                string baseDir = AppContext.BaseDirectory;
                _logDirectory = Path.Combine(baseDir, "LocalMods", "Database IO Test", "Logs");
                Directory.CreateDirectory(_logDirectory);
                _enabled = true;
            }
            catch
            {
                _enabled = false;
            }
        }

        private static string GetRuntimeRole()
        {
            if (GameMain.NetworkMember == null) { return "SP"; }
            return GameMain.NetworkMember.IsServer ? "Server" : "Client";
        }

        public static void Write(string category, string message)
        {
            EnsureInitialized();
            if (!_enabled) { return; }

            try
            {
                string fileName = $"dbiotest-{DateTime.Now:yyyyMMdd}.log";
                string path = Path.Combine(_logDirectory, fileName);
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{GetRuntimeRole()}] [{category}] {message}{Environment.NewLine}";

                lock (WriteLock)
                {
                    File.AppendAllText(path, line, Utf8NoBom);
                }
            }
            catch
            {
                // Keep silent: file logging should never break gameplay logic.
            }
        }
    }
}
