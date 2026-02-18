DatabaseIOTestLua = DatabaseIOTestLua or {}
DatabaseIOTestLua.Client = DatabaseIOTestLua.Client or {}

local DBClient = DatabaseIOTestLua.Client
DBClient.NetTakeRequest = "DBIOTEST_RequestTakeByIdentifier"
DBClient.NetTakeResult = "DBIOTEST_TakeResult"
DBClient.NetViewSubscribe = "DBIOTEST_ViewSubscribe"
DBClient.NetViewUnsubscribe = "DBIOTEST_ViewUnsubscribe"
DBClient.NetViewSnapshot = "DBIOTEST_ViewSnapshot"
DBClient.NetViewDelta = "DBIOTEST_ViewDelta"

DBClient.State = DBClient.State or {
    activeTerminal = nil,
    subscribedTerminalId = "",
    databaseId = "default",
    serial = 0,
    entriesByKey = {},
    totalEntries = 0,
    totalAmount = 0,
    searchText = "",
    sortMode = "name_asc",
    lastRefresh = 0,
    lastLocalSync = 0,
    lastSubscribeAt = 0,
    awaitingSnapshot = false,
    dirty = true
}

DBClient.IconCache = DBClient.IconCache or {}

local function Log(line)
    print("[DBIOTEST][B1][Client] " .. tostring(line or ""))
end

if not CLIENT then
    return
end

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

local function ReadIntString(message)
    return tonumber(message.ReadString() or "0") or 0
end

local function ReadFloatString(message)
    return tonumber(message.ReadString() or "0") or 0
end

local function NormalizeIdentifier(identifier)
    if identifier == nil then
        return ""
    end
    return string.lower(tostring(identifier))
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

local function GetTerminalComponent(terminal)
    if terminal == nil then
        return nil
    end

    local component = nil
    pcall(function()
        component = terminal.GetComponentString("DatabaseTerminalComponent")
    end)
    return component
end

local function BuildLocalEntryMap(terminal)
    local component = GetTerminalComponent(terminal)
    if component ~= nil then
        local rows = nil
        local ok = pcall(function()
            rows = component.GetVirtualViewSnapshot(true)
        end)
        if ok and rows ~= nil then
            local map = {}
            local totalEntries = 0
            local totalAmount = 0
            local iterOk = pcall(function()
                for row in rows do
                    if row ~= nil then
                        local identifier = tostring(row.Identifier or "")
                        local key = NormalizeIdentifier(identifier)
                        if key ~= "" then
                            local amount = math.max(0, math.floor(tonumber(row.Amount) or 0))
                            map[key] = {
                                key = key,
                                identifier = identifier,
                                prefabIdentifier = tostring(row.PrefabIdentifier or identifier),
                                displayName = tostring(row.DisplayName or identifier),
                                amount = amount,
                                bestQuality = math.floor(tonumber(row.BestQuality) or 0),
                                avgCondition = tonumber(row.AverageCondition) or 100.0
                            }
                            totalEntries = totalEntries + 1
                            totalAmount = totalAmount + amount
                        end
                    end
                end
            end)
            if iterOk then
                return map, totalEntries, totalAmount
            end
        end
    end

    local map = {}
    local totalAmount = 0
    local inventory = GetTerminalInventory(terminal)
    ForEachInventoryItem(inventory, function(item)
        local identifier = GetItemIdentifier(item)
        local key = NormalizeIdentifier(identifier)
        if key ~= "" then
            local entry = map[key]
            if entry == nil then
                entry = {
                    key = key,
                    identifier = identifier,
                    prefabIdentifier = identifier,
                    displayName = GetItemDisplayName(item),
                    amount = 0,
                    bestQuality = 0,
                    avgCondition = 100
                }
                map[key] = entry
            end
            entry.amount = (tonumber(entry.amount) or 0) + 1
            totalAmount = totalAmount + 1
        end
        return false
    end)

    local totalEntries = 0
    for _, _ in pairs(map) do
        totalEntries = totalEntries + 1
    end
    return map, totalEntries, totalAmount
end

