using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;
#if CLIENT
using Microsoft.Xna.Framework;
#endif

public partial class DatabaseTerminalComponent : ItemComponent, IServerSerializable, IClientSerializable
{
    public DatabaseTerminalComponent(Item item, ContentXElement element) : base(item, element)
    {
        IsActive = true;
        _creationTime = Timing.TotalTime;
    }

    public int TerminalEntityId => item?.ID ?? -1;
}
