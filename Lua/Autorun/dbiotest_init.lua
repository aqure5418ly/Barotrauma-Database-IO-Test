DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Path = table.pack(...)[1]
DatabaseIOTestLua.Bootstrap = DatabaseIOTestLua.Bootstrap or {}
DatabaseIOTestLua.FileLog = DatabaseIOTestLua.FileLog or {
    initialized = false,
    path = "",
    cache = "",
    writeFailed = false,
    lastError = ""
}

local ForceLuaDebugLog = true
local LoggerBridgeMissingPrinted = false
local MaxLuaLogCacheChars = 400000
local KeepLuaLogCacheTailChars = 280000

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

local function TryWriteBootstrap(level, line)
    local text = tostring(line or "")
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

local function AppendLuaFileLogLine(line)
    local state = DatabaseIOTestLua.FileLog
    if not EnsureLuaFileLogger(DatabaseIOTestLua.Path) then
        return false
    end

    local text = tostring(line or "")
    local nextCache = tostring(state.cache or "") .. text .. "\n"
    if string.len(nextCache) > MaxLuaLogCacheChars then
        nextCache = string.sub(nextCache, -KeepLuaLogCacheTailChars)
    end

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
        return true
    end

    state.writeFailed = true
    state.lastError = "File.Write failed for both primary and fallback paths."
    return false
end

DatabaseIOTestLua.AppendLuaFileLogLine = AppendLuaFileLogLine

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