local function MatchSearch(entry, searchText)
    if searchText == nil or searchText == "" then
        return true
    end

    local q = string.lower(searchText)
    local name = string.lower(entry.displayName or "")
    local identifier = string.lower(entry.identifier or "")
    return string.find(name, q, 1, true) ~= nil or string.find(identifier, q, 1, true) ~= nil
end

local function BuildEntries()
    local entries = {}
    for _, entry in pairs(DBClient.State.entriesByKey or {}) do
        if MatchSearch(entry, DBClient.State.searchText) then
            table.insert(entries, entry)
        end
    end

    if DBClient.State.sortMode == "name_desc" then
        table.sort(entries, function(a, b)
            return string.lower(a.displayName or "") > string.lower(b.displayName or "")
        end)
    elseif DBClient.State.sortMode == "count_desc" then
        table.sort(entries, function(a, b)
            if (a.amount or 0) == (b.amount or 0) then
                return string.lower(a.displayName or "") < string.lower(b.displayName or "")
            end
            return (a.amount or 0) > (b.amount or 0)
        end)
    elseif DBClient.State.sortMode == "count_asc" then
        table.sort(entries, function(a, b)
            if (a.amount or 0) == (b.amount or 0) then
                return string.lower(a.displayName or "") < string.lower(b.displayName or "")
            end
            return (a.amount or 0) < (b.amount or 0)
        end)
    else
        table.sort(entries, function(a, b)
            return string.lower(a.displayName or "") < string.lower(b.displayName or "")
        end)
    end

    return entries
end

local function ResolveIconSprite(entry)
    local key = NormalizeIdentifier(entry.prefabIdentifier or entry.identifier)
    if key == "" then
        return nil
    end

    local cached = DBClient.IconCache[key]
    if cached ~= nil then
        return cached == false and nil or cached
    end

    local sprite = nil
    pcall(function()
        local prefab = ItemPrefab.GetItemPrefab(tostring(entry.prefabIdentifier or entry.identifier or ""))
        if prefab ~= nil then
            sprite = prefab.InventoryIcon or prefab.Sprite
        end
    end)

    if sprite == nil then
        DBClient.IconCache[key] = false
        return nil
    end

    DBClient.IconCache[key] = sprite
    return sprite
end

local function BuildEntryTooltip(entry)
    local amount = math.max(0, math.floor(tonumber(entry.amount) or 0))
    local quality = math.floor(tonumber(entry.bestQuality) or 0)
    local condition = tonumber(entry.avgCondition) or 0
    local header = tostring(entry.displayName or entry.identifier or "?")
    local identifier = tostring(entry.identifier or "")

    local tooltipLines = {
        header,
        "id=" .. identifier,
        "x" .. tostring(amount),
        string.format("quality=%d condition=%.1f", quality, condition)
    }

    local prefabText = nil
    pcall(function()
        local prefab = ItemPrefab.GetItemPrefab(tostring(entry.prefabIdentifier or entry.identifier or ""))
        if prefab ~= nil then
            local rich = prefab.GetTooltip()
            if rich ~= nil then
                local asString = tostring(rich)
                pcall(function()
                    asString = tostring(rich.ToString())
                end)
                if asString ~= nil and asString ~= "" then
                    prefabText = asString
                end
            end
        end
    end)

    local body = prefabText
    if body == nil or body == "" then
        body = table.concat(tooltipLines, "\n")
    else
        body = body .. "\n" .. tooltipLines[2] .. "\n" .. tooltipLines[3]
    end

    return RichString.Rich(body)
end

local function ResetViewState(clearTerminal)
    DBClient.State.serial = 0
    DBClient.State.entriesByKey = {}
    DBClient.State.totalEntries = 0
    DBClient.State.totalAmount = 0
    DBClient.State.awaitingSnapshot = false
    DBClient.State.dirty = true
    DBClient.State.renderToken = (tonumber(DBClient.State.renderToken) or 0) + 1
    if clearTerminal then
        DBClient.State.activeTerminal = nil
        DBClient.State.subscribedTerminalId = ""
        DBClient.State.databaseId = "default"
    end
end

