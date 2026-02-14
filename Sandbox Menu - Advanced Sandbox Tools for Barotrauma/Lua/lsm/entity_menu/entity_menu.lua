-- Entity Spawn Menu (Адаптация на основе item_menu_cl.lua)
-- Спавн живых сущностей (NPC, монстры)

SandboxMenu.EntitySpawn = SandboxMenu.EntitySpawn or {}

-- Строки мода через единый модуль LSM (lsm_localization.lua)
local function L(key, fallback)
    local result = nil
    pcall(function()
        local localized = TextManager.Get(key)
        if localized and localized.Value and localized.Value ~= "" then
            result = localized.Value
        end
    end)
    return result or fallback or key
end

SandboxMenu.EntitySpawn.EntityList = {}
SandboxMenu.EntitySpawn.SelectedGroup = nil  -- Фильтр по группе (из API)
SandboxMenu.EntitySpawn.SelectedTeam = 0     -- CharacterTeamType: 0=None, 1=Team1, 2=Team2, 3=FriendlyNPC
SandboxMenu.EntitySpawn.SelectedCount = 1
SandboxMenu.EntitySpawn.SelectedMod = nil
SandboxMenu.EntitySpawn.EntityName = ""
SandboxMenu.EntitySpawn.SearchID = 0

-- Двухэтапный спавн: состояние
SandboxMenu.EntitySpawn.SelectedEntity = nil
SandboxMenu.EntitySpawn.SpawnMode = false

-- Режим контроля сущностей
SandboxMenu.EntitySpawn.ControlMode = false
SandboxMenu.EntitySpawn.OriginalCharacter = nil  -- Сохраняем ссылку на изначального персонажа

-- Режим многократного спавна
SandboxMenu.EntitySpawn.MultiSpawnMode = false

-- Списки для фильтров (заполняются динамически из API)
SandboxMenu.EntitySpawn.GroupList = {}   -- Список групп из API
SandboxMenu.EntitySpawn.ModList = {}     -- Список модов из API

-- Получаем список всех CharacterPrefab
SandboxMenu.EntitySpawn.AllEntities = CharacterPrefab.Prefabs

-- Команды для CharacterTeamType (единственный словарь - это enum из игры)
SandboxMenu.EntitySpawn.teamEnums = {
    [0] = "None",
    [1] = "Team1 (Coalition)",
    [2] = "Team2 (Separatists)",
    [3] = "FriendlyNPC",
}

-- Простая интуитивная цветовая схема:
-- Зелёный = дружелюбный/человек
-- Синий = питомец/нейтральный  
-- Жёлтый/Оранжевый = средняя угроза
-- Красный = опасный/босс
-- Серый = неизвестно

local groupColors = {
    -- Дружелюбные (зелёные оттенки)
    human = Color(80, 180, 100),
    friendlynpc = Color(100, 200, 120),
    
    -- Питомцы (синие оттенки)
    pet = Color(100, 160, 220),
    

    -- Средняя угроза (оранжевые/жёлтые)
    crawler = Color(220, 160, 60),
    mudraptor = Color(200, 140, 60),
    spineling = Color(200, 120, 80),
    mantis = Color(180, 160, 60),
    latcher = Color(190, 130, 90),
    
    -- Опасные (красные оттенки)
    husk = Color(180, 100, 100),
    hammerhead = Color(200, 80, 80),
    moloch = Color(220, 60, 60),
    endworm = Color(230, 50, 70),
    charybdis = Color(200, 70, 90),
    
    -- По умолчанию (серый)
    default = Color(120, 130, 140),
}

-- Цвета рамок - простая схема по типу
local borderColors = {
    humanoid = Color(80, 180, 80),      -- Зелёная = гуманоид (можно давать работу)
    pet = Color(80, 150, 200),          -- Синяя = питомец
    monster = Color(180, 80, 80),       -- Красная = монстр
    default = Color(100, 100, 120),     -- Серая = неизвестно
}

-- Получить цвет для группы
local function GetGroupColor(group)
    if not group or group == "" then return groupColors.default end
    local groupLower = string.lower(group)
    return groupColors[groupLower] or groupColors.default
end

