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
        private Harmony _harmony;

        public DatabaseIOMod()
        {
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Loaded");
            DebugConsole.NewMessage($"{Constants.LogPrefix} Loaded", Microsoft.Xna.Framework.Color.Green);
            RegisterHooks();
            InstallPatches();
        }

        public override void Stop()
        {
            UnregisterHooks();
            UninstallPatches();
            Services.DatabaseStore.Clear();
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Unloaded");
            DebugConsole.NewMessage($"{Constants.LogPrefix} Unloaded", Microsoft.Xna.Framework.Color.Yellow);
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
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                int patchedCount = EndGameSaveDecisionPatch.GetPatchedMethodCount();
                Services.DatabaseStore.SetSaveDecisionBindingEnabled(patchedCount > 0);
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Harmony patches installed. EndGame targets={patchedCount}");
            }
            catch (Exception ex)
            {
                Services.DatabaseStore.SetSaveDecisionBindingEnabled(false);
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} InstallPatches failed: {ex.Message}");
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
    }
}
