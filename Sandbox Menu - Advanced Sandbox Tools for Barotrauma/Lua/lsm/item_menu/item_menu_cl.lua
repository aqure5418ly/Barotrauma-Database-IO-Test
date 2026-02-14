-- Спавн предметов
SandboxMenu.ItemSpawn = SandboxMenu.ItemSpawn or {}
SandboxMenu.ItemSpawn.ItemList = {}
SandboxMenu.ItemSpawn.SelectedCategory = -1
SandboxMenu.ItemSpawn.SelectedQuality = 0
SandboxMenu.ItemSpawn.SelectedCount = 1
SandboxMenu.ItemSpawn.ItemCount = 1
SandboxMenu.ItemSpawn.SelectedMod = -1 
SandboxMenu.ItemSpawn.ItemName = ""
SandboxMenu.ItemSpawn.SearchID = 0

SandboxMenu.ItemSpawn.CachedSprites = {}

-- Режим размещения предметов в мире
SandboxMenu.ItemSpawn.PlaceMode = false
SandboxMenu.ItemSpawn.SelectedItemForPlace = nil

-- Режим многократного размещения
SandboxMenu.ItemSpawn.MultiPlaceMode = false

-- Локализация через LSM (lsm_localization.lua)

-- === ФУНКЦИИ ДЛЯ РЕЖИМА РАЗМЕЩЕНИЯ ===

-- Найти персонажа под курсором
local function FindCharacterAtCursor()
    local cam = Screen.Selected.Cam
    if not cam then return nil end
    
    local cursorWorldPos = cam.ScreenToWorld(PlayerInput.MousePosition)
    local closestCharacter = nil
    local closestDist = 150  -- Максимальная дистанция для выбора (в пикселях)
    
    for character in Character.CharacterList do
        if character and not character.Removed and character.AnimController then
            local charPos = character.WorldPosition
            local dist = Vector2.Distance(cursorWorldPos, charPos)
            
            if dist < closestDist then
                closestDist = dist
                closestCharacter = character
            end
        end
    end
    
    return closestCharacter
end

-- Проверить, можно ли предмет держать в инвентаре
local function CanItemBeHeld(itemPrefab)
    if not itemPrefab then return false end

    -- Проверяем категорию - Structure, Decorative, Machine обычно нельзя держать
    local category = itemPrefab.Category

    -- Эти категории обычно содержат пропы и механизмы
    local nonHoldableCategories = {
        [1] = true,   -- Structure
        [2] = true,   -- Decorative
        [4] = true,   -- Machine
    }

    -- Проверяем по категории
    if nonHoldableCategories[category] then
        return false
    end

    return true
end

-- Кэш для поиска контейнеров (обновляется каждый кадр)
local containerCache = {}
local cacheFrame = 0

-- Найти контейнер под курсором
local function FindContainerAtCursor()
    local cam = Screen.Selected.Cam
    if not cam then return nil end

    local cursorWorldPos = cam.ScreenToWorld(PlayerInput.MousePosition)
    local currentFrame = math.floor(Timer.Time * 60)

    -- Обновляем кэш не чаще раза в кадр для производительности
    if cacheFrame ~= currentFrame then
        containerCache = {}
        cacheFrame = currentFrame

        -- Ограничиваем поиск разумным радиусом (меньше чем у персонажей)
        local searchRadius = 100  -- пикселей

        -- Перебираем EntityGrid (с защитой от ошибок API)
        local success, entityGrids = pcall(function() return Hull.EntityGrids end)
        if not success or not entityGrids then
            return nil
        end

        for grid in entityGrids do
            -- Проверяем, что grid существует и доступен
            local gridSuccess, entities = pcall(function() return grid.GetEntities(cursorWorldPos) end)
            if gridSuccess and entities then
                for entity in entities do
                    if entity and entity.GetComponent then
                        -- Проверяем расстояние до курсора
                        local dist = Vector2.Distance(entity.WorldPosition, cursorWorldPos)
                        if dist <= searchRadius then
                            -- Проверяем, есть ли ItemContainer компонент
                            local containerSuccess, container = pcall(function() return entity.GetComponent("ItemContainer") end)
                            if containerSuccess and container and container.Inventory then
                                -- Проверяем, что контейнер имеет место и доступен
                                local hasSpace = false
                                pcall(function()
                                    if container.Inventory.Capacity > 0 then
                                        -- Проверяем, есть ли свободные слоты
                                        for i = 0, container.Inventory.Capacity - 1 do
                                            if not container.Inventory.GetItemAt(i) then
                                                hasSpace = true
                                                break
                                            end
                                        end
                                    end
                                end)

                                if hasSpace then
                                    table.insert(containerCache, {
                                        item = entity,
                                        container = container,
                                        distance = dist
                                    })
                                end
                            end
                        end
                    end
                end
            end
        end

        -- Сортируем по расстоянию (ближайшие первыми)
        table.sort(containerCache, function(a, b) return a.distance < b.distance end)
    end

    -- Возвращаем ближайший контейнер
    return containerCache[1]