-- Получить цвет рамки для типа существа
local function GetBorderColor(entityData)
    if entityData.hasCharacterInfo then
        return borderColors.humanoid
    elseif entityData.group and string.lower(entityData.group) == "pet" then
        return borderColors.pet
    else
        return borderColors.monster
    end
end

-- === ФУНКЦИИ КОНТРОЛЯ ПЕРСОНАЖЕЙ ===

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

-- Переключить контроль на персонажа
local function TakeControlOf(character)
    if not character then return false end
    
    -- Сохраняем оригинального персонажа, если ещё не сохранён
    if not SandboxMenu.EntitySpawn.OriginalCharacter and Character.Controlled then
        SandboxMenu.EntitySpawn.OriginalCharacter = Character.Controlled
    end
    
    if Game.IsMultiplayer then
        -- В мультиплеере используем консольную команду (требует админ-права)
        -- Имя оборачиваем в кавычки на случай пробелов
        local characterName = character.Name or tostring(character.ID)
        local command = 'control "' .. characterName .. '"'
        Game.ExecuteCommand(command)
        print("[Entity Menu] Sent control command: " .. command)
        GUI.AddMessage(string.format(LSM.ML("taking_control"), characterName), Color.Lime)
    else
        -- В одиночной игре напрямую меняем контролируемого персонажа
        Character.Controlled = character
        print("[Entity Menu] Now controlling: " .. (character.Name or tostring(character.ID)))
        GUI.AddMessage(string.format(LSM.ML("now_controlling"), character.Name or "entity"), Color.Lime)
    end
    
    return true
end

-- Вернуться в оригинальное тело
local function ReturnToOriginalBody()
    local originalChar = SandboxMenu.EntitySpawn.OriginalCharacter
    
    if originalChar and not originalChar.Removed and not originalChar.IsDead then
        if Game.IsMultiplayer then
            -- Имя оборачиваем в кавычки на случай пробелов
            local characterName = originalChar.Name or tostring(originalChar.ID)
            local command = 'control "' .. characterName .. '"'
            Game.ExecuteCommand(command)
            print("[Entity Menu] Returning to: " .. characterName)
            GUI.AddMessage(string.format(LSM.ML("returning_to"), characterName), Color.Lime)
        else
            Character.Controlled = originalChar
            print("[Entity Menu] Returned to original body")
            GUI.AddMessage(LSM.ML("returned"), Color.Lime)
        end
        return true
    else
        -- Если оригинальный персонаж недоступен, пробуем найти своего клиента
        if Game.IsMultiplayer then
            local myClient = Game.Client
            if myClient and myClient.Character then
                local command = 'control "' .. myClient.Character.Name .. '"'
                Game.ExecuteCommand(command)
                SandboxMenu.EntitySpawn.OriginalCharacter = myClient.Character
                print("[Entity Menu] Returned to client character")
                GUI.AddMessage(LSM.ML("returned"), Color.Lime)
                return true
            end
        end
        GUI.AddMessage(LSM.ML("original_not_available"), Color.Red)
        return false
    end
end

-- Остановить режим контроля
local function StopControlMode()
    SandboxMenu.EntitySpawn.ControlMode = false
    Hook.Remove("think", "entity_control_wait_click")
    GUI.AddMessage(LSM.ML("control_cancelled"), Color.Yellow)
end

-- Управление режимом многократного спавна
local function SetMultiSpawnMode(enabled)
    -- Проверяем конфликты с другими режимами
    if enabled and SandboxMenu.EntitySpawn.ControlMode then
        GUI.AddMessage(LSM.ML("entity_multi_spawn_blocked_by_control"), Color.Red)
        return false
    end

    SandboxMenu.EntitySpawn.MultiSpawnMode = enabled

    if enabled then
        -- Сбрасываем активный спавн если был
        if SandboxMenu.EntitySpawn.SpawnMode then
            SandboxMenu.EntitySpawn.SpawnMode = false
            SandboxMenu.EntitySpawn.SelectedEntity = nil
            Hook.Remove("think", "entity_spawn_wait_click")
        end
        GUI.AddMessage(LSM.ML("multi_spawn_enabled"), Color.Cyan)
    else
        -- Сбрасываем активный спавн
        if SandboxMenu.EntitySpawn.SpawnMode then
            SandboxMenu.EntitySpawn.SpawnMode = false
            SandboxMenu.EntitySpawn.SelectedEntity = nil
            Hook.Remove("think", "entity_spawn_wait_click")
        end
        GUI.AddMessage(LSM.ML("multi_spawn_disabled"), Color.Yellow)
    end

    return true
