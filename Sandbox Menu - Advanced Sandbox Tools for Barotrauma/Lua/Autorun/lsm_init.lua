SandboxMenu = SandboxMenu or {}
SandboxMenu.NextButtonXOffset = 25

if SERVER then
    require("lsm.item_menu.give_items_sv")
    require("lsm.affliction_menu.affliction_menu_sv")
else
    require("lsm.lsm_localization")
    require("lsm.base_cl")
    require("lsm.item_menu.item_menu_cl")
    require("lsm.entity_menu.entity_menu")
    require("lsm.affliction_menu.affliction_menu_cl")
end