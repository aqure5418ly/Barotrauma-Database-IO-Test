Networking.Receive("SpawnMenuMod_RequestSpawn", function(msg, sender)
    if (not sender.HasPermission(0x80)) then
        return
    end

    local itemID = msg.ReadString()
    local itemQuality = msg.ReadInt16()
    local itemCount = msg.ReadInt16()

    local prefab = ItemPrefab.GetItemPrefab(itemID)
    
    for i = 1, itemCount do
        Entity.Spawner.AddItemToSpawnQueue(prefab, sender.Character.Inventory, nil, itemQuality, nil)
    end

    print(prefab.Name.ToString() .. " has been spawned by " .. sender.Name .. " in amount of " .. itemCount .. ".")           
end)