end

-- Активировать режим контроля
local function StartControlMode()
    -- Проверяем конфликт с многократным спавном
    if SandboxMenu.EntitySpawn.MultiSpawnMode then
        GUI.AddMessage(LSM.ML("entity_control_blocked_by_multi_spawn"), Color.Red)
        return false
    end

    SandboxMenu.EntitySpawn.ControlMode = true

    -- Сохраняем текущего персонажа как оригинального
    if Character.Controlled and not SandboxMenu.EntitySpawn.OriginalCharacter then
        SandboxMenu.EntitySpawn.OriginalCharacter = Character.Controlled
    end

    GUI.AddMessage(LSM.ML("control_mode_hint"), Color.Lime)

    Hook.Remove("think", "entity_control_wait_click")

    Hook.Add("think", "entity_control_wait_click", function()
        if SandboxMenu.EntitySpawn.ControlMode then
            -- Подсветка персонажа под курсором
            local targetChar = FindCharacterAtCursor()

            if PlayerInput.PrimaryMouseButtonClicked() and GUI.MouseOn == nil then
                if targetChar then
                    TakeControlOf(targetChar)
                    SandboxMenu.EntitySpawn.ControlMode = false
                    Hook.Remove("think", "entity_control_wait_click")
                else
                    GUI.AddMessage(LSM.ML("no_character"), Color.Orange)
                end
            elseif PlayerInput.SecondaryMouseButtonClicked() then
                StopControlMode()
            end
        end
    end)

    return true
end

-- Получить локализованное имя существа
local function GetLocalizedName(speciesName)
    local localizedName = nil
    pcall(function() 
        local result = TextManager.Get("Character." .. speciesName)
        if result then
            localizedName = result.Value
        end
    end)
    return localizedName
end

-- Кэш спрайтов для существ (загружаем из уже заспавненных персонажей)
SandboxMenu.EntitySpawn.SpriteCache = {}

-- Попытка получить спрайт для существа
-- Примечание: спрайты human отключены, т.к. голова не вписывается в иконку
local function GetEntitySprite(speciesName)
    -- Отключаем спрайты для human - они плохо выглядят в иконках
    -- if speciesName == "human" then
    --     return nil
    -- end
    
    -- Проверяем кэш
    -- if SandboxMenu.EntitySpawn.SpriteCache[speciesName] then
    --     return SandboxMenu.EntitySpawn.SpriteCache[speciesName]
    -- end
    
    -- Ищем среди заспавненных персонажей
    -- for character in Character.CharacterList do
    --     if character and not character.Removed and character.SpeciesName then
    --         local charSpecies = tostring(character.SpeciesName)
    --         if charSpecies == speciesName and character.AnimController then
    --             local sprite = nil
                
    --             -- Пробуем получить спрайт тела (MainLimb лучше выглядит чем голова)
    --             pcall(function()
    --                 local mainLimb = character.AnimController.MainLimb
    --                 if mainLimb and mainLimb.Sprite then
    --                     sprite = mainLimb.Sprite
    --                 end
    --             end)
                
    --             -- Fallback на голову если MainLimb недоступен
    --             if not sprite then
    --                 pcall(function()
    --                     local headLimb = character.AnimController.GetLimb(LimbType.Head)
    --                     if headLimb and headLimb.Sprite then
    --                         sprite = headLimb.Sprite
    --                     end
    --                 end)
    --             end
                
    --             if sprite then
    --                 SandboxMenu.EntitySpawn.SpriteCache[speciesName] = sprite
    --                 return sprite
    --             end
    --         end
    --     end
    -- end
    
    return nil
end