local function SendUnsubscribe()
    if not Game.IsMultiplayer then
        DBClient.State.subscribedTerminalId = ""
        return
    end

    if DBClient.State.subscribedTerminalId == "" then
        return
    end

    local message = Networking.Start(DBClient.NetViewUnsubscribe)
    message.WriteString(DBClient.State.subscribedTerminalId)
    Networking.Send(message)

    Log("Unsubscribe terminal=" .. tostring(DBClient.State.subscribedTerminalId))
    DBClient.State.subscribedTerminalId = ""
end

local function SendSubscribe(terminal, force)
    if terminal == nil then
        return
    end

    local terminalId = tostring(terminal.ID)
    if terminalId == "" then
        return
    end

    if not Game.IsMultiplayer then
        DBClient.State.subscribedTerminalId = terminalId
        return
    end

    if not force and DBClient.State.subscribedTerminalId == terminalId and not DBClient.State.awaitingSnapshot then
        return
    end

    local message = Networking.Start(DBClient.NetViewSubscribe)
    message.WriteString(terminalId)
    Networking.Send(message)

    DBClient.State.subscribedTerminalId = terminalId
    DBClient.State.awaitingSnapshot = true
    DBClient.State.lastSubscribeAt = Now()

    Log("Subscribe terminal=" .. tostring(terminalId) .. " force=" .. tostring(force == true))
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
        message.WriteString(tostring(entry.identifier or ""))
        Networking.Send(message)
        return
    end

    local success = false
    local reasonCode = ""
    local component = GetTerminalComponent(terminal)
    if component ~= nil then
        local okCall, reason = pcall(function()
            return tostring(component.TryTakeOneByIdentifierFromVirtualSession(entry.identifier, character) or "")
        end)
        if okCall then
            reasonCode = tostring(reason or "")
        else
            reasonCode = "not_ready"
        end
        if reasonCode == "" then
            success = true
        end
    end

    if success then
        GUI.AddMessage(L("dbiotest.ui.take.success", "Item moved to terminal buffer."), Color.Lime)
    elseif reasonCode == "inventory_full" then
        GUI.AddMessage(L("dbiotest.ui.take.full", "Buffer is full."), Color.Red)
    elseif reasonCode == "session_closed" then
        GUI.AddMessage(L("dbiotest.ui.take.closed", "Terminal session is closed."), Color.Red)
    else
        GUI.AddMessage(L("dbiotest.ui.take.failed", "Failed to transfer item."), Color.Red)
    end
    DBClient.State.dirty = true
end

local function ReadEntry(message)
    local identifier = tostring(message.ReadString() or "")
    local prefabIdentifier = tostring(message.ReadString() or identifier)
    local displayName = tostring(message.ReadString() or identifier)
    local amount = ReadIntString(message)
    local bestQuality = ReadIntString(message)
    local avgCondition = ReadFloatString(message)

    local key = NormalizeIdentifier(identifier)
    if key == "" then
        return nil
    end

    return {
        key = key,
        identifier = identifier,
        prefabIdentifier = prefabIdentifier,
        displayName = displayName,
        amount = math.max(0, amount),
        bestQuality = bestQuality,
        avgCondition = avgCondition
    }
end

local function ApplySnapshot(message)
    local terminalId = tostring(message.ReadString() or "")
    local databaseId = tostring(message.ReadString() or "default")
    local serial = ReadIntString(message)
    local totalEntries = ReadIntString(message)
    local totalAmount = ReadIntString(message)
    local payloadCount = ReadIntString(message)

    if DBClient.State.subscribedTerminalId ~= "" and terminalId ~= DBClient.State.subscribedTerminalId then
        return
    end

    DBClient.State.serial = math.max(0, serial)
    DBClient.State.databaseId = databaseId
    DBClient.State.entriesByKey = {}

    for i = 1, math.max(0, payloadCount) do
        local entry = ReadEntry(message)
        if entry ~= nil then
            DBClient.State.entriesByKey[entry.key] = entry
        end
    end

    DBClient.State.totalEntries = math.max(0, totalEntries)
    DBClient.State.totalAmount = math.max(0, totalAmount)
    DBClient.State.awaitingSnapshot = false
    DBClient.State.dirty = true

    Log(string.format(
        "Snapshot terminal=%s serial=%d entries=%d amount=%d payload=%d",
        terminalId,
        DBClient.State.serial,
        DBClient.State.totalEntries,
        DBClient.State.totalAmount,
        payloadCount))
