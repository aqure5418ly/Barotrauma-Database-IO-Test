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
DBServer.NextSnapshotDiagAt = DBServer.NextSnapshotDiagAt or 0
local PerfLogCooldown = 0.8
local PerfSyncWarnMs = 8.0
local ForceLuaDebugLog = false
pcall(function()
    if DatabaseIOTestLua ~= nil and DatabaseIOTestLua.IsLuaDebugEnabled ~= nil then
        ForceLuaDebugLog = DatabaseIOTestLua.IsLuaDebugEnabled() == true
    end
end)
local LoggerBridgeMissingPrinted = false
DBServer.ComponentCache = DBServer.ComponentCache or {}

local function TryWriteFileLog(level, line)
    local text = tostring(line or "")
    local wrote = false

    local function TryLogger(logger)
        if logger == nil then
            return false
        end

        local mode = tostring(level or "info")
        if mode == "debug" then
            local okPreferred = pcall(function()
                logger.WriteLuaServerDebug(text)
            end)
            if okPreferred then
                return true
            end

            local okFallbackDebug = pcall(function()
                logger.WriteDebug("LuaServer", text)
            end)
            if okFallbackDebug then
                return true
            end

            if ForceLuaDebugLog then
                local okDebugToInfo = pcall(function()
                    logger.WriteLuaServer("[DBG->INFO] " .. text)
                end)
                if okDebugToInfo then
                    return true
                end

                local okFallbackInfo = pcall(function()
                    logger.Write("LuaServer", "[DBG->INFO] " .. text)
                end)
                if okFallbackInfo then
                    return true
                end
            end

            return false
        end

        local okPreferred = pcall(function()
            logger.WriteLuaServer(text)
        end)
        if okPreferred then
            return true
        end

        local okFallback = pcall(function()
            logger.Write("LuaServer", text)
        end)
        return okFallback
    end

    pcall(function()
        if wrote then
            return
        end
        if DatabaseIOTest ~= nil and
            DatabaseIOTest.Services ~= nil and
            DatabaseIOTest.Services.ModFileLog ~= nil then
            local logger = DatabaseIOTest.Services.ModFileLog
            wrote = TryLogger(logger)
        end
    end)

    pcall(function()
        if wrote then
            return
        end
        if CS ~= nil and
            CS.DatabaseIOTest ~= nil and
            CS.DatabaseIOTest.Services ~= nil and
            CS.DatabaseIOTest.Services.ModFileLog ~= nil then
            local logger = CS.DatabaseIOTest.Services.ModFileLog
            wrote = TryLogger(logger)
        end
    end)

    pcall(function()
        if wrote then
            return
        end
        if DatabaseIOTestLua ~= nil and DatabaseIOTestLua.AppendLuaFileLogLine ~= nil then
            local payload = text
            if tostring(level or "info") == "debug" then
                payload = "[DBG->INFO] " .. payload
            end
            wrote = DatabaseIOTestLua.AppendLuaFileLogLine(payload) == true
        end
    end)

    if not wrote and not LoggerBridgeMissingPrinted then
        LoggerBridgeMissingPrinted = true
        print("[DBIOTEST][B1][Server][LOGFAIL] Lua file logger unavailable; fallback to print only.")
    end
    return wrote
end

local function Log(line)
    local text = "[DBIOTEST][B1][Server] " .. tostring(line or "")
    pcall(function()
        if DatabaseIOTestLua ~= nil and DatabaseIOTestLua.StampLogLine ~= nil then
            text = DatabaseIOTestLua.StampLogLine(text)
        end
    end)
    TryWriteFileLog("info", text)
    print(text)
end

local function LogDebug(line)
    if not ForceLuaDebugLog then
        return
    end
    local text = "[DBIOTEST][B1][Server][DBG] " .. tostring(line or "")
    pcall(function()
        if DatabaseIOTestLua ~= nil and DatabaseIOTestLua.StampLogLine ~= nil then
            text = DatabaseIOTestLua.StampLogLine(text)
        end
    end)
    TryWriteFileLog("debug", text)
end