-- Функция фильтрации по имени/ID
local function AllEntitiesWithCorrectName()
    local tbl = {}
    local searchText = string.lower(SandboxMenu.EntitySpawn.EntityName)
    
    for k, v in pairs(SandboxMenu.EntitySpawn.EntityList) do
        local nameMatch = v.localizedName and string.find(string.lower(v.localizedName), searchText)
        local idMatch = v.speciesName and string.find(string.lower(v.speciesName), searchText)
        local groupMatch = v.group and string.find(string.lower(v.group), searchText)
        
        if nameMatch or idMatch or groupMatch then
            table.insert(tbl, v)
        end
    end

    return tbl
end

-- Функция фильтрации по группе (из API)
local function AllEntitiesWithCorrectGroup(unfiltredTable)
    local tbl = {}
    local targetGroup = SandboxMenu.EntitySpawn.SelectedGroup
    
    if not targetGroup or targetGroup == "" then return unfiltredTable end
    
    for k, v in pairs(unfiltredTable) do
        if v.group and string.lower(v.group) == string.lower(targetGroup) then
            table.insert(tbl, v)
        end
    end

    return tbl
end

-- Функция фильтрации по моду
local function AllEntitiesWithSelectedMod(unfiltredTable)
    local tbl = {}
    local targetMod = SandboxMenu.EntitySpawn.SelectedMod
    
    if not targetMod or targetMod == "" then return unfiltredTable end
    
    for k, v in pairs(unfiltredTable) do
        if v.mod == targetMod then
            table.insert(tbl, v)
        end
    end

    return tbl
end

-- Сортировка по алфавиту (по локализованному имени)
local function SortTableAlphabetically(unfiltredTable)
    local function sortObj(objA, objB)
        return (objA.localizedName or objA.speciesName) < (objB.localizedName or objB.speciesName)
    end
    table.sort(unfiltredTable, sortObj)
end

-- Заполнение списка существ из API
for prefab in SandboxMenu.EntitySpawn.AllEntities do
    local characterPrefab = prefab
    
    if characterPrefab then
        local speciesName = tostring(characterPrefab.Identifier)
        local group = ""
        local modName = "Vanilla"
        
        -- Получаем группу из API
        pcall(function()
            if characterPrefab.Group and not characterPrefab.Group.IsEmpty then
                group = tostring(characterPrefab.Group)
            end
        end)
        
        -- Получаем имя мода из API
        pcall(function()
            if characterPrefab.ContentFile and characterPrefab.ContentFile.ContentPackage then
                modName = characterPrefab.ContentFile.ContentPackage.Name or "Unknown"
            end
        end)
        
        -- Получаем локализованное имя
        local localizedName = GetLocalizedName(speciesName)
        if not localizedName or localizedName == "" or localizedName:match("^%s*$") then
            localizedName = speciesName
        end
        
        -- Получаем HasCharacterInfo из API
        local hasCharacterInfo = false
        pcall(function()
            hasCharacterInfo = characterPrefab.HasCharacterInfo or false
        end)
        
        table.insert(SandboxMenu.EntitySpawn.EntityList, {
            speciesName = speciesName,
            localizedName = localizedName,
            group = group,
            mod = modName,
            hasCharacterInfo = hasCharacterInfo,
            prefab = characterPrefab,
        })
        
        -- Собираем уникальные группы
        if group ~= "" and not SandboxMenu.TableHasValue(SandboxMenu.EntitySpawn.GroupList, group) then
            table.insert(SandboxMenu.EntitySpawn.GroupList, group)
        end
        
        -- Собираем уникальные моды
        if not SandboxMenu.TableHasValue(SandboxMenu.EntitySpawn.ModList, modName) then
            table.insert(SandboxMenu.EntitySpawn.ModList, modName)
        end
    end
end

-- Сортируем списки фильтров
table.sort(SandboxMenu.EntitySpawn.GroupList)
table.sort(SandboxMenu.EntitySpawn.ModList)

-- Создание вкладки
local entityTab = GUI.Frame(GUI.RectTransform(Vector2(0.995, 0.935), SandboxMenu.menuContent.RectTransform, GUI.Anchor.TopCenter))
entityTab.RectTransform.AbsoluteOffset = Point(0, 65)
entityTab.Visible = false

-- Список сущностей
local entitySelectMenu = GUI.ListBox(GUI.RectTransform(Vector2(0.95, 0.9), entityTab.RectTransform, GUI.Anchor.BottomCenter))
entitySelectMenu.RectTransform.AbsoluteOffset = Point(0, 25)

