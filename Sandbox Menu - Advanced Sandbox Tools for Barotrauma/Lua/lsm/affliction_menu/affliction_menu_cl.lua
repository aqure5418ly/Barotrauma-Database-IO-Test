SandboxMenu.Afflictions = SandboxMenu.Afflictions or {}
SandboxMenu.Afflictions.HiddenAfflictionIds = {  
    "luabotomy"
} 
SandboxMenu.Afflictions.AllAfflictions = AfflictionPrefab.Prefabs.Keys  
SandboxMenu.Afflictions.TargetPlayer = Character.Controlled and Character.Controlled.DisplayName or LSM.ML("affliction_not_selected")
SandboxMenu.Afflictions.SelectedLimb = 1  
SandboxMenu.Afflictions.Intensity = 50  
SandboxMenu.Afflictions.LastGlobalUpdate = 0  
SandboxMenu.Afflictions.UpdateInterval = 1.0
SandboxMenu.Afflictions.SelectedAffliction = "burn"  
SandboxMenu.Afflictions.StrengthHistory = {}
SandboxMenu.Afflictions.SearchName = ""
  
local function sortObj(objA, objB)  
    return AfflictionPrefab.Prefabs[tostring(objA)].Name < AfflictionPrefab.Prefabs[tostring(objB)].Name  
end  
table.sort(SandboxMenu.Afflictions.AllAfflictions, sortObj)  
  
local afflictionsTab = GUI.Frame(GUI.RectTransform(Vector2(0.995, 0.935), SandboxMenu.menuContent.RectTransform, GUI.Anchor.TopCenter))  
afflictionsTab.RectTransform.AbsoluteOffset = Point(0, 65)  
afflictionsTab.Visible = false  
  
local selectedPlayer = GUI.DropDown(GUI.RectTransform(Vector2(0.15, 0.05), afflictionsTab.RectTransform, GUI.Anchor.TopLeft), SandboxMenu.Afflictions.TargetPlayer, 6, nil, false)  
selectedPlayer.RectTransform.AbsoluteOffset = Point(27, 15)  
  
for k, v in ipairs(Character.CharacterList) do  
    selectedPlayer.AddItem(v.DisplayName, v.DisplayName)  
end   
  
selectedPlayer.OnSelected = function(s, obj)
    SandboxMenu.Afflictions.TargetPlayer = obj
    SandboxMenu.Afflictions.UpdateAllStrengthTracking()
    SandboxMenu.Afflictions.UpdateCharacterAfflictionsList()
end
  
local intensityText = GUI.TextBlock(GUI.RectTransform(Vector2(0.35, 0.1), afflictionsTab.RectTransform), "50", nil, nil, GUI.Alignment.Center)  
intensityText.RectTransform.AbsoluteOffset = Point(180*GUI.xScale, 15)  
  
local intensity = GUI.ScrollBar(GUI.RectTransform(Vector2(0.35, 0.1), afflictionsTab.RectTransform), 0.1, nil, "GUISlider")  
intensity.RectTransform.AbsoluteOffset = Point(180*GUI.xScale, 15)  
intensity.Range = Vector2(-100, 250)  
intensity.BarScrollValue = 50  
intensity.OnMoved = function ()  
    SandboxMenu.Afflictions.Intensity = math.floor(intensity.BarScrollValue)  
    intensityText.Text = tostring(SandboxMenu.Afflictions.Intensity)  
end  
  
local limbs = {  
    [1] = LSM.ML("affliction_whole_body"),  
    [LimbType.Head] = LSM.ML("affliction_head"),  
    [LimbType.Torso] = LSM.ML("affliction_torso"),  
    [LimbType.LeftArm] = LSM.ML("affliction_left_arm"),  
    [LimbType.RightArm] = LSM.ML("affliction_right_arm"),  
    [LimbType.LeftLeg] = LSM.ML("affliction_left_leg"),  
    [LimbType.RightLeg] = LSM.ML("affliction_right_leg"),  
}  
  
