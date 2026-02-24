using System;
using System.Reflection;
using Barotrauma;
using DatabaseIOTest.Patches;
using HarmonyLib;

namespace DatabaseIOTest
{
    partial class DatabaseIOMod : ACsMod
    {
        private const string RoundStartHookId = "DBIOTEST.RoundStart";
        private const string RoundEndHookId = "DBIOTEST.RoundEnd";
        private const string HarmonyId = "DatabaseIOTest.SaveConsistency";
        private const string BuildStamp = "dev-20260223-fixed-ui-strategy-updateonly";
        private Harmony _harmony;

        public DatabaseIOMod()
        {
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Loaded");
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Debug log mode active={Services.ModFileLog.IsDebugEnabled}");
            string asmLocation = "";
            try
            {
                asmLocation = Assembly.GetExecutingAssembly().Location ?? "";
            }
            catch
            {
                asmLocation = "";
            }

            Services.ModFileLog.Write(
                "Core",
                $"{Constants.LogPrefix} BuildStamp='{BuildStamp}' asm='{asmLocation}' cwd='{Environment.CurrentDirectory}'");
            LogLuaBridgeDiagnostics();
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} Loaded", Microsoft.Xna.Framework.Color.Green);
            RegisterHooks();
            InstallPatches();
        }

        public override void Stop()
        {
            UnregisterHooks();
            UninstallPatches();
            Services.DatabaseStore.Clear();
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Unloaded");
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} Unloaded", Microsoft.Xna.Framework.Color.Yellow);
        }

        private void RegisterHooks()
        {
            try
            {
                if (GameMain.LuaCs?.Hook == null)
                {
                    Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Hook API unavailable; lifecycle hooks skipped.");
                    return;
                }

                GameMain.LuaCs.Hook.Add("roundStart", RoundStartHookId, _ =>
                {
                    Services.DatabaseStore.BeginRound("hook:roundStart");
                    return null;
                });

                GameMain.LuaCs.Hook.Add("roundEnd", RoundEndHookId, _ =>
                {
                    Services.DatabaseStore.OnRoundEndObserved("hook:roundEnd");
                    return null;
                });
            }
            catch (Exception ex)
            {
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} RegisterHooks failed: {ex.Message}");
            }
        }

        private void UnregisterHooks()
        {
            try
            {
                if (GameMain.LuaCs?.Hook == null) { return; }
                GameMain.LuaCs.Hook.Remove("roundStart", RoundStartHookId);
                GameMain.LuaCs.Hook.Remove("roundEnd", RoundEndHookId);
            }
            catch (Exception ex)
            {
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} UnregisterHooks failed: {ex.Message}");
            }
        }

        private void InstallPatches()
        {
            try
            {
                _harmony = new Harmony(HarmonyId);

                int uiPatchOk = 0;
                int uiPatchFail = 0;
                int savePatchOk = 0;
                int savePatchFail = 0;

                var uiPatchTypes = new[]
                {
                    typeof(DatabaseTerminalUiHijackPatch),
                    typeof(DatabaseTerminalContainerSilencePatch),
                    typeof(DatabaseTerminalFixedHudPatch),
                    typeof(DatabaseAutoRestockerPreviewPatch)
                };

                foreach (Type patchType in uiPatchTypes)
                {
                    if (TryPatchClass(patchType, "UI"))
                    {
                        uiPatchOk++;
                    }
                    else
                    {
                        uiPatchFail++;
                    }
                }

                if (TryPatchClass(typeof(EndGameSaveDecisionPatch), "SaveDecision"))
                {
                    savePatchOk++;
                }
                else
                {
                    savePatchFail++;
                }

                int patchedCount = EndGameSaveDecisionPatch.GetPatchedMethodCount();
                bool saveBindingEnabled = patchedCount > 0;
                Services.DatabaseStore.SetSaveDecisionBindingEnabled(saveBindingEnabled);

                Services.ModFileLog.Write(
                    "Core",
                    $"{Constants.LogPrefix} Harmony patches installed. uiPatchOk={uiPatchOk} uiPatchFail={uiPatchFail} " +
                    $"savePatchOk={savePatchOk} savePatchFail={savePatchFail} EndGame targets={patchedCount}");
            }
            catch (Exception ex)
            {
                Services.DatabaseStore.SetSaveDecisionBindingEnabled(false);
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} InstallPatches failed: {ex.Message}");
            }
        }

        private bool TryPatchClass(Type patchType, string group)
        {
            if (_harmony == null || patchType == null)
            {
                return false;
            }

            try
            {
                MethodInfo createProcessorMethod = typeof(Harmony).GetMethod(
                    "CreateClassProcessor",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Type) },
                    null);

                if (createProcessorMethod == null)
                {
                    Services.ModFileLog.Write(
                        "Core",
                        $"{Constants.LogPrefix} Patch group '{group}' failed type='{patchType.FullName}': CreateClassProcessor not found.");
                    return false;
                }

                object processor = createProcessorMethod.Invoke(_harmony, new object[] { patchType });
                if (processor == null)
                {
                    Services.ModFileLog.Write(
                        "Core",
                        $"{Constants.LogPrefix} Patch group '{group}' failed type='{patchType.FullName}': processor is null.");
                    return false;
                }

                MethodInfo patchMethod = processor.GetType().GetMethod(
                    "Patch",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

                if (patchMethod == null)
                {
                    Services.ModFileLog.Write(
                        "Core",
                        $"{Constants.LogPrefix} Patch group '{group}' failed type='{patchType.FullName}': Patch() not found.");
                    return false;
                }

                patchMethod.Invoke(processor, null);
                return true;
            }
            catch (Exception ex)
            {
                Services.ModFileLog.Write(
                    "Core",
                    $"{Constants.LogPrefix} Patch group '{group}' failed type='{patchType.FullName}': {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private void UninstallPatches()
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} UninstallPatches failed: {ex.Message}");
            }
            finally
            {
                _harmony = null;
            }
        }

        private static void LogLuaBridgeDiagnostics()
        {
            try
            {
                var storeType = typeof(Services.DatabaseStore);
                var legacyBridgeType = typeof(Services.DatabaseLuaBridge);
                bool hasStoreOpen = storeType.GetMethod("IsTerminalSessionOpenForLua", BindingFlags.Public | BindingFlags.Static) != null;
                bool hasStoreDbId = storeType.GetMethod("GetTerminalDatabaseIdForLua", BindingFlags.Public | BindingFlags.Static) != null;
                bool hasStoreSnapshot = storeType.GetMethod("GetTerminalVirtualSnapshotForLua", BindingFlags.Public | BindingFlags.Static) != null;
                bool hasStoreTake = storeType.GetMethod("TryTakeOneByIdentifierFromTerminalSessionForLua", BindingFlags.Public | BindingFlags.Static) != null;
                bool hasLegacyOpen = legacyBridgeType.GetMethod("IsTerminalSessionOpen", BindingFlags.Public | BindingFlags.Static) != null;
                bool hasLegacySnapshot = legacyBridgeType.GetMethod("GetTerminalVirtualSnapshot", BindingFlags.Public | BindingFlags.Static) != null;
                bool hasLegacyTake = legacyBridgeType.GetMethod("TryTakeOneByIdentifierFromTerminalSession", BindingFlags.Public | BindingFlags.Static) != null;

                Services.ModFileLog.Write(
                    "Core",
                    $"{Constants.LogPrefix} LuaBridgeDiag storeType='{storeType.FullName}' legacyType='{legacyBridgeType.FullName}' " +
                    $"storeOpen={hasStoreOpen} storeDbId={hasStoreDbId} storeSnapshot={hasStoreSnapshot} storeTake={hasStoreTake} " +
                    $"legacyOpen={hasLegacyOpen} legacySnapshot={hasLegacySnapshot} legacyTake={hasLegacyTake}");
            }
            catch (Exception ex)
            {
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} LuaBridgeDiag failed: {ex.Message}");
            }
        }
    }
}