end

-- Остановить режим размещения
local function StopPlaceMode()
    SandboxMenu.ItemSpawn.PlaceMode = false
    SandboxMenu.ItemSpawn.SelectedItemForPlace = nil
    Hook.Remove("think", "item_place_wait_click")
    GUI.AddMessage(LSM.ML("place_cancelled"), Color.Yellow)
end

-- Управление режимом многократного размещения
local function SetMultiPlaceMode(enabled)
    if enabled and not SandboxMenu.ItemSpawn.PlaceModeEnabled then
        GUI.AddMessage(LSM.ML("multi_place_requires_place_mode"), Color.Red)
        return false
    end

    SandboxMenu.ItemSpawn.MultiPlaceMode = enabled

    if enabled then
        GUI.AddMessage(LSM.ML("multi_place_enabled"), Color.Cyan)
    else
        -- Сбросить активное размещение если было
        if SandboxMenu.ItemSpawn.PlaceMode then
            SandboxMenu.ItemSpawn.PlaceMode = false
            SandboxMenu.ItemSpawn.SelectedItemForPlace = nil
            Hook.Remove("think", "item_place_wait_click")
        end
        GUI.AddMessage(LSM.ML("multi_place_disabled"), Color.Yellow)
    end

    return true
end

-- Универсальная функция для спавна предметов
-- @param itemData - объект с полями id и name
-- @param target - цель спавна: инвентарь персонажа, контейнер, персонаж, или позиция Vector2
-- @param quality - качество предмета (опционально, по умолчанию SelectedQuality)
-- @param count - количество предметов (опционально, по умолчанию SelectedCount)
local function SpawnItem(itemData, target, quality, count)
    if not itemData then return end
    
    quality = quality or SandboxMenu.ItemSpawn.SelectedQuality
    count = count or SandboxMenu.ItemSpawn.SelectedCount
    
    local itemPrefab = ItemPrefab.GetItemPrefab(itemData.id.ToString())
    if not itemPrefab then return end
    
    -- Определяем тип цели
    local targetType = type(target)
    
    -- Проверяем инвентарь безопасно
    local isInventory = false
    if targetType == "userdata" then
        local success, result = pcall(function() return target.AllItems ~= nil end)
        isInventory = success and result
    end
    
    local isContainer = targetType == "table" and target.type == "container"
    local isCharacter = targetType == "table" and target.type == "character"
    local isPosition = targetType == "table" and target.type == "position"
    
    -- Проверяем Vector2 только если это не инвентарь
    local isVector2 = false
    if targetType == "userdata" and not isInventory then
        local successX, hasX = pcall(function() return target.X ~= nil end)
        local successY, hasY = pcall(function() return target.Y ~= nil end)
        isVector2 = successX and successY and hasX and hasY
    end
    
    if Game.IsMultiplayer then
        -- Мультиплеер
        if isInventory then
            -- Спавн в инвентарь через Networking с fallback на команды
            local netMsg = Networking.Start("SpawnMenuMod_RequestSpawn")
            netMsg.WriteString(itemData.id.ToString())
            netMsg.WriteInt16(quality)
            netMsg.WriteInt16(count)
            Networking.Send(netMsg)
        else
            -- Для остальных целей используем команды (по одному предмету за раз)
            for i = 1, count do
                if isCharacter then
                    -- Передача персонажу через команду
                    local command = 'spawnitem "' .. itemData.id.ToString() .. '" "' .. target.target.Name .. '"'
                    Game.ExecuteCommand(command)
                elseif isContainer then
                    -- Размещение в контейнер через команду
                    local containerItem = target.target.item
                    if containerItem and containerItem.ID then
                        local command = 'spawnitem "' .. itemData.id.ToString() .. '" container ' .. tostring(containerItem.ID)
                        Game.ExecuteCommand(command)
                    else
                        -- Fallback на позицию курсора
                        local command = 'spawnitem "' .. itemData.id.ToString() .. '" cursor'
                        Game.ExecuteCommand(command)
                    end
                elseif isPosition or isVector2 then
                    -- Размещение на позиции через команду
                    local command = 'spawnitem "' .. itemData.id.ToString() .. '" cursor'
                    Game.ExecuteCommand(command)
                end
            end
        end
    else
        -- Одиночная игра
        for i = 1, count do
            if isInventory then
                -- Спавн в инвентарь
                Entity.Spawner.AddItemToSpawnQueue(
                    itemPrefab,
                    target,
                    nil,
                    quality,
                    nil
                )
            elseif isCharacter then
                -- Передача персонажу
                Entity.Spawner.AddItemToSpawnQueue(
                    itemPrefab,
                    target.target.Inventory,
                    nil,
                    quality,
                    nil
                )
            elseif isContainer then
                -- Размещение в контейнер
                Entity.Spawner.AddItemToSpawnQueue(
                    itemPrefab,
                    target.target.container.Inventory,
                    nil,
                    quality,
                    nil
                )
            elseif isPosition or isVector2 then
                -- Размещение на позиции
                local pos = isVector2 and target or nil
                if not pos and isPosition then
                    local cam = Screen.Selected.Cam
                    if cam then
                        pos = cam.ScreenToWorld(PlayerInput.MousePosition)
                    end
                end
                if pos then
                    Entity.Spawner.AddItemToSpawnQueue(
                        itemPrefab,
                        pos,
                        nil,
                        quality,
                        nil
                    )
                end
            end
        end
    end
