-- luacheck: ignore 111

--[[--
Barotrauma ItemPrefab class with some additional functions and fields

Barotrauma source code: [ItemPrefab.cs](https://github.com/evilfactory/LuaCsForBarotrauma/blob/master/Barotrauma/BarotraumaShared/SharedSource/Items/ItemPrefab.cs)
]]
-- @code ItemPrefab
-- @pragma nostrip

local ItemPrefab = {}

--- Add ItemPrefab to spawn queue and spawns it at the specified position
-- @tparam ItemPrefab itemPrefab
-- @tparam Vector2 position
-- @tparam function spawned
-- @realm server 
function ItemPrefab.AddToSpawnQueue(itemPrefab, position, spawned) end

--- Add ItemPrefab to spawn queue and spawns it inside the specified inventory
-- @tparam ItemPrefab itemPrefab
-- @tparam Inventory inventory
-- @tparam function spawned
-- @realm server 
function ItemPrefab.AddToSpawnQueue(itemPrefab, inventory, spawned) end

--- Get a item prefab via name or id
-- @tparam string itemNameOrId
-- @treturn ItemPrefab
-- @realm shared 
function ItemPrefab.GetItemPrefab(itemNameOrId) end

---
-- Identifier, the identifier of the prefab.
-- @realm shared
-- @string Identifier