end

local function ApplyDelta(message)
    local terminalId = tostring(message.ReadString() or "")
    local databaseId = tostring(message.ReadString() or "default")
    local serial = ReadIntString(message)
    local totalEntries = ReadIntString(message)
    local totalAmount = ReadIntString(message)
    local removedCount = ReadIntString(message)

    if DBClient.State.subscribedTerminalId ~= "" and terminalId ~= DBClient.State.subscribedTerminalId then
        for i = 1, math.max(0, removedCount) do
            message.ReadString()
        end
        local ignoreUpserts = ReadIntString(message)
        for i = 1, math.max(0, ignoreUpserts) do
            ReadEntry(message)
        end
        return
    end

    if serial <= (DBClient.State.serial or 0) then
        for i = 1, math.max(0, removedCount) do
            message.ReadString()
        end
        local ignoreUpserts = ReadIntString(message)
        for i = 1, math.max(0, ignoreUpserts) do
            ReadEntry(message)
        end
        return
    end

    if serial > (DBClient.State.serial + 1) then
        for i = 1, math.max(0, removedCount) do
            message.ReadString()
        end
        local ignoreUpserts = ReadIntString(message)
        for i = 1, math.max(0, ignoreUpserts) do
            ReadEntry(message)
        end

        DBClient.State.awaitingSnapshot = true
        if DBClient.State.activeTerminal ~= nil then
            SendSubscribe(DBClient.State.activeTerminal, true)
        end
        Log(string.format(
            "Delta gap detected terminal=%s localSerial=%d incoming=%d -> resubscribe",
            terminalId,
            DBClient.State.serial,
            serial))
        return
    end

    for i = 1, math.max(0, removedCount) do
        local identifier = tostring(message.ReadString() or "")
        local key = NormalizeIdentifier(identifier)
        if key ~= "" then
            DBClient.State.entriesByKey[key] = nil
        end
    end

    local upsertCount = ReadIntString(message)
    for i = 1, math.max(0, upsertCount) do
        local entry = ReadEntry(message)
        if entry ~= nil then
            DBClient.State.entriesByKey[entry.key] = entry
        end
    end

    DBClient.State.databaseId = databaseId
    DBClient.State.serial = serial
    DBClient.State.totalEntries = math.max(0, totalEntries)
    DBClient.State.totalAmount = math.max(0, totalAmount)
    DBClient.State.awaitingSnapshot = false
    DBClient.State.dirty = true

    Log(string.format(
        "Delta terminal=%s serial=%d removed=%d upserts=%d entries=%d amount=%d",
        terminalId,
        serial,
        removedCount,
        upsertCount,
        DBClient.State.totalEntries,
        DBClient.State.totalAmount))
end

DBClient.Root = GUI.Frame(GUI.RectTransform(Vector2(1, 1)), nil)
DBClient.Root.CanBeFocused = false

DBClient.Panel = GUI.Frame(GUI.RectTransform(Vector2(0.42, 0.66), DBClient.Root.RectTransform, GUI.Anchor.CenterRight), "GUIFrame")
DBClient.Panel.RectTransform.AbsoluteOffset = Point(-24, 0)
DBClient.Panel.Visible = false

DBClient.DragHandle = GUI.DragHandle(
    GUI.RectTransform(Vector2(1, 1), DBClient.Panel.RectTransform, GUI.Anchor.Center),
    DBClient.Panel.RectTransform,
    nil
)

DBClient.Title = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.95, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopCenter),
    L("dbiotest.ui.title", "Database Terminal"),
    nil,
    nil,
    GUI.Alignment.Center
)
DBClient.Title.RectTransform.AbsoluteOffset = Point(0, 8)

