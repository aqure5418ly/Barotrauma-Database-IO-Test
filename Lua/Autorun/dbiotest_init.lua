DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Path = table.pack(...)[1]
DatabaseIOTestLua.Bootstrap = DatabaseIOTestLua.Bootstrap or {}
DatabaseIOTestLua.FileLog = DatabaseIOTestLua.FileLog or {
    initialized = false,
    path = "",
    cache = "",
    pending = {},
    pendingCount = 0,
    lastFlushAt = 0,
    nextFlushAt = 0,
    flushInterval = 0.75,
    forceFlushEveryLines = 24,
    writeFailed = false,
    lastError = ""
}
DatabaseIOTestLua.FileLog.pending = DatabaseIOTestLua.FileLog.pending or {}
DatabaseIOTestLua.FileLog.pendingCount = tonumber(DatabaseIOTestLua.FileLog.pendingCount) or 0
DatabaseIOTestLua.FileLog.lastFlushAt = tonumber(DatabaseIOTestLua.FileLog.lastFlushAt) or 0
DatabaseIOTestLua.FileLog.nextFlushAt = tonumber(DatabaseIOTestLua.FileLog.nextFlushAt) or 0
DatabaseIOTestLua.FileLog.flushInterval = tonumber(DatabaseIOTestLua.FileLog.flushInterval) or 0.75
DatabaseIOTestLua.FileLog.forceFlushEveryLines = tonumber(DatabaseIOTestLua.FileLog.forceFlushEveryLines) or 24

local ForceLuaDebugLog = false
local LoggerBridgeMissingPrinted = false
local MaxLuaLogCacheChars = 400000
local KeepLuaLogCacheTailChars = 280000

local function LogNow()
    local t = 0
    pcall(function()
        t = Timer.Time
    end)
    if t == 0 and os ~= nil and os.clock ~= nil then
        t = tonumber(os.clock()) or 0
    end
    return tonumber(t) or 0
end

local function BuildTimestamp()
    local stamp = nil
    pcall(function()
        if CS ~= nil and CS.System ~= nil and CS.System.DateTime ~= nil then
            stamp = tostring(CS.System.DateTime.Now:ToString("yyyy-MM-dd HH:mm:ss.fff"))
        end
    end)

    if stamp == nil or stamp == "" then
        local base = tostring(os.date("%Y-%m-%d %H:%M:%S") or "")
        local now = LogNow()
        local millis = math.floor((now - math.floor(now)) * 1000)
        if millis < 0 then millis = 0 end
        if millis > 999 then millis = 999 end
        if base == "" then
            base = "1970-01-01 00:00:00"
        end
        stamp = string.format("%s.%03d", base, millis)
    end

    return stamp
end

local function StampLogLine(line)
    local text = tostring(line or "")
    if string.match(text, "^%d%d%d%d%-%d%d%-%d%d %d%d:%d%d:%d%d%.%d%d%d%s") ~= nil then
        return text
    end
    return BuildTimestamp() .. " " .. text
end

DatabaseIOTestLua.BuildTimestamp = BuildTimestamp
DatabaseIOTestLua.StampLogLine = StampLogLine

local function TryGetLoggerBridge()
    if DatabaseIOTest ~= nil and
        DatabaseIOTest.Services ~= nil and
        DatabaseIOTest.Services.ModFileLog ~= nil then
        return DatabaseIOTest.Services.ModFileLog, "DatabaseIOTest.Services.ModFileLog"
    end
    if CS ~= nil and
        CS.DatabaseIOTest ~= nil and
        CS.DatabaseIOTest.Services ~= nil and
        CS.DatabaseIOTest.Services.ModFileLog ~= nil then
        return CS.DatabaseIOTest.Services.ModFileLog, "CS.DatabaseIOTest.Services.ModFileLog"
    end
    return nil, "none"
end

local function ResolveLuaDebugEnabled(basePath)
    local enabled = false
    local source = "default:false"

    local logger, bridge = TryGetLoggerBridge()
    if logger ~= nil then
        local found = false
        pcall(function()
            if logger.IsDebugEnabled ~= nil then
                enabled = logger.IsDebugEnabled == true
                found = true
            end
        end)
        if found then
            return enabled, "bridge:" .. tostring(bridge or "unknown")
        end
    end

    if File ~= nil then
        local base = tostring(basePath or ""):gsub("\\", "/"):gsub("/+$", "")
        local candidates = {
            base .. "/debug.enabled",
            base .. "/Logs/debug.enabled"
        }
        for _, path in ipairs(candidates) do
            local exists = false
            pcall(function()
                exists = File.Exists(path) == true
            end)
            if exists then
                return true, "marker:" .. tostring(path)
            end
        end
    end

    return enabled, source
