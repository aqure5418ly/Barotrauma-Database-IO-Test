DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Client = DatabaseIOTestLua.Client or {}

local DBClient = DatabaseIOTestLua.Client
DBClient.NetTakeRequest = "DBIOTEST_RequestTakeByIdentifier"
DBClient.NetTakeResult = "DBIOTEST_TakeResult"

DBClient.State = DBClient.State or {
    activeTerminal = nil,
    searchText = "",
    sortMode = "name_asc",
    lastRefresh = 0,
    dirty = true
}

local function L(key, fallback)
    local value = nil
    pcall(function()
        local text = TextManager.Get(key)
        if text ~= nil and text.Value ~= nil and text.Value ~= "" then
            value = text.Value
        end
    end)
    return value or fallback
end

local function Now()
    local t = 0
    pcall(function() t = Timer.GetTime() end)
    if t == 0 then
        pcall(function() t = Timer.Time end)
    end
    return t or 0
end

local function GetItemIdentifier(item)
    if item == nil or item.Prefab == nil then
        return ""
    end

    local identifier = ""
    pcall(function()
        identifier = item.Prefab.Identifier.Value
    end)
    if identifier == nil or identifier == "" then
        pcall(function()
            identifier = item.Prefab.Identifier.ToString()
        end)
    end
    if identifier == nil or identifier == "" then
        pcall(function()
            identifier = tostring(item.Prefab.Identifier)
        end)
    end
    return identifier or ""
end

local function GetItemDisplayName(item)
    if item == nil then
        return "Unknown"
    end

    local name = ""
    pcall(function()
        name = tostring(item.Name)
    end)
    if name == nil or name == "" then
        pcall(function()
            name = item.Prefab.Name.Value
        end)
    end
    if name == nil or name == "" then
        name = GetItemIdentifier(item)
    end
    return name or "Unknown"
end

local function NormalizeIdentifier(identifier)
    if identifier == nil then
        return ""
    end
    return string.lower(tostring(identifier))
end

local function IsSessionTerminal(item)
    if item == nil or item.Removed then
        return false
    end

    if NormalizeIdentifier(GetItemIdentifier(item)) == "databaseterminalsession" then
        return true
    end

    local hasTag = false
    pcall(function()
        hasTag = item.HasTag("database_terminal_session")
    end)
    return hasTag == true
end

local function GetTerminalInventory(terminal)
    if terminal == nil then
        return nil
    end

    local itemContainer = nil
    pcall(function()
        itemContainer = terminal.GetComponentString("ItemContainer")
    end)
    if itemContainer == nil then
        return nil
    end

    local inventory = nil
    pcall(function()
        inventory = itemContainer.Inventory
    end)
    return inventory
end

local function ForEachInventoryItem(inventory, callback)
    if inventory == nil then
        return
    end

    local ok = pcall(function()
        for contained in inventory.AllItems do
            if contained ~= nil and not contained.Removed then
                local stop = callback(contained)
                if stop == true then
                    break
                end
            end
        end
    end)
    if ok then
        return
    end

    pcall(function()
        for contained in inventory.AllItemsMod do
            if contained ~= nil and not contained.Removed then
                local stop = callback(contained)
                if stop == true then
                    break
                end
            end
        end
    end)
end

local function FindFirstMatchingItem(inventory, wantedIdentifier)
    local wanted = NormalizeIdentifier(wantedIdentifier)
    if wanted == "" then
        return nil
    end

    local result = nil
    ForEachInventoryItem(inventory, function(contained)
        if NormalizeIdentifier(GetItemIdentifier(contained)) == wanted then
            result = contained
            return true
        end
        return false
    end)
    return result
end

local function FindHeldSessionTerminal(character)
    if character == nil then
        return nil
    end

    local selected = nil
    local selectedSecondary = nil
    pcall(function() selected = character.SelectedItem end)
    pcall(function() selectedSecondary = character.SelectedSecondaryItem end)

    if IsSessionTerminal(selected) then
        return selected
    end
    if IsSessionTerminal(selectedSecondary) then
        return selectedSecondary
    end

    local inventory = nil
    pcall(function() inventory = character.Inventory end)
    if inventory == nil then
        return nil
    end

    local found = nil
    ForEachInventoryItem(inventory, function(item)
        if IsSessionTerminal(item) then
            found = item
            return true
        end
        return false
    end)
    return found
end

local function GetTerminalDatabaseId(terminal)
    if terminal == nil then
        return "default"
    end

    local component = nil
    pcall(function()
        component = terminal.GetComponentString("DatabaseTerminalComponent")
    end)
    if component == nil then
        return "default"
    end

    local databaseId = "default"
    pcall(function()
        databaseId = tostring(component.DatabaseId or "default")
    end)
    return databaseId or "default"
end