end

-- Определить цель для размещения предмета
local function GetPlacementTarget(itemData)
    local itemPrefab = ItemPrefab.GetItemPrefab(itemData.id.ToString())
    local canHold = CanItemBeHeld(itemPrefab)

    if not canHold then
        -- Предмет нельзя держать - всегда размещаем на позиции курсора
        return {type = "position"}
    end

    -- Сначала ищем контейнер под курсором
    local containerTarget = FindContainerAtCursor()
    if containerTarget then
        return {type = "container", target = containerTarget}
    end

    -- Затем ищем персонажа
    local characterTarget = FindCharacterAtCursor()
    if characterTarget then
        return {type = "character", target = characterTarget}
    end

    -- Наконец, позиция курсора
    return {type = "position"}
end

-- Разместить предмет в мире или передать персонажу/контейнеру
local function PlaceItem(itemData, placementTarget)
    if not itemData or not placementTarget then return end

    -- Используем универсальную функцию спавна
    SpawnItem(itemData, placementTarget, SandboxMenu.ItemSpawn.SelectedQuality, SandboxMenu.ItemSpawn.SelectedCount)

    local countStr = SandboxMenu.ItemSpawn.SelectedCount > 1 and (SandboxMenu.ItemSpawn.SelectedCount .. "x ") or ""

    -- Сообщения в зависимости от типа цели
    if placementTarget.type == "container" then
        local containerName = placementTarget.target.item.Name or LSM.ML("container")
        GUI.AddMessage(string.format(LSM.ML("placed_in_container"), countStr, itemData.name, containerName), Color.Lime)
        print("[Item Menu] Placed " .. countStr .. itemData.name .. " in container: " .. containerName)
    elseif placementTarget.type == "character" then
        GUI.AddMessage(string.format(LSM.ML("gave_item"), countStr, itemData.name, placementTarget.target.Name), Color.Lime)
        print("[Item Menu] Gave " .. countStr .. itemData.name .. " to " .. placementTarget.target.Name)
    else
        GUI.AddMessage(string.format(LSM.ML("placed_item"), countStr, itemData.name), Color.Lime)
        print("[Item Menu] Placed " .. countStr .. itemData.name .. " at cursor")
    end