Log("server_terminal_take loaded; CLIENT=" .. tostring(CLIENT) .. " SERVER=" .. tostring(SERVER))
Log("server debug enabled=" .. tostring(ForceLuaDebugLog))
LogDebug(string.format(
    "NetChannels takeReq=%s takeResult=%s sub=%s unsub=%s snapshot=%s delta=%s",
    tostring(DBServer.NetTakeRequest),
    tostring(DBServer.NetTakeResult),
    tostring(DBServer.NetViewSubscribe),
    tostring(DBServer.NetViewUnsubscribe),
    tostring(DBServer.NetViewSnapshot),
    tostring(DBServer.NetViewDelta)))

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

local function LogDebugThrottled(key, cooldown, line)
    if not ForceLuaDebugLog then
        return
    end
    local now = Now()
    local stateKey = "__nextDbgLogAt_" .. tostring(key or "default")
    if now < (DBServer[stateKey] or 0) then
        return
    end
    DBServer[stateKey] = now + (tonumber(cooldown) or 0.6)
    LogDebug(line)
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

local TerminalComponentLookupNames = {
    "DatabaseTerminalComponent",
    "databaseTerminalComponent",
    "DatabaseIOTest.DatabaseTerminalComponent"
}

local function GetTerminalId(item)
    if item == nil or item.Removed then
        return "none"
    end
    local id = "none"
    pcall(function()
        id = tostring(item.ID)
    end)
    return id or "none"
end

local function ResolveTerminalComponent(item)
    if item == nil or item.Removed then
        return nil
    end

    local now = Now()
    local cacheKey = tostring(GetTerminalId(item))
    local cache = DBServer.ComponentCache[cacheKey]
    if cache ~= nil and cache.item == item then
        if cache.component ~= nil and cache.component ~= false then
            return cache.component
        end
        if cache.component == false and now < (cache.retryAt or 0) then
            return nil
        end
    end

    local component = nil
    for _, name in ipairs(TerminalComponentLookupNames) do
        pcall(function()
            if component == nil then
                component = item.GetComponentString(name)
            end
        end)
        if component ~= nil then
            break
        end
    end

    if component == nil then
        pcall(function()
            local components = item.Components
            if components ~= nil then
                for comp in components do
                    if comp ~= nil then
                        local typeName = string.lower(tostring(comp) or "")
                        if string.find(typeName, "databaseterminalcomponent", 1, true) ~= nil then
                            component = comp
                            break
                        end
                    end
                end
            end
        end)
    end

    if component ~= nil then
        DBServer.ComponentCache[cacheKey] = {
            item = item,
            component = component,
            retryAt = 0
        }
        return component
    end

    DBServer.ComponentCache[cacheKey] = {
        item = item,
        component = false,
        retryAt = now + 1.5
    }
    return nil
end

local UnpackArgs = table.unpack or unpack
local function CallComponentMethod(component, methodName, ...)
    if component == nil then
        return false, nil, "component_nil", "component=nil"
    end

    local method = nil
    local okGet, getErr = pcall(function()
        method = component[methodName]
    end)
    if not okGet or method == nil then
        return false, nil, "method_missing", tostring(getErr or "missing")
    end

    local args = table.pack(...)
    local okSelf, resultSelf = pcall(function()
        return method(component, UnpackArgs(args, 1, args.n))
    end)
    if okSelf then
        return true, resultSelf, "self", ""
    end

    local selfErr = tostring(resultSelf or "")
    local okBound, resultBound = pcall(function()
        return method(UnpackArgs(args, 1, args.n))
    end)
    if okBound then
        return true, resultBound, "bound", ""
    end

    return false, nil, "invoke_failed", selfErr .. " || " .. tostring(resultBound or "")
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
        local component = ResolveTerminalComponent(item)
        if component ~= nil then
            local okOpen, openValue, openMode, openErr = CallComponentMethod(component, "IsVirtualSessionOpenForUi")
            local open = okOpen and openValue == true
            if not okOpen then
                LogDebugThrottled(
                    "server-fixed-open-callfail-" .. tostring(GetTerminalId(item)),
                    2.0,
                    string.format(
                        "IsSessionTerminal fixed-open call failed terminal=%s mode=%s err=%s",
                        tostring(GetTerminalId(item)),
                        tostring(openMode or "?"),
                        tostring(openErr or "")))
            end
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

    local component = ResolveTerminalComponent(terminal)
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

    return ResolveTerminalComponent(terminal)