end

ForceLuaDebugLog, DatabaseIOTestLua.DebugSource = ResolveLuaDebugEnabled(DatabaseIOTestLua.Path)
DatabaseIOTestLua.DebugEnabled = ForceLuaDebugLog == true
DatabaseIOTestLua.IsLuaDebugEnabled = function()
    return DatabaseIOTestLua.DebugEnabled == true
end

local function TryWriteBootstrap(level, line)
    local text = StampLogLine(line)
    local logger, bridge = TryGetLoggerBridge()
    local wrote = false

    if logger ~= nil then
        if tostring(level or "info") == "debug" then
            local okDebug = pcall(function()
                logger.WriteDebug("LuaBootstrap", text)
            end)
            if okDebug then
                wrote = true
            elseif ForceLuaDebugLog then
                local okFallback = pcall(function()
                    logger.Write("LuaBootstrap", "[DBG->INFO] " .. text)
                end)
                wrote = okFallback
            end
        else
            local okInfo = pcall(function()
                logger.Write("LuaBootstrap", text)
            end)
            wrote = okInfo
        end

        if wrote then
            DatabaseIOTestLua.Bootstrap.LoggerBridge = bridge
        end
    end

    if not wrote and not LoggerBridgeMissingPrinted then
        LoggerBridgeMissingPrinted = true
        print("[Database IO Test][Lua][Bootstrap] File logger bridge unavailable; only print logs will be shown.")
    end
    print(text)
    return wrote
end

local function BuildLuaLogPath(basePath)
    local day = tostring(os.date("%Y%m%d") or "")
    if day == "" then
        day = "unknown"
    end
    local base = tostring(basePath or ""):gsub("\\", "/")
    base = base:gsub("/+$", "")
    return string.format("%s/Logs/lualog/dbiotest-lua-%s.log", base, day)
end

local function BuildLuaLogFallbackPath(basePath)
    local day = tostring(os.date("%Y%m%d") or "")
    if day == "" then
        day = "unknown"
    end
    local base = tostring(basePath or ""):gsub("\\", "/")
    base = base:gsub("/+$", "")
    return string.format("%s/Logs/dbiotest-lua-%s.log", base, day)
end

local function EnsureLuaFileLogger(basePath)
    local state = DatabaseIOTestLua.FileLog
    if state.initialized then
        return state.path ~= ""
    end

    if File == nil then
        state.initialized = true
        state.path = ""
        state.lastError = "File API missing"
        return false
    end

    local path = BuildLuaLogPath(basePath)
    state.path = path
    state.cache = ""
    state.pending = {}
    state.pendingCount = 0
    state.lastFlushAt = 0
    state.nextFlushAt = 0
    state.writeFailed = false
    state.lastError = ""

    pcall(function()
        if File.Exists(path) then
            state.cache = tostring(File.Read(path) or "")
        end
    end)
    if state.cache ~= "" and string.sub(state.cache, -1) ~= "\n" then
        state.cache = state.cache .. "\n"
    end

    state.initialized = true
    return state.path ~= ""
end

local function TrimLogCache(text)
    local content = tostring(text or "")
    if string.len(content) > MaxLuaLogCacheChars then
        content = string.sub(content, -KeepLuaLogCacheTailChars)
        local firstNewline = string.find(content, "\n", 1, true)
        if firstNewline ~= nil and firstNewline > 0 and firstNewline < 256 then
            content = string.sub(content, firstNewline + 1)
        end
    end
    return content
end

local function FlushLuaFileLog(forceFlush)
    local state = DatabaseIOTestLua.FileLog
    if not EnsureLuaFileLogger(DatabaseIOTestLua.Path) then
        return false
    end
    if state.pendingCount <= 0 then
        return true
    end

    local now = LogNow()
    local force = forceFlush == true
    if not force and now < (state.nextFlushAt or 0) and
        state.pendingCount < (state.forceFlushEveryLines or 24) then
        return true
    end

    local pendingText = table.concat(state.pending, "\n")
    if pendingText ~= "" then
        pendingText = pendingText .. "\n"
    end
    local nextCache = TrimLogCache(tostring(state.cache or "") .. pendingText)

    local ok = false
    pcall(function()
        File.Write(state.path, nextCache)
        ok = true
    end)

    if not ok then
        local fallbackPath = BuildLuaLogFallbackPath(DatabaseIOTestLua.Path)
        pcall(function()
            File.Write(fallbackPath, nextCache)
            state.path = fallbackPath
            ok = true
        end)
    end

    if ok then
        state.cache = nextCache
        state.pending = {}
        state.pendingCount = 0
        state.lastFlushAt = now
        state.nextFlushAt = now + (state.flushInterval or 0.75)
        state.writeFailed = false
        state.lastError = ""
        return true
    end

    state.writeFailed = true
    state.lastError = "File.Write failed for both primary and fallback paths."
    return false