end

-- Активировать режим размещения
local function StartPlaceMode(itemData)
    if not itemData then return end

    SandboxMenu.ItemSpawn.PlaceMode = true
    SandboxMenu.ItemSpawn.SelectedItemForPlace = itemData

    -- Закрываем меню
    if SandboxMenu.menuContent and SandboxMenu.menuContent.Parent then
        SandboxMenu.menuContent.Parent.Visible = false
    end

    local itemPrefab = ItemPrefab.GetItemPrefab(itemData.id.ToString())
    local canHold = CanItemBeHeld(itemPrefab)

    -- Разные подсказки в зависимости от режима
    if SandboxMenu.ItemSpawn.MultiPlaceMode then
        GUI.AddMessage(LSM.ML("multi_place_hint"), Color.Cyan)
    elseif canHold then
        GUI.AddMessage(LSM.ML("place_mode_hint_hold"), Color.Cyan)
    else
        GUI.AddMessage(string.format(LSM.ML("place_mode_hint_place"), itemData.name), Color.Cyan)
    end

    Hook.Remove("think", "item_place_wait_click")

    Hook.Add("think", "item_place_wait_click", function()
        if SandboxMenu.ItemSpawn.PlaceMode then
            if PlayerInput.PrimaryMouseButtonClicked() and GUI.MouseOn == nil then
                local placementTarget = GetPlacementTarget(SandboxMenu.ItemSpawn.SelectedItemForPlace)
                PlaceItem(SandboxMenu.ItemSpawn.SelectedItemForPlace, placementTarget)

                -- Сбрасываем состояние только если НЕ многократный режим
                if not SandboxMenu.ItemSpawn.MultiPlaceMode then
                    SandboxMenu.ItemSpawn.PlaceMode = false
                    SandboxMenu.ItemSpawn.SelectedItemForPlace = nil
                    Hook.Remove("think", "item_place_wait_click")
                end

            elseif PlayerInput.SecondaryMouseButtonClicked() then
                StopPlaceMode()
            end
        end
    end)
end

SandboxMenu.ItemSpawn.ModList = {
    [-1] = LSM.ML("item_all_mods")
}
SandboxMenu.ItemSpawn.AllItems = ItemPrefab.Prefabs.Keys

SandboxMenu.ItemSpawn.itemCategoryEnums = {}
for _, idx in ipairs(LSM.ItemCategoryOrder) do
    local key = LSM.ItemCategoryKeys[idx]
    if key then SandboxMenu.ItemSpawn.itemCategoryEnums[idx] = LSM.ML(key) end
end


local function AllItemsWithCorrectName()
    local tbl = {}
    local searchText = string.lower(SandboxMenu.ItemSpawn.ItemName)
    
    for k, v in pairs(SandboxMenu.ItemSpawn.ItemList) do
        if string.find(string.lower(v.name), searchText) or
           (v.description and string.find(string.lower(v.description), searchText)) or
           string.find(string.lower(v.id.ToString()), searchText) then
            table.insert(tbl, v)
        end
    end

    return tbl
end

-- FILTRATION FUNCTIONS
local function AllItemsWithCorrectCategory(unfiltredTable)
    local tbl = {}
    
    for k, v in pairs(unfiltredTable) do
        if v.category == SandboxMenu.ItemSpawn.SelectedCategory then    
            table.insert(tbl, v)
        end
    end

    return tbl
end

local function AllItemsWithSelectedMod(unfiltredTable)
    local tbl = {}
    
    for k, v in pairs(unfiltredTable) do
        if SandboxMenu.TableFindKeyByValue(SandboxMenu.ItemSpawn.ModList, v.mod) == SandboxMenu.ItemSpawn.SelectedMod then
            table.insert(tbl, v)
        end
    end

    return tbl
end

local function SortTableAlphabetically(unfiltredTable)

    local function sortObj(objA, objB)
        return objA.name < objB.name
    end

    table.sort(unfiltredTable, sortObj)

end

function SandboxMenu.ItemSpawn.EnumItemCategoryToInteger(category) -- Convert enumerator id to int id
    if (category == 1) then
        return 1
    end
    return math.floor(math.log(category, 2) + 1)
