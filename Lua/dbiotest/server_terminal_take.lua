DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Server = DatabaseIOTestLua.Server or {}

local DBServer = DatabaseIOTestLua.Server
DBServer.NetTakeRequest = "DBIOTEST_RequestTakeByIdentifier"
DBServer.NetTakeResult = "DBIOTEST_TakeResult"
DBServer.NetViewSubscribe = "DBIOTEST_ViewSubscribe"
DBServer.NetViewUnsubscribe = "DBIOTEST_ViewUnsubscribe"
DBServer.NetViewSnapshot = "DBIOTEST_ViewSnapshot"
DBServer.NetViewDelta = "DBIOTEST_ViewDelta"

DBServer.Subscriptions = DBServer.Subscriptions or {}
DBServer.NextViewSyncAt = DBServer.NextViewSyncAt or 0
DBServer.NextPerfLogAt = DBServer.NextPerfLogAt or 0
local PerfLogCooldown = 0.8
local PerfSyncWarnMs = 8.0

local function Log(line)
    print("[DBIOTEST][B1][Server] " .. tostring(line or ""))
end

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

local function NormalizeIdentifier(identifier)
    if identifier == nil then
        return ""
    end
    return string.lower(tostring(identifier))
end

local function Now()
    local now = 0
    pcall(function() now = Timer.Time end)
    return now or 0
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

local function IsSessionTerminal(item)
    if item == nil or item.Removed then
        return false
    end

    local normalized = NormalizeIdentifier(GetItemIdentifier(item))
    if normalized == "databaseterminalsession" then
        return true
    end

    local hasSessionTag = false
    local hasFixedTag = false
    pcall(function()
        hasSessionTag = item.HasTag("database_terminal_session")
        hasFixedTag = item.HasTag("database_terminal_fixed")
    end)
    if hasSessionTag == true then
        return true
    end

    if normalized == "databaseterminalfixed" or hasFixedTag == true then
        local component = nil
        pcall(function()
            component = item.GetComponentString("DatabaseTerminalComponent")
        end)
        if component ~= nil then
            local open = false
            pcall(function()
                open = component.IsVirtualSessionOpenForUi() == true
            end)
            return open == true
        end
    end

    return false
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

local function GetTerminalDatabaseId(terminal)
    if terminal == nil then
        return "default"
    end

    local component = nil
    pcall(function()
        component = terminal.GetComponentString("DatabaseTerminalComponent")
    end)
    if component == nil then
        return "default"
    end

    local dbid = "default"
    pcall(function()
        dbid = tostring(component.DatabaseId or "default")
    end)
    return dbid or "default"
end

local function GetTerminalComponent(terminal)
    if terminal == nil then
        return nil
    end

    local component = nil
    pcall(function()
        component = terminal.GetComponentString("DatabaseTerminalComponent")
    end)
    return component
end

local function BuildEntryMapFromComponent(terminal)
    local component = GetTerminalComponent(terminal)
    if component == nil then
        return nil
    end

    local rows = nil
    local ok = pcall(function()
        rows = component.GetVirtualViewSnapshot(true)
    end)
    if not ok or rows == nil then
        return nil
    end

    local map = {}
    local totalEntries = 0
    local totalAmount = 0

    local iterOk = pcall(function()
        for row in rows do
            if row ~= nil then
                local identifier = tostring(row.Identifier or "")
                local key = NormalizeIdentifier(identifier)
                if key ~= "" then
                    local amount = math.max(0, math.floor(tonumber(row.Amount) or 0))
                    map[key] = {
                        key = key,
                        identifier = identifier,
                        prefabIdentifier = tostring(row.PrefabIdentifier or identifier),
                        displayName = tostring(row.DisplayName or identifier),
                        amount = amount,
                        bestQuality = math.floor(tonumber(row.BestQuality) or 0),
                        avgCondition = tonumber(row.AverageCondition) or 100.0
                    }
                    totalEntries = totalEntries + 1
                    totalAmount = totalAmount + amount
                end
            end
        end
    end)

    if not iterOk then
        return nil
    end

    return map, totalEntries, totalAmount
end

local function BuildEntryMap(terminal)
    local byComponent, entryCount, amountCount = BuildEntryMapFromComponent(terminal)
    if byComponent ~= nil then
        return byComponent, entryCount, amountCount
    end

    return {}, 0, 0
end

local function CloneEntryMap(source)
    local map = {}
    for key, entry in pairs(source or {}) do
        map[key] = {
            key = key,
            identifier = tostring(entry.identifier or ""),
            prefabIdentifier = tostring(entry.prefabIdentifier or entry.identifier or ""),
            displayName = tostring(entry.displayName or entry.identifier or ""),
            amount = tonumber(entry.amount) or 0,
            bestQuality = tonumber(entry.bestQuality) or 0,
            avgCondition = tonumber(entry.avgCondition) or 100.0
        }
    end
    return map
