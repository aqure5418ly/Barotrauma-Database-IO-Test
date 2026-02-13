using Barotrauma;

namespace DatabaseIOTest
{
    partial class DatabaseIOMod : ACsMod
    {
        public DatabaseIOMod()
        {
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Loaded");
            DebugConsole.NewMessage($"{Constants.LogPrefix} Loaded", Microsoft.Xna.Framework.Color.Green);
        }

        public override void Stop()
        {
            Services.DatabaseStore.Clear();
            Services.ModFileLog.Write("Core", $"{Constants.LogPrefix} Unloaded");
            DebugConsole.NewMessage($"{Constants.LogPrefix} Unloaded", Microsoft.Xna.Framework.Color.Yellow);
        }
    }
}