end


-- populate ModList/ItemList
for k, v in ipairs(SandboxMenu.ItemSpawn.AllItems) do
    if (ItemPrefab.GetItemPrefab(v)) then

        local itemPrefab = ItemPrefab.GetItemPrefab(v)


        table.insert(SandboxMenu.ItemSpawn.ItemList, {
            id = v,
            name = itemPrefab.ToString(),
            description = itemPrefab.Description.ToString(),
            inventoryIcon = itemPrefab.InventoryIcon or itemPrefab.Sprite,
            category = SandboxMenu.ItemSpawn.EnumItemCategoryToInteger(itemPrefab.category),
            mod = itemPrefab.ContentPackage.Name,
        } )

        if not SandboxMenu.TableHasValue(SandboxMenu.ItemSpawn.ModList, itemPrefab.ContentPackage.Name) then -- Adding mod in modlist if we didn't added it befor
            table.insert(SandboxMenu.ItemSpawn.ModList, itemPrefab.ContentPackage.Name)
        end
    end
end


local itemTab = GUI.Frame(GUI.RectTransform(Vector2(0.995, 0.935), SandboxMenu.menuContent.RectTransform, GUI.Anchor.TopCenter))
itemTab.RectTransform.AbsoluteOffset = Point(0, 65)
itemTab.Visible = false


-- Item List
local itemSelectMenu = GUI.ListBox(GUI.RectTransform(Vector2(0.95, 0.9), itemTab.RectTransform, GUI.Anchor.BottomCenter)) 
itemSelectMenu.RectTransform.AbsoluteOffset = Point(0, 25)

local maxItemsInRow = 12

function SandboxMenu.ItemSpawn.DrawItems()

    local processedItemsList = AllItemsWithCorrectName() -- Seeking by name 

    if SandboxMenu.ItemSpawn.SelectedCategory ~= -1 then -- if selected not All items
        processedItemsList = AllItemsWithCorrectCategory(processedItemsList) -- Seeking by category
    end
    if SandboxMenu.ItemSpawn.SelectedMod ~= -1 then -- if selected not All Mods
        processedItemsList = AllItemsWithSelectedMod(processedItemsList) -- Seeking by category
    end

    SortTableAlphabetically(processedItemsList)

    local itemCount = math.ceil(#processedItemsList / maxItemsInRow)

    itemSelectMenu.ClearChildren() 

    local itemsLines = {}

    for i = 1, itemCount do
        itemsLines[i] = GUI.ListBox(GUI.RectTransform(Vector2(1, 0.1), itemSelectMenu.Content.RectTransform, GUI.Anchor.TopLeft), true)
    end


    local function DrawNextItem(k, id)
        if (id ~= SandboxMenu.ItemSpawn.SearchID) then return end

        local v = processedItemsList[k]
        if (not v) then return end

        local currentItemLine = math.floor(k / maxItemsInRow)
        if (k % maxItemsInRow ~= 0) then
            currentItemLine = currentItemLine + 1
        end

        local iconSize = 64 / GameSettings.CurrentConfig.Graphics.HUDScale -- remove the 'HUD Scaling' setting factor, its too much
        local imageItemFrame = GUI.Button(GUI.RectTransform(Point(iconSize*GUI.xScale, iconSize*GUI.yScale), itemsLines[currentItemLine].Content.RectTransform))

        imageItemFrame.Color = Color(0,0,0,0)
        imageItemFrame.HoverColor = Color(0,0,0,0)
        imageItemFrame.PressedColor = Color(0,0,0,0)
        imageItemFrame.SelectedColor = Color(0,0,0,0)

        imageItemFrame.OnClicked = function()
            -- Если режим Place Mode включён - запускаем размещение
            if SandboxMenu.ItemSpawn.PlaceModeEnabled then
                StartPlaceMode(v)
                return true
            end
            
            -- Обычный клик = спавн в инвентарь
            local targetInventory = nil
            for _, character in pairs(Character.CharacterList) do
                if character.IsLocalPlayer then
                    targetInventory = character.Inventory
                    break
                end
            end
            
            if targetInventory then
                SpawnItem(v, targetInventory, SandboxMenu.ItemSpawn.SelectedQuality, SandboxMenu.ItemSpawn.SelectedCount)
            end
            
            return true
        end

        imageItemFrame.RectTransform.MinSize = Point(0, iconSize*GUI.yScale)
        local sprite = v.inventoryIcon
        local image = GUI.Image(GUI.RectTransform(Vector2(0.95, 0.95), imageItemFrame.RectTransform, GUI.Anchor.Center), sprite)
        image.ToolTip = RichString.Rich(ItemPrefab.GetItemPrefab(v.id.ToString()).GetTooltip().ToString() .. "\n‖color:gui.white‖(identifier: " .. v.id.ToString() ..")‖end‖" );
        
        Timer.NextFrame(function()
            DrawNextItem(k+1, id) 
        end)
   
    end

    SandboxMenu.ItemSpawn.SearchID = SandboxMenu.ItemSpawn.SearchID + 1
    DrawNextItem(1, SandboxMenu.ItemSpawn.SearchID)
end


-- Item categories drop menu
local itemCategory = GUI.DropDown(GUI.RectTransform(Vector2(0.15, 0.05), itemTab.RectTransform, GUI.Anchor.TopLeft), LSM.ML("item_cat_all"), 6, nil, false)
itemCategory.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 15)