end

local function EntriesEqual(left, right)
    if left == nil or right == nil then
        return false
    end
    if tonumber(left.amount or 0) ~= tonumber(right.amount or 0) then
        return false
    end
    if tonumber(left.bestQuality or 0) ~= tonumber(right.bestQuality or 0) then
        return false
    end

    local lc = tonumber(left.avgCondition or 0)
    local rc = tonumber(right.avgCondition or 0)
    return math.abs(lc - rc) <= 0.05
end

local function BuildDelta(previousEntries, currentEntries)
    local removed = {}
    local upserts = {}

    for key, oldEntry in pairs(previousEntries or {}) do
        if currentEntries[key] == nil then
            table.insert(removed, tostring(oldEntry.identifier or key or ""))
        end
    end

    for key, newEntry in pairs(currentEntries or {}) do
        local oldEntry = previousEntries[key]
        if oldEntry == nil or not EntriesEqual(oldEntry, newEntry) then
            table.insert(upserts, newEntry)
        end
    end

    table.sort(removed, function(a, b)
        return string.lower(tostring(a or "")) < string.lower(tostring(b or ""))
    end)

    table.sort(upserts, function(a, b)
        return string.lower(tostring(a.identifier or "")) < string.lower(tostring(b.identifier or ""))
    end)

    return removed, upserts
end

local function WriteEntry(message, entry)
    message.WriteString(tostring(entry.identifier or ""))
    message.WriteString(tostring(entry.prefabIdentifier or entry.identifier or ""))
    message.WriteString(tostring(entry.displayName or entry.identifier or ""))
    message.WriteString(tostring(math.max(0, math.floor(tonumber(entry.amount) or 0))))
    message.WriteString(tostring(math.floor(tonumber(entry.bestQuality) or 0)))
    message.WriteString(string.format("%.2f", tonumber(entry.avgCondition) or 100.0))
end