local selectedLimb = GUI.DropDown(GUI.RectTransform(Vector2(0.15, 0.05), afflictionsTab.RectTransform, GUI.Anchor.TopLeft), limbs[SandboxMenu.Afflictions.SelectedLimb], 6, nil, false)  
selectedLimb.RectTransform.AbsoluteOffset = Point(27, 45)  
  
for k, v in pairs(limbs) do  
    selectedLimb.AddItem(v, k)  
end   
  
selectedLimb.OnSelected = function(s, obj)  
    SandboxMenu.Afflictions.SelectedLimb = obj  
end  

local searchAfflictionTextBox = GUI.TextBox(GUI.RectTransform(Vector2(0.25, 0.2), afflictionsTab.RectTransform, GUI.Anchor.TopRight), SandboxMenu.Afflictions.SearchName)
searchAfflictionTextBox.RectTransform.AbsoluteOffset = Point(25 * GUI.xScale, 15)

searchAfflictionTextBox.OnTextChangedDelegate = function(textBox)
    SandboxMenu.Afflictions.SearchName = textBox.Text
    SandboxMenu.Afflictions.UpdateAllAfflictionsList()
end

local function FilterAfflictionsBySearch(unfilteredList)
    local tbl = {}
    local searchText = string.lower(SandboxMenu.Afflictions.SearchName)
    
    if searchText == "" then
        return unfilteredList
    end
    
    for k, v in ipairs(unfilteredList) do
        local prefab = AfflictionPrefab.Prefabs[tostring(v)]
        if prefab then
            local name = string.lower(prefab.Name.Value or "")
            local identifier = string.lower(tostring(v))
            local description = ""
            
            pcall(function()
                local desc = prefab:GetDescription(50, prefab.Description.TargetType.Self)
                if desc and desc ~= "" then
                    description = string.lower(tostring(desc))
                end
            end)
            
            if string.find(name, searchText) or 
               string.find(identifier, searchText) or
               (description ~= "" and string.find(description, searchText)) then
                table.insert(tbl, v)
            end
        end
    end
    
    return tbl
end

local afflictionSelection = GUI.ListBox(GUI.RectTransform(Vector2(0.35, 0.9), afflictionsTab.RectTransform, GUI.Anchor.TopRight))  
afflictionSelection.RectTransform.AbsoluteOffset = Point(50, 50)

local function CreateAfflictionTooltip(prefab, afflictionId)
    local tooltipText = prefab.Name.Value or ""
    
    if prefab.Identifier and prefab.Identifier.Value ~= "" then
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_id") .. prefab.Identifier.Value
    elseif afflictionId then
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_id") .. tostring(afflictionId)
    end
    
    if prefab.IsBuff then
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_type_buff")
    else
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_type_affliction")
    end
    
    if prefab.Duration and prefab.Duration > 0 then
        tooltipText = tooltipText .. string.format(LSM.ML("affliction_tooltip_duration"), prefab.Duration)
    end
    
    pcall(function()
        local desc = prefab:GetDescription(50, prefab.Description.TargetType.Self)
        if desc and desc ~= "" then
            tooltipText = tooltipText .. LSM.ML("affliction_tooltip_description") .. tostring(desc):gsub("(\r?\n)%s*\n", "%1")
        end
    end)
    
    return tooltipText
end

function SandboxMenu.Afflictions.UpdateAllAfflictionsList()
    afflictionSelection.ClearChildren()
    
    local filteredAfflictions = FilterAfflictionsBySearch(SandboxMenu.Afflictions.AllAfflictions)
    
    for k, v in ipairs(filteredAfflictions) do
        local prefab = AfflictionPrefab.Prefabs[tostring(v)]
        if prefab and prefab.Name.Value ~= "" then
            local availableAffliction = GUI.Button(GUI.RectTransform(Vector2(1, 1), afflictionSelection.Content.RectTransform, GUI.Anchor.TopCenter), prefab.Name, GUI.Alignment.Center, "GUIButtonSmall")
            
            local tooltipText = CreateAfflictionTooltip(prefab, v)
            availableAffliction.ToolTip = RichString.Rich(tooltipText)
            
            availableAffliction.OnClicked = function()  
                SandboxMenu.Afflictions.SelectedAffliction = tostring(v)  
                SandboxMenu.Afflictions.RequestAffliction()  
            end
        end
    end
    
    afflictionSelection.RecalculateChildren()
