DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Client = DatabaseIOTestLua.Client or {}

local DBClient = DatabaseIOTestLua.Client
if DBClient.__loaded == true then
    return
end
DBClient.__loaded = true

DBClient.NetTakeRequest = "DBIOTEST_RequestTakeByIdentifier"
DBClient.NetTakeResult = "DBIOTEST_TakeResult"
DBClient.NetViewSubscribe = "DBIOTEST_ViewSubscribe"
DBClient.NetViewUnsubscribe = "DBIOTEST_ViewUnsubscribe"
DBClient.NetViewSnapshot = "DBIOTEST_ViewSnapshot"
DBClient.NetViewDelta = "DBIOTEST_ViewDelta"

DBClient.State = DBClient.State or {
    activeTerminal = nil,
    subscribedTerminalId = "",
    databaseId = "default",
    serial = 0,
    entriesByKey = {},
    totalEntries = 0,
    totalAmount = 0,
    searchText = "",
    sortMode = "name_asc",
    lastRefresh = 0,
    lastLocalSync = 0,
    lastSubscribeAt = 0,
    awaitingSnapshot = false,
    dirty = true
}
DBClient.State.componentCache = DBClient.State.componentCache or {}
DBClient.State.componentMissDiag = DBClient.State.componentMissDiag or {}

DBClient.IconCache = DBClient.IconCache or {}
local EnableNearbyTerminalScan = false
local NearbyTerminalScanInterval = 1.0
local NearbyTerminalScanDistance = 220
local NearbyOpenFixedScanInterval = 0.65
local TerminalLostGraceSeconds = 0.18
local ActiveTerminalKeepAliveSeconds = 0.85
local PerfLogCooldown = 0.8
local PerfThinkWarnMs = 10.0
local PerfBuildWarnMs = 6.0
local PerfRedrawWarnMs = 6.0
local ForceLuaDebugLog = false
pcall(function()
    if DatabaseIOTestLua ~= nil and DatabaseIOTestLua.IsLuaDebugEnabled ~= nil then
        ForceLuaDebugLog = DatabaseIOTestLua.IsLuaDebugEnabled() == true
    end
end)
local LoggerBridgeMissingPrinted = false

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
                logger.WriteLuaClientDebug(text)
            end)
            if okPreferred then
                return true
            end

            local okFallbackDebug = pcall(function()
                logger.WriteDebug("LuaClient", text)
            end)
            if okFallbackDebug then
                return true
            end

            if ForceLuaDebugLog then
                local okDebugToInfo = pcall(function()
                    logger.WriteLuaClient("[DBG->INFO] " .. text)
                end)
                if okDebugToInfo then
                    return true
                end

                local okFallbackInfo = pcall(function()
                    logger.Write("LuaClient", "[DBG->INFO] " .. text)
                end)
                if okFallbackInfo then
                    return true
                end
            end

            return false
        end

        local okPreferred = pcall(function()
            logger.WriteLuaClient(text)
        end)
        if okPreferred then
            return true
        end

        local okFallback = pcall(function()
            logger.Write("LuaClient", text)
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
        print("[DBIOTEST][B1][Client][LOGFAIL] Lua file logger unavailable; fallback to print only.")
    end
    return wrote
end

local function Log(line)
    local text = "[DBIOTEST][B1][Client] " .. tostring(line or "")
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
    local text = "[DBIOTEST][B1][Client][DBG] " .. tostring(line or "")
    pcall(function()
        if DatabaseIOTestLua ~= nil and DatabaseIOTestLua.StampLogLine ~= nil then
            text = DatabaseIOTestLua.StampLogLine(text)
        end
    end)
    TryWriteFileLog("debug", text)
end

Log("client_terminal_ui loaded; CLIENT=" .. tostring(CLIENT) .. " SERVER=" .. tostring(SERVER))
Log("client debug enabled=" .. tostring(ForceLuaDebugLog))
LogDebug(string.format(
    "NetChannels takeReq=%s takeResult=%s sub=%s unsub=%s snapshot=%s delta=%s",
    tostring(DBClient.NetTakeRequest),
    tostring(DBClient.NetTakeResult),
    tostring(DBClient.NetViewSubscribe),
    tostring(DBClient.NetViewUnsubscribe),
    tostring(DBClient.NetViewSnapshot),
    tostring(DBClient.NetViewDelta)))

if not CLIENT then
    return
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

local function Now()
    local t = 0
    pcall(function() t = Timer.GetTime() end)
    if t == 0 then
        pcall(function() t = Timer.Time end)
    end
    return t or 0
end

local function LogThrottled(key, cooldown, line)
    local now = Now()
    local stateKey = "__nextLogAt_" .. tostring(key or "default")
    if now < (DBClient.State[stateKey] or 0) then
        return
    end
    DBClient.State[stateKey] = now + (tonumber(cooldown) or 0.9)
    Log(line)
end

local function LogDebugThrottled(key, cooldown, line)
    if not ForceLuaDebugLog then
        return
    end
    local now = Now()
    local stateKey = "__nextDbgLogAt_" .. tostring(key or "default")
    if now < (DBClient.State[stateKey] or 0) then
        return
    end
    DBClient.State[stateKey] = now + (tonumber(cooldown) or 0.6)
    LogDebug(line)
end

local function GetActiveTerminalId()
    local terminal = DBClient.State.activeTerminal
    if terminal == nil or terminal.Removed then
        return "none"
    end

    local id = "none"
    pcall(function()
        id = tostring(terminal.ID)
    end)
    return id or "none"
end

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

local function ToTerminalEntityId(item)
    local value = tonumber(GetTerminalId(item))
    return math.floor(value or 0)
end

local function ShortText(value, maxLen)
    local text = tostring(value or "")
    text = text:gsub("[\r\n]+", " ")
    local cap = tonumber(maxLen) or 160
    if string.len(text) > cap then
        text = string.sub(text, 1, cap) .. "..."
    end
    return text
end

local function ProbeValue(label, getter)
    local ok, value = pcall(getter)
    if not ok then
        return string.format("%s ok=false err=%s", tostring(label), ShortText(value, 220))
    end
    return string.format(
        "%s ok=true type=%s value=%s",
        tostring(label),
        tostring(type(value)),
        ShortText(value, 140))
end

local function LogBridgeProbe(tag)
    local key = "__bridgeProbePrinted_" .. tostring(tag or "default")
    if DBClient.State[key] == true then
        return
    end
    DBClient.State[key] = true

    local lines = {
        ProbeValue("DatabaseIOTest", function() return DatabaseIOTest end),
        ProbeValue("DatabaseIOTest.Services", function() return DatabaseIOTest.Services end),
        ProbeValue("DatabaseIOTest.Services.DatabaseLuaBridge", function() return DatabaseIOTest.Services.DatabaseLuaBridge end),
        ProbeValue("CS.DatabaseIOTest", function() return CS.DatabaseIOTest end),
        ProbeValue("CS.DatabaseIOTest.Services", function() return CS.DatabaseIOTest.Services end),
        ProbeValue("CS.DatabaseIOTest.Services.DatabaseLuaBridge", function() return CS.DatabaseIOTest.Services.DatabaseLuaBridge end),
        ProbeValue("DatabaseIOTestLua", function() return DatabaseIOTestLua end),
        ProbeValue("DatabaseIOTestLua.Server", function() return DatabaseIOTestLua.Server end),
        ProbeValue("DatabaseIOTestLua.Server.LocalB1Subscribe", function() return DatabaseIOTestLua.Server.LocalB1Subscribe end),
        ProbeValue("DatabaseIOTestLua.Server.LocalB1Poll", function() return DatabaseIOTestLua.Server.LocalB1Poll end),
        ProbeValue("DatabaseIOTestLua.Server.LocalB1IsTerminalSessionOpen", function() return DatabaseIOTestLua.Server.LocalB1IsTerminalSessionOpen end),
        ProbeValue("DatabaseIOTestLua.Server.LocalB1GetTerminalDatabaseId", function() return DatabaseIOTestLua.Server.LocalB1GetTerminalDatabaseId end),
        ProbeValue("DatabaseIOTestLua.Server.LocalB1RequestTake", function() return DatabaseIOTestLua.Server.LocalB1RequestTake end)
    }

    Log(string.format("BridgeProbe[%s] begin", tostring(tag or "")))
    for _, line in ipairs(lines) do
        Log(string.format("BridgeProbe[%s] %s", tostring(tag or ""), tostring(line)))
    end
    Log(string.format("BridgeProbe[%s] end", tostring(tag or "")))
end

local function LogBridgeMethodProbe(tag, bridge, methods)
    if bridge == nil then
        Log(string.format("BridgeMethodProbe[%s] bridge=nil", tostring(tag or "")))
        return
    end
    for _, methodName in ipairs(methods or {}) do
        local ok, member = pcall(function()
            return bridge[methodName]
        end)
        Log(string.format(
            "BridgeMethodProbe[%s] method=%s ok=%s type=%s value=%s",
            tostring(tag or ""),
            tostring(methodName or ""),
            tostring(ok == true),
            tostring(ok and type(member) or "error"),
            ShortText(ok and member or member, 160)))
    end
end

local function GetLuaBridge()
    if DBClient.LuaBridge == false then
        return nil
    end
    if DBClient.LuaBridge ~= nil then
        return DBClient.LuaBridge
    end

    local bridge = nil
    pcall(function()
        if DatabaseIOTest ~= nil and
            DatabaseIOTest.Services ~= nil and
            DatabaseIOTest.Services.DatabaseLuaBridge ~= nil then
            bridge = DatabaseIOTest.Services.DatabaseLuaBridge
        end
    end)
    if bridge == nil then
        pcall(function()
            if CS ~= nil and
                CS.DatabaseIOTest ~= nil and
                CS.DatabaseIOTest.Services ~= nil and
                CS.DatabaseIOTest.Services.DatabaseLuaBridge ~= nil then
                bridge = CS.DatabaseIOTest.Services.DatabaseLuaBridge
            end
        end)
    end

    if bridge ~= nil then
        DBClient.LuaBridge = bridge
        Log("Lua bridge resolved: DatabaseLuaBridge")
        LogBridgeMethodProbe("legacy-client", bridge, {
            "IsTerminalSessionOpen",
            "GetTerminalDatabaseId",
            "GetTerminalVirtualSnapshot",
            "TryTakeOneByIdentifierFromTerminalSession"
        })
        return bridge
    end

    DBClient.LuaBridge = false
    Log("Lua bridge unavailable: DatabaseLuaBridge not found.")
    LogBridgeProbe("legacy-client-missing")
    return nil
end

local function BridgeIsTerminalSessionOpen(terminalEntityId)
    local bridge = GetLuaBridge()
    if bridge == nil then
        return false, false, "bridge_missing", "bridge=nil"
    end

    local errors = {}
    local ok, result = pcall(function()
        return bridge.IsTerminalSessionOpen(terminalEntityId)
    end)
    if ok then
        return true, result == true, "dot", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge.IsTerminalSessionOpen(bridge, terminalEntityId)
    end)
    if ok then
        return true, result == true, "dot-self", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge:IsTerminalSessionOpen(terminalEntityId)
    end)
    if ok then
        return true, result == true, "colon", ""
    end
    table.insert(errors, tostring(result or ""))

    return false, false, "invoke_failed", table.concat(errors, " || ")
