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
        private static readonly object WriteLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool _initDone;
        private static bool _enabled;
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
            }
            catch
            {
                _enabled = false;
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
                        $"{DatabaseIOTest.Constants.LogPrefix} File log ready: {joined}",
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

        public static void Write(string category, string message)
        {
            EnsureInitialized();
            if (!_enabled) { return; }

            try
            {
                string fileName = $"dbiotest-{DateTime.Now:yyyyMMdd}.log";
                string line =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{GetRuntimeRole()}] [{category}] {message}{Environment.NewLine}";

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