local function MatchSearch(entry, searchText)
    if searchText == nil or searchText == "" then
        return true
    end

    local q = string.lower(searchText)
    local name = string.lower(entry.name or "")
    local identifier = string.lower(entry.identifier or "")
    return string.find(name, q, 1, true) ~= nil or string.find(identifier, q, 1, true) ~= nil
end

local function BuildEntries(terminal)
    local inventory = GetTerminalInventory(terminal)
    local groups = {}

    ForEachInventoryItem(inventory, function(item)
        local identifier = GetItemIdentifier(item)
        local normalized = NormalizeIdentifier(identifier)
        if normalized ~= "" then
            local group = groups[normalized]
            if group == nil then
                group = {
                    identifier = identifier,
                    name = GetItemDisplayName(item),
                    count = 0
                }
                groups[normalized] = group
            end
            group.count = group.count + 1
        end
        return false
    end)

    local entries = {}
    for _, entry in pairs(groups) do
        if MatchSearch(entry, DBClient.State.searchText) then
            table.insert(entries, entry)
        end
    end

    if DBClient.State.sortMode == "name_desc" then
        table.sort(entries, function(a, b) return string.lower(a.name) > string.lower(b.name) end)
    elseif DBClient.State.sortMode == "count_desc" then
        table.sort(entries, function(a, b)
            if a.count == b.count then
                return string.lower(a.name) < string.lower(b.name)
            end
            return a.count > b.count
        end)
    elseif DBClient.State.sortMode == "count_asc" then
        table.sort(entries, function(a, b)
            if a.count == b.count then
                return string.lower(a.name) < string.lower(b.name)
            end
            return a.count < b.count
        end)
    else
        table.sort(entries, function(a, b) return string.lower(a.name) < string.lower(b.name) end)
    end

    return entries
end

local function TryTakeItemLocally(terminal, identifier, character)
    if terminal == nil or character == nil or character.Inventory == nil then
        return false
    end

    local inventory = GetTerminalInventory(terminal)
    if inventory == nil then
        return false
    end

    local item = FindFirstMatchingItem(inventory, identifier)
    if item == nil then
        return false
    end

    local moved = false
    pcall(function()
        moved = character.Inventory.TryPutItem(item, character, CharacterInventory.AnySlot)
    end)
    return moved == true
end

local function RequestTake(entry)
    local terminal = DBClient.State.activeTerminal
    local character = Character.Controlled
    if terminal == nil or entry == nil or character == nil then
        return
    end

    if Game.IsMultiplayer then
        local message = Networking.Start(DBClient.NetTakeRequest)
        message.WriteString(tostring(terminal.ID))
        message.WriteString(entry.identifier)
        Networking.Send(message)
        return
    end

    local success = TryTakeItemLocally(terminal, entry.identifier, character)
    if success then
        GUI.AddMessage(L("dbiotest.ui.take.success", "Item moved to inventory."), Color.Lime)
    else
        GUI.AddMessage(L("dbiotest.ui.take.full", "Inventory full."), Color.Red)
    end
    DBClient.State.dirty = true
end

DBClient.Root = GUI.Frame(GUI.RectTransform(Vector2(1, 1)), nil)
DBClient.Root.CanBeFocused = false

DBClient.Panel = GUI.Frame(GUI.RectTransform(Vector2(0.30, 0.62), DBClient.Root.RectTransform, GUI.Anchor.CenterRight), "GUIFrame")
DBClient.Panel.RectTransform.AbsoluteOffset = Point(-24, 0)
DBClient.Panel.Visible = false

DBClient.DragHandle = GUI.DragHandle(GUI.RectTransform(Vector2(1, 1), DBClient.Panel.RectTransform, GUI.Anchor.Center), DBClient.Panel.RectTransform, nil)

DBClient.Title = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.95, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopCenter),
    L("dbiotest.ui.title", "Database Terminal"),
    nil,
    nil,
    GUI.Alignment.Center
)
DBClient.Title.RectTransform.AbsoluteOffset = Point(0, 8)