end

local function AppendLuaFileLogLine(line)
    local state = DatabaseIOTestLua.FileLog
    if not EnsureLuaFileLogger(DatabaseIOTestLua.Path) then
        return false
    end

    local text = tostring(line or "")
    table.insert(state.pending, text)
    state.pendingCount = tonumber(state.pendingCount or 0) + 1
    return FlushLuaFileLog(false)
end

DatabaseIOTestLua.AppendLuaFileLogLine = AppendLuaFileLogLine
DatabaseIOTestLua.FlushLuaFileLog = FlushLuaFileLog

local function BootInfo(line)
    local text = "[DBIOTEST][B1][Bootstrap] " .. tostring(line or "")
    local wrote = TryWriteBootstrap("info", text)
    if not wrote then
        AppendLuaFileLogLine(text)
    end
end

local function BootDebug(line)
    if not ForceLuaDebugLog then
        return
    end
    local text = "[DBIOTEST][B1][Bootstrap][DBG] " .. tostring(line or "")
    local wrote = TryWriteBootstrap("debug", text)
    if not wrote then
        AppendLuaFileLogLine("[DBG->INFO] " .. text)
    end
end

local function Now()
    local t = 0
    pcall(function()
        t = Timer.Time
    end)
    return tonumber(t) or 0
end

local basePath = tostring(DatabaseIOTestLua.Path or "")
if basePath == "" then
    BootInfo("Missing mod path, lua bootstrap aborted.")
    return
end

local loggerReady = EnsureLuaFileLogger(basePath)
if loggerReady then
    print("[Database IO Test][Lua][Bootstrap] Local file logger ready at " .. tostring(DatabaseIOTestLua.FileLog.path))
else
    print("[Database IO Test][Lua][Bootstrap] Local file logger unavailable: " .. tostring(DatabaseIOTestLua.FileLog.lastError or "unknown"))
end
BootInfo(string.format("Lua debug enabled=%s source=%s", tostring(ForceLuaDebugLog), tostring(DatabaseIOTestLua.DebugSource or "unknown")))

local function safeLoad(relativePath)
    local fullPath = basePath .. "/" .. relativePath
    local startedAt = Now()
    BootDebug("safeLoad start path='" .. tostring(relativePath) .. "' fullPath='" .. tostring(fullPath) .. "'")

    local ok, err = pcall(function()
        if loadfile ~= nil then
            local chunk, loadErr = loadfile(fullPath)
            if chunk == nil then
                error(tostring(loadErr))
            end
            chunk(basePath)
        else
            dofile(fullPath)
        end
    end)

    local elapsedMs = math.max(0, (Now() - startedAt) * 1000.0)
    if not ok then
        BootInfo("Failed to load '" .. tostring(relativePath) .. "': " .. tostring(err))
        BootDebug(string.format("safeLoad fail path='%s' ms=%.2f", tostring(relativePath), elapsedMs))
        return false
    end

    BootInfo(string.format("Loaded '%s' (%.2fms)", tostring(relativePath), elapsedMs))
    return true
end

local isClient = CLIENT == true
local isServerAuthority = SERVER == true
local isMultiplayer = true
pcall(function()
    isMultiplayer = Game.IsMultiplayer == true
end)
if not isServerAuthority then
    isServerAuthority = isClient and (not isMultiplayer)
end

BootInfo(string.format(
    "Init start basePath='%s' CLIENT=%s SERVER=%s multiplayer=%s serverAuthority=%s",
    tostring(basePath),
    tostring(isClient),
    tostring(SERVER == true),
    tostring(isMultiplayer),
    tostring(isServerAuthority)))

if isServerAuthority then
    safeLoad("Lua/dbiotest/server_terminal_take.lua")
else
    BootDebug("Skip server_terminal_take.lua because server authority is false.")
end

if isClient then
    safeLoad("Lua/dbiotest/client_terminal_ui.lua")
else
    BootDebug("Skip client_terminal_ui.lua because CLIENT is false.")
end

BootInfo("Init completed.")
FlushLuaFileLog(true)
