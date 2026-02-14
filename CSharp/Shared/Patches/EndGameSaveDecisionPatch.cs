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
        private static bool _loggedMissingTarget;

        internal static int GetPatchedMethodCount() => PatchedTargets.Count;

        static IEnumerable<MethodBase> TargetMethods()
        {
            var matches = new List<MethodBase>();

            foreach (var serverType in ResolveGameServerTypes())
            {
                var fromType = serverType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => string.Equals(m.Name, "EndGame", StringComparison.Ordinal))
                    .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(bool)))
                    .Cast<MethodBase>();
                matches.AddRange(fromType);
            }

            matches = matches.Distinct().ToList();

            if (matches.Count <= 0)
            {
                LogMissingTargetOnce("No EndGame(bool, ...) overload matched for save decision patch.");
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

            bool? wasSaved = TryResolveWasSaved(__originalMethod, __args);
            if (!wasSaved.HasValue)
            {
                ModFileLog.Write(
                    "Core",
                    $"{Constants.LogPrefix} EndGame patch fallback commit: cannot resolve wasSaved on method '{__originalMethod?.Name ?? "unknown"}'.");
                DatabaseStore.CommitRound("harmony:EndGame(unknown)->commitFallback");
            }
            else if (wasSaved.Value)
            {
                DatabaseStore.CommitRound("harmony:EndGame");
            }
            else
            {
                DatabaseStore.RollbackRound("harmony:EndGame(wasSaved=false)");
            }
        }

        private static bool IsServerAuthority()
        {
            return GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
        }

        private static IEnumerable<Type> ResolveGameServerTypes()
        {
            var result = new List<Type>();

            void AddIfPresent(string fullName)
            {
                Type type = AccessTools.TypeByName(fullName);
                if (type != null)
                {
                    result.Add(type);
                }
            }

            AddIfPresent("Barotrauma.GameServer");
            AddIfPresent("Barotrauma.Networking.GameServer");

            if (result.Count <= 0)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] asmTypes;
                    try
                    {
                        asmTypes = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        asmTypes = ex.Types.Where(t => t != null).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var t in asmTypes)
                    {
                        if (t == null) { continue; }
                        if (!string.Equals(t.Name, "GameServer", StringComparison.Ordinal)) { continue; }
                        result.Add(t);
                    }
                }
            }

            return result.Distinct();
        }

        private static bool? TryResolveWasSaved(MethodBase method, object[] args)
        {
            if (method == null || args == null || args.Length <= 0)
            {
                return null;
            }

            var parameters = method.GetParameters();
            int boolIndex = -1;

            for (int i = 0; i < parameters.Length && i < args.Length; i++)
            {
                var p = parameters[i];
                if (p.ParameterType != typeof(bool)) { continue; }

                string name = p.Name ?? "";
                if (name.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    boolIndex = i;
                    break;
                }

                if (boolIndex < 0)
                {
                    boolIndex = i;
                }
            }

            if (boolIndex >= 0 && boolIndex < args.Length)
            {
                object value = args[boolIndex];
                if (value is bool directBool)
                {
                    return directBool;
                }

                if (value != null && bool.TryParse(value.ToString(), out bool parsedBool))
                {
                    return parsedBool;
                }
            }

            bool? fromSingleBool = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is bool b)
                {
                    if (fromSingleBool.HasValue)
                    {
                        return null;
                    }
                    fromSingleBool = b;
                }
            }

            return fromSingleBool;
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
    }
}