DBClient.SearchBox = GUI.TextBox(
    GUI.RectTransform(Vector2(0.55, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopLeft),
    DBClient.State.searchText
)
DBClient.SearchBox.RectTransform.AbsoluteOffset = Point(12, 40)
DBClient.SearchBox.ToolTip = L("dbiotest.ui.search.tooltip", "Search by name or identifier.")
DBClient.SearchBox.OnTextChangedDelegate = function(textBox)
    DBClient.State.searchText = textBox.Text or ""
    DBClient.State.dirty = true
end

DBClient.SortDropDown = GUI.DropDown(
    GUI.RectTransform(Vector2(0.31, 0.06), DBClient.Panel.RectTransform, GUI.Anchor.TopRight),
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
    if DBClient.State.activeTerminal ~= nil then
        SendSubscribe(DBClient.State.activeTerminal, true)
    end
    DBClient.State.dirty = true
    return true
end

DBClient.StatusText = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.70, 0.05), DBClient.Panel.RectTransform, GUI.Anchor.TopRight),
    "",
    nil,
    nil,
    GUI.Alignment.Right
)
DBClient.StatusText.RectTransform.AbsoluteOffset = Point(12, 76)

DBClient.List = GUI.ListBox(
    GUI.RectTransform(Vector2(0.95, 0.71), DBClient.Panel.RectTransform, GUI.Anchor.Center),
    true
)
DBClient.List.RectTransform.AbsoluteOffset = Point(0, 24)

