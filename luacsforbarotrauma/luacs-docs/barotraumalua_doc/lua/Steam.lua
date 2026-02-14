-- luacheck: ignore 111

--[[--
Provides steam integration.
]]
-- @code Steam
-- @pragma nostrip

local Steam = {}


--- Downloads an item from workshop and places it in a folder.
-- @tparam string id
-- @tparam string destinationFolder
-- @tparam function callback
-- @realm shared
-- @usage
-- Steam.DownloadWorkshopItem("2805065898", "LocalMods/MyMod/Temp/Download", function(workshopItem)
--    if workshopItem == nil then print("failed to download workshop item") return end
--
--    print(workshopItem.Title .. " has been successfully downloaded.")
-- end)
function Steam.DownloadWorkshopItem(id, destinationFolder, callback) end

--- Gets information about a workshop item.
-- @tparam string id
-- @tparam function callback
-- @realm shared
function Steam.GetWorkshopItem(id, callback) end

--- Callback is called with the list of all workshop items inside the specified collection.
-- @tparam string id
-- @tparam function callback
-- @realm shared
function Steam.GetWorkshopCollection(id, callback) end