for _, idx in ipairs(LSM.ItemCategoryOrder) do
    local label = SandboxMenu.ItemSpawn.itemCategoryEnums[idx]
    if label then itemCategory.AddItem(label, idx) end
end

itemCategory.OnSelected = function (guiComponent, object) -- Selection Function
    SandboxMenu.ItemSpawn.SelectedCategory = object
    SandboxMenu.ItemSpawn.DrawItems()
end

-- Mods drop menu
local modSelection = GUI.DropDown(GUI.RectTransform(Vector2(0.15, 0.05), itemTab.RectTransform, GUI.Anchor.TopLeft), LSM.ML("item_all_mods"), 6, nil, false)
modSelection.RectTransform.AbsoluteOffset = Point(160 * GUI.xScale, 15)

for k, v in pairs(SandboxMenu.ItemSpawn.ModList) do -- Adding all categories
    modSelection.AddItem(v, k)
end

modSelection.OnSelected = function (guiComponent, object) -- Selection Function
    SandboxMenu.ItemSpawn.SelectedMod = object
    SandboxMenu.ItemSpawn.DrawItems()
end

local itemQuality = GUI.DropDown(GUI.RectTransform(Vector2(0.15, 0.05), itemTab.RectTransform, GUI.Anchor.TopLeft), LSM.ML("item_quality_normal"), 4, nil, false)
itemQuality.RectTransform.AbsoluteOffset = Point(293 * GUI.xScale, 15)
itemQuality.AddItem(LSM.ML("item_quality_normal"), 0)
itemQuality.AddItem(LSM.ML("item_quality_good"), 1)
itemQuality.AddItem(LSM.ML("item_quality_excellent"), 2)
itemQuality.AddItem(LSM.ML("item_quality_masterwork"), 3)

itemQuality.OnSelected = function (s, object)
    SandboxMenu.ItemSpawn.SelectedQuality = object
end

local countText = GUI.TextBlock(GUI.RectTransform(Vector2(0.35, 0.1), itemTab.RectTransform), "1", nil, nil, GUI.Alignment.Center)
countText.RectTransform.AbsoluteOffset = Point(380 * GUI.xScale, 10)

local selectedCount = GUI.ScrollBar(GUI.RectTransform(Vector2(0.22, 0.1), itemTab.RectTransform), 0.1, nil, "GUISlider")
selectedCount.RectTransform.AbsoluteOffset = Point(440 * GUI.xScale, 15)
selectedCount.Range = Vector2(1, 62)
selectedCount.BarScrollValue = 1
selectedCount.OnMoved = function ()
    SandboxMenu.ItemSpawn.SelectedCount = math.floor(selectedCount.BarScrollValue)
    countText.Text = tostring(SandboxMenu.ItemSpawn.SelectedCount)
end

-- Item name textbox
local findItemByNameTextBox = GUI.TextBox(GUI.RectTransform(Vector2(0.25, 0.2), itemTab.RectTransform, GUI.Anchor.TopRight), SandboxMenu.ItemSpawn.ItemName) -- Find item by name
findItemByNameTextBox.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 15)

