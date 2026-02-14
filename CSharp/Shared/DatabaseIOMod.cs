using Barotrauma;
using System;
using System.Reflection;

namespace DatabaseIOTest
{
    partial class DatabaseIOMod : ACsMod
    {
        private bool _endGamePatchAttempted;

        public DatabaseIOMod()
        {
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Loaded");
            DebugConsole.NewMessage($"{Constants.LogPrefix} Loaded", Microsoft.Xna.Framework.Color.Green);

            Services.DatabaseStore.BeginRound("mod-load");
            TryInstallSaveDecisionPatch();
        }

        public override void Stop()
        {
            Services.DatabaseStore.RollbackRound("mod-stop");
            Services.DatabaseStore.Clear();
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Unloaded");
            DebugConsole.NewMessage($"{Constants.LogPrefix} Unloaded", Microsoft.Xna.Framework.Color.Yellow);
        }

        private void TryInstallSaveDecisionPatch()
        {
            if (_endGamePatchAttempted) { return; }
            _endGamePatchAttempted = true;

            try
            {
                var hookType = Type.GetType("Hook") ?? Type.GetType("Barotrauma.Hook");
                if (hookType == null)
                {
                    Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Hook type not found; save decision patch unavailable.");
                    return;
                }

                var patchMethod = hookType.GetMethod("Patch", BindingFlags.Public | BindingFlags.Static);
                if (patchMethod == null)
                {
                    Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Hook.Patch not found; save decision patch unavailable.");
                    return;
                }

                // Best-effort installation. Keep round-start fallback behavior in DatabaseStore.
                // Signature is runtime-dependent in LuaCs, so this path intentionally avoids hard compile-time binding.
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Save decision patch point pending runtime signature binding.");
            }
            catch (Exception ex)
            {
                Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Save decision patch attempt failed: {ex.Message}");
            }
        }
    }
}
