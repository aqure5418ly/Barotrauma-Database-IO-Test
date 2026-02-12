using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;
#if CLIENT
using Microsoft.Xna.Framework;
#endif

public partial class DatabaseFabricatorOrderComponent : ItemComponent, IClientSerializable
{
    [Editable, Serialize(DatabaseIOTest.Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = DatabaseIOTest.Constants.DefaultDatabaseId;

    [Serialize(0, IsPropertySaveable.No, description: "XML action request (1=PullMaterials).")]
    public int XmlActionRequest { get; set; } = 0;

    private string _resolvedDatabaseId = DatabaseIOTest.Constants.DefaultDatabaseId;
    private Fabricator _fabricator;
    private string _pendingIdentifier;
    private int _pendingAmount;
    private double _nextXmlActionAllowedTime;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
    private const double XmlActionCooldownSeconds = 0.4;

#if CLIENT
    private GUIButton _hookedButton;
    private GUIButton.OnClickedHandler _originalActivateHandler;
    private double _nextHookCheckTime;
#endif

    public DatabaseFabricatorOrderComponent(Item item, ContentXElement element) : base(item, element)
    {
        IsActive = true;
    }

    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);
        _fabricator = item.GetComponent<Fabricator>();
    }

    public override void Update(float deltaTime, Camera cam)
    {
#if CLIENT
        if (Timing.TotalTime >= _nextHookCheckTime)
        {
            _nextHookCheckTime = Timing.TotalTime + 0.5;
            EnsureActivateButtonHooked();
        }
#endif

        if (!IsServerAuthority) { return; }
        if (XmlActionRequest != 0 && Timing.TotalTime >= _nextXmlActionAllowedTime)
        {
            int action = XmlActionRequest;
            XmlActionRequest = 0;
            _nextXmlActionAllowedTime = Timing.TotalTime + XmlActionCooldownSeconds;
            if (action == 1)
            {
                HandleOrderFromServerSelection(Character.Controlled);
            }
        }
    }

#if CLIENT
    private void EnsureActivateButtonHooked()
    {
        if (_fabricator == null)
        {
            _fabricator = item.GetComponent<Fabricator>();
        }
        if (_fabricator == null) { return; }

        var button = _fabricator.ActivateButton;
        if (button == null) { return; }

        if (ReferenceEquals(button, _hookedButton)) { return; }

        _hookedButton = button;
        _originalActivateHandler = button.OnClicked;
        button.OnClicked = (btn, obj) =>
        {
            TrySendOrderFromClient();
            if (_originalActivateHandler != null)
            {
                return _originalActivateHandler(btn, obj);
            }
            return true;
        };
    }

    private void TrySendOrderFromClient()
    {
        if (_fabricator == null) { return; }
        Identifier identifier = _fabricator.SelectedItemIdentifier;
        if (identifier.IsEmpty) { return; }

        int amount = Math.Max(1, _fabricator.AmountToFabricate);
        if (GameMain.NetworkMember == null)
        {
            HandleOrderServer(identifier.Value, amount, Character.Controlled);
            return;
        }

        _pendingIdentifier = identifier.Value;
        _pendingAmount = amount;
        item.CreateClientEvent(this);
    }
#endif

    public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
    {
        msg.WriteString(_pendingIdentifier ?? "");
        msg.WriteInt32(_pendingAmount);
        _pendingIdentifier = "";
        _pendingAmount = 0;
    }

    public void ServerEventRead(IReadMessage msg, Client c)
    {
        if (!IsServerAuthority) { return; }
        string identifier = msg.ReadString();
        int amount = msg.ReadInt32();
        if (string.IsNullOrWhiteSpace(identifier) || amount <= 0) { return; }
        HandleOrderServer(identifier, amount, c?.Character);
    }