end

local function ForEachVirtualRow(rows, callback)
    if rows == nil then
        return false, 0, "rows_nil"
    end

    local indexedCount = nil
    pcall(function() indexedCount = tonumber(rows.Count) end)
    if indexedCount == nil then
        pcall(function() indexedCount = tonumber(rows.Length) end)
    end
    if indexedCount ~= nil and indexedCount >= 0 then
        local iterated = 0
        local okIndexed = pcall(function()
            for i = 0, indexedCount - 1 do
                local row = nil
                pcall(function() row = rows[i] end)
                if row == nil then
                    pcall(function() row = rows.get_Item(i) end)
                end
                if row ~= nil then
                    iterated = iterated + 1
                    callback(row)
                end
            end
        end)
        if okIndexed then
            return true, iterated, "indexed"
        end
    end

    local iterated = 0
    local okIterator = pcall(function()
        for row in rows do
            if row ~= nil then
                iterated = iterated + 1
                callback(row)
            end
        end
    end)
    if okIterator then
        return true, iterated, "iterator"
    end

    iterated = 0
    local okEnumerator = pcall(function()
        local enumerator = rows.GetEnumerator()
        if enumerator ~= nil then
            while enumerator.MoveNext() do
                local row = enumerator.Current
                if row ~= nil then
                    iterated = iterated + 1
                    callback(row)
                end
            end
        end
    end)
    if okEnumerator then
        return true, iterated, "enumerator"
    end

    return false, 0, "iteration_failed"
end

local function BuildEntryMapFromComponent(terminal)
    local component = GetTerminalComponent(terminal)
    if component == nil then
        LogDebugThrottled(
            "server-buildmap-component-missing-" .. tostring(GetTerminalId(terminal)),
            2.0,
            string.format(
                "BuildEntryMapFromComponent component missing terminal=%s",
                tostring(GetTerminalId(terminal))))
        return nil
    end

    local okRows, rows, rowsMode, rowsErr = CallComponentMethod(component, "GetVirtualViewSnapshot", true)
    if not okRows or rows == nil then
        LogDebugThrottled(
            "server-buildmap-callfail-" .. tostring(GetTerminalId(terminal)),
            1.2,
            string.format(
                "BuildEntryMapFromComponent snapshot call failed terminal=%s mode=%s err=%s",
                tostring(GetTerminalId(terminal)),
                tostring(rowsMode or "?"),
                tostring(rowsErr or "")))
        return nil
    end

    local map = {}
    local totalEntries = 0
    local totalAmount = 0

    local iterOk, rowCount, iterMode = ForEachVirtualRow(rows, function(row)
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
    end)

    if not iterOk then
        local now = Now()
        if now >= (DBServer.NextSnapshotDiagAt or 0) then
            DBServer.NextSnapshotDiagAt = now + 0.9
            Log(string.format(
                "BuildEntryMapFromComponent FAILED terminal=%s mode=%s rowsMode=%s",
                tostring(terminal and terminal.ID or "none"),
                tostring(iterMode or "unknown"),
                tostring(rowsMode or "?")))
        end
        return nil
    end

    local now = Now()
    if now >= (DBServer.NextSnapshotDiagAt or 0) then
        DBServer.NextSnapshotDiagAt = now + 0.9
        Log(string.format(
            "BuildEntryMapFromComponent terminal=%s mode=%s rowCount=%d entries=%d amount=%d",
            tostring(terminal and terminal.ID or "none"),
            tostring(iterMode or "?"),
            tonumber(rowCount or 0),
            tonumber(totalEntries or 0),
            tonumber(totalAmount or 0)))
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
        LogDebug(string.format(
            "EnsureSubscription reused client='%s' terminal=%s db='%s'",
            tostring(client.Name or "?"),
            tostring(terminalId),
            tostring(current.databaseId or "default")))
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
        LogDebug(string.format(
            "RemoveSubscription details client='%s' serial=%s forceSnapshot=%s forcePush=%s",
            tostring(client.Name or "?"),
            tostring(sub.serial or 0),
            tostring(sub.forceSnapshot == true),
            tostring(sub.forcePush == true)))
    end
    DBServer.Subscriptions[client] = nil
