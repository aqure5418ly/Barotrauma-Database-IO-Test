-- luacheck: ignore 111

--[[--
Class providing util functions for interacting with the Common Language Runtime.
]]
-- @code LuaUserData
-- @pragma nostrip

local LuaUserData = {}

--- Checks if a CLR type is registered, returns the type descriptor if it exists, otherwise returns nil.
-- @realm shared 
function LuaUserData.IsRegistered(typeName) end

--- Registers a CLR type to be able to be interacted with Lua, returns a type descriptor. Automatically inserts the descriptor into a global table called Descriptors.
-- @realm shared 
function LuaUserData.RegisterType(typeName) end

--- Returns the static reference to the reference, allowing you to call static methods and access static fields in that type. Set second argument to true if you want to also add a call constructor to the type, which allows you to create a new instance of it.
-- @realm shared 
function LuaUserData.CreateStatic(typeName, addCallConstructor) end

--- Unregisters a CLR type.
-- @realm shared 
function LuaUserData.UnregisterType(typeName) end

--- Checks if the provided object is the target type.
-- @realm shared
-- @usage 
-- local unknown = ...
-- if LuaUserData.IsTargetType(unknown, "Barotrauma.Character") then
--     print("This is a character!")
-- end
function LuaUserData.IsTargetType(object, typeName) end

--- Returns the type name of the provided object.
-- @realm shared
function LuaUserData.TypeOf(object) end

--- Returns a table with the assigned enum values from CLR.
-- @realm shared
function LuaUserData.CreateEnumTable(typeName) end

--- Makes a non-public field accessible on a specific type.
-- @usage 
-- LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.Item"], "activeSprite")
--
-- local someItem = Item.ItemList
-- someItem.activeSprite = Sprite("LocalMods/Something/someSprite.png")
-- @realm shared
function LuaUserData.MakeFieldAccessible(descriptor, fieldName) end

--- Makes a non-public method accessible on a specific type.
-- @realm shared
function LuaUserData.MakeMethodAccessible(descriptor, fieldName, typeArgs) end

--- Makes a non-public property accessible on a specific type.
-- @realm shared
function LuaUserData.MakePropertyAccessible(descriptor, propertyName) end