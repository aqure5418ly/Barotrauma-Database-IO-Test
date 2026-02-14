Networking.Receive("SpawnMenuMod_RequestAffliction", function(msg, sender)
    if (not sender.HasPermission(0x80)) then
        return
    end

    local aff = AfflictionPrefab.Prefabs[ msg.ReadString() ]

    local charName = msg.ReadString() 
    local char

    for k, v in ipairs(Character.CharacterList) do
        if v.DisplayName == charName then
            char = v
            break
        end
    end

    if not char then
        return
    end

    local limb = char.AnimController.GetLimb( msg.ReadInt16() )

    local intensity = msg.ReadInt16()

    char.CharacterHealth.ApplyAffliction(limb, aff.Instantiate(intensity))
end)