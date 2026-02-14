# Common Questions

## What does attempt to index a nil value mean!?
It means you tried to access a field of something that doesn't exist, for example:
```
local thisIsNil = nil
print(thisIsNil.what)
print(thisIsNil.something())
```

## Why i'm getting "Cannot access field test of userdata<something>" errors?
Because you are trying to access a field in a C# class that doesn't exist.
```
print(Item.thisDoesntExist)
```

## What is "Attempt to access instance member "Drop" from a static userdata"
It means you are trying to access a member that requires an instance to accessed, for example:
```
-- there's a static global class called Item, but because it's static, you can't call item specific 
-- things on it, this will error.
Item.Drop()
Item.ItemList[1].Drop() -- this won't error, it will drop the first item ever created.
```

## I'm getting a super big error with things related to the C# side
It usually happens when you call something on the C# side and you provide nil inputs, you can get a better idea of it by analyzing the error message and trying to link it your lua code, for example:
```
Game.SendMessage(nil)
```
This will result in 
```
[LUA ERROR] System.NullReferenceException: Object reference not set to an instance of an object.
at Barotrauma.Networking.ChatMessage.GetChatMessageCommand(String message, String& messageWithoutCommand)
at Barotrauma.Networking.GameServer.SendChatMessage(String message, Nullable`1 type, Client senderClient, 
Character senderCharacter, PlayerConnectionChangeType changeType)
```
You can easily tell that the error has something to do with chat messages, and by looking back at your Lua code you can easily see whats causing it.

## How do i list all clients, characters and items?
```
for _, client in pairs(Client.ClientList) do

end

for _, character in pairs(Character.CharacterList) do

end

for _, item in pairs(Item.ItemList) do

end
```

## Running pairs() on an enumerator doesn't work!
pairs() Returns an enumerator that iterates through the entire table keys, if you already have an enumerator, you can just pass it in directly.
```
-- get first item ever created and loop through all the items stored inside it.
for item in Item.ItemList[1].OwnInventory.AllItems do

end
```

## How do i spawn an item?
```
local prefab = ItemPrefab.GetItemPrefab("screwdriver")
local firstPlayerCharacter = Client.ClientList[1].Character

-- Spawn on the world
Entity.Spawner.AddItemToSpawnQueue(prefab, firstPlayerCharacter.WorldPosition, nil, nil, function(item)
    print(item.Name .. " Has been spawned.")
end)

-- Spawn inside an inventory
Entity.Spawner.AddItemToSpawnQueue(prefab, firstPlayerCharacter.Inventory, nil, nil, function(item)
    print(item.Name .. " Has been spawned.")
end)
```

## How do i give a character a certain affliction

```
local burnPrefab = AfflictionPrefab.Prefabs["burn"]

local char = Character.CharacterList[1]
local limb = char.AnimController.MainLimb
-- or        char.AnimController.Limbs[1]

char.CharacterHealth.ApplyAffliction(limb, burnPrefab.Instantiate(100))

```

## How do i get the amount of a affliction that a character has?

```
local char = Character.CharacterList[1]

print(char.CharacterHealth.GetAffliction("burn"))
-- or
print(char.CharacterHealth.GetAffliction("burn", char.AnimController.Limbs[1]))
```

## How do i send a private chat message?

```
local chatMessage = ChatMessage.Create("Sender name", "text here", ChatMessageType.MessageBox, nil, nil)
chatMessage.Color = Color(255, 255, 0, 255)
Game.SendDirectChatMessage(chatMessage, Client.ClientList[1])
```

## How do i teleport a character or an item?

```

-- teleports an item to 0, 0
local item = Item.ItemList[1]
item.SetTransform(Vector2(0, 0), item.Rotation)

-- teleports a character to 0, 0
local character = Client.ClientList[1].Character
character.TeleportTo(Vector2(0, 0))
```