local maxEntitiesInRow = 12

-- Функция отрисовки сущностей
function SandboxMenu.EntitySpawn.DrawEntities()
    local processedEntityList = AllEntitiesWithCorrectName()

    if SandboxMenu.EntitySpawn.SelectedGroup and SandboxMenu.EntitySpawn.SelectedGroup ~= "" then
        processedEntityList = AllEntitiesWithCorrectGroup(processedEntityList)
    end
    
    if SandboxMenu.EntitySpawn.SelectedMod and SandboxMenu.EntitySpawn.SelectedMod ~= "" then
        processedEntityList = AllEntitiesWithSelectedMod(processedEntityList)
    end

    SortTableAlphabetically(processedEntityList)

    local entityCount = math.ceil(#processedEntityList / maxEntitiesInRow)
    if entityCount < 1 then entityCount = 1 end

    entitySelectMenu.ClearChildren()

    local entityLines = {}

    for i = 1, entityCount do
        entityLines[i] = GUI.ListBox(GUI.RectTransform(Vector2(1, 0.1), entitySelectMenu.Content.RectTransform, GUI.Anchor.TopLeft), true)
    end

    local function DrawNextEntity(k, id)
        if (id ~= SandboxMenu.EntitySpawn.SearchID) then return end

        local v = processedEntityList[k]
        if (not v) then return end

        local currentEntityLine = math.floor(k / maxEntitiesInRow)
        if (k % maxEntitiesInRow ~= 0) then
            currentEntityLine = currentEntityLine + 1
        end

        local iconSize = 64 / GameSettings.CurrentConfig.Graphics.HUDScale
        local imageEntityFrame = GUI.Button(GUI.RectTransform(Point(iconSize*GUI.xScale, iconSize*GUI.yScale), entityLines[currentEntityLine].Content.RectTransform))

        imageEntityFrame.Color = Color(0,0,0,0)
        imageEntityFrame.HoverColor = Color(80,80,80,150)
        imageEntityFrame.PressedColor = Color(120,120,120,200)
        imageEntityFrame.SelectedColor = Color(0,0,0,0)

        -- ДВУХЭТАПНЫЙ СПАВН: Выбираем сущность, но не спавним её
        imageEntityFrame.OnClicked = function()
            -- Проверяем, не конфликтует ли с режимом управления
            if SandboxMenu.EntitySpawn.ControlMode then
                GUI.AddMessage("Нельзя спавнить во время режима управления", Color.Red)
                return true
            end

            SandboxMenu.EntitySpawn.SelectedEntity = v
            SandboxMenu.EntitySpawn.SpawnMode = true

            -- Разное сообщение в зависимости от режима
            if SandboxMenu.EntitySpawn.MultiSpawnMode then
                GUI.AddMessage(string.format(LSM.ML("multi_spawn_hint"), v.localizedName), Color.Cyan)
            else
                GUI.AddMessage(string.format(LSM.ML("spawn_hint"), v.localizedName), Color.White)
            end

            Hook.Remove("think", "entity_spawn_wait_click")

            Hook.Add("think", "entity_spawn_wait_click", function()
                if SandboxMenu.EntitySpawn.SpawnMode then
                    if PlayerInput.PrimaryMouseButtonClicked() and GUI.MouseOn == nil then
                        local teamName = "None"
                        if SandboxMenu.EntitySpawn.SelectedTeam == 1 then
                            teamName = "Team1"
                        elseif SandboxMenu.EntitySpawn.SelectedTeam == 2 then
                            teamName = "Team2"
                        elseif SandboxMenu.EntitySpawn.SelectedTeam == 3 then
                            teamName = "FriendlyNPC"
                        end

                        local command = "spawn " .. SandboxMenu.EntitySpawn.SelectedEntity.speciesName .. " cursor " .. teamName

                        for i = 1, SandboxMenu.EntitySpawn.SelectedCount do
                            Game.ExecuteCommand(command)
                        end

                        print("[Entity Menu] Spawned " .. SandboxMenu.EntitySpawn.SelectedCount .. "x " .. SandboxMenu.EntitySpawn.SelectedEntity.localizedName)

                        -- Сбрасываем состояние только если НЕ многократный режим
                        if not SandboxMenu.EntitySpawn.MultiSpawnMode then
                            SandboxMenu.EntitySpawn.SpawnMode = false
                            SandboxMenu.EntitySpawn.SelectedEntity = nil
                            Hook.Remove("think", "entity_spawn_wait_click")
                        end

                    elseif PlayerInput.SecondaryMouseButtonClicked() then
                        SandboxMenu.EntitySpawn.SpawnMode = false
                        SandboxMenu.EntitySpawn.SelectedEntity = nil
                        Hook.Remove("think", "entity_spawn_wait_click")
                        GUI.AddMessage(LSM.ML("spawn_cancelled"), Color.Yellow)
                    end
                end
            end)

            return true
        end

        imageEntityFrame.RectTransform.MinSize = Point(0, iconSize*GUI.yScale)

        -- Пробуем получить спрайт существа
        local entitySprite = GetEntitySprite(v.speciesName)
        local groupColor = GetGroupColor(v.group)
        local borderColor = GetBorderColor(v)
        
        -- Внешняя рамка (показывает тип существа)
        local borderFrame = GUI.Frame(GUI.RectTransform(Vector2(0.92, 0.92), imageEntityFrame.RectTransform, GUI.Anchor.Center))
        borderFrame.Color = borderColor
        borderFrame.CanBeFocused = false
        
        if entitySprite then
            -- Показываем спрайт существа (уменьшенный в 2 раза)
            local image = GUI.Image(GUI.RectTransform(Vector2(0.45, 0.45), borderFrame.RectTransform, GUI.Anchor.Center), entitySprite)
            image.CanBeFocused = false
        else
            -- Красивое оформление без спрайта
            local innerFrame = GUI.Frame(GUI.RectTransform(Vector2(0.88, 0.88), borderFrame.RectTransform, GUI.Anchor.Center))
            innerFrame.Color = groupColor
            innerFrame.CanBeFocused = false
            
            -- Тёмный внутренний фон для контраста
            local darkBg = GUI.Frame(GUI.RectTransform(Vector2(0.85, 0.85), innerFrame.RectTransform, GUI.Anchor.Center))
            darkBg.Color = Color(30, 30, 40, 200)
            darkBg.CanBeFocused = false
            
            -- Инициалы (первые 2 буквы имени) - более крупные
            local initials = v.localizedName:sub(1, 2):upper()
            local initialLabel = GUI.TextBlock(
                GUI.RectTransform(Vector2(1.0, 0.7), darkBg.RectTransform, GUI.Anchor.Center),
                initials,
                nil, nil, GUI.Alignment.Center
            )
            initialLabel.TextColor = groupColor
            initialLabel.TextScale = 1.1
            initialLabel.CanBeFocused = false
            
            -- Маленький индикатор группы внизу
            if v.group and v.group ~= "" then
                local groupIndicator = GUI.Frame(GUI.RectTransform(Vector2(0.8, 0.15), darkBg.RectTransform, GUI.Anchor.BottomCenter))
                groupIndicator.RectTransform.AbsoluteOffset = Point(0, 2)
                groupIndicator.Color = groupColor
                groupIndicator.CanBeFocused = false
            end
        end

        -- Tooltip с полной информацией (ID, локализованное имя, группа, мод)
        local tooltipText = "‖color:gui.orange‖" .. v.localizedName .. "‖end‖\n"
        tooltipText = tooltipText .. "‖color:gui.blue‖ID: " .. v.speciesName .. "‖end‖\n"
        if v.group and v.group ~= "" then
            tooltipText = tooltipText .. L("spawnsubmenu.group", "Group") .. ": " .. v.group .. "\n"
        end
        tooltipText = tooltipText .. "Mod: " .. v.mod
        if v.hasCharacterInfo then
            tooltipText = tooltipText .. "\n‖color:gui.green‖" .. LSM.ML("humanoid_hint") .. "‖end‖"
        end
        
        imageEntityFrame.ToolTip = RichString.Rich(tooltipText)

        Timer.NextFrame(function()
            DrawNextEntity(k+1, id)
        end)
    end

    SandboxMenu.EntitySpawn.SearchID = SandboxMenu.EntitySpawn.SearchID + 1
    DrawNextEntity(1, SandboxMenu.EntitySpawn.SearchID)
end

-- Dropdown для групп (данные из API)
local groupDropdown = GUI.DropDown(GUI.RectTransform(Vector2(0.12, 0.05), entityTab.RectTransform, GUI.Anchor.TopLeft), LSM.ML("all_groups"), 8, nil, false)
groupDropdown.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 15)