end

local function FlagTerminalDirty(terminalEntityId)
    local target = tostring(terminalEntityId or "")
    if target == "" then
        return
    end

    local flagged = 0
    for _, sub in pairs(DBServer.Subscriptions) do
        if sub ~= nil and tostring(sub.terminalEntityId or "") == target then
            sub.forcePush = true
            flagged = flagged + 1
        end
    end
    LogDebug(string.format(
        "FlagTerminalDirty terminal=%s flaggedSubscriptions=%d",
        tostring(target),
        tonumber(flagged or 0)))
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
    LogDebug(string.format(
        "SendTakeResult client='%s' success=%s text='%s'",
        tostring(client.Name or "?"),
        tostring(success == true),
        tostring(text or "")))
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
    LogDebug("server_terminal_take exiting: runAsServerAuthority=false")
    return
end

if DBServer.__loadedServer == true then
    LogDebug("server_terminal_take already loaded; duplicate bootstrap ignored.")
    return
end
DBServer.__loadedServer = true
LogDebug("server_terminal_take server authority active; handlers will register.")

Networking.Receive(DBServer.NetViewSubscribe, function(message, client)
    if client == nil or client.Character == nil then
        LogDebug("NetViewSubscribe ignored: client or character is nil.")
        return
    end

    local terminalEntityId = tostring(message.ReadString() or "")
    LogDebug(string.format(
        "NetViewSubscribe recv client='%s' terminal=%s",
        tostring(client.Name or "?"),
        tostring(terminalEntityId)))
    local terminal = FindItemByEntityId(terminalEntityId)
    if terminal == nil or not IsSessionTerminal(terminal) then
        LogDebug("NetViewSubscribe rejected: terminal invalid or not session terminal.")
        RemoveSubscription(client, "invalid terminal")
        return
    end

    if not CharacterCanUseTerminal(client.Character, terminal) then
        LogDebug("NetViewSubscribe rejected: permission denied.")
        RemoveSubscription(client, "permission denied")
        return
    end

    local sub = EnsureSubscription(client, terminal)
    if sub ~= nil then
        sub.forceSnapshot = true
        sub.forcePush = true
        LogDebug("NetViewSubscribe accepted: forceSnapshot/forcePush set.")
    end
end)
LogDebug("Networking.Receive registered: " .. tostring(DBServer.NetViewSubscribe))

Networking.Receive(DBServer.NetViewUnsubscribe, function(message, client)
    if client == nil then
        LogDebug("NetViewUnsubscribe ignored: client nil.")
        return
    end

    local terminalEntityId = tostring(message.ReadString() or "")
    LogDebug(string.format(
        "NetViewUnsubscribe recv client='%s' terminal=%s",
        tostring(client.Name or "?"),
        tostring(terminalEntityId)))
    local sub = DBServer.Subscriptions[client]
    if sub == nil then
        LogDebug("NetViewUnsubscribe ignored: no active subscription for client.")
        return
    end

    if terminalEntityId == "" or tostring(sub.terminalEntityId or "") == terminalEntityId then
        RemoveSubscription(client, "client request")
    else
        LogDebug(string.format(
            "NetViewUnsubscribe ignored: terminal mismatch req=%s sub=%s",
            tostring(terminalEntityId),
            tostring(sub.terminalEntityId or "")))
    end
end)
LogDebug("Networking.Receive registered: " .. tostring(DBServer.NetViewUnsubscribe))

