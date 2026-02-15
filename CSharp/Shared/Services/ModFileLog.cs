using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace DatabaseIOTest.Services
{
    public static class ModFileLog
    {
        private enum LogLevel
        {
            Info = 0,
            Debug = 1
        }

        private const string DebugEnvName = "DBIOTEST_DEBUG_LOG";
        private const string DebugMarkerFileName = "debug.enabled";
        private static readonly object WriteLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool _initDone;
        private static bool _enabled;
        private static bool _debugEnabled;
        private static string _debugModeSource = "default";
        private static bool _initNoticeSent;
        private static readonly List<string> LogDirectories = new List<string>();

        private static void EnsureInitialized()
        {
            if (_initDone) { return; }
            _initDone = true;

            try
            {
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in EnumerateCandidateDirectories())
                {
                    if (string.IsNullOrWhiteSpace(candidate)) { continue; }

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(candidate);
                    }
                    catch
                    {
                        continue;
                    }

                    if (unique.Contains(fullPath)) { continue; }
                    try
                    {
                        Directory.CreateDirectory(fullPath);
                        unique.Add(fullPath);
                    }
                    catch
                    {
                        // Try next candidate.
                    }
                }

                LogDirectories.Clear();
                LogDirectories.AddRange(unique);
                _enabled = LogDirectories.Count > 0;
                _debugEnabled = ResolveDebugEnabled(out _debugModeSource);
            }
            catch
            {
                _enabled = false;
                _debugEnabled = false;
                _debugModeSource = "default";
            }

            EmitInitNotice();
        }

        private static IEnumerable<string> EnumerateCandidateDirectories()
        {
            // 1) Normal game root layouts.
            foreach (var path in BuildBaseCandidates(AppContext.BaseDirectory))
            {
                yield return path;
            }

            // 2) Fallback when base dir differs.
            foreach (var path in BuildBaseCandidates(Environment.CurrentDirectory))
            {
                yield return path;
            }

            // 3) Assembly-relative lookup (client/server script bin paths).
            string modRoot = TryFindModRootFromAssembly();
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                yield return Path.Combine(modRoot, "Logs");
            }
        }

        private static IEnumerable<string> BuildBaseCandidates(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) { yield break; }
            yield return Path.Combine(root, "LocalMods", "Database IO Test", "Logs");
            yield return Path.Combine(root, "ServerLogs", "Database IO Test");
        }

        private static string TryFindModRootFromAssembly()
        {
            string assemblyDir;
            try
            {
                assemblyDir = Path.GetDirectoryName(typeof(ModFileLog).Assembly.Location);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(assemblyDir)) { return null; }

            string current = assemblyDir;
            for (int i = 0; i < 10 && !string.IsNullOrWhiteSpace(current); i++)
            {
                if (string.Equals(Path.GetFileName(current), "Database IO Test", StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                current = Directory.GetParent(current)?.FullName;
            }

            return null;
        }

        private static void EmitInitNotice()
        {
            if (_initNoticeSent) { return; }
            _initNoticeSent = true;

            try
            {
                if (_enabled)
                {
                    string joined = string.Join(" | ", LogDirectories);
                    DebugConsole.NewMessage(
                        $"{DatabaseIOTest.Constants.LogPrefix} File log ready: {joined} | debug={(_debugEnabled ? "on" : "off")} ({_debugModeSource})",
                        Color.LightGray);
                }
                else
                {
                    DebugConsole.NewMessage(
                        $"{DatabaseIOTest.Constants.LogPrefix} File log disabled: no writable log path.",
                        Color.Orange);
                }
            }
            catch
            {
                // Keep silent if console logging fails.
            }
        }

        private static string GetRuntimeRole()
        {
            if (GameMain.NetworkMember == null) { return "SP"; }
            return GameMain.NetworkMember.IsServer ? "Server" : "Client";
        }

        private static bool ResolveDebugEnabled(out string source)
        {
            source = "default";

            string envRaw = null;
            try
            {
                envRaw = Environment.GetEnvironmentVariable(DebugEnvName);
            }
            catch
            {
                envRaw = null;
            }

            if (TryParseBool(envRaw, out bool envValue))
            {
                source = $"env:{DebugEnvName}";
                return envValue;
            }

            foreach (var markerPath in EnumerateDebugMarkerCandidates())
            {
                if (string.IsNullOrWhiteSpace(markerPath)) { continue; }
                try
                {
                    if (File.Exists(markerPath))
                    {
                        source = $"marker:{markerPath}";
                        return true;
                    }
                }
                catch
                {
                    // Skip invalid/unreadable marker candidate.
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateDebugMarkerCandidates()
        {
            foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                if (string.IsNullOrWhiteSpace(root)) { continue; }
                yield return Path.Combine(root, "LocalMods", "Database IO Test", DebugMarkerFileName);
                yield return Path.Combine(root, "ServerLogs", "Database IO Test", DebugMarkerFileName);
            }

            string modRoot = TryFindModRootFromAssembly();
            if (!string.IsNullOrWhiteSpace(modRoot))
            {
                yield return Path.Combine(modRoot, DebugMarkerFileName);
                yield return Path.Combine(modRoot, "Logs", DebugMarkerFileName);
            }
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(raw)) { return false; }

            string text = raw.Trim().ToLowerInvariant();
            if (text == "1" || text == "true" || text == "on" || text == "yes")
            {
                value = true;
                return true;
            }

            if (text == "0" || text == "false" || text == "off" || text == "no")
            {
                value = false;
                return true;
            }

            return false;
        }

        public static bool IsDebugEnabled
        {
            get
            {
                EnsureInitialized();
                return _debugEnabled;
            }
        }

        public static void SetDebugEnabled(bool enabled, string source = "runtime")
        {
            EnsureInitialized();
            bool changed = _debugEnabled != enabled;
            _debugEnabled = enabled;
            _debugModeSource = string.IsNullOrWhiteSpace(source) ? "runtime" : source.Trim();

            Write(
                "Core",
                $"{DatabaseIOTest.Constants.LogPrefix} Debug logging {(_debugEnabled ? "enabled" : "disabled")} source='{_debugModeSource}' changed={changed}");
        }

        public static void Write(string category, string message)
        {
            WriteInternal(category, message, LogLevel.Info);
        }

        public static void WriteDebug(string category, string message)
        {
            WriteInternal(category, message, LogLevel.Debug);
        }

        private static void WriteInternal(string category, string message, LogLevel level)
        {
            EnsureInitialized();
            if (!_enabled) { return; }
            if (level == LogLevel.Debug && !_debugEnabled) { return; }

            try
            {
                string fileName = $"dbiotest-{DateTime.Now:yyyyMMdd}.log";
                string levelTag = level == LogLevel.Debug ? " [DBG]" : "";
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{GetRuntimeRole()}] [{category}]{levelTag} {message}{Environment.NewLine}";

                lock (WriteLock)
                {
                    foreach (var dir in LogDirectories)
                    {
                        if (string.IsNullOrWhiteSpace(dir)) { continue; }
                        try
                        {
                            string path = Path.Combine(dir, fileName);
                            File.AppendAllText(path, line, Utf8NoBom);
                        }
                        catch
                        {
                            // Try next target path.
                        }
                    }
                }
            }
            catch
            {
                // Keep silent: file logging should never break gameplay logic.
            }
        }
    }
}