groupDropdown.AddItem(LSM.ML("all_groups"), "")
for _, group in ipairs(SandboxMenu.EntitySpawn.GroupList) do
    groupDropdown.AddItem(group, group)
end

groupDropdown.OnSelected = function(guiComponent, object)
    SandboxMenu.EntitySpawn.SelectedGroup = object
    SandboxMenu.EntitySpawn.DrawEntities()
end

-- Dropdown для модов (данные из API)
local modSelection = GUI.DropDown(GUI.RectTransform(Vector2(0.14, 0.05), entityTab.RectTransform, GUI.Anchor.TopLeft), LSM.ML("all_mods"), 6, nil, false)
modSelection.RectTransform.AbsoluteOffset = Point(130 * GUI.xScale, 15)

modSelection.AddItem(LSM.ML("all_mods"), "")
for _, mod in ipairs(SandboxMenu.EntitySpawn.ModList) do
    modSelection.AddItem(mod, mod)
end

modSelection.OnSelected = function(guiComponent, object)
    SandboxMenu.EntitySpawn.SelectedMod = object
    SandboxMenu.EntitySpawn.DrawEntities()
end

-- Dropdown для выбора команды
local teamSelection = GUI.DropDown(GUI.RectTransform(Vector2(0.14, 0.05), entityTab.RectTransform, GUI.Anchor.TopLeft), "None", 4, nil, false)
teamSelection.RectTransform.AbsoluteOffset = Point(260 * GUI.xScale, 15)