end

local function BridgeGetTerminalDatabaseId(terminalEntityId)
    local bridge = GetLuaBridge()
    if bridge == nil then
        return false, "default", "bridge_missing", "bridge=nil"
    end

    local errors = {}
    local ok, result = pcall(function()
        return bridge.GetTerminalDatabaseId(terminalEntityId)
    end)
    if ok then
        return true, tostring(result or "default"), "dot", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge.GetTerminalDatabaseId(bridge, terminalEntityId)
    end)
    if ok then
        return true, tostring(result or "default"), "dot-self", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge:GetTerminalDatabaseId(terminalEntityId)
    end)
    if ok then
        return true, tostring(result or "default"), "colon", ""
    end
    table.insert(errors, tostring(result or ""))

    return false, "default", "invoke_failed", table.concat(errors, " || ")
end

local function BridgeGetTerminalVirtualSnapshot(terminalEntityId, refreshCurrentPage)
    local bridge = GetLuaBridge()
    if bridge == nil then
        return false, nil, "bridge_missing", "bridge=nil"
    end

    local refresh = refreshCurrentPage == true
    local errors = {}
    local ok, result = pcall(function()
        return bridge.GetTerminalVirtualSnapshot(terminalEntityId, refresh)
    end)
    if ok then
        return true, result, "dot", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge.GetTerminalVirtualSnapshot(bridge, terminalEntityId, refresh)
    end)
    if ok then
        return true, result, "dot-self", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge:GetTerminalVirtualSnapshot(terminalEntityId, refresh)
    end)
    if ok then
        return true, result, "colon", ""
    end
    table.insert(errors, tostring(result or ""))

    return false, nil, "invoke_failed", table.concat(errors, " || ")
end

local function BridgeTryTakeOneByIdentifier(terminalEntityId, identifier, actor)
    local bridge = GetLuaBridge()
    if bridge == nil then
        return false, "bridge_missing", "bridge_missing", "bridge=nil"
    end

    local wanted = tostring(identifier or "")
    local errors = {}
    local ok, result = pcall(function()
        return bridge.TryTakeOneByIdentifierFromTerminalSession(terminalEntityId, wanted, actor)
    end)
    if ok then
        return true, tostring(result or ""), "dot", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge.TryTakeOneByIdentifierFromTerminalSession(bridge, terminalEntityId, wanted, actor)
    end)
    if ok then
        return true, tostring(result or ""), "dot-self", ""
    end
    table.insert(errors, tostring(result or ""))

    ok, result = pcall(function()
        return bridge:TryTakeOneByIdentifierFromTerminalSession(terminalEntityId, wanted, actor)
    end)
    if ok then
        return true, tostring(result or ""), "colon", ""
    end
    table.insert(errors, tostring(result or ""))

    return false, "invoke_failed", "invoke_failed", table.concat(errors, " || ")
end

local function GetLocalServerBridge()
    if DBClient.LocalServerBridge == false then
        return nil
    end
    if DBClient.LocalServerBridge ~= nil then
        return DBClient.LocalServerBridge
    end

    local bridge = nil
    pcall(function()
        if DatabaseIOTestLua ~= nil and
            DatabaseIOTestLua.Server ~= nil and
            DatabaseIOTestLua.Server.LocalB1Subscribe ~= nil and
            DatabaseIOTestLua.Server.LocalB1Poll ~= nil then
            bridge = DatabaseIOTestLua.Server
        end
    end)

    if bridge ~= nil then
        DBClient.LocalServerBridge = bridge
        Log("Local server bridge resolved: DatabaseIOTestLua.Server")
        LogBridgeMethodProbe("local-server", bridge, {
            "LocalB1Subscribe",
            "LocalB1Unsubscribe",
            "LocalB1Poll",
            "LocalB1IsTerminalSessionOpen",
            "LocalB1GetTerminalDatabaseId",
            "LocalB1RequestTake"
        })
        return bridge
    end

    DBClient.LocalServerBridge = false
    Log("Local server bridge unavailable: LocalB1 API not found.")
    LogBridgeProbe("local-server-missing")
    return nil
end

local function LocalServerCall(methodName, ...)
    local bridge = GetLocalServerBridge()
    if bridge == nil then
        return false, nil, "bridge_missing", "bridge=nil"
    end

    local method = bridge[methodName]
    if type(method) ~= "function" then
        return false, nil, "method_missing", tostring(methodName or "")
    end

    local args = table.pack(...)
    local ok, a, b, c = pcall(function()
        return method(table.unpack(args, 1, args.n))
    end)
    if ok then
        return true, a, b, c
    end
    return false, nil, "invoke_failed", tostring(a or "")
end

local function LocalB1Subscribe(terminalEntityId, character)
    local ok, accepted, reason, extra = LocalServerCall(
        "LocalB1Subscribe",
        tostring(terminalEntityId or ""),
        character)
    if not ok then
        return false, false, tostring(reason or ""), tostring(extra or "")
    end
    return true, accepted == true, tostring(reason or ""), tostring(extra or "")
end

local function LocalB1Unsubscribe(terminalEntityId)
    local ok, accepted, reason, extra = LocalServerCall(
        "LocalB1Unsubscribe",
        tostring(terminalEntityId or ""))
    if not ok then
        return false, false, tostring(reason or ""), tostring(extra or "")
    end
    return true, accepted == true, tostring(reason or ""), tostring(extra or "")
end

local function LocalB1Poll()
    local ok, packets, reason, extra = LocalServerCall("LocalB1Poll")
    if not ok then
        return false, nil, tostring(reason or ""), tostring(extra or "")
    end
    return true, packets or {}, tostring(reason or ""), tostring(extra or "")
end

local function LocalB1IsTerminalSessionOpen(terminalEntityId, character)
    local ok, open, reason, extra = LocalServerCall(
        "LocalB1IsTerminalSessionOpen",
        tostring(terminalEntityId or ""),
        character)
    if not ok then
        return false, false, tostring(reason or ""), tostring(extra or "")
    end
    return true, open == true, tostring(reason or ""), tostring(extra or "")
end

local function LocalB1GetTerminalDatabaseId(terminalEntityId)
    local ok, dbId, reason, extra = LocalServerCall(
        "LocalB1GetTerminalDatabaseId",
        tostring(terminalEntityId or ""))
    if not ok then
        return false, "default", tostring(reason or ""), tostring(extra or "")
    end
    return true, tostring(dbId or "default"), tostring(reason or ""), tostring(extra or "")
end

local function LocalB1RequestTake(terminalEntityId, identifier, character)
    local ok, success, text, extra = LocalServerCall(
        "LocalB1RequestTake",
        tostring(terminalEntityId or ""),
        tostring(identifier or ""),
        character)
    if not ok then
        return false, false, tostring(text or ""), tostring(extra or "")
    end
    return true, success == true, tostring(text or ""), tostring(extra or "")
end

local TerminalComponentLookupNames = {
    "DatabaseTerminalComponent",
    "databaseTerminalComponent",
    "DatabaseIOTest.DatabaseTerminalComponent"
}

local function ResolveTerminalComponent(item)
    if item == nil or item.Removed then
        return nil
    end

    local now = Now()
    local terminalId = GetTerminalId(item)
    local cacheKey = tostring(terminalId)
    local cache = DBClient.State.componentCache[cacheKey]
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
        DBClient.State.componentCache[cacheKey] = {
            item = item,
            component = component,
            retryAt = 0
        }
        return component
    end

    DBClient.State.componentCache[cacheKey] = {
        item = item,
        component = false,
        retryAt = now + 1.5
    }
    return nil
