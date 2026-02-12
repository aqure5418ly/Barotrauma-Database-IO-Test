using Barotrauma;

namespace DatabaseIOTest
{
    partial class DatabaseIOMod : ACsMod
    {
        public DatabaseIOMod()
        {
            DebugConsole.NewMessage($"{Constants.LogPrefix} Loaded", Microsoft.Xna.Framework.Color.Green);
        }

        public override void Stop()
        {
            Services.DatabaseStore.Clear();
            DebugConsole.NewMessage($"{Constants.LogPrefix} Unloaded", Microsoft.Xna.Framework.Color.Yellow);
        }
    }
}