findItemByNameTextBox.OnTextChangedDelegate = function(textBox)
    SandboxMenu.ItemSpawn.ItemName = textBox.Text
    SandboxMenu.ItemSpawn.DrawItems()
end

-- === ЧЕКБОКС PLACE MODE ===
-- Чекбокс для переключения режима размещения предметов
local placeCheckbox = GUI.TickBox(GUI.RectTransform(Vector2(0.12, 0.05), itemTab.RectTransform, GUI.Anchor.TopRight), LSM.ML("place_mode"))
placeCheckbox.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 50)  -- Под полем поиска
placeCheckbox.ToolTip = LSM.ML("place_mode_hint_hold")
placeCheckbox.Selected = false

-- Состояние режима размещения
SandboxMenu.ItemSpawn.PlaceModeEnabled = false

placeCheckbox.OnSelected = function(tickbox)
    SandboxMenu.ItemSpawn.PlaceModeEnabled = tickbox.Selected

    -- Если отключаем обычный режим, то и многократный тоже отключаем
    if not tickbox.Selected and SandboxMenu.ItemSpawn.MultiPlaceMode then
        SetMultiPlaceMode(false)
    end

    return true
end

-- Кнопка MULTI PLACE - переключение режима многократного размещения
local multiPlaceButton = GUI.Button(GUI.RectTransform(Vector2(0.12, 0.05), itemTab.RectTransform, GUI.Anchor.TopRight), LSM.ML("multi_place_mode"), GUI.Alignment.Center, "GUIButtonSmall")
multiPlaceButton.RectTransform.AbsoluteOffset = Point(150 * GUI.xScale, 50)  -- Правее чекбокса
multiPlaceButton.Color = Color(100, 100, 150)  -- Серый по умолчанию
multiPlaceButton.HoverColor = Color(120, 120, 170)
multiPlaceButton.TextColor = Color.White

multiPlaceButton.OnClicked = function()
    local newState = not SandboxMenu.ItemSpawn.MultiPlaceMode
    if SetMultiPlaceMode(newState) then
        -- Обновляем цвет кнопки в зависимости от состояния
        if newState then
            multiPlaceButton.Color = Color(150, 100, 200)  -- Фиолетовый при активном режиме
            multiPlaceButton.HoverColor = Color(170, 120, 220)
        else
            multiPlaceButton.Color = Color(100, 100, 150)  -- Серый при неактивном
            multiPlaceButton.HoverColor = Color(120, 120, 170)
        end
    end
    return true
end

-- Creating button
local itemButton = GUI.Button(GUI.RectTransform(Vector2(0.1, 0.15), SandboxMenu.menuContent.RectTransform, GUI.Anchor.TopLeft), LSM.ML("items"), GUI.Alignment.Center, "GUIButtonSmall")
itemButton.RectTransform.AbsoluteOffset = Point(SandboxMenu.NextButtonXOffset, 25)
SandboxMenu.NextButtonXOffset = SandboxMenu.NextButtonXOffset + 130
itemButton.OnClicked = function ()
    -- Сброс активного режима размещения (если был в процессе выбора)
    SandboxMenu.ItemSpawn.PlaceMode = false
    SandboxMenu.ItemSpawn.SelectedItemForPlace = nil
    Hook.Remove("think", "item_place_wait_click")

    -- Синхронизируем чекбокс с состоянием
    placeCheckbox.Selected = SandboxMenu.ItemSpawn.PlaceModeEnabled

    -- Синхронизируем кнопку MultiPlaceMode
    if SandboxMenu.ItemSpawn.MultiPlaceMode then
        multiPlaceButton.Color = Color(150, 100, 200)
        multiPlaceButton.HoverColor = Color(170, 120, 220)
    else
        multiPlaceButton.Color = Color(100, 100, 150)
        multiPlaceButton.HoverColor = Color(120, 120, 170)
    end

    SandboxMenu.HideAllTabs()
    itemTab.Visible = true
    SandboxMenu.ItemSpawn.DrawItems()
end

SandboxMenu.RegisterTab(itemTab)