# Networking

# Singleplayer
Singleplayer is the easiest of all, as there's no networking, so everything is already synced for you!
In singleplayer the global variable `CLIENT` is set to true and `Game.IsSingleplayer` is also true.

# Server
The server is responsible for receiving inputs from clients and syncing state with all other clients. 
The global variable `SERVER` is set to true if we are a server.
The only difference between a **Dedicated Server** and a **Player-Hosted Server** in Barotrauma is the fact that the latter uses Steam Networking to communicate with clients, if you for some reason want to know if you are running inside a Dedicated Server, you can use `Game.IsDedicated`.

# Client
The client is the one who connects to servers. The global variable `CLIENT` is set to true and `Game.IsMultiplayer` is also true.

# Syncing

## Serializable Properties
Serializable Properties are special members that are able to be synced with clients (and also with the server in some cases), they are very useful for server-side only code.

Example showing how to sync the sprite color of an item without any client-side code:

```
local item = ...

item.SpriteColor = Color(0, 0, 255, 255)

local property = item.SerializableProperties[Identifier("SpriteColor")]
Networking.CreateEntityEvent(item, Item.ChangePropertyEventData(property, item))
```

This is also possible to do with item components:

```
local item = ...
local light = item.GetComponentString("LightComponent")

light.LightColor = Color(0, 0, 255, 255)
local property = light.SerializableProperties[Identifier("LightColor")]
Networking.CreateEntityEvent(item, Item.ChangePropertyEventData(property, light))    
```

## Sending Custom Net Messages

This is one of the ways you can send data between the client and server.

Example on sending data from client to server

```
if CLIENT then
    -- send from client to server
    local message = Networking.Start("something")
    message.WriteString("hello")
    Networking.Send(message)
end

if SERVER then
    -- receive in server
    Networking.Receive("something", function(message, client)
        print(client.Name .. " sent " .. message.ReadString())
    end)
end
```

Example on sending data from server to client

```
if CLIENT then
    Networking.Receive("something", function(message)
        print(message.ReadString())
    end)
end

if SERVER then
    -- send from server to client
    local message = Networking.Start("something")
    message.WriteString("hello")
    Networking.Send(message, Client.ClientList[1].Connection)
end
```