DBClient.SearchBox = GUI.TextBox(
    GUI.RectTransform(Vector2(0.56, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopLeft),
    DBClient.State.searchText
)
DBClient.SearchBox.RectTransform.AbsoluteOffset = Point(12, 40)
DBClient.SearchBox.ToolTip = L("dbiotest.ui.search.tooltip", "Search by name or identifier.")
DBClient.SearchBox.OnTextChangedDelegate = function(textBox)
    DBClient.State.searchText = textBox.Text or ""
    DBClient.State.dirty = true
end

DBClient.SortDropDown = GUI.DropDown(
    GUI.RectTransform(Vector2(0.34, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopRight),
    L("dbiotest.ui.sort.nameasc", "Name A-Z"),
    4,
    nil,
    false
)
DBClient.SortDropDown.RectTransform.AbsoluteOffset = Point(12, 40)
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.nameasc", "Name A-Z"), "name_asc")
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.namedesc", "Name Z-A"), "name_desc")
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.countdesc", "Count High-Low"), "count_desc")
DBClient.SortDropDown.AddItem(L("dbiotest.ui.sort.countasc", "Count Low-High"), "count_asc")
DBClient.SortDropDown.OnSelected = function(_, obj)
    DBClient.State.sortMode = tostring(obj or "name_asc")
    DBClient.State.dirty = true
end

DBClient.RefreshButton = GUI.Button(
    GUI.RectTransform(Vector2(0.20, 0.05), DBClient.Panel.RectTransform, GUI.Anchor.TopLeft),
    L("dbiotest.ui.refresh", "Refresh"),
    GUI.Alignment.Center,
    "GUIButtonSmall"
)
DBClient.RefreshButton.RectTransform.AbsoluteOffset = Point(12, 75)
DBClient.RefreshButton.OnClicked = function()
    DBClient.State.dirty = true
    return true
end

DBClient.List = GUI.ListBox(
    GUI.RectTransform(Vector2(0.95, 0.74), DBClient.Panel.RectTransform, GUI.Anchor.Center),
    true
)
DBClient.List.RectTransform.AbsoluteOffset = Point(0, 18)

DBClient.Footer = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.95, 0.08), DBClient.Panel.RectTransform, GUI.Anchor.BottomCenter),
    L("dbiotest.ui.footer", "Left-click item to transfer one. Right-click terminal to close session."),
    nil,
    nil,
    GUI.Alignment.Center
)
DBClient.Footer.TextColor = Color(210, 210, 210)

local function RedrawList()
    local terminal = DBClient.State.activeTerminal
    DBClient.List.ClearChildren()

    if terminal == nil or terminal.Removed then
        DBClient.Title.Text = L("dbiotest.ui.title", "Database Terminal")
        DBClient.List.RecalculateChildren()
        return
    end

    local databaseId = GetTerminalDatabaseId(terminal)
    DBClient.Title.Text = string.format("%s [%s]", L("dbiotest.ui.title", "Database Terminal"), databaseId)

    local entries = BuildEntries(terminal)
    if #entries == 0 then
        local emptyText = GUI.TextBlock(
            GUI.RectTransform(Vector2(1, 0.08), DBClient.List.Content.RectTransform, GUI.Anchor.TopCenter),
            L("dbiotest.ui.empty", "No items in database session."),
            nil,
            nil,
            GUI.Alignment.Center
        )
        emptyText.TextColor = Color(200, 200, 200)
    else
        for _, entry in ipairs(entries) do
            local label = string.format("%s x%d", entry.name, entry.count)
            local button = GUI.Button(
                GUI.RectTransform(Vector2(1, 0.08), DBClient.List.Content.RectTransform, GUI.Anchor.TopCenter),
                label,
                GUI.Alignment.Left,
                "GUIButtonSmall"
            )
            button.ToolTip = RichString.Rich(entry.identifier)
            button.OnClicked = function()
                RequestTake(entry)
                return true
            end
        end
    end

    DBClient.List.RecalculateChildren()
    DBClient.State.dirty = false
end

Hook.Patch("Barotrauma.GameScreen", "AddToGUIUpdateList", function()
    DBClient.Root.AddToGUIUpdateList(false, 1)
end)

Hook.Patch("Barotrauma.NetLobbyScreen", "AddToGUIUpdateList", function()
    DBClient.Root.AddToGUIUpdateList(false, 1)
end)

Hook.Add("think", "DBIOTEST_ClientTerminalUiThink", function()
    local character = Character.Controlled
    local terminal = FindHeldSessionTerminal(character)

    if terminal == nil then
        DBClient.Panel.Visible = false
        if DBClient.State.activeTerminal ~= nil then
            DBClient.State.activeTerminal = nil
            DBClient.State.dirty = true
        end
        return
    end

    DBClient.Panel.Visible = true
    if DBClient.State.activeTerminal ~= terminal then
        DBClient.State.activeTerminal = terminal
        DBClient.State.dirty = true
        DBClient.State.lastRefresh = 0
    end

    local now = Now()
    if DBClient.State.dirty or (now - DBClient.State.lastRefresh) > 0.35 then
        DBClient.State.lastRefresh = now
        RedrawList()
    end
end)

Networking.Receive(DBClient.NetTakeResult, function(message)
    local success = message.ReadBoolean()
    local text = message.ReadString()
    if text == nil or text == "" then
        text = success
            and L("dbiotest.ui.take.success", "Item moved to inventory.")
            or L("dbiotest.ui.take.failed", "Failed to transfer item.")
    end

    GUI.AddMessage(text, success and Color.Lime or Color.Red)
    DBClient.State.dirty = true
end)