for k, v in pairs(SandboxMenu.EntitySpawn.teamEnums) do
    teamSelection.AddItem(v, k)
end

teamSelection.OnSelected = function(guiComponent, object)
    SandboxMenu.EntitySpawn.SelectedTeam = object
end

-- Текст количества
local countText = GUI.TextBlock(GUI.RectTransform(Vector2(0.35, 0.1), entityTab.RectTransform), "1", nil, nil, GUI.Alignment.Center)
countText.RectTransform.AbsoluteOffset = Point(385 * GUI.xScale, 10)

-- Слайдер количества
local selectedCount = GUI.ScrollBar(GUI.RectTransform(Vector2(0.15, 0.1), entityTab.RectTransform), 0.1, nil, "GUISlider")
selectedCount.RectTransform.AbsoluteOffset = Point(430 * GUI.xScale, 15)
selectedCount.Range = Vector2(1, 20)
selectedCount.BarScrollValue = 1
selectedCount.OnMoved = function()
    SandboxMenu.EntitySpawn.SelectedCount = math.floor(selectedCount.BarScrollValue)
    countText.Text = tostring(SandboxMenu.EntitySpawn.SelectedCount)
end

-- Поле поиска по имени
local findEntityByNameTextBox = GUI.TextBox(GUI.RectTransform(Vector2(0.20, 0.2), entityTab.RectTransform, GUI.Anchor.TopRight), SandboxMenu.EntitySpawn.EntityName)
findEntityByNameTextBox.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 15)

findEntityByNameTextBox.OnTextChangedDelegate = function(textBox)
    SandboxMenu.EntitySpawn.EntityName = textBox.Text
    SandboxMenu.EntitySpawn.DrawEntities()
end

-- === КНОПКИ КОНТРОЛЯ ===
-- Размещаем справа от поля поиска, используя относительное позиционирование

-- Кнопка RETURN - вернуться в своё тело (правее всех)
local returnButton = GUI.Button(GUI.RectTransform(Vector2(0.08, 0.05), entityTab.RectTransform, GUI.Anchor.TopRight), LSM.ML("return_body"), GUI.Alignment.Center, "GUIButtonSmall")
returnButton.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 50)  -- Под полем поиска
returnButton.Color = Color(150, 100, 60)
returnButton.HoverColor = Color(180, 120, 80)
returnButton.TextColor = Color.White

