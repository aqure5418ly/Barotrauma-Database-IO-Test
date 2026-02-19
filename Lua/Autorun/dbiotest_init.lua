DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Path = table.pack(...)[1]

local basePath = tostring(DatabaseIOTestLua.Path or "")
if basePath == "" then
    print("[Database IO Test][Lua] Missing mod path, lua bootstrap aborted.")
    return
end

local function safeLoad(relativePath)
    local fullPath = basePath .. "/" .. relativePath
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
    if not ok then
        print("[Database IO Test][Lua] Failed to load " .. tostring(relativePath) .. ": " .. tostring(err))
    end
end

local isClient = CLIENT == true
local isServerAuthority = SERVER == true
if not isServerAuthority then
    local isMultiplayer = true
    pcall(function()
        isMultiplayer = Game.IsMultiplayer == true
    end)
    isServerAuthority = isClient and (not isMultiplayer)
end

if isServerAuthority then
    safeLoad("Lua/dbiotest/server_terminal_take.lua")
end

if isClient then
    safeLoad("Lua/dbiotest/client_terminal_ui.lua")
end
