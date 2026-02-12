DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Server = DatabaseIOTestLua.Server or {}

local DBServer = DatabaseIOTestLua.Server
DBServer.NetTakeRequest = "DBIOTEST_RequestTakeByIdentifier"
DBServer.NetTakeResult = "DBIOTEST_TakeResult"

local function L(key, fallback)
    local value = nil
    pcall(function()
        local text = TextManager.Get(key)
        if text ~= nil and text.Value ~= nil and text.Value ~= "" then
            value = text.Value
        end
    end)
    return value or fallback
end

local function GetItemIdentifier(item)
    if item == nil or item.Prefab == nil then
        return ""
    end

    local identifier = ""
    pcall(function()
        identifier = item.Prefab.Identifier.Value
    end)
    if identifier == nil or identifier == "" then
        pcall(function()
            identifier = item.Prefab.Identifier.ToString()
        end)
    end
    if identifier == nil or identifier == "" then
        pcall(function()
            identifier = tostring(item.Prefab.Identifier)
        end)
    end

    return identifier or ""
end

local function NormalizeIdentifier(identifier)
    if identifier == nil then
        return ""
    end
    return string.lower(tostring(identifier))
end

local function IsSessionTerminal(item)
    if item == nil or item.Removed then
        return false
    end

    if NormalizeIdentifier(GetItemIdentifier(item)) == "databaseterminalsession" then
        return true
    end

    local hasTag = false
    pcall(function()
        hasTag = item.HasTag("database_terminal_session")
    end)
    return hasTag == true
end

local function FindItemByEntityId(entityId)
    if entityId == nil or entityId == "" then
        return nil
    end

    local targetId = tostring(entityId)
    for item in Item.ItemList do
        if item ~= nil and not item.Removed and tostring(item.ID) == targetId then
            return item
        end
    end

    return nil
end

local function GetTerminalInventory(terminal)
    if terminal == nil then
        return nil
    end

    local container = nil
    pcall(function()
        container = terminal.GetComponentString("ItemContainer")
    end)
    if container == nil then
        return nil
    end

    local inventory = nil
    pcall(function()
        inventory = container.Inventory
    end)
    return inventory
end

local function FindFirstMatchingItem(inventory, wantedIdentifier)
    if inventory == nil then
        return nil
    end

    local wanted = NormalizeIdentifier(wantedIdentifier)
    if wanted == "" then
        return nil
    end

    local result = nil

    local ok = pcall(function()
        for contained in inventory.AllItems do
            if contained ~= nil and not contained.Removed then
                if NormalizeIdentifier(GetItemIdentifier(contained)) == wanted then
                    result = contained
                    break
                end
            end
        end
    end)

    if ok and result ~= nil then
        return result
    end

    pcall(function()
        for contained in inventory.AllItemsMod do
            if contained ~= nil and not contained.Removed then
                if NormalizeIdentifier(GetItemIdentifier(contained)) == wanted then
                    result = contained
                    break
                end
            end
        end
    end)

    return result
end

local function CharacterCanUseTerminal(character, terminal)
    if character == nil or character.Removed or character.IsDead or terminal == nil then
        return false
    end

    local selected = nil
    local selectedSecondary = nil
    pcall(function() selected = character.SelectedItem end)
    pcall(function() selectedSecondary = character.SelectedSecondaryItem end)
    if selected == terminal or selectedSecondary == terminal then
        return true
    end

    local owner = nil
    pcall(function()
        local parentInventory = terminal.ParentInventory
        if parentInventory ~= nil then
            owner = parentInventory.Owner
        end
    end)
    if owner == character then
        return true
    end

    local closeEnough = false
    pcall(function()
        closeEnough = Vector2.Distance(character.WorldPosition, terminal.WorldPosition) <= 220
    end)
    return closeEnough
end

local function SendTakeResult(client, success, text)
    if not Game.IsMultiplayer then
        return
    end
    if client == nil or client.Connection == nil then
        return
    end

    local message = Networking.Start(DBServer.NetTakeResult)
    message.WriteBoolean(success == true)
    message.WriteString(text or "")
    Networking.Send(message, client.Connection)
end

local function TryMoveItemToCharacter(item, character)
    if item == nil or character == nil or character.Inventory == nil then
        return false
    end

    local moved = false
    pcall(function()
        moved = character.Inventory.TryPutItem(item, character, CharacterInventory.AnySlot)
    end)

    return moved == true
end

if SERVER then
    Networking.Receive(DBServer.NetTakeRequest, function(message, client)
        if client == nil or client.Character == nil then
            return
        end

        local terminalEntityId = message.ReadString()
        local wantedIdentifier = message.ReadString()
        local character = client.Character

        local terminal = FindItemByEntityId(terminalEntityId)
        if terminal == nil or not IsSessionTerminal(terminal) then
            SendTakeResult(client, false, L("dbiotest.ui.take.invalidterminal", "Terminal session not found."))
            return
        end

        if not CharacterCanUseTerminal(character, terminal) then
            SendTakeResult(client, false, L("dbiotest.ui.take.denied", "You are not allowed to use this terminal."))
            return
        end

        local inventory = GetTerminalInventory(terminal)
        if inventory == nil then
            SendTakeResult(client, false, L("dbiotest.ui.take.notready", "Terminal inventory is not ready."))
            return
        end

        local targetItem = FindFirstMatchingItem(inventory, wantedIdentifier)
        if targetItem == nil then
            SendTakeResult(client, false, L("dbiotest.ui.take.notfound", "Item not found in terminal."))
            return
        end

        local moved = TryMoveItemToCharacter(targetItem, character)
        if not moved then
            SendTakeResult(client, false, L("dbiotest.ui.take.full", "Inventory full."))
            return
        end

        SendTakeResult(client, true, L("dbiotest.ui.take.success", "Item moved to inventory."))
    end)
end
