SandboxMenu.MenuModules = {}

function SandboxMenu.TableFindKeyByValue(tbl, key)
    for k, v in pairs(tbl) do
        if v == key then
            return k
        end
    end

    return -1
end

function SandboxMenu.TableHasValue(tbl, value)

    for k, v in pairs(tbl) do
        if v == value then
            return true
        end
    end

    return false
end 

local GameMain = LuaUserData.CreateStatic('Barotrauma.GameMain')
local extraUIWidth = GUI.UIWidth < GameMain.GraphicsWidth and (GameMain.GraphicsWidth - GUI.UIWidth) or 0 -- ultrawide's have a smaller UIWidth than the actual screen width

-- Main sandbox frame
SandboxMenu.frame = GUI.Frame(GUI.RectTransform(Vector2(1, 1)), nil)
SandboxMenu.frame.CanBeFocused = false

-- menu frame
local menu = GUI.Frame(GUI.RectTransform(Vector2(1 + 0.2/GUI.xScale, 1 + 0.3), SandboxMenu.frame.RectTransform, GUI.Anchor.Center), nil)
menu.CanBeFocused = true  
menu.Visible = false 

SandboxMenu.menuContent = GUI.Frame(GUI.RectTransform(Vector2(0.4, 0.6), menu.RectTransform, GUI.Anchor.Center))

-- make draggable
local frameHandle = GUI.DragHandle(GUI.RectTransform(Vector2(1,1), SandboxMenu.menuContent.RectTransform, GUI.Anchor.Center), SandboxMenu.menuContent.RectTransform, nil)

-- === GRID LAYOUT SYSTEM FOR MODULE BUTTONS ===
-- Система автоматического размещения кнопок модулей в сетке
SandboxMenu.ModuleButtons = {}
SandboxMenu.MaxButtonsPerRow = 6
SandboxMenu.ButtonWidth = 135
SandboxMenu.ButtonHeight = 10
SandboxMenu.ButtonSpacing = 5

-- Функция для позиционирования кнопки модуля в сетке
function SandboxMenu.PositionModuleButton(button, index)
    if not button or not button.RectTransform then
        return
    end
    
    -- Пересчитываем extraUIWidth для текущего разрешения
    local currentExtraUIWidth = GUI.UIWidth < GameMain.GraphicsWidth and (GameMain.GraphicsWidth - GUI.UIWidth) or 0
    
    -- Вычисляем строку и столбец (индекс начинается с 1)
    local row = math.floor((index - 1) / SandboxMenu.MaxButtonsPerRow)
    local col = (index - 1) % SandboxMenu.MaxButtonsPerRow
    
    -- Вычисляем смещения с учетом масштабирования GUI
    local xOffset = col * (SandboxMenu.ButtonWidth + SandboxMenu.ButtonSpacing) * GUI.xScale
    local yOffset = row * (SandboxMenu.ButtonHeight + SandboxMenu.ButtonSpacing)
    
    -- Базовое смещение от правого нижнего угла
    local baseXOffset = 24 * GUI.xScale - currentExtraUIWidth
    
    -- Применяем позиционирование
    button.RectTransform.AbsoluteOffset = Point(
        baseXOffset - xOffset,
        -yOffset
    )
end

-- Функция для регистрации кнопки модуля в системе grid layout
function SandboxMenu.RegisterModuleButton(button)
    if not button then
        return
    end
    
    table.insert(SandboxMenu.ModuleButtons, button)
    local index = #SandboxMenu.ModuleButtons
    SandboxMenu.PositionModuleButton(button, index)
end

-- Функция для пересчета позиций всех кнопок (например, при изменении разрешения)
function SandboxMenu.RepositionAllModuleButtons()
    for i, button in ipairs(SandboxMenu.ModuleButtons) do
        SandboxMenu.PositionModuleButton(button, i)
    end
end

-- Menu open button (регистрируем как первую кнопку в grid системе)
local button = GUI.Button(GUI.RectTransform(Point(135*GUI.xScale, 10), SandboxMenu.frame.RectTransform, GUI.Anchor.BottomRight), "Sandbox menu", GUI.Alignment.Center, "GUIButtonSmall")
button.OnClicked = function ()
    menu.Visible = not menu.Visible
end
SandboxMenu.RegisterModuleButton(button)


Hook.Patch("Barotrauma.GameScreen", "AddToGUIUpdateList", function()
    SandboxMenu.frame.AddToGUIUpdateList(false, 1)  -- Добавлен параметр order = 2  
end)
    
Hook.Patch("Barotrauma.SubEditorScreen", "AddToGUIUpdateList", function()
    SandboxMenu.frame.AddToGUIUpdateList(false, 1)  -- Добавлен параметр order = 2  
end)
Hook.Patch("Barotrauma.GUI", "TogglePauseMenu", {}, function(instance, ptable)
    if not GUI.PauseMenuOpen and menu.Visible then
        menu.Visible = false
        ptable.PreventExecution = true
    end
end, Hook.HookMethodType.Before)

function SandboxMenu.RegisterTab(tab)
    table.insert(SandboxMenu.MenuModules, tab)
end

function SandboxMenu.HideAllTabs()
    for k, v in ipairs(SandboxMenu.MenuModules) do
        v.Visible = false        
    end
end

Hook.Patch("Barotrauma.NetLobbyScreen", "AddToGUIUpdateList", function()  
    SandboxMenu.frame.AddToGUIUpdateList(false, 1)

    -- -- Найти все dropdown'ы в меню и установить правильный порядок  
    -- local dropdowns = SandboxMenu.menuContent.GetAllChildren("GUIDropDown")  
    -- for _, dropdown in ipairs(dropdowns) do  
    --     dropdown.AddToGUIUpdateList(false, 2)  
    --     dropdown.ListBox.AddToGUIUpdateList(false, 2)  
    -- end  
end)