end

SandboxMenu.Afflictions.UpdateAllAfflictionsList()  
  
local characterAfflictionSelection = GUI.ListBox(GUI.RectTransform(Vector2(0.35, 0.75), afflictionsTab.RectTransform, GUI.Anchor.TopLeft))  
characterAfflictionSelection.RectTransform.AbsoluteOffset = Point(25, 90)  
  
function SandboxMenu.Afflictions.TrackStrength(afflictionId, strength)
    if not SandboxMenu.Afflictions.StrengthHistory[afflictionId] then
        SandboxMenu.Afflictions.StrengthHistory[afflictionId] = {}
    end

    local history = SandboxMenu.Afflictions.StrengthHistory[afflictionId]
    local currentTime = Timer.Time or 0

    table.insert(history, {time = currentTime, strength = strength})

    if #history > 20 then
        table.remove(history, 1)
    end
end
  
function SandboxMenu.Afflictions.UpdateAllStrengthTracking()
    local currentTime = Timer.Time

    if currentTime - SandboxMenu.Afflictions.LastGlobalUpdate < SandboxMenu.Afflictions.UpdateInterval then
        return
    end

    local char = SandboxMenu.GetCharacterByName(SandboxMenu.Afflictions.TargetPlayer)
    if not char then return end

    local trackedCount = 0
    for value in char.CharacterHealth.GetAllAfflictions() do
        local prefab = value.Prefab
        if prefab.Identifier.Value ~= "" and value.Strength > 0.01 then
            SandboxMenu.Afflictions.TrackStrength(prefab.Identifier.Value, value.Strength)
            trackedCount = trackedCount + 1
        end
    end

    SandboxMenu.Afflictions.LastGlobalUpdate = currentTime
end

function SandboxMenu.Afflictions.CalculateLinearRegressionRate(history)
    if not history or #history < 2 then
        return nil
    end

    local n = #history
    local sum_t = 0
    local sum_s = 0
    local sum_ts = 0
    local sum_t2 = 0

    local t_offset = history[1].time

    for i = 1, n do
        local t = history[i].time - t_offset
        local s = history[i].strength
        sum_t = sum_t + t
        sum_s = sum_s + s
        sum_ts = sum_ts + t * s
        sum_t2 = sum_t2 + t * t
    end

    local denominator = n * sum_t2 - sum_t * sum_t
    
    if denominator == 0 then
        return nil
    end

    local rate = (n * sum_ts - sum_t * sum_s) / denominator

    return rate
end
  
function SandboxMenu.Afflictions.CalculateTimeUntilRemoval(affliction, prefab, currentStrength)
    if affliction.Duration and affliction.Duration > 0 then    
        return affliction.Duration    
    end    
    
    if prefab.Duration > 0 then    
        return prefab.Duration    
    end    
    
    local afflictionId = prefab.Identifier.Value
    local history = SandboxMenu.Afflictions.StrengthHistory[afflictionId]

    if not history or #history < 3 then
        return nil
    end

    local rate = SandboxMenu.Afflictions.CalculateLinearRegressionRate(history)

    if not rate then
        return nil
    end

    if rate >= -0.001 then
        return nil
    end

    local estimatedTime = -currentStrength / rate

    return estimatedTime > 0 and estimatedTime or nil
end


