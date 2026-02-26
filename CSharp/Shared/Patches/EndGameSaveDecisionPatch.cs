using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using DatabaseIOTest.Services;
using HarmonyLib;

namespace DatabaseIOTest.Patches
{
    [HarmonyPatch]
    internal static class EndGameSaveDecisionPatch
    {
        private static readonly List<MethodBase> PatchedTargets = new List<MethodBase>();
        private static readonly HashSet<string> LoggedReflectionIssues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loggedMissingTarget;

        internal static int GetPatchedMethodCount() => PatchedTargets.Count;

        static IEnumerable<MethodBase> TargetMethods()
        {
            var matches = new List<MethodBase>();
            AddMatchingMethods(matches, "Barotrauma.Networking.GameServer", "EndGame", requireBoolParameter: true);
            AddMatchingMethods(matches, "Barotrauma.GameServer", "EndGame", requireBoolParameter: true);
            AddMatchingMethods(matches, "Barotrauma.GameMain", "QuitToMainMenu", requireBoolParameter: true);
            AddMatchingMethods(matches, "Barotrauma.SaveUtil", "SaveGame", requireBoolParameter: true);
            AddMatchingMethods(matches, "Barotrauma.GameSession", "Save", requireBoolParameter: true);

            matches = matches.Distinct().ToList();
            matches.RemoveAll(m => m == null);

            if (matches.Count <= 0)
            {
                LogMissingTargetOnce("No save-decision patch target methods matched.");
                return Enumerable.Empty<MethodBase>();
            }

            PatchedTargets.Clear();
            PatchedTargets.AddRange(matches);
            return matches;
        }

        static void Prefix(MethodBase __originalMethod, object[] __args)
        {
            if (!IsServerAuthority())
            {
                return;
            }
            if (__originalMethod == null)
            {
                return;
            }

            string declaringType = __originalMethod.DeclaringType?.FullName ?? "";
            string methodName = __originalMethod.Name ?? "";

            if (declaringType.IndexOf("GameServer", StringComparison.OrdinalIgnoreCase) >= 0 &&
                string.Equals(methodName, "EndGame", StringComparison.Ordinal))
            {
                bool? wasSaved = TryResolveBoolArgument(__originalMethod, __args, "wasSaved", "save");
                if (wasSaved == true)
                {
                    DatabaseStore.CommitRound("harmony:GameServer.EndGame(wasSaved=true)");
                }
                else if (wasSaved == false)
                {
                    // No-save branch must be authoritative; later SaveUtil/GameSession calls can still occur
                    // for non-campaign save paths and should not convert this round into a commit.
                    DatabaseStore.RollbackRound("harmony:GameServer.EndGame(wasSaved=false)");
                }
                else
                {
                    DatabaseStore.OnRoundEndObserved("harmony:GameServer.EndGame(wasSaved=unknown,deferred)");
                }
                return;
            }

            if (string.Equals(declaringType, "Barotrauma.GameMain", StringComparison.Ordinal) &&
                string.Equals(methodName, "QuitToMainMenu", StringComparison.Ordinal))
            {
                bool? save = TryResolveBoolArgument(__originalMethod, __args, "save");
                if (save == true)
                {
                    DatabaseStore.CommitRound("harmony:GameMain.QuitToMainMenu(save=true)");
                }
                else if (save == false)
                {
                    DatabaseStore.RollbackRound("harmony:GameMain.QuitToMainMenu(save=false)");
                }
                else
                {
                    ModFileLog.Write(
                        "Core",
                        $"{Constants.LogPrefix} QuitToMainMenu patch skipped: cannot resolve save flag.");
                }
                return;
            }

            if (string.Equals(declaringType, "Barotrauma.SaveUtil", StringComparison.Ordinal) &&
                string.Equals(methodName, "SaveGame", StringComparison.Ordinal))
            {
                DatabaseStore.CommitRound("harmony:SaveUtil.SaveGame");
                return;
            }

            if (string.Equals(declaringType, "Barotrauma.GameSession", StringComparison.Ordinal) &&
                string.Equals(methodName, "Save", StringComparison.Ordinal))
            {
                DatabaseStore.CommitRound("harmony:GameSession.Save");
                return;
            }
        }

        private static bool IsServerAuthority()
        {
            return GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
        }

        private static void AddMatchingMethods(
            List<MethodBase> matches,
            string typeFullName,
            string methodName,
            bool requireBoolParameter)
        {
            Type type;
            try
            {
                type = AccessTools.TypeByName(typeFullName);
            }
            catch (Exception ex)
            {
                LogReflectionIssueOnce(
                    $"{typeFullName}:type",
                    $"Save decision reflection failed resolving type {typeFullName}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (type == null)
            {
                return;
            }

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                LogReflectionIssueOnce(
                    $"{typeFullName}:methods",
                    $"Save decision reflection failed enumerating {typeFullName}.{methodName}: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            foreach (MethodInfo method in methods)
            {
                if (method == null) { continue; }

                try
                {
                    if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (requireBoolParameter)
                    {
                        ParameterInfo[] parameters;
                        try
                        {
                            parameters = method.GetParameters();
                        }
                        catch (Exception ex)
                        {
                            LogReflectionIssueOnce(
                                $"{typeFullName}:{methodName}:params",
                                $"Save decision reflection skipped {typeFullName}.{methodName} parameter scan: {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }

                        if (!parameters.Any(p => p.ParameterType == typeof(bool)))
                        {
                            continue;
                        }
                    }

                    matches.Add(method);
                }
                catch (Exception ex)
                {
                    LogReflectionIssueOnce(
                        $"{typeFullName}:{methodName}:method",
                        $"Save decision reflection skipped method in {typeFullName}.{methodName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static bool? TryResolveBoolArgument(MethodBase method, object[] args, params string[] preferredParameterNames)
        {
            if (method == null || args == null || args.Length <= 0)
            {
                return null;
            }

            var parameters = method.GetParameters();
            int selectedIndex = -1;

            for (int i = 0; i < parameters.Length && i < args.Length; i++)
            {
                var p = parameters[i];
                if (p.ParameterType != typeof(bool)) { continue; }

                string name = p.Name ?? "";
                if (preferredParameterNames != null &&
                    preferredParameterNames.Any(pref =>
                        !string.IsNullOrWhiteSpace(pref) &&
                        name.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (selectedIndex < 0)
            {
                int boolCount = 0;
                int lastBoolIndex = -1;
                for (int i = 0; i < parameters.Length && i < args.Length; i++)
                {
                    if (parameters[i].ParameterType != typeof(bool)) { continue; }
                    boolCount++;
                    lastBoolIndex = i;
                }
                if (boolCount == 1)
                {
                    selectedIndex = lastBoolIndex;
                }
            }

            if (selectedIndex >= 0 && selectedIndex < args.Length)
            {
                object value = args[selectedIndex];
                if (value is bool directBool)
                {
                    return directBool;
                }

                if (value != null && bool.TryParse(value.ToString(), out bool parsedBool))
                {
                    return parsedBool;
                }
            }

            return null;
        }

        private static void LogMissingTargetOnce(string message)
        {
            if (_loggedMissingTarget)
            {
                return;
            }

            _loggedMissingTarget = true;
            ModFileLog.Write("Core", $"{Constants.LogPrefix} {message}");
        }

        private static void LogReflectionIssueOnce(string key, string message)
        {
            if (!LoggedReflectionIssues.Add(key))
            {
                return;
            }

            ModFileLog.Write("Core", $"{Constants.LogPrefix} {message}");
        }
    }
}