Networking.Receive(DBServer.NetTakeRequest, function(message, client)
    if client == nil or client.Character == nil then
        LogDebug("NetTakeRequest ignored: client or character is nil.")
        return
    end

    local terminalEntityId = message.ReadString()
    local wantedIdentifier = message.ReadString()
    local character = client.Character
    LogDebug(string.format(
        "NetTakeRequest recv client='%s' terminal=%s wanted=%s",
        tostring(client.Name or "?"),
        tostring(terminalEntityId or ""),
        tostring(wantedIdentifier or "")))

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
        LogDebug("NetTakeRequest rejected: terminal missing or invalid.")
        SendTakeResult(client, false, L("dbiotest.ui.take.invalidterminal", "Terminal session not found."))
        return
    end

    if not CharacterCanUseTerminal(character, terminal) then
        LogDebug("NetTakeRequest rejected: permission denied.")
        SendTakeResult(client, false, L("dbiotest.ui.take.denied", "You are not allowed to use this terminal."))
        return
    end

    local component = GetTerminalComponent(terminal)
    if component ~= nil then
        local okCall, reasonValue, callMode, callErr = CallComponentMethod(
            component,
            "TryTakeOneByIdentifierFromVirtualSession",
            wantedIdentifier,
            character)
        local reason = tostring(reasonValue or "")
        if okCall then
            if reason ~= "" then
                LogDebug("NetTakeRequest failed by virtual API reason='" .. tostring(reason) .. "'")
                SendTakeResult(client, false, MapVirtualTakeError(reason))
                return
            end

            SendTakeResult(client, true, L("dbiotest.ui.take.success", "Item moved to terminal buffer."))
            FlagTerminalDirty(terminalEntityId)
            LogDebug("NetTakeRequest succeeded and flagged terminal dirty.")
            return
        end
        Log("Virtual take API failed: " .. tostring(callErr or reason or ""))
        LogDebug(string.format(
            "NetTakeRequest virtual API invoke failed mode=%s err=%s",
            tostring(callMode or "?"),
            tostring(callErr or "")))
    end

    SendTakeResult(client, false, L("dbiotest.ui.take.notready", "Terminal API is not ready."))
end)
LogDebug("Networking.Receive registered: " .. tostring(DBServer.NetTakeRequest))

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
    local estimatedSubCount = 0
    for _ in pairs(DBServer.Subscriptions) do
        estimatedSubCount = estimatedSubCount + 1
    end
    if estimatedSubCount > 0 then
        LogDebugThrottled("server-sync-loop-active", 1.8, string.format(
            "SyncThink loop begin subCount=%d",
            estimatedSubCount))
    else
        LogDebugThrottled("server-sync-loop-idle", 8.0, "SyncThink loop begin subCount=0")
    end

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
            LogDebug(string.format(
                "SyncThink mark remove client='%s' reason='%s'",
                tostring(client ~= nil and client.Name or "?"),
                tostring(removeReason or "")))
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
            else
                LogDebugThrottled(
                    "server-sync-unchanged-" .. tostring(sub.terminalEntityId or ""),
                    0.7,
                    string.format(
                        "SyncThink unchanged client='%s' terminal=%s serial=%s",
                        tostring(client ~= nil and client.Name or "?"),
                        tostring(sub.terminalEntityId or ""),
                        tostring(sub.serial or 0)))
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

    local summaryState = subCount > 0 and "active" or "idle"
    local summaryCooldown = subCount > 0 and 1.2 or 8.0
    LogDebugThrottled(
        "server-sync-summary-" .. summaryState,
        summaryCooldown,
        string.format(
            "SyncThink summary %.2fms subs=%d processed=%d removed=%d snapshots=%d deltas=%d fallback=%d",
            elapsedMs,
            subCount,
            processedCount,
            #toRemove,
            snapshotCount,
            deltaCount,
            fallbackLookupCount))
end)
LogDebug("Hook.Add registered: DBIOTEST_ServerViewSyncThink")

Hook.Add("roundEnd", "DBIOTEST_ServerViewRoundEnd", function()
    DBServer.Subscriptions = {}
    DBServer.NextPerfLogAt = 0
    DBServer.ComponentCache = {}
    LogDebug("Hook roundEnd: cleared subscriptions.")
end)
LogDebug("Hook.Add registered: DBIOTEST_ServerViewRoundEnd")

Hook.Add("stop", "DBIOTEST_ServerViewStop", function()
    DBServer.Subscriptions = {}
    DBServer.NextPerfLogAt = 0
    DBServer.ComponentCache = {}
    LogDebug("Hook stop: cleared subscriptions.")
end)
LogDebug("Hook.Add registered: DBIOTEST_ServerViewStop")