    private void HandleOrderServer(string targetIdentifier, int amount, Character user)
    {
        if (!IsServerAuthority) { return; }
        if (_fabricator == null)
        {
            _fabricator = item.GetComponent<Fabricator>();
        }
        if (_fabricator == null) { return; }

        var recipe = FindRecipeByIdentifier(_fabricator, targetIdentifier);
        if (recipe == null)
        {
            DebugConsole.NewMessage($"{DatabaseIOTest.Constants.LogPrefix} Fabricator recipe not found: {targetIdentifier}", Microsoft.Xna.Framework.Color.OrangeRed);
            ModFileLog.Write("Fabricator", $"{DatabaseIOTest.Constants.LogPrefix} recipe not found target='{targetIdentifier}' db='{_resolvedDatabaseId}'");
            return;
        }

        var inputContainer = _fabricator.InputContainer;
        if (inputContainer?.Inventory == null)
        {
            DebugConsole.NewMessage($"{DatabaseIOTest.Constants.LogPrefix} Fabricator input container missing for '{targetIdentifier}'.", Microsoft.Xna.Framework.Color.OrangeRed);
            ModFileLog.Write("Fabricator", $"{DatabaseIOTest.Constants.LogPrefix} input container missing target='{targetIdentifier}' db='{_resolvedDatabaseId}'");
            return;
        }

        int batchAmount = Math.Max(1, amount);
        var targetItem = recipe.TargetItem?.Identifier.Value ?? targetIdentifier;
        DebugConsole.NewMessage(
            $"{DatabaseIOTest.Constants.LogPrefix} Recipe '{targetItem}' x{batchAmount} (db='{_resolvedDatabaseId}')",
            Microsoft.Xna.Framework.Color.LightGray);
        ModFileLog.Write(
            "Fabricator",
            $"{DatabaseIOTest.Constants.LogPrefix} order begin target='{targetItem}' amount={batchAmount} db='{_resolvedDatabaseId}' user='{user?.Name ?? "none"}'");

        var allTaken = new List<ItemData>();
        int reqIndex = 0;
        foreach (var required in GetRequiredItems(recipe))
        {
            reqIndex++;
            int perCraft = Math.Max(0, required.Amount);
            int need = perCraft * batchAmount;
            if (need <= 0) { continue; }

            var allowedIds = GetAllowedIdentifiers(required);
            if (allowedIds.Count == 0) { continue; }

            bool useCondition = required.UseCondition;
            float minCondition = required.MinCondition;
            float maxCondition = required.MaxCondition;
            string allowedText = string.Join(",", allowedIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            DebugConsole.NewMessage(
                $"{DatabaseIOTest.Constants.LogPrefix} Req#{reqIndex}: need={need} ids=[{allowedText}]",
                Microsoft.Xna.Framework.Color.Gray);
            ModFileLog.Write(
                "Fabricator",
                $"{DatabaseIOTest.Constants.LogPrefix} order req#{reqIndex} target='{targetItem}' need={need} perCraft={perCraft} ids=[{allowedText}] useCondition={useCondition} min={minCondition:0.##} max={maxCondition:0.##}");

            bool ok = DatabaseStore.TryTakeItemsForAutomation(
                _resolvedDatabaseId,
                data =>
                {
                    if (data == null) { return false; }
                    if (!allowedIds.Contains(data.Identifier)) { return false; }
                    return MatchesRequiredCondition(data, useCondition, minCondition, maxCondition);
                },
                need,
                out var taken,
                DatabaseStore.TakePolicy.Fifo);

            if (!ok)
            {
                if (allTaken.Count > 0)
                {
                    DatabaseStore.AppendItems(_resolvedDatabaseId, allTaken);
                }
                ModFileLog.Write(
                    "Fabricator",
                    $"{DatabaseIOTest.Constants.LogPrefix} order fail target='{targetItem}' amount={batchAmount} failedReq#{reqIndex} need={need} ids=[{allowedText}]");
                DebugConsole.NewMessage(
                    $"{DatabaseIOTest.Constants.LogPrefix} Insufficient materials in database for '{targetIdentifier}'.",
                    Microsoft.Xna.Framework.Color.Orange);
                return;
            }

            allTaken.AddRange(taken);
        }

        if (allTaken.Count == 0) { return; }
        ModFileLog.Write(
            "Fabricator",
            $"{DatabaseIOTest.Constants.LogPrefix} order success target='{targetItem}' amount={batchAmount} consumed={CountFlatItems(allTaken)} entries={allTaken.Count}");
        SpawnService.SpawnItemsIntoInventory(allTaken, inputContainer.Inventory, user);
        _fabricator.RefreshAvailableIngredients();
    }

    private void HandleOrderFromServerSelection(Character user)
    {
        if (!IsServerAuthority) { return; }
        if (_fabricator == null)
        {
            _fabricator = item.GetComponent<Fabricator>();
        }
        if (_fabricator == null) { return; }

        Identifier identifier = _fabricator.SelectedItemIdentifier;
        if (identifier.IsEmpty) { return; }
        int amount = Math.Max(1, _fabricator.AmountToFabricate);
        HandleOrderServer(identifier.Value, amount, user);
    }

    private static IEnumerable<FabricationRecipe.RequiredItem> GetRequiredItems(FabricationRecipe recipe)
    {
        if (recipe?.RequiredItems == null) { yield break; }
        foreach (var req in recipe.RequiredItems)
        {
            if (req != null) { yield return req; }
        }
    }

    private static HashSet<string> GetAllowedIdentifiers(FabricationRecipe.RequiredItem required)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (required == null) { return result; }

        try
        {
            if (required.ItemPrefabs != null)
            {
                foreach (var prefab in required.ItemPrefabs)
                {
                    if (prefab?.Identifier != null)
                    {
                        result.Add(prefab.Identifier.Value);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        if (result.Count == 0 && required.FirstMatchingPrefab?.Identifier != null)
        {
            result.Add(required.FirstMatchingPrefab.Identifier.Value);
        }

        return result;
    }

    private static FabricationRecipe FindRecipeByIdentifier(Fabricator fabricator, string identifier)
    {
        if (fabricator == null || string.IsNullOrWhiteSpace(identifier)) { return null; }
        string target = identifier.Trim();

        foreach (var recipe in GetFabricatorRecipes(fabricator))
        {
            var targetItem = recipe?.TargetItem;
            if (targetItem?.Identifier == null) { continue; }
            if (string.Equals(targetItem.Identifier.Value, target, StringComparison.OrdinalIgnoreCase))
            {
                return recipe;
            }
        }

        return null;
    }

    private static IEnumerable<FabricationRecipe> GetFabricatorRecipes(Fabricator fabricator)
    {
        if (fabricator == null) { yield break; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(Fabricator).GetField("fabricationRecipes", flags);
        var value = field?.GetValue(fabricator);
        if (value == null) { yield break; }

        if (value is IEnumerable<FabricationRecipe> directList)
        {
            foreach (var recipe in directList)
            {
                if (recipe != null) { yield return recipe; }
            }
            yield break;
        }

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is FabricationRecipe recipe)
                {
                    yield return recipe;
                }
            }
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                if (entry is FabricationRecipe recipe)
                {
                    yield return recipe;
                }
            }
        }
    }

    private static int CountFlatItems(IEnumerable<ItemData> items)
    {
        int count = 0;
        if (items == null) { return 0; }
        foreach (var item in items)
        {
            if (item == null) { continue; }
            count += Math.Max(1, item.StackSize);
        }
        return count;
    }

    private static bool MatchesRequiredCondition(ItemData data, bool useCondition, float minCondition, float maxCondition)
    {
        if (!useCondition) { return true; }
        if (data == null) { return false; }

        float min = minCondition;
        float max = maxCondition;
        if (min > max)
        {
            float tmp = min;
            min = max;
            max = tmp;
        }

        const float eps = 0.0001f;
        float cond = data.Condition;

        // 1) Try direct condition compare (some recipes use absolute condition values).
        if (cond >= min - eps && cond <= max + eps)
        {
            return true;
        }

        // 2) If recipe range looks normalized (0..1), also accept percentage-scaled condition.
        if (max <= 1.0001f && cond > 1.0001f)
        {
            float condPercent = cond / 100f;
            if (condPercent >= min - eps && condPercent <= max + eps)
            {
                return true;
            }
        }

        return false;
    }
}
