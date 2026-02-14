-- vim:ft=lua

file = {
  "lua"
}

module_file = {}

dir = "build"
project = "LuaForBarotrauma"
title = "LuaForBarotrauma Documentation"

no_space_before_args = true
style = "css"
template = "templates"
format = "markdown"
ignore = true
topics = "manual"
use_markdown_titles = true
kind_names = {
  topic = "Manual",
  module = "Classes"
}
merge = true
sort = true
sort_modules = true

simple_args_string = true -- we show optionals/defaults outside of the display name
strip_metamethod_prefix = true -- remove the name of the table when displaying metamethod names
no_viewed_topic_at_top = true -- don't put the currently viewed topic at the top
use_new_templates = true -- new templating system
pretty_urls = true -- avoid showing .html in urls
pretty_topic_names = true -- strips extension from manual filenames, this does not check filename collisions

custom_tags = {
  {"realm", hidden = true},
  {"deprecated", hidden = true},
  {"internal", hidden = true}
}

custom_display_name_handler = function(item, default_handler)
  if (item.type == "function" and item.module) then
    if (item.module.type == "classmod" or item.module.type == "panel") then
      return item.module.mod_name .. ":" .. default_handler(item)
    elseif (item.module.type == "hooks") then
      return item.module.mod_name:upper() .. ":" .. default_handler(item)
    end
  end

  return default_handler(item)
end

new_type("code", "Code", true)
new_type("enum", "Enum", true)

tparam_alias("Client", "client")
tparam_alias("Character", "character")
tparam_alias("Submarine", "submarine")
tparam_alias("Item", "item")
tparam_alias("ItemPrefab", "ItemPrefab")
tparam_alias("Vector2", "Vector2")
tparam_alias("Vector3", "Vector3")
tparam_alias("Vector4", "Vector4")
tparam_alias("CauseOfDeath", "CauseOfDeath")
tparam_alias("PhysicsBody", "PhysicsBody")
tparam_alias("JobPrefab", "JobPrefab")
tparam_alias("Job", "Job")
tparam_alias("Inventory", "Inventory")
tparam_alias("CharacterInventory", "CharacterInventory")
tparam_alias("ItemInventory", "ItemInventory")
tparam_alias("Camera", "Camera")
tparam_alias("Enumerable", "Enumerable")
tparam_alias("Identifier", "Identifier")
tparam_alias("CharacterHealth", "CharacterHealth")


tparam_alias("string", "string")
tparam_alias("bool", "boolean")
tparam_alias("function", "function")
tparam_alias("tab", "table")