local function createAfflictionButton(data, isBuff, char)
    local value = data.affliction
    local prefab = data.prefab
    local limbtype

    local button = GUI.Button(GUI.RectTransform(Vector2(1, 0.04), characterAfflictionSelection.Content.RectTransform, GUI.Anchor.TopCenter), "", GUI.Alignment.Center, nil)
      
    local tooltipText = prefab.Name.Value
    if prefab.Identifier and prefab.Identifier.Value ~= "" then
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_id") .. prefab.Identifier.Value
    end
      
    if prefab.LimbSpecific then
        local limb = char.CharacterHealth.GetAfflictionLimb(value)
        if limb then
            limbtype = SandboxMenu.NormalizeLimbType(limb.type)
            tooltipText = tooltipText .. LSM.ML("affliction_tooltip_location") .. limbs[limbtype]
        end
    else
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_location_whole")
    end
      
    local timeUntilRemoval = SandboxMenu.Afflictions.CalculateTimeUntilRemoval(value, prefab, value.Strength)
    if timeUntilRemoval then
        tooltipText = tooltipText .. string.format(LSM.ML("affliction_tooltip_removes_in"), timeUntilRemoval)
    else
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_removes_unknown")
    end

    local desc = prefab:GetDescription(value.Strength, prefab.Description.TargetType.Self)  
    if desc and desc ~= "" then  
        tooltipText = tooltipText .. LSM.ML("affliction_tooltip_desc_label") .. tostring(desc):gsub("(\r?\n)%s*\n", "%1")
    end
    
    button.ToolTip = RichString.Rich(tooltipText)
      
    if prefab.Icon then
        local icon = GUI.Image(GUI.RectTransform(Vector2(0.1, 0.8), button.RectTransform, 0), prefab.Icon, true)
        icon.Color = CharacterHealth.GetAfflictionIconColor(prefab, value.Strength)
        icon.RectTransform.RelativeOffset = Vector2(0, 0.1)
        icon.CanBeFocused = false
    end
      
    local text = prefab.Name.Value .. " ("..tostring(math.floor(value.Strength*10)/10)..")"
      
    if timeUntilRemoval then
        text = text .. string.format(" (%.1fs)", timeUntilRemoval)
    end
      
    if limbtype then
        text = text .. " - " .. limbs[limbtype]
    end
      
    local textBlock = GUI.TextBlock(GUI.RectTransform(Vector2(0.85, 1), button.RectTransform, 1), text, nil, nil, GUI.Alignment.Left)
    textBlock.CanBeFocused = false
    if isBuff then
        textBlock.TextColor = Color.Green
    end
      
    button.OnClicked = function()
        SandboxMenu.Afflictions.SelectedAffliction = prefab.Identifier.Value
        SandboxMenu.Afflictions.SelectedLimb = limbtype or 1
        SandboxMenu.Afflictions.RequestAffliction()
    end
end

function SandboxMenu.Afflictions.UpdateCharacterAfflictionsList()
    local char = SandboxMenu.GetCharacterByName(SandboxMenu.Afflictions.TargetPlayer)
    if not char then return end
  
    local afflictions = {}
    local buffs = {}
      
    for value in char.CharacterHealth.GetAllAfflictions() do
        local prefab = value.Prefab
        local afflictionId = prefab.Identifier.Value
          
        local isHidden = false
        for _, hiddenId in ipairs(SandboxMenu.Afflictions.HiddenAfflictionIds) do
            if afflictionId == hiddenId then
                isHidden = true
                break
            end
        end
          
        if not isHidden and prefab.Name.Value ~= "" and prefab.Name ~= "Lualess" and value.Strength > 0.01 then
            if prefab.IsBuff then
                table.insert(buffs, {affliction = value, prefab = prefab})
            else
                table.insert(afflictions, {affliction = value, prefab = prefab})
            end
        end
    end
  
    characterAfflictionSelection.ClearChildren()
  
    for _, data in ipairs(afflictions) do
        createAfflictionButton(data, false, char)
    end
      
    if #afflictions > 0 and #buffs > 0 then
        local separator = GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.02), characterAfflictionSelection.Content.RectTransform), LSM.ML("affliction_list_separator"), nil, nil, GUI.Alignment.Left)
        separator.TextColor = Color.Green
    end
      
    for _, data in ipairs(buffs) do
        createAfflictionButton(data, true, char)
    end
  
    characterAfflictionSelection.RecalculateChildren()

    if not SandboxMenu.Afflictions.updateTimer then
        SandboxMenu.Afflictions.updateTimer = 0
    end
    
    if not SandboxMenu.Afflictions.autoUpdateTimer then
        SandboxMenu.Afflictions.autoUpdateTimer = Timer.Wait(function()
            if afflictionsTab.Visible then
                SandboxMenu.Afflictions.UpdateAllStrengthTracking()
                SandboxMenu.Afflictions.UpdateCharacterAfflictionsList()
            end
        end, 1000)
    end