DBClient.Footer = GUI.TextBlock(
    GUI.RectTransform(Vector2(0.95, 0.08), DBClient.Panel.RectTransform, GUI.Anchor.BottomCenter),
    L("dbiotest.ui.footer", "Click icon to spawn one item into terminal buffer."),
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
        DBClient.StatusText.Text = ""
        DBClient.List.RecalculateChildren()
        return
    end

    local dbid = DBClient.State.databaseId or GetTerminalDatabaseId(terminal)
    DBClient.Title.Text = string.format("%s [%s]", L("dbiotest.ui.title", "Database Terminal"), tostring(dbid or "default"))
    DBClient.StatusText.Text = string.format("%d entries | %d items", DBClient.State.totalEntries or 0, DBClient.State.totalAmount or 0)

    local entries = BuildEntries()
    if #entries == 0 then
        local text = DBClient.State.awaitingSnapshot
            and L("dbiotest.ui.loading", "Waiting for snapshot...")
            or L("dbiotest.ui.empty", "No items in terminal view.")
        local emptyText = GUI.TextBlock(
            GUI.RectTransform(Vector2(1, 0.08), DBClient.List.Content.RectTransform, GUI.Anchor.TopCenter),
            text,
            nil,
            nil,
            GUI.Alignment.Center
        )
        emptyText.TextColor = Color(200, 200, 200)
    else
        local hudScale = 1
        pcall(function() hudScale = tonumber(GameSettings.CurrentConfig.Graphics.HUDScale) or 1 end)
        local iconSize = math.floor(64 / math.max(0.8, hudScale))
        local iconWidth = math.max(1, math.floor(iconSize * GUI.xScale))
        local iconHeight = math.max(1, math.floor(iconSize * GUI.yScale))
        local listRect = DBClient.List.Content and DBClient.List.Content.Rect or nil
        local listWidth = listRect and math.max(1, listRect.Width) or 900
        local listHeight = listRect and math.max(1, listRect.Height) or 560
        local maxIconsPerRow = math.max(4, math.min(10, math.floor(listWidth / (iconWidth + 4))))
        local rowHeight = math.max(0.08, math.min(0.18, (iconHeight + 6) / listHeight))
        local totalRows = math.max(1, math.ceil(#entries / maxIconsPerRow))
        local rowLists = {}

        for row = 1, totalRows do
            rowLists[row] = GUI.ListBox(
                GUI.RectTransform(Vector2(1, rowHeight), DBClient.List.Content.RectTransform, GUI.Anchor.TopLeft),
                true
            )
        end

        DBClient.State.renderToken = (tonumber(DBClient.State.renderToken) or 0) + 1
        local token = DBClient.State.renderToken

        local function DrawNextIcon(index)
            if token ~= DBClient.State.renderToken then
                return
            end

            local entry = entries[index]
            if entry == nil then
                return
            end

            local currentRow = math.floor((index - 1) / maxIconsPerRow) + 1
            local rowList = rowLists[currentRow]
            if rowList ~= nil and rowList.Content ~= nil then
                local button = GUI.Button(
                    GUI.RectTransform(Point(iconSize * GUI.xScale, iconSize * GUI.yScale), rowList.Content.RectTransform)
                )
                button.Color = Color(0, 0, 0, 0)
                button.HoverColor = Color(80, 80, 80, 150)
                button.PressedColor = Color(120, 120, 120, 200)
                button.SelectedColor = Color(0, 0, 0, 0)
                button.RectTransform.MinSize = Point(0, iconHeight)

                local icon = ResolveIconSprite(entry)
                if icon ~= nil then
                    local image = GUI.Image(
                        GUI.RectTransform(Vector2(0.95, 0.95), button.RectTransform, GUI.Anchor.Center),
                        icon
                    )
                    image.ToolTip = BuildEntryTooltip(entry)
                else
                    local fallback = GUI.TextBlock(
                        GUI.RectTransform(Vector2(1, 1), button.RectTransform, GUI.Anchor.Center),
                        "?",
                        nil,
                        nil,
                        GUI.Alignment.Center
                    )
                    fallback.TextColor = Color(220, 220, 220)
                    button.ToolTip = BuildEntryTooltip(entry)
                end

                button.OnClicked = function()
                    RequestTake(entry)
                    return true
                end
            end

            Timer.NextFrame(function()
                DrawNextIcon(index + 1)
            end)
        end

        DrawNextIcon(1)
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
            SendUnsubscribe()
            ResetViewState(true)
        end
        return
    end

    DBClient.Panel.Visible = true
    if DBClient.State.activeTerminal ~= terminal then
        SendUnsubscribe()
        DBClient.State.activeTerminal = terminal
        DBClient.State.databaseId = GetTerminalDatabaseId(terminal)
        DBClient.State.subscribedTerminalId = ""
        DBClient.State.lastLocalSync = 0
        ResetViewState(false)
        SendSubscribe(terminal, true)
    end

    if not Game.IsMultiplayer then
        local now = Now()
        if (now - (DBClient.State.lastLocalSync or 0)) > 0.30 then
            local localMap, totalEntries, totalAmount = BuildLocalEntryMap(terminal)
            DBClient.State.entriesByKey = localMap
            DBClient.State.totalEntries = totalEntries
            DBClient.State.totalAmount = totalAmount
            DBClient.State.serial = DBClient.State.serial + 1
            DBClient.State.lastLocalSync = now
            DBClient.State.awaitingSnapshot = false
            DBClient.State.dirty = true
        end
    else
        if DBClient.State.awaitingSnapshot and (Now() - (DBClient.State.lastSubscribeAt or 0)) > 1.2 then
            SendSubscribe(terminal, true)
        end
    end

    local now = Now()
    if DBClient.State.dirty or (now - (DBClient.State.lastRefresh or 0)) > 0.35 then
        DBClient.State.lastRefresh = now
        RedrawList()
    end
end)

Networking.Receive(DBClient.NetViewSnapshot, function(message)
    ApplySnapshot(message)
end)

Networking.Receive(DBClient.NetViewDelta, function(message)
    ApplyDelta(message)
end)

Networking.Receive(DBClient.NetTakeResult, function(message)
    local success = message.ReadBoolean()
    local text = message.ReadString()
    if text == nil or text == "" then
        text = success
            and L("dbiotest.ui.take.success", "Item moved to terminal buffer.")
            or L("dbiotest.ui.take.failed", "Failed to transfer item.")
    end

    GUI.AddMessage(text, success and Color.Lime or Color.Red)
    if success and DBClient.State.activeTerminal ~= nil then
        SendSubscribe(DBClient.State.activeTerminal, true)
    end
    DBClient.State.dirty = true
end)