returnButton.OnClicked = function()
    ReturnToOriginalBody()
    return true
end

-- Кнопка MULTI SPAWN - переключение режима многократного спавна (левее CONTROL)
local multiSpawnButton = GUI.Button(GUI.RectTransform(Vector2(0.08, 0.05), entityTab.RectTransform, GUI.Anchor.TopRight), LSM.ML("multi_spawn_mode"), GUI.Alignment.Center, "GUIButtonSmall")
multiSpawnButton.RectTransform.AbsoluteOffset = Point(205 * GUI.xScale, 50)  -- Под полем поиска
multiSpawnButton.Color = Color(100, 100, 150)  -- Серый по умолчанию
multiSpawnButton.HoverColor = Color(120, 120, 170)
multiSpawnButton.TextColor = Color.White

multiSpawnButton.OnClicked = function()
    local newState = not SandboxMenu.EntitySpawn.MultiSpawnMode
    if SetMultiSpawnMode(newState) then
        -- Обновляем цвет кнопки в зависимости от состояния
        if newState then
            multiSpawnButton.Color = Color(150, 100, 200)  -- Фиолетовый при активном режиме
            multiSpawnButton.HoverColor = Color(170, 120, 220)
        else
            multiSpawnButton.Color = Color(100, 100, 150)  -- Серый при неактивном
            multiSpawnButton.HoverColor = Color(120, 120, 170)
        end
    end
    return true
end

-- Кнопка CONTROL - взять управление сущностью (левее RETURN)
local controlButton = GUI.Button(GUI.RectTransform(Vector2(0.08, 0.05), entityTab.RectTransform, GUI.Anchor.TopRight), LSM.ML("control_mode"), GUI.Alignment.Center, "GUIButtonSmall")
controlButton.RectTransform.AbsoluteOffset = Point(115 * GUI.xScale, 50)  -- Под полем поиска, левее RETURN
controlButton.Color = Color(60, 150, 60)
controlButton.HoverColor = Color(80, 180, 80)
controlButton.TextColor = Color.White

controlButton.OnClicked = function()
    -- Закрываем меню и активируем режим контроля
    -- menu - это Parent от menuContent
    if SandboxMenu.menuContent and SandboxMenu.menuContent.Parent then
        SandboxMenu.menuContent.Parent.Visible = false
    end
    StartControlMode()
    return true
end

-- Создание кнопки в главном меню
local entityButton = GUI.Button(GUI.RectTransform(Vector2(0.1, 0.15), SandboxMenu.menuContent.RectTransform, GUI.Anchor.TopLeft), LSM.ML("entities"), GUI.Alignment.Center, "GUIButtonSmall")
entityButton.RectTransform.AbsoluteOffset = Point(SandboxMenu.NextButtonXOffset, 25)
SandboxMenu.NextButtonXOffset = SandboxMenu.NextButtonXOffset + 130

entityButton.OnClicked = function()
    -- Сброс режимов
    SandboxMenu.EntitySpawn.SpawnMode = false
    SandboxMenu.EntitySpawn.SelectedEntity = nil
    SandboxMenu.EntitySpawn.ControlMode = false
    Hook.Remove("think", "entity_spawn_wait_click")
    Hook.Remove("think", "entity_control_wait_click")

    -- Синхронизируем состояние кнопки MultiSpawnMode
    if SandboxMenu.EntitySpawn.MultiSpawnMode then
        multiSpawnButton.Color = Color(150, 100, 200)
        multiSpawnButton.HoverColor = Color(170, 120, 220)
    else
        multiSpawnButton.Color = Color(100, 100, 150)
        multiSpawnButton.HoverColor = Color(120, 120, 170)
    end

    SandboxMenu.HideAllTabs()
    entityTab.Visible = true
    SandboxMenu.EntitySpawn.DrawEntities()
end

SandboxMenu.RegisterTab(entityTab)

print("[Entity Menu] Initialized with " .. #SandboxMenu.EntitySpawn.EntityList .. " entities, " .. #SandboxMenu.EntitySpawn.GroupList .. " groups, " .. #SandboxMenu.EntitySpawn.ModList .. " mods.")