end
  
function SandboxMenu.GetCharacterByName(name)  
    for k, v in ipairs(Character.CharacterList) do  
        if v.DisplayName == name then  
            return v  
        end  
    end  
end  
  
function SandboxMenu.NormalizeLimbType(limbtype)  
    if  
        limbtype == LimbType.Head or  
        limbtype == LimbType.Torso or  
        limbtype == LimbType.RightArm or  
        limbtype == LimbType.LeftArm or  
        limbtype == LimbType.RightLeg or  
        limbtype == LimbType.LeftLeg then  
        return limbtype  
    end  
  
    if limbtype == LimbType.LeftForearm or limbtype==LimbType.LeftHand then return LimbType.LeftArm end  
    if limbtype == LimbType.RightForearm or limbtype==LimbType.RightHand then return LimbType.RightArm end  
    if limbtype == LimbType.LeftThigh or limbtype==LimbType.LeftFoot then return LimbType.LeftLeg end  
    if limbtype == LimbType.RightThigh or limbtype==LimbType.RightFoot then return LimbType.RightLeg end  
    if limbtype == LimbType.Waist then return LimbType.Torso end  
  
    return limbtype  
end  
  
function SandboxMenu.Afflictions.RequestAffliction()  
    if (Game.IsSingleplayer) then  
        local char = SandboxMenu.GetCharacterByName(SandboxMenu.Afflictions.TargetPlayer)  
        if not char then  
            return  
        end  
  
        local limb = char.AnimController.GetLimb(SandboxMenu.Afflictions.SelectedLimb)  
        local intensity = SandboxMenu.Afflictions.Intensity  
        local prefab = AfflictionPrefab.Prefabs[SandboxMenu.Afflictions.SelectedAffliction]  
        char.CharacterHealth.ApplyAffliction(limb, prefab.Instantiate(intensity))  
  
        return  
    end  
  
    local netMsg = Networking.Start("SpawnMenuMod_RequestAffliction");  
    netMsg.WriteString(SandboxMenu.Afflictions.SelectedAffliction)  
    netMsg.WriteString(SandboxMenu.Afflictions.TargetPlayer)  
    netMsg.WriteInt16(SandboxMenu.Afflictions.SelectedLimb)  
    netMsg.WriteInt16(SandboxMenu.Afflictions.Intensity)  
  
    Networking.Send(netMsg)  
  
    Timer.Wait(SandboxMenu.Afflictions.UpdateCharacterAfflictionsList, 1000)  
end  
  
local afflictionsButton = GUI.Button(GUI.RectTransform(Vector2(0.1, 0.15), SandboxMenu.menuContent.RectTransform, GUI.Anchor.TopLeft), LSM.ML("affliction_tab_button"), GUI.Alignment.Center, "GUIButtonSmall")  
afflictionsButton.RectTransform.AbsoluteOffset = Point(SandboxMenu.NextButtonXOffset, 25)
SandboxMenu.NextButtonXOffset = SandboxMenu.NextButtonXOffset + 130  
afflictionsButton.OnClicked = function()  
    selectedPlayer.ClearChildren()  
    for k, v in ipairs(Character.CharacterList) do    
        selectedPlayer.AddItem(v.DisplayName, v.DisplayName)    
        if Character.Controlled and v.DisplayName == Character.Controlled.DisplayName then  
            selectedPlayer:SelectItem(v.DisplayName)
            SandboxMenu.Afflictions.UpdateCharacterAfflictionsList()
        end  
    end
    
    SandboxMenu.Afflictions.UpdateAllAfflictionsList()
      
    SandboxMenu.HideAllTabs()  
    afflictionsTab.Visible = true  
end  
  
SandboxMenu.RegisterTab(afflictionsTab)