local function SendSnapshot(client, sub, entries, totalEntries, totalAmount, reason)
    if not Game.IsMultiplayer then
        return
    end
    if client == nil or client.Connection == nil or sub == nil then
        return
    end

    local list = {}
    for _, entry in pairs(entries or {}) do
        table.insert(list, entry)
    end
    table.sort(list, function(a, b)
        return string.lower(tostring(a.identifier or "")) < string.lower(tostring(b.identifier or ""))
    end)

    local message = Networking.Start(DBServer.NetViewSnapshot)
    message.WriteString(tostring(sub.terminalEntityId or ""))
    message.WriteString(tostring(sub.databaseId or "default"))
    message.WriteString(tostring(sub.serial or 0))
    message.WriteString(tostring(totalEntries or 0))
    message.WriteString(tostring(totalAmount or 0))
    message.WriteString(tostring(#list))
    for _, entry in ipairs(list) do
        WriteEntry(message, entry)
    end
    Networking.Send(message, client.Connection)

    Log(string.format(
        "Snapshot client='%s' terminal=%s serial=%s entries=%d amount=%d reason='%s'",
        tostring(client.Name or "?"),
        tostring(sub.terminalEntityId or ""),
        tostring(sub.serial or 0),
        tonumber(totalEntries or 0),
        tonumber(totalAmount or 0),
        tostring(reason or "sync")))
end

local function SendDelta(client, sub, removed, upserts, totalEntries, totalAmount, reason)
    if not Game.IsMultiplayer then
        return
    end
    if client == nil or client.Connection == nil or sub == nil then
        return
    end

    local message = Networking.Start(DBServer.NetViewDelta)
    message.WriteString(tostring(sub.terminalEntityId or ""))
    message.WriteString(tostring(sub.databaseId or "default"))
    message.WriteString(tostring(sub.serial or 0))
    message.WriteString(tostring(totalEntries or 0))
    message.WriteString(tostring(totalAmount or 0))
    message.WriteString(tostring(#removed))
    for _, identifier in ipairs(removed) do
        message.WriteString(tostring(identifier or ""))
    end
    message.WriteString(tostring(#upserts))
    for _, entry in ipairs(upserts) do
        WriteEntry(message, entry)
    end
    Networking.Send(message, client.Connection)

    Log(string.format(
        "Delta client='%s' terminal=%s serial=%s removed=%d upserts=%d entries=%d amount=%d reason='%s'",
        tostring(client.Name or "?"),
        tostring(sub.terminalEntityId or ""),
        tostring(sub.serial or 0),
        #removed,
        #upserts,
        tonumber(totalEntries or 0),
        tonumber(totalAmount or 0),
        tostring(reason or "delta")))
end

local function EnsureSubscription(client, terminal)
    if client == nil or terminal == nil then
        return nil
    end

    local terminalId = tostring(terminal.ID)
    local current = DBServer.Subscriptions[client]
    if current ~= nil and tostring(current.terminalEntityId or "") == terminalId then
        current.terminal = terminal
        return current
    end

    local sub = {
        terminalEntityId = terminalId,
        terminal = terminal,
        databaseId = GetTerminalDatabaseId(terminal),
        serial = 0,
        lastEntries = {},
        lastTotalEntries = 0,
        lastTotalAmount = 0,
        forceSnapshot = true,
        forcePush = true
    }
    DBServer.Subscriptions[client] = sub
    Log(string.format(
        "Subscribe client='%s' terminal=%s db='%s'",
        tostring(client.Name or "?"),
        terminalId,
        tostring(sub.databaseId or "default")))
    return sub
end

local function RemoveSubscription(client, reason)
    if client == nil then
        return
    end
    local sub = DBServer.Subscriptions[client]
    if sub ~= nil then
        Log(string.format(
            "Unsubscribe client='%s' terminal=%s reason='%s'",
            tostring(client.Name or "?"),
            tostring(sub.terminalEntityId or ""),
            tostring(reason or "")))
    end
    DBServer.Subscriptions[client] = nil
end

local function FlagTerminalDirty(terminalEntityId)
    local target = tostring(terminalEntityId or "")
    if target == "" then
        return
    end

    for _, sub in pairs(DBServer.Subscriptions) do
        if sub ~= nil and tostring(sub.terminalEntityId or "") == target then
            sub.forcePush = true
        end
    end
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

local function MapVirtualTakeError(reason)
    local code = string.lower(tostring(reason or ""))
    if code == "" then
        return ""
    end
    if code == "inventory_full" then
        return L("dbiotest.ui.take.full", "Buffer is full.")
    end
    if code == "not_found" then
        return L("dbiotest.ui.take.notfound", "Item not found in terminal.")
    end
    if code == "inventory_unavailable" or code == "page_load_failed" then
        return L("dbiotest.ui.take.notready", "Terminal API is not ready.")
    end
    if code == "session_closed" then
        return L("dbiotest.ui.take.invalidterminal", "Terminal session not found.")
    end
    return L("dbiotest.ui.take.failed", "Failed to transfer item.")
end

local runAsServerAuthority = SERVER == true
if not runAsServerAuthority then
    local isClient = CLIENT == true
    local isMultiplayer = true
    pcall(function()
        isMultiplayer = Game.IsMultiplayer == true
    end)
    runAsServerAuthority = isClient and (not isMultiplayer)
end

if not runAsServerAuthority then
    return
end

if DBServer.__loadedServer == true then
    return
end
DBServer.__loadedServer = true

Networking.Receive(DBServer.NetViewSubscribe, function(message, client)
    if client == nil or client.Character == nil then
        return
    end

    local terminalEntityId = tostring(message.ReadString() or "")
    local terminal = FindItemByEntityId(terminalEntityId)
    if terminal == nil or not IsSessionTerminal(terminal) then
        RemoveSubscription(client, "invalid terminal")
        return
    end

    if not CharacterCanUseTerminal(client.Character, terminal) then
        RemoveSubscription(client, "permission denied")
        return
    end

    local sub = EnsureSubscription(client, terminal)
    if sub ~= nil then
        sub.forceSnapshot = true
        sub.forcePush = true
    end
end)

Networking.Receive(DBServer.NetViewUnsubscribe, function(message, client)
    if client == nil then
        return
    end

    local terminalEntityId = tostring(message.ReadString() or "")
    local sub = DBServer.Subscriptions[client]
    if sub == nil then
        return
    end

    if terminalEntityId == "" or tostring(sub.terminalEntityId or "") == terminalEntityId then
        RemoveSubscription(client, "client request")
    end
end)

Networking.Receive(DBServer.NetTakeRequest, function(message, client)
    if client == nil or client.Character == nil then
        return
    end

    local terminalEntityId = message.ReadString()
    local wantedIdentifier = message.ReadString()
    local character = client.Character

    local sub = DBServer.Subscriptions[client]
    local terminal = nil
    if sub ~= nil and tostring(sub.terminalEntityId or "") == tostring(terminalEntityId or "") then
        terminal = sub.terminal
    end
    if terminal == nil or terminal.Removed then
        terminal = FindItemByEntityId(terminalEntityId)
        if sub ~= nil and terminal ~= nil then
            sub.terminal = terminal
        end
    end

    if terminal == nil or not IsSessionTerminal(terminal) then
        SendTakeResult(client, false, L("dbiotest.ui.take.invalidterminal", "Terminal session not found."))
        return
    end

    if not CharacterCanUseTerminal(character, terminal) then
        SendTakeResult(client, false, L("dbiotest.ui.take.denied", "You are not allowed to use this terminal."))
        return
    end

    local component = GetTerminalComponent(terminal)
    if component ~= nil then
        local okCall, reason = pcall(function()
            return tostring(component.TryTakeOneByIdentifierFromVirtualSession(wantedIdentifier, character) or "")
        end)
        if okCall then
            if tostring(reason or "") ~= "" then
                SendTakeResult(client, false, MapVirtualTakeError(reason))
                return
            end

            SendTakeResult(client, true, L("dbiotest.ui.take.success", "Item moved to terminal buffer."))
            FlagTerminalDirty(terminalEntityId)
            return
        end
        Log("Virtual take API failed: " .. tostring(reason))
    end

    SendTakeResult(client, false, L("dbiotest.ui.take.notready", "Terminal API is not ready."))
end)

Hook.Add("think", "DBIOTEST_ServerViewSyncThink", function()
    local now = Now()
    if now < (DBServer.NextViewSyncAt or 0) then
        return
    end
    DBServer.NextViewSyncAt = now + 0.25
    local syncStartedAt = now

    local toRemove = {}
    local subCount = 0
    local processedCount = 0
    local snapshotCount = 0
    local deltaCount = 0
    local fallbackLookupCount = 0

    for client, sub in pairs(DBServer.Subscriptions) do
        subCount = subCount + 1
        local removeReason = nil
        if client == nil or client.Connection == nil or client.Character == nil then
            removeReason = "client offline"
        elseif sub == nil then
            removeReason = "invalid subscription"
        end

        local terminal = nil
        if removeReason == nil then
            terminal = sub.terminal
            if terminal == nil or terminal.Removed then
                fallbackLookupCount = fallbackLookupCount + 1
                terminal = FindItemByEntityId(sub.terminalEntityId)
                sub.terminal = terminal
            end
            if terminal == nil or terminal.Removed or not IsSessionTerminal(terminal) then
                removeReason = "terminal missing"
            end
        end

        if removeReason == nil and not CharacterCanUseTerminal(client.Character, terminal) then
            removeReason = "out of range"
        end

        if removeReason ~= nil then
            table.insert(toRemove, { client = client, reason = removeReason })
        else
            processedCount = processedCount + 1
            local entries, totalEntries, totalAmount = BuildEntryMap(terminal)
            local removed, upserts = BuildDelta(sub.lastEntries, entries)

            local changed = sub.forcePush == true or #removed > 0 or #upserts > 0
            if changed then
                sub.serial = tonumber(sub.serial or 0) + 1
                local tooManyChanges = (#removed + #upserts) > 24
                if sub.forceSnapshot == true or sub.serial <= 1 or tooManyChanges then
                    SendSnapshot(client, sub, entries, totalEntries, totalAmount, tooManyChanges and "change burst" or "snapshot")
                    snapshotCount = snapshotCount + 1
                else
                    SendDelta(client, sub, removed, upserts, totalEntries, totalAmount, "delta")
                    deltaCount = deltaCount + 1
                end

                sub.lastEntries = CloneEntryMap(entries)
                sub.lastTotalEntries = totalEntries
                sub.lastTotalAmount = totalAmount
                sub.forceSnapshot = false
                sub.forcePush = false
            end
        end
    end

    for _, entry in ipairs(toRemove) do
        RemoveSubscription(entry.client, entry.reason)
    end

    local elapsedMs = math.max(0, (Now() - syncStartedAt) * 1000.0)
    if (elapsedMs >= PerfSyncWarnMs or fallbackLookupCount > 0) and
        now >= (DBServer.NextPerfLogAt or 0) then
        DBServer.NextPerfLogAt = now + PerfLogCooldown
        Log(string.format(
            "[PERF] SyncThink %.2fms subs=%d processed=%d removed=%d snapshots=%d deltas=%d fallbackLookup=%d",
            elapsedMs,
            subCount,
            processedCount,
            #toRemove,
            snapshotCount,
            deltaCount,
            fallbackLookupCount))
    end
end)

Hook.Add("roundEnd", "DBIOTEST_ServerViewRoundEnd", function()
    DBServer.Subscriptions = {}
    DBServer.NextPerfLogAt = 0
end)

Hook.Add("stop", "DBIOTEST_ServerViewStop", function()
    DBServer.Subscriptions = {}
    DBServer.NextPerfLogAt = 0
end)