end

local function CallComponentMethod(component, methodName, ...)
    if component == nil then
        return false, nil, "component_nil", "component=nil"
    end

    local args = table.pack(...)
    local errors = {}
    local function TryCall(mode, fn)
        local ok, result = pcall(fn)
        if ok then
            return true, result, mode, ""
        end
        table.insert(errors, tostring(result or ""))
        return false, nil, nil, nil
    end

    if methodName == "IsVirtualSessionOpenForUi" then
        do
            local ok, result, mode, err = TryCall("dot", function()
                return component.IsVirtualSessionOpenForUi()
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("colon", function()
                return component:IsVirtualSessionOpenForUi()
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("dot-self", function()
                return component.IsVirtualSessionOpenForUi(component)
            end)
            if ok then return ok, result, mode, err end
        end
    elseif methodName == "GetVirtualViewSnapshot" then
        local refresh = args[1]
        do
            local ok, result, mode, err = TryCall("dot", function()
                return component.GetVirtualViewSnapshot(refresh)
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("colon", function()
                return component:GetVirtualViewSnapshot(refresh)
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("dot-self", function()
                return component.GetVirtualViewSnapshot(component, refresh)
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("dot-noarg", function()
                return component.GetVirtualViewSnapshot()
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("colon-noarg", function()
                return component:GetVirtualViewSnapshot()
            end)
            if ok then return ok, result, mode, err end
        end
    elseif methodName == "TryTakeOneByIdentifierFromVirtualSession" then
        local identifier = args[1]
        local actor = args[2]
        do
            local ok, result, mode, err = TryCall("dot", function()
                return component.TryTakeOneByIdentifierFromVirtualSession(identifier, actor)
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("colon", function()
                return component:TryTakeOneByIdentifierFromVirtualSession(identifier, actor)
            end)
            if ok then return ok, result, mode, err end
        end
        do
            local ok, result, mode, err = TryCall("dot-self", function()
                return component.TryTakeOneByIdentifierFromVirtualSession(component, identifier, actor)
            end)
            if ok then return ok, result, mode, err end
        end
    else
        return false, nil, "method_unsupported", tostring(methodName or "")
    end

    return false, nil, "invoke_failed", table.concat(errors, " || ")
end

local function LogPerf(tag, startedAt, extra)
    local now = Now()
    local elapsedMs = math.max(0, (now - (startedAt or now)) * 1000.0)
    if elapsedMs < PerfThinkWarnMs and tag == "think" then
        return
    end
    if elapsedMs < PerfBuildWarnMs and tag == "build" then
        return
    end
    if elapsedMs < PerfRedrawWarnMs and tag == "redraw" then
        return
    end

    if now < (DBClient.State.nextPerfLogAt or 0) then
        return
    end
    DBClient.State.nextPerfLogAt = now + PerfLogCooldown

    Log(string.format(
        "[PERF] %s %.2fms terminal=%s entries=%d amount=%d dirty=%s awaiting=%s %s",
        tostring(tag or "unknown"),
        elapsedMs,
        tostring(GetActiveTerminalId()),
        tonumber(DBClient.State.totalEntries or 0),
        tonumber(DBClient.State.totalAmount or 0),
        tostring(DBClient.State.dirty == true),
        tostring(DBClient.State.awaitingSnapshot == true),
        tostring(extra or "")))
end

local function ReadIntString(message)
    return tonumber(message.ReadString() or "0") or 0
end

local function ReadFloatString(message)
    return tonumber(message.ReadString() or "0") or 0
end

local function NormalizeIdentifier(identifier)
    if identifier == nil then
        return ""
    end
    return string.lower(tostring(identifier))
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

local function GetItemDisplayName(item)
    if item == nil then
        return "Unknown"
    end

    local name = ""
    pcall(function()
        name = tostring(item.Name)
    end)
    if name == nil or name == "" then
        pcall(function()
            name = item.Prefab.Name.Value
        end)
    end
    if name == nil or name == "" then
        name = GetItemIdentifier(item)
    end
    return name or "Unknown"
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

local function IsFixedTerminal(item)
    if item == nil or item.Removed then
        return false
    end

    local normalized = NormalizeIdentifier(GetItemIdentifier(item))
    local hasTag = false
    pcall(function()
        hasTag = item.HasTag("database_terminal_fixed")
    end)
    return normalized == "databaseterminalfixed" or hasTag == true
end

local function IsComponentSessionOpenForUi(item)
    if item == nil or item.Removed then
        return false
    end

    local terminalEntityId = ToTerminalEntityId(item)
    if not Game.IsMultiplayer then
        local character = Character.Controlled
        local localOk, localOpen, localMode, localErr = LocalB1IsTerminalSessionOpen(terminalEntityId, character)
        if localOk then
            LogDebugThrottled(
                "ui-local-open-" .. tostring(GetTerminalId(item)),
                1.5,
                string.format(
                    "IsComponentSessionOpenForUi localB1 terminal=%s open=%s mode=%s",
                    tostring(GetTerminalId(item)),
                    tostring(localOpen == true),
                    tostring(localMode or "")))
            return localOpen == true
        end
        LogDebugThrottled(
            "ui-local-open-fail-" .. tostring(GetTerminalId(item)),
            2.0,
            string.format(
                "IsComponentSessionOpenForUi localB1 failed terminal=%s mode=%s err=%s",
                tostring(GetTerminalId(item)),
                tostring(localMode or "?"),
                tostring(localErr or "")))
        return false
    end

    local bridgeOk, bridgeOpen, bridgeMode, bridgeErr = BridgeIsTerminalSessionOpen(terminalEntityId)
    if bridgeOk then
        LogDebugThrottled(
            "ui-bridge-open-" .. tostring(GetTerminalId(item)),
            2.0,
            string.format(
                "IsComponentSessionOpenForUi bridge terminal=%s open=%s mode=%s",
                tostring(GetTerminalId(item)),
                tostring(bridgeOpen == true),
                tostring(bridgeMode or "?")))
        return bridgeOpen == true
    end
    LogDebugThrottled(
        "ui-bridge-open-fail-" .. tostring(GetTerminalId(item)),
        2.5,
        string.format(
            "IsComponentSessionOpenForUi bridge failed terminal=%s mode=%s err=%s",
            tostring(GetTerminalId(item)),
            tostring(bridgeMode or "?"),
            tostring(bridgeErr or "")))

    local component = ResolveTerminalComponent(item)
    if component == nil then
        LogDebugThrottled(
            "ui-component-missing-" .. tostring(GetTerminalId(item)),
            5.0,
            string.format(
                "IsComponentSessionOpenForUi component missing terminal=%s identifier=%s",
                tostring(GetTerminalId(item)),
                tostring(GetItemIdentifier(item))))
        return false
    end

    local okCall, openValue, callMode, callErr = CallComponentMethod(component, "IsVirtualSessionOpenForUi")
    local open = okCall and openValue == true
    if not okCall then
        LogDebugThrottled(
            "ui-component-open-callfail-" .. tostring(GetTerminalId(item)),
            2.0,
            string.format(
                "IsComponentSessionOpenForUi call failed terminal=%s mode=%s err=%s",
                tostring(GetTerminalId(item)),
                tostring(callMode or "?"),
                tostring(callErr or "")))
    end
    LogDebugThrottled(
        "ui-component-open-" .. tostring(GetTerminalId(item)),
        2.5,
        string.format(
            "IsComponentSessionOpenForUi terminal=%s open=%s mode=%s",
            tostring(GetTerminalId(item)),
            tostring(open == true),
            tostring(callMode or "?")))
    return open == true
end

local function IsActiveUiTerminal(item)
    if IsSessionTerminal(item) then
        return true
    end
    if item == nil or item.Removed then
        return false
    end

    if not IsFixedTerminal(item) then
        return false
    end
    return IsComponentSessionOpenForUi(item)
end

local function GetTerminalInventory(terminal)
    if terminal == nil then
        return nil
    end

    local itemContainer = nil
    pcall(function()
        itemContainer = terminal.GetComponentString("ItemContainer")
    end)
    if itemContainer == nil then
        return nil
    end

    local inventory = nil
    pcall(function()
        inventory = itemContainer.Inventory
    end)
    return inventory
end

local function ForEachInventoryItem(inventory, callback)
    if inventory == nil then
        return
    end

    local ok = pcall(function()
        for contained in inventory.AllItems do
            if contained ~= nil and not contained.Removed then
                local stop = callback(contained)
                if stop == true then
                    break
                end
            end
        end
    end)
    if ok then
        return
    end

    pcall(function()
        for contained in inventory.AllItemsMod do
            if contained ~= nil and not contained.Removed then
                local stop = callback(contained)
                if stop == true then
                    break
                end
            end
        end
    end)
end

local function FindNearbySessionTerminal(character)
    if character == nil or character.Removed then
        return nil
    end

    local now = Now()
    local cached = DBClient.State.nearbyTerminal
    if cached ~= nil and not cached.Removed and IsSessionTerminal(cached) and (now - (DBClient.State.lastNearbyScanAt or 0)) < NearbyTerminalScanInterval then
        local stillNearby = false
        pcall(function()
            stillNearby = Vector2.Distance(character.WorldPosition, cached.WorldPosition) <= NearbyTerminalScanDistance
        end)
        if stillNearby then
            return cached
        end
    end

    DBClient.State.lastNearbyScanAt = now
    DBClient.State.nearbyTerminal = nil

    local best = nil
    local bestDistanceSq = NearbyTerminalScanDistance * NearbyTerminalScanDistance
    pcall(function()
        for item in Item.ItemList do
            if item ~= nil and not item.Removed and IsSessionTerminal(item) then
                local distanceSq = Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition)
                if distanceSq <= bestDistanceSq then
                    bestDistanceSq = distanceSq
                    best = item
                end
            end
        end
    end)

    DBClient.State.nearbyTerminal = best
    return best
end

local function CanCharacterReachTerminal(character, terminal)
    if character == nil or terminal == nil or terminal.Removed then
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
        closeEnough = Vector2.Distance(character.WorldPosition, terminal.WorldPosition) <= NearbyTerminalScanDistance
    end)
    return closeEnough == true
end

local function FindNearbyOpenFixedTerminal(character)
    if character == nil or character.Removed then
        return nil
    end

    local now = Now()
    local cached = DBClient.State.nearbyOpenFixedTerminal
    if cached ~= nil and
        not cached.Removed and
        IsFixedTerminal(cached) and
        IsComponentSessionOpenForUi(cached) and
        CanCharacterReachTerminal(character, cached) and
        (now - (DBClient.State.lastNearbyOpenFixedScanAt or 0)) < NearbyOpenFixedScanInterval then
        return cached
    end

    if (now - (DBClient.State.lastNearbyOpenFixedScanAt or 0)) < NearbyOpenFixedScanInterval then
        return nil
    end

    DBClient.State.lastNearbyOpenFixedScanAt = now
    DBClient.State.nearbyOpenFixedTerminal = nil

    local best = nil
    local bestDistanceSq = NearbyTerminalScanDistance * NearbyTerminalScanDistance
    local fixedCount = 0
    local fixedOpenCount = 0
    pcall(function()
        for item in Item.ItemList do
            if item ~= nil and not item.Removed and IsFixedTerminal(item) then
                fixedCount = fixedCount + 1
                if IsComponentSessionOpenForUi(item) then
                    fixedOpenCount = fixedOpenCount + 1
                    local distanceSq = Vector2.DistanceSquared(character.WorldPosition, item.WorldPosition)
                    if distanceSq <= bestDistanceSq then
                        bestDistanceSq = distanceSq
                        best = item
                    end
                end
            end
        end
    end)

    LogDebugThrottled(
        "nearby-open-fixed-scan",
        1.5,
        string.format(
            "NearbyOpenFixedScan fixed=%d open=%d best=%s",
            tonumber(fixedCount or 0),
            tonumber(fixedOpenCount or 0),
            tostring(GetTerminalId(best))))

    DBClient.State.nearbyOpenFixedTerminal = best
    return best
end

local function FindHeldSessionTerminal(character)
    if character == nil then
        return nil
    end

    local selected = nil
    local selectedSecondary = nil
    pcall(function() selected = character.SelectedItem end)
    pcall(function() selectedSecondary = character.SelectedSecondaryItem end)

    if IsActiveUiTerminal(selected) then
        return selected
    end
    if IsActiveUiTerminal(selectedSecondary) then
        return selectedSecondary
    end

    local inventory = nil
    pcall(function() inventory = character.Inventory end)
    if inventory == nil then
        return nil
    end

    local found = nil
    ForEachInventoryItem(inventory, function(item)
        if IsSessionTerminal(item) then
            found = item
            return true
        end
        return false
    end)
    if found ~= nil then
        return found
    end

    if EnableNearbyTerminalScan then
        return FindNearbySessionTerminal(character)
    end
    return nil
end

local function ResolveUiTerminal(character)
    local terminal = FindHeldSessionTerminal(character)
    if terminal ~= nil then
        return terminal, "held_or_inventory"
    end

    local active = DBClient.State.activeTerminal
    if active ~= nil and
        not active.Removed and
        IsActiveUiTerminal(active) and
        CanCharacterReachTerminal(character, active) then
        return active, "sticky_active"
    end

    local nearbyOpenFixed = FindNearbyOpenFixedTerminal(character)
    if nearbyOpenFixed ~= nil then
        return nearbyOpenFixed, "nearby_open_fixed"
    end

    return nil, "none"
end

local function GetTerminalDatabaseId(terminal)
    if terminal == nil then
        return "default"
    end

    local terminalEntityId = ToTerminalEntityId(terminal)
    if not Game.IsMultiplayer then
        local localOk, localDbId, localMode, localErr = LocalB1GetTerminalDatabaseId(terminalEntityId)
        if localOk and tostring(localDbId or "") ~= "" then
            LogDebugThrottled(
                "dbid-local-" .. tostring(GetTerminalId(terminal)),
                2.0,
                string.format(
                    "GetTerminalDatabaseId localB1 terminal=%s db=%s mode=%s",
                    tostring(GetTerminalId(terminal)),
                    tostring(localDbId or "default"),
                    tostring(localMode or "")))
            return tostring(localDbId or "default")
        end
        LogDebugThrottled(
            "dbid-local-fail-" .. tostring(GetTerminalId(terminal)),
            2.0,
            string.format(
                "GetTerminalDatabaseId localB1 failed terminal=%s mode=%s err=%s",
                tostring(GetTerminalId(terminal)),
                tostring(localMode or "?"),
                tostring(localErr or "")))
    end

    local bridgeOk, bridgeDbId, bridgeMode, bridgeErr = BridgeGetTerminalDatabaseId(terminalEntityId)
    if bridgeOk and tostring(bridgeDbId or "") ~= "" then
        LogDebugThrottled(
            "dbid-bridge-" .. tostring(GetTerminalId(terminal)),
            2.5,
            string.format(
                "GetTerminalDatabaseId bridge terminal=%s db=%s mode=%s",
                tostring(GetTerminalId(terminal)),
                tostring(bridgeDbId or "default"),
                tostring(bridgeMode or "?")))
        return tostring(bridgeDbId or "default")
    end
    LogDebugThrottled(
        "dbid-bridge-fail-" .. tostring(GetTerminalId(terminal)),
        2.5,
        string.format(
            "GetTerminalDatabaseId bridge failed terminal=%s mode=%s err=%s",
            tostring(GetTerminalId(terminal)),
            tostring(bridgeMode or "?"),
            tostring(bridgeErr or "")))

    local component = ResolveTerminalComponent(terminal)
    if component == nil then
        return "default"
    end

    local databaseId = "default"
    pcall(function()
        databaseId = tostring(component.DatabaseId or "default")
    end)
    return databaseId or "default"
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

local function BuildLocalEntryMap(terminal)
    local terminalId = GetTerminalId(terminal)
    local terminalEntityId = ToTerminalEntityId(terminal)

    local function ShortErr(err)
        local text = tostring(err or "")
        text = text:gsub("[\r\n]+", " ")
        if string.len(text) > 180 then
            text = string.sub(text, 1, 180) .. "..."
        end
        return text
    end

    local bridgeRowsOk, bridgeRows, bridgeRowsMode, bridgeRowsErr = BridgeGetTerminalVirtualSnapshot(terminalEntityId, true)
    if bridgeRowsOk and bridgeRows ~= nil then
        local map = {}
        local totalEntries = 0
        local totalAmount = 0
        local iterOk, rowCount, iterMode = ForEachVirtualRow(bridgeRows, function(row)
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
        if iterOk then
            LogDebugThrottled(
                "localsnapshot-bridge-" .. tostring(terminalId),
                1.2,
                string.format(
                    "LocalSnapshot bridge terminal=%s bridgeMode=%s iterMode=%s rowCount=%d entries=%d amount=%d",
                    terminalId,
                    tostring(bridgeRowsMode or "?"),
                    tostring(iterMode or "?"),
                    tonumber(rowCount or 0),
                    tonumber(totalEntries or 0),
                    tonumber(totalAmount or 0)))
            return map, totalEntries, totalAmount
        end

        LogThrottled(
            "localsnapshot-bridge-iterfail-" .. tostring(terminalId),
            1.2,
            string.format(
                "LocalSnapshot bridge iter failed terminal=%s bridgeMode=%s iterMode=%s -> fallback",
                terminalId,
                tostring(bridgeRowsMode or "?"),
                tostring(iterMode or "?")))
    else
        LogThrottled(
            "localsnapshot-bridge-callfail-" .. tostring(terminalId),
            1.2,
            string.format(
                "LocalSnapshot bridge call failed terminal=%s mode=%s err='%s'",
                terminalId,
                tostring(bridgeRowsMode or "?"),
                ShortErr(bridgeRowsErr)))
    end

    local component = GetTerminalComponent(terminal)
    if component ~= nil then
        local okRows, rows, rowsMode, rowsErr = CallComponentMethod(component, "GetVirtualViewSnapshot", true)
        if okRows and rows ~= nil then
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
            if iterOk then
                LogDebugThrottled(
                    "localsnapshot-debug-" .. tostring(terminalId),
                    1.5,
                    string.format(
                        "LocalSnapshot terminal=%s rowsMode=%s iterMode=%s rowCount=%d entries=%d amount=%d",
                        terminalId,
                        tostring(rowsMode or "?"),
                        tostring(iterMode or "?"),
                        tonumber(rowCount or 0),
                        tonumber(totalEntries or 0),
                        tonumber(totalAmount or 0)))
                return map, totalEntries, totalAmount
            end

            local okOpen, openValue, openMode, openErr = CallComponentMethod(component, "IsVirtualSessionOpenForUi")
            local open = okOpen and openValue == true
            LogThrottled(
                "localsnapshot-failed-" .. tostring(terminalId),
                1.2,
                string.format(
                    "LocalSnapshot FAILED terminal=%s open=%s rowsMode=%s iterMode=%s openMode=%s err='%s' openErr='%s' -> fallback inventory",
                    terminalId,
                    tostring(open == true),
                    tostring(rowsMode or "unknown"),
                    tostring(iterMode or "unknown"),
                    tostring(openMode or "?"),
                    ShortErr(rowsErr),
                    ShortErr(openErr)))
        else
            local okOpen, openValue, openMode, openErr = CallComponentMethod(component, "IsVirtualSessionOpenForUi")
            LogThrottled(
                "localsnapshot-callfail-" .. tostring(terminalId),
                1.2,
                string.format(
                    "LocalSnapshot call failed terminal=%s rowsMode=%s err='%s' open=%s openMode=%s openErr='%s' -> fallback inventory",
                    terminalId,
                    tostring(rowsMode or "?"),
                    ShortErr(rowsErr),
                    tostring(okOpen and openValue == true),
                    tostring(openMode or "?"),
                    ShortErr(openErr)))
        end
    else
        LogDebugThrottled(
            "localsnapshot-component-missing-" .. tostring(terminalId),
            2.0,
            string.format(
                "LocalSnapshot component missing terminal=%s identifier=%s; fallback inventory",
                tostring(terminalId),
                tostring(GetItemIdentifier(terminal))))
    end

    local map = {}
    local totalAmount = 0
    local inventory = GetTerminalInventory(terminal)
    ForEachInventoryItem(inventory, function(item)
        local identifier = GetItemIdentifier(item)
        local key = NormalizeIdentifier(identifier)
        if key ~= "" then
            local entry = map[key]
            if entry == nil then
                entry = {
                    key = key,
                    identifier = identifier,
                    prefabIdentifier = identifier,
                    displayName = GetItemDisplayName(item),
                    amount = 0,
                    bestQuality = 0,
                    avgCondition = 100
                }
                map[key] = entry
            end
            entry.amount = (tonumber(entry.amount) or 0) + 1
            totalAmount = totalAmount + 1
        end
        return false
    end)

    local totalEntries = 0
    for _, _ in pairs(map) do
        totalEntries = totalEntries + 1
    end
    LogDebugThrottled(
        "localsnapshot-fallback-" .. tostring(terminalId),
        2.0,
        string.format(
            "LocalSnapshot fallback inventory terminal=%s entries=%d amount=%d",
            tostring(terminalId),
            tonumber(totalEntries or 0),
            tonumber(totalAmount or 0)))
    return map, totalEntries, totalAmount
end

local function EntryMapEquivalent(currentMap, currentEntries, currentAmount, nextMap, nextEntries, nextAmount)
    if tonumber(currentEntries or 0) ~= tonumber(nextEntries or 0) then
        return false
    end
    if tonumber(currentAmount or 0) ~= tonumber(nextAmount or 0) then
        return false
    end

    currentMap = currentMap or {}
    nextMap = nextMap or {}

    for key, left in pairs(currentMap) do
        local right = nextMap[key]
        if right == nil then
            return false
        end

        if tostring(left.identifier or "") ~= tostring(right.identifier or "") then
            return false
        end
        if tonumber(left.amount or 0) ~= tonumber(right.amount or 0) then
            return false
        end
        if tonumber(left.bestQuality or 0) ~= tonumber(right.bestQuality or 0) then
            return false
        end
        if math.abs((tonumber(left.avgCondition) or 0) - (tonumber(right.avgCondition) or 0)) > 0.05 then
            return false
        end
    end

    for key, _ in pairs(nextMap) do
        if currentMap[key] == nil then
            return false
        end
    end

    return true
end

local function MatchSearch(entry, searchText)
    if searchText == nil or searchText == "" then
        return true
    end

    local q = string.lower(searchText)
    local name = string.lower(entry.displayName or "")
    local identifier = string.lower(entry.identifier or "")
    return string.find(name, q, 1, true) ~= nil or string.find(identifier, q, 1, true) ~= nil
end

local function BuildEntries()
    local entries = {}
    for _, entry in pairs(DBClient.State.entriesByKey or {}) do
        if MatchSearch(entry, DBClient.State.searchText) then
            table.insert(entries, entry)
        end
    end

    if DBClient.State.sortMode == "name_desc" then
        table.sort(entries, function(a, b)
            return string.lower(a.displayName or "") > string.lower(b.displayName or "")
        end)
    elseif DBClient.State.sortMode == "count_desc" then
        table.sort(entries, function(a, b)
            if (a.amount or 0) == (b.amount or 0) then
                return string.lower(a.displayName or "") < string.lower(b.displayName or "")
            end
            return (a.amount or 0) > (b.amount or 0)
        end)
    elseif DBClient.State.sortMode == "count_asc" then
        table.sort(entries, function(a, b)
            if (a.amount or 0) == (b.amount or 0) then
                return string.lower(a.displayName or "") < string.lower(b.displayName or "")
            end
            return (a.amount or 0) < (b.amount or 0)
        end)
    else
        table.sort(entries, function(a, b)
            return string.lower(a.displayName or "") < string.lower(b.displayName or "")
        end)
    end

    return entries
end

local function ResolveIconSprite(entry)
    local key = NormalizeIdentifier(entry.prefabIdentifier or entry.identifier)
    if key == "" then
        return nil
    end

    local cached = DBClient.IconCache[key]
    if cached ~= nil then
        return cached == false and nil or cached
    end

    local sprite = nil
    pcall(function()
        local prefab = ItemPrefab.GetItemPrefab(tostring(entry.prefabIdentifier or entry.identifier or ""))
        if prefab ~= nil then
            sprite = prefab.InventoryIcon or prefab.Sprite
        end
    end)

    if sprite == nil then
        DBClient.IconCache[key] = false
        return nil
    end

    DBClient.IconCache[key] = sprite
    return sprite
end

local function BuildEntryTooltip(entry)
    local amount = math.max(0, math.floor(tonumber(entry.amount) or 0))
    local quality = math.floor(tonumber(entry.bestQuality) or 0)
    local condition = tonumber(entry.avgCondition) or 0
    local header = tostring(entry.displayName or entry.identifier or "?")
    local identifier = tostring(entry.identifier or "")

    local tooltipLines = {
        header,
        "id=" .. identifier,
        "x" .. tostring(amount),
        string.format("quality=%d condition=%.1f", quality, condition)
    }

    local prefabText = nil
    pcall(function()
        local prefab = ItemPrefab.GetItemPrefab(tostring(entry.prefabIdentifier or entry.identifier or ""))
        if prefab ~= nil then
            local rich = prefab.GetTooltip()
            if rich ~= nil then
                local asString = tostring(rich)
                pcall(function()
                    asString = tostring(rich.ToString())
                end)
                if asString ~= nil and asString ~= "" then
                    prefabText = asString
                end
            end
        end
    end)

    local body = prefabText
    if body == nil or body == "" then
        body = table.concat(tooltipLines, "\n")
    else
        body = body .. "\n" .. tooltipLines[2] .. "\n" .. tooltipLines[3]
    end

    return RichString.Rich(body)
end

local function ResetViewState(clearTerminal)
    DBClient.State.serial = 0
    DBClient.State.entriesByKey = {}
    DBClient.State.totalEntries = 0
    DBClient.State.totalAmount = 0
    DBClient.State.awaitingSnapshot = false
    DBClient.State.dirty = true
    DBClient.State.renderToken = (tonumber(DBClient.State.renderToken) or 0) + 1
    DBClient.State.lastNearbyScanAt = 0
    DBClient.State.nearbyTerminal = nil
    if clearTerminal then
        DBClient.State.activeTerminal = nil
        DBClient.State.subscribedTerminalId = ""
        DBClient.State.databaseId = "default"
    end
    LogDebugThrottled(
        "reset-view",
        0.3,
        string.format(
            "ResetViewState clearTerminal=%s",
            tostring(clearTerminal == true)))
end

local function SendUnsubscribe()
    if not Game.IsMultiplayer then
        local target = tostring(DBClient.State.subscribedTerminalId or "")
        if target ~= "" then
            local ok, accepted, mode, err = LocalB1Unsubscribe(target)
            LogDebug(string.format(
                "SendUnsubscribe localB1 terminal=%s ok=%s accepted=%s mode=%s err=%s",
                tostring(target),
                tostring(ok == true),
                tostring(accepted == true),
                tostring(mode or ""),
                tostring(err or "")))
        else
            LogDebug("SendUnsubscribe localB1 skipped: subscribedTerminalId is empty.")
        end
        DBClient.State.subscribedTerminalId = ""
        return
    end

    if DBClient.State.subscribedTerminalId == "" then
        LogDebug("SendUnsubscribe skipped because subscribedTerminalId is empty.")
        return
    end

    LogDebug("SendUnsubscribe begin terminal=" .. tostring(DBClient.State.subscribedTerminalId))
    local message = Networking.Start(DBClient.NetViewUnsubscribe)
    message.WriteString(DBClient.State.subscribedTerminalId)
    Networking.Send(message)

    Log("Unsubscribe terminal=" .. tostring(DBClient.State.subscribedTerminalId))
    DBClient.State.subscribedTerminalId = ""
end

local function SendSubscribe(terminal, force)
    if terminal == nil then
        LogDebug("SendSubscribe skipped: terminal is nil.")
        return
    end

    local terminalId = tostring(terminal.ID)
    if terminalId == "" then
        LogDebug("SendSubscribe skipped: terminal id empty.")
        return
    end

    if not force and DBClient.State.subscribedTerminalId == terminalId and not DBClient.State.awaitingSnapshot then
        LogDebug(string.format(
            "SendSubscribe skipped terminal=%s force=%s awaitingSnapshot=%s",
            tostring(terminalId),
            tostring(force == true),
            tostring(DBClient.State.awaitingSnapshot == true)))
        return
    end

    if not Game.IsMultiplayer then
        local character = Character.Controlled
        local ok, accepted, mode, err = LocalB1Subscribe(terminalId, character)
        if ok and accepted then
            DBClient.State.subscribedTerminalId = terminalId
            DBClient.State.awaitingSnapshot = true
            DBClient.State.lastSubscribeAt = Now()
            LogDebug(string.format(
                "SendSubscribe localB1 accepted terminal=%s force=%s mode=%s",
                tostring(terminalId),
                tostring(force == true),
                tostring(mode or "")))
        else
            DBClient.State.subscribedTerminalId = ""
            DBClient.State.awaitingSnapshot = false
            LogDebug(string.format(
                "SendSubscribe localB1 rejected terminal=%s force=%s ok=%s accepted=%s mode=%s err=%s",
                tostring(terminalId),
                tostring(force == true),
                tostring(ok == true),
                tostring(accepted == true),
                tostring(mode or ""),
                tostring(err or "")))
        end
        return
    end

    LogDebug(string.format(
        "SendSubscribe begin terminal=%s force=%s currentSub=%s awaitingSnapshot=%s",
        tostring(terminalId),
        tostring(force == true),
        tostring(DBClient.State.subscribedTerminalId),
        tostring(DBClient.State.awaitingSnapshot == true)))
    local message = Networking.Start(DBClient.NetViewSubscribe)
    message.WriteString(terminalId)
    Networking.Send(message)

    DBClient.State.subscribedTerminalId = terminalId
    DBClient.State.awaitingSnapshot = true
    DBClient.State.lastSubscribeAt = Now()

    Log("Subscribe terminal=" .. tostring(terminalId) .. " force=" .. tostring(force == true))
end

local function RequestTake(entry)
    local terminal = DBClient.State.activeTerminal
    local character = Character.Controlled
    if terminal == nil or entry == nil or character == nil then
        LogDebug(string.format(
            "RequestTake skipped terminalNil=%s entryNil=%s characterNil=%s",
            tostring(terminal == nil),
            tostring(entry == nil),
            tostring(character == nil)))
        return
    end

    LogDebug(string.format(
        "RequestTake begin terminal=%s identifier=%s multiplayer=%s",
        tostring(GetTerminalId(terminal)),
        tostring(entry.identifier or ""),
        tostring(Game.IsMultiplayer == true)))

    if Game.IsMultiplayer then
        local message = Networking.Start(DBClient.NetTakeRequest)
        message.WriteString(tostring(terminal.ID))
        message.WriteString(tostring(entry.identifier or ""))
        Networking.Send(message)
        LogDebug("RequestTake sent to server.")
        return
    end

    local terminalEntityId = ToTerminalEntityId(terminal)
    local callOk, success, text, err = LocalB1RequestTake(
        terminalEntityId,
        entry.identifier,
        character)
    if not callOk then
        local fallbackText = L("dbiotest.ui.take.notready", "Terminal API is not ready.")
        GUI.AddMessage(fallbackText, Color.Red)
        LogDebug(string.format(
            "RequestTake localB1 invoke failed terminal=%s identifier=%s err=%s",
            tostring(terminalEntityId),
            tostring(entry.identifier or ""),
            tostring(err or "")))
        DBClient.State.dirty = true
        return
    end

    if text == nil or text == "" then
        text = success
            and L("dbiotest.ui.take.success", "Item moved to terminal buffer.")
            or L("dbiotest.ui.take.failed", "Failed to transfer item.")
    end

    GUI.AddMessage(text, success and Color.Lime or Color.Red)
    LogDebug(string.format(
        "RequestTake localB1 finished success=%s text='%s'",
        tostring(success == true),
        tostring(text or "")))
    if success and DBClient.State.activeTerminal ~= nil then
        SendSubscribe(DBClient.State.activeTerminal, true)
    end
    DBClient.State.dirty = true
end

local function ReadEntry(message)
    local identifier = tostring(message.ReadString() or "")
    local prefabIdentifier = tostring(message.ReadString() or identifier)
    local displayName = tostring(message.ReadString() or identifier)
    local amount = ReadIntString(message)
    local bestQuality = ReadIntString(message)
    local avgCondition = ReadFloatString(message)

    local key = NormalizeIdentifier(identifier)
    if key == "" then
        return nil
    end

    return {
        key = key,
        identifier = identifier,
        prefabIdentifier = prefabIdentifier,
        displayName = displayName,
        amount = math.max(0, amount),
        bestQuality = bestQuality,
        avgCondition = avgCondition
    }
end

local function NormalizePacketEntry(entry)
    if entry == nil then
        return nil
    end
    local identifier = tostring(entry.identifier or entry.Identifier or "")
    local key = NormalizeIdentifier(identifier)
    if key == "" then
        return nil
    end
    return {
        key = key,
        identifier = identifier,
        prefabIdentifier = tostring(entry.prefabIdentifier or entry.PrefabIdentifier or identifier),
        displayName = tostring(entry.displayName or entry.DisplayName or identifier),
        amount = math.max(0, math.floor(tonumber(entry.amount or entry.Amount) or 0)),
        bestQuality = math.floor(tonumber(entry.bestQuality or entry.BestQuality) or 0),
        avgCondition = tonumber(entry.avgCondition or entry.AverageCondition) or 100.0
    }
end

local function ApplySnapshotValues(terminalId, databaseId, serial, totalEntries, totalAmount, payload, sourceTag)
    local payloadCount = 0
    pcall(function() payloadCount = tonumber(#(payload or {})) or 0 end)
    sourceTag = tostring(sourceTag or "unknown")

    LogDebug(string.format(
        "ApplySnapshot[%s] recv terminal=%s db=%s serial=%d entries=%d amount=%d payload=%d sub=%s",
        sourceTag,
        tostring(terminalId),
        tostring(databaseId),
        tonumber(serial or 0),
        tonumber(totalEntries or 0),
        tonumber(totalAmount or 0),
        tonumber(payloadCount or 0),
        tostring(DBClient.State.subscribedTerminalId)))

    if DBClient.State.subscribedTerminalId ~= "" and terminalId ~= DBClient.State.subscribedTerminalId then
        LogDebug(string.format(
            "ApplySnapshot[%s] ignored terminal mismatch incoming=%s subscribed=%s",
            sourceTag,
            tostring(terminalId),
            tostring(DBClient.State.subscribedTerminalId)))
        return
    end

    DBClient.State.serial = math.max(0, tonumber(serial) or 0)
    DBClient.State.databaseId = tostring(databaseId or "default")
    DBClient.State.entriesByKey = {}

    for _, rawEntry in ipairs(payload or {}) do
        local entry = NormalizePacketEntry(rawEntry)
        if entry ~= nil then
            DBClient.State.entriesByKey[entry.key] = entry
        end
    end

    DBClient.State.totalEntries = math.max(0, tonumber(totalEntries) or 0)
    DBClient.State.totalAmount = math.max(0, tonumber(totalAmount) or 0)
    DBClient.State.awaitingSnapshot = false
    DBClient.State.dirty = true

    Log(string.format(
        "Snapshot[%s] terminal=%s serial=%d entries=%d amount=%d payload=%d",
        sourceTag,
        tostring(terminalId),
        DBClient.State.serial,
        DBClient.State.totalEntries,
        DBClient.State.totalAmount,
        payloadCount))
end

local function ApplyDeltaValues(terminalId, databaseId, serial, totalEntries, totalAmount, removed, upserts, sourceTag)
    removed = removed or {}
    upserts = upserts or {}
    sourceTag = tostring(sourceTag or "unknown")

    LogDebug(string.format(
        "ApplyDelta[%s] recv terminal=%s db=%s serial=%d localSerial=%d removed=%d upserts=%d entries=%d amount=%d sub=%s",
        sourceTag,
        tostring(terminalId),
        tostring(databaseId),
        tonumber(serial or 0),
        tonumber(DBClient.State.serial or 0),
        tonumber(#removed or 0),
        tonumber(#upserts or 0),
        tonumber(totalEntries or 0),
        tonumber(totalAmount or 0),
        tostring(DBClient.State.subscribedTerminalId)))

    if DBClient.State.subscribedTerminalId ~= "" and terminalId ~= DBClient.State.subscribedTerminalId then
        LogDebug(string.format(
            "ApplyDelta[%s] ignored terminal mismatch incoming=%s subscribed=%s",
            sourceTag,
            tostring(terminalId),
            tostring(DBClient.State.subscribedTerminalId)))
        return
    end

    serial = tonumber(serial) or 0
    if serial <= (tonumber(DBClient.State.serial) or 0) then
        LogDebug(string.format(
            "ApplyDelta[%s] ignored stale serial incoming=%d local=%d",
            sourceTag,
            serial,
            tonumber(DBClient.State.serial or 0)))
        return
    end

    if serial > (tonumber(DBClient.State.serial or 0) + 1) then
        DBClient.State.awaitingSnapshot = true
        if DBClient.State.activeTerminal ~= nil then
            SendSubscribe(DBClient.State.activeTerminal, true)
        end
        Log(string.format(
            "Delta gap detected[%s] terminal=%s localSerial=%d incoming=%d -> resubscribe",
            sourceTag,
            tostring(terminalId),
            tonumber(DBClient.State.serial or 0),
            serial))
        return
    end

    for _, identifier in ipairs(removed) do
        local key = NormalizeIdentifier(tostring(identifier or ""))
        if key ~= "" then
            DBClient.State.entriesByKey[key] = nil
        end
    end

    for _, rawEntry in ipairs(upserts) do
        local entry = NormalizePacketEntry(rawEntry)
        if entry ~= nil then
            DBClient.State.entriesByKey[entry.key] = entry
        end
    end

    DBClient.State.databaseId = tostring(databaseId or "default")
    DBClient.State.serial = serial
    DBClient.State.totalEntries = math.max(0, tonumber(totalEntries) or 0)
    DBClient.State.totalAmount = math.max(0, tonumber(totalAmount) or 0)
    DBClient.State.awaitingSnapshot = false
    DBClient.State.dirty = true

    Log(string.format(
        "Delta[%s] terminal=%s serial=%d removed=%d upserts=%d entries=%d amount=%d",
        sourceTag,
        tostring(terminalId),
        serial,
        tonumber(#removed or 0),
        tonumber(#upserts or 0),
        DBClient.State.totalEntries,
        DBClient.State.totalAmount))
end

local function PollLocalB1Packets()
    if Game.IsMultiplayer then
        return
    end

    local ok, packets, mode, err = LocalB1Poll()
    if not ok then
        LogDebugThrottled("local-poll-failed", 1.5, string.format(
            "LocalB1 poll failed mode=%s err=%s",
            tostring(mode or "?"),
            tostring(err or "")))
        return
    end

    local count = 0
    for _, packet in ipairs(packets or {}) do
        count = count + 1
        local kind = tostring(packet.kind or "")
        if kind == "snapshot" then
            ApplySnapshotValues(
                tostring(packet.terminalEntityId or ""),
                tostring(packet.databaseId or "default"),
                tonumber(packet.serial) or 0,
                tonumber(packet.totalEntries) or 0,
                tonumber(packet.totalAmount) or 0,
                packet.payload or {},
                "localB1")
        elseif kind == "delta" then
            ApplyDeltaValues(
                tostring(packet.terminalEntityId or ""),
                tostring(packet.databaseId or "default"),
                tonumber(packet.serial) or 0,
                tonumber(packet.totalEntries) or 0,
                tonumber(packet.totalAmount) or 0,
                packet.removed or {},
                packet.upserts or {},
                "localB1")
        else
            LogDebug(string.format(
                "LocalB1 poll ignored unknown packet kind='%s'",
                tostring(kind)))
        end
    end
    if count > 0 then
        LogDebug(string.format("LocalB1 poll processed packets=%d", tonumber(count or 0)))
    else
        LogDebugThrottled("local-poll-empty", 3.0, "LocalB1 poll queue empty.")
    end
end

local function ApplySnapshot(message)
    local terminalId = tostring(message.ReadString() or "")
    local databaseId = tostring(message.ReadString() or "default")
    local serial = ReadIntString(message)
    local totalEntries = ReadIntString(message)
    local totalAmount = ReadIntString(message)
    local payloadCount = ReadIntString(message)

    local payload = {}
    for i = 1, math.max(0, payloadCount) do
        local entry = ReadEntry(message)
        if entry ~= nil then
            payload[#payload + 1] = entry
        end
    end

    ApplySnapshotValues(
        terminalId,
        databaseId,
        serial,
        totalEntries,
        totalAmount,
        payload,
        "net")
end

local function ApplyDelta(message)
    local terminalId = tostring(message.ReadString() or "")
    local databaseId = tostring(message.ReadString() or "default")
    local serial = ReadIntString(message)
    local totalEntries = ReadIntString(message)
    local totalAmount = ReadIntString(message)
    local removedCount = ReadIntString(message)

    local removed = {}
    for i = 1, math.max(0, removedCount) do
        removed[#removed + 1] = tostring(message.ReadString() or "")
    end

    local upsertCount = ReadIntString(message)
    local upserts = {}
    for i = 1, math.max(0, upsertCount) do
        local entry = ReadEntry(message)
        if entry ~= nil then
            upserts[#upserts + 1] = entry
        end
    end

    ApplyDeltaValues(
        terminalId,
        databaseId,
        serial,
        totalEntries,
        totalAmount,
        removed,
        upserts,
        "net")
end

DBClient.Root = GUI.Frame(GUI.RectTransform(Vector2(1, 1)), nil)
DBClient.Root.CanBeFocused = false

DBClient.Panel = GUI.Frame(GUI.RectTransform(Vector2(0.42, 0.66), DBClient.Root.RectTransform, GUI.Anchor.CenterRight), "GUIFrame")
DBClient.Panel.RectTransform.AbsoluteOffset = Point(-24, 0)
DBClient.Panel.Visible = false

DBClient.DragHandle = GUI.DragHandle(
    GUI.RectTransform(Vector2(1, 1), DBClient.Panel.RectTransform, GUI.Anchor.Center),
    DBClient.Panel.RectTransform,
    nil
)

DBClient.Title = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.95, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopCenter),
    L("dbiotest.ui.title", "Database Terminal"),
    nil,
    nil,
    GUI.Alignment.Center
)
DBClient.Title.RectTransform.AbsoluteOffset = Point(0, 8)

DBClient.SearchBox = GUI.TextBox(
    GUI.RectTransform(Vector2(0.55, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopLeft),
    DBClient.State.searchText
)
DBClient.SearchBox.RectTransform.AbsoluteOffset = Point(12, 40)
DBClient.SearchBox.ToolTip = L("dbiotest.ui.search.tooltip", "Search by name or identifier.")
DBClient.SearchBox.OnTextChangedDelegate = function(textBox)
    DBClient.State.searchText = textBox.Text or ""
    DBClient.State.dirty = true
    LogDebugThrottled(
        "ui-search-changed",
        0.2,
        "Search text changed: '" .. tostring(DBClient.State.searchText) .. "'")
end

DBClient.SortDropDown = GUI.DropDown(
    GUI.RectTransform(Vector2(0.31, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopRight),
    L("dbiotest.ui.sort.nameasc", "Name A-Z"),
    4,
    nil,
    false
)
DBClient.SortDropDown.RectTransform.AbsoluteOffset = Point(12, 40)
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.nameasc", "Name A-Z"), "name_asc")
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.namedesc", "Name Z-A"), "name_desc")
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.countdesc", "Count High-Low"), "count_desc")
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.countasc", "Count Low-High"), "count_asc")
DBClient.SortDropDown.OnSelected = function(_, obj)
    DBClient.State.sortMode = tostring(obj or "name_asc")
    DBClient.State.dirty = true
    LogDebug("Sort mode changed: " .. tostring(DBClient.State.sortMode))
end

DBClient.RefreshButton = GUI.Button(
    GUI.RectTransform(Vector2(0.20, 0.05), DBClient.Panel.RectTransform, GUI.Anchor.TopLeft),
    L("dbiotest.ui.refresh", "Refresh"),
    GUI.Alignment.Center,
    "GUIButtonSmall"
)
DBClient.RefreshButton.RectTransform.AbsoluteOffset = Point(12, 75)
DBClient.RefreshButton.OnClicked = function()
    LogDebug("Refresh button clicked.")
    if DBClient.State.activeTerminal ~= nil then
        SendSubscribe(DBClient.State.activeTerminal, true)
    end
    DBClient.State.dirty = true
    return true
end

DBClient.StatusText = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.70, 0.05), DBClient.Panel.RectTransform, GUI.Anchor.TopRight),
    "",
    nil,
    nil,
    GUI.Alignment.Right
)
DBClient.StatusText.RectTransform.AbsoluteOffset = Point(12, 76)

DBClient.List = GUI.ListBox(
    GUI.RectTransform(Vector2(0.95, 0.71), DBClient.Panel.RectTransform, GUI.Anchor.Center),
    true
)
DBClient.List.RectTransform.AbsoluteOffset = Point(0, 24)

DBClient.Footer = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.95, 0.08), DBClient.Panel.RectTransform, GUI.Anchor.BottomCenter),
    L("dbiotest.ui.footer", "Click icon to spawn one item into terminal buffer."),
    nil,
    nil,
    GUI.Alignment.Center
)
DBClient.Footer.TextColor = Color(210, 210, 210)

local function RedrawList()
    local terminal = DBClient.State.activeTerminal
    DBClient.List.ClearChildren()

    if terminal == nil or terminal.Removed then
        DBClient.Title.Text = L("dbiotest.ui.title", "Database Terminal")
        DBClient.StatusText.Text = ""
        DBClient.List.RecalculateChildren()
        return
    end

    local dbid = DBClient.State.databaseId or GetTerminalDatabaseId(terminal)
    DBClient.Title.Text = string.format("%s [%s]", L("dbiotest.ui.title", "Database Terminal"), tostring(dbid or "default"))
    DBClient.StatusText.Text = string.format("%d entries | %d items", DBClient.State.totalEntries or 0, DBClient.State.totalAmount or 0)

    local entries = BuildEntries()
    if #entries == 0 then
        local text = DBClient.State.awaitingSnapshot
            and L("dbiotest.ui.loading", "Waiting for snapshot...")
            or L("dbiotest.ui.empty", "No items in terminal view.")
        local emptyText = GUI.TextBlock(
            GUI.RectTransform(Vector2(1, 0.08), DBClient.List.Content.RectTransform, GUI.Anchor.TopCenter),
            text,
            nil,
            nil,
            GUI.Alignment.Center
        )
        emptyText.TextColor = Color(200, 200, 200)
    else
        local hudScale = 1
        pcall(function() hudScale = tonumber(GameSettings.CurrentConfig.Graphics.HUDScale) or 1 end)
        local iconSize = math.floor(64 / math.max(0.8, hudScale))
        local iconWidth = math.max(1, math.floor(iconSize * GUI.xScale))
        local iconHeight = math.max(1, math.floor(iconSize * GUI.yScale))
        local listRect = DBClient.List.Content and DBClient.List.Content.Rect or nil
        local listWidth = listRect and math.max(1, listRect.Width) or 900
        local listHeight = listRect and math.max(1, listRect.Height) or 560
        local maxIconsPerRow = math.max(4, math.min(10, math.floor(listWidth / (iconWidth + 4))))
        local rowHeight = math.max(0.08, math.min(0.18, (iconHeight + 6) / listHeight))
        local totalRows = math.max(1, math.ceil(#entries / maxIconsPerRow))
        local rowLists = {}

        for row = 1, totalRows do
            rowLists[row] = GUI.ListBox(
                GUI.RectTransform(Vector2(1, rowHeight), DBClient.List.Content.RectTransform, GUI.Anchor.TopLeft),
                true
            )
        end

        DBClient.State.renderToken = (tonumber(DBClient.State.renderToken) or 0) + 1
        local token = DBClient.State.renderToken

        local function DrawNextIcon(index)
            if token ~= DBClient.State.renderToken then
                return
            end

            local entry = entries[index]
            if entry == nil then
                return
            end

            local currentRow = math.floor((index - 1) / maxIconsPerRow) + 1
            local rowList = rowLists[currentRow]
            if rowList ~= nil and rowList.Content ~= nil then
                local button = GUI.Button(
                    GUI.RectTransform(Point(iconSize * GUI.xScale, iconSize * GUI.yScale), rowList.Content.RectTransform)
                )
                button.Color = Color(0, 0, 0, 0)
                button.HoverColor = Color(80, 80, 80, 150)
                button.PressedColor = Color(120, 120, 120, 200)
                button.SelectedColor = Color(0, 0, 0, 0)
                button.RectTransform.MinSize = Point(0, iconHeight)

                local icon = ResolveIconSprite(entry)
                if icon ~= nil then
                    local image = GUI.Image(
                        GUI.RectTransform(Vector2(0.95, 0.95), button.RectTransform, GUI.Anchor.Center),
                        icon
                    )
                    image.ToolTip = BuildEntryTooltip(entry)
                else
                    local fallback = GUI.TextBlock(
                        GUI.RectTransform(Vector2(1, 1), button.RectTransform, GUI.Anchor.Center),
                        "?",
                        nil,
                        nil,
                        GUI.Alignment.Center
                    )
                    fallback.TextColor = Color(220, 220, 220)
                    button.ToolTip = BuildEntryTooltip(entry)
                end

                button.OnClicked = function()
                    LogDebug(string.format(
                        "UI take click identifier=%s amount=%d",
                        tostring(entry.identifier or ""),
                        tonumber(entry.amount or 0)))
                    RequestTake(entry)
                    return true
                end
            end

            Timer.NextFrame(function()
                DrawNextIcon(index + 1)
            end)
        end

        DrawNextIcon(1)
    end

    DBClient.List.RecalculateChildren()
    DBClient.State.dirty = false
end

Hook.Patch("Barotrauma.GameScreen", "AddToGUIUpdateList", function()
    DBClient.Root.AddToGUIUpdateList(false, 1)
end)
LogDebug("Hook.Patch registered: Barotrauma.GameScreen.AddToGUIUpdateList")

Hook.Patch("Barotrauma.NetLobbyScreen", "AddToGUIUpdateList", function()
    DBClient.Root.AddToGUIUpdateList(false, 1)
end)
LogDebug("Hook.Patch registered: Barotrauma.NetLobbyScreen.AddToGUIUpdateList")

Hook.Add("think", "DBIOTEST_ClientTerminalUiThink", function()
    local thinkStartedAt = Now()
    local hasSession = false
    pcall(function()
        hasSession = Game.GameSession ~= nil
    end)
    if not hasSession then
        DBClient.Panel.Visible = false
        if DBClient.State.activeTerminal ~= nil then
            SendUnsubscribe()
            ResetViewState(true)
        end
        LogDebugThrottled("think-no-session", 6.0, "Think state=no_session panel hidden")
        LogPerf("think", thinkStartedAt, "state=no_session")
        return
    end

    local character = Character.Controlled
    local now = Now()
    local terminal, terminalSource = ResolveUiTerminal(character)

    if terminal == nil then
        local active = DBClient.State.activeTerminal
        local activeLostAgo = now - (DBClient.State.lastActiveSeenAt or 0)
        if active ~= nil and not active.Removed and activeLostAgo < TerminalLostGraceSeconds then
            terminal = active
            terminalSource = "grace"
        end
    end

    if terminal == nil then
        if DBClient.State.activeTerminal ~= nil then
            local activeLostAgo = now - (DBClient.State.lastActiveSeenAt or 0)
            if activeLostAgo < ActiveTerminalKeepAliveSeconds then
                DBClient.Panel.Visible = true
                LogDebugThrottled(
                    "think-keepalive",
                    2.5,
                    string.format("Think keepalive activeLostAgo=%.2f", activeLostAgo))
                LogPerf("think", thinkStartedAt, "state=keepalive")
                return
            end

            local previousId = GetActiveTerminalId()
            SendUnsubscribe()
            ResetViewState(true)
            LogThrottled(
                "terminal-unbind",
                0.4,
                string.format(
                    "TerminalUnbind terminal=%s reason='inactive' lostFor=%.2fs",
                    previousId,
                    activeLostAgo))
        end
        DBClient.Panel.Visible = false
        LogDebugThrottled("think-no-terminal", 6.0, "Think state=no_terminal panel hidden")
        LogPerf("think", thinkStartedAt, "state=no_terminal")
        return
    end

    DBClient.State.lastActiveSeenAt = now
    DBClient.Panel.Visible = true
    if DBClient.State.activeTerminal ~= terminal then
        local previousId = GetActiveTerminalId()
        SendUnsubscribe()
        DBClient.State.activeTerminal = terminal
        DBClient.State.databaseId = GetTerminalDatabaseId(terminal)
        DBClient.State.subscribedTerminalId = ""
        DBClient.State.lastLocalSync = 0
        ResetViewState(false)
        SendSubscribe(terminal, true)
        LogThrottled(
            "terminal-bind",
            0.25,
            string.format(
                "TerminalBind prev=%s next=%s source=%s",
                previousId,
                GetTerminalId(terminal),
                tostring(terminalSource or "unknown")))
        LogDebug(string.format(
            "Terminal switched prev=%s next=%s source=%s db=%s",
            tostring(previousId),
            tostring(GetTerminalId(terminal)),
            tostring(terminalSource or "unknown"),
            tostring(DBClient.State.databaseId or "default")))
    end

    if not Game.IsMultiplayer then
        PollLocalB1Packets()
    end

    if DBClient.State.awaitingSnapshot and (Now() - (DBClient.State.lastSubscribeAt or 0)) > 1.2 then
        LogDebug("Snapshot timeout; resubscribe requested.")
        SendSubscribe(terminal, true)
    end

    local now = Now()
    if DBClient.State.dirty then
        DBClient.State.lastRefresh = now
        local redrawStartedAt = Now()
        RedrawList()
        LogPerf("redraw", redrawStartedAt, "phase=draw")
    end
    LogPerf("think", thinkStartedAt, "state=active")
end)
LogDebug("Hook.Add registered: DBIOTEST_ClientTerminalUiThink")

Networking.Receive(DBClient.NetViewSnapshot, function(message)
    LogDebugThrottled("net-snapshot-recv", 2.0, "Networking.Receive snapshot packet.")
    ApplySnapshot(message)
end)
LogDebug("Networking.Receive registered: " .. tostring(DBClient.NetViewSnapshot))

Networking.Receive(DBClient.NetViewDelta, function(message)
    LogDebugThrottled("net-delta-recv", 2.0, "Networking.Receive delta packet.")
    ApplyDelta(message)
end)
LogDebug("Networking.Receive registered: " .. tostring(DBClient.NetViewDelta))

Networking.Receive(DBClient.NetTakeResult, function(message)
    local success = message.ReadBoolean()
    local text = message.ReadString()
    if text == nil or text == "" then
        text = success
            and L("dbiotest.ui.take.success", "Item moved to terminal buffer.")
            or L("dbiotest.ui.take.failed", "Failed to transfer item.")
    end

    GUI.AddMessage(text, success and Color.Lime or Color.Red)
    if success and DBClient.State.activeTerminal ~= nil then
        SendSubscribe(DBClient.State.activeTerminal, true)
    end
    LogDebug(string.format(
        "TakeResult success=%s text='%s' activeTerminal=%s",
        tostring(success == true),
        tostring(text or ""),
        tostring(GetActiveTerminalId())))
    DBClient.State.dirty = true
end)
LogDebug("Networking.Receive registered: " .. tostring(DBClient.NetTakeResult))
