using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;
#if CLIENT
using Microsoft.Xna.Framework;
#endif

public partial class DatabaseFabricatorOrderComponent : ItemComponent, IClientSerializable, IServerSerializable
{
    [Editable, Serialize(DatabaseIOTest.Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = DatabaseIOTest.Constants.DefaultDatabaseId;

    [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Automatically hook Fabricator activate button to trigger DB Fill.")]
    public bool AutoHookActivateButton { get; set; } = true;

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

        // XML CustomInterface button runs on client. Forward it explicitly to the server.
        if (XmlActionRequest != 0 && Timing.TotalTime >= _nextXmlActionAllowedTime)
        {
            int action = XmlActionRequest;
            XmlActionRequest = 0;
            _nextXmlActionAllowedTime = Timing.TotalTime + XmlActionCooldownSeconds;
            if (action == 1)
            {
                ModFileLog.Write(
                    "Fabricator",
                    $"{DatabaseIOTest.Constants.LogPrefix} db fill client request db='{_resolvedDatabaseId}' itemId={item?.ID}");
                RequestDbFillFromClient();
            }
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
                ModFileLog.Write(
                    "Fabricator",
                    $"{DatabaseIOTest.Constants.LogPrefix} db fill server request db='{_resolvedDatabaseId}' itemId={item?.ID}");
                RequestDbFillFromClient();
            }
        }
    }

#if CLIENT
    private void EnsureActivateButtonHooked()
    {
        if (!AutoHookActivateButton)
        {
            if (_hookedButton != null)
            {
                _hookedButton.OnClicked = _originalActivateHandler;
                _hookedButton = null;
                _originalActivateHandler = null;
            }
            return;
        }

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
        if (_fabricator == null)
        {
            ModFileLog.Write(
                "Fabricator",
                $"{DatabaseIOTest.Constants.LogPrefix} client order ignored: fabricator component missing db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }
        if (!FabricatorRecipeResolver.TryResolveSelectedRecipe(_fabricator, isServerAuthority: false, out var selected) ||
            selected == null ||
            string.IsNullOrWhiteSpace(selected.Identifier))
        {
            ModFileLog.Write(
                "Fabricator",
                $"{DatabaseIOTest.Constants.LogPrefix} client order ignored: empty selection db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        string identifier = selected.Identifier;
        int amount = Math.Max(1, selected.Amount);
        if (GameMain.NetworkMember == null)
        {
            HandleOrderServer(identifier, amount, Character.Controlled);
            return;
        }

        _pendingIdentifier = identifier;
        _pendingAmount = amount;
        item.CreateClientEvent(this);
    }
#endif

    public bool RequestDbFillFromClient()
    {
        if (_fabricator == null)
        {
            _fabricator = item.GetComponent<Fabricator>();
        }
        if (_fabricator == null) { return false; }

#if CLIENT
        if (GameMain.NetworkMember?.IsClient == true)
        {
            TrySendOrderFromClient();
            return true;
        }
#endif

        if (IsServerAuthority)
        {
            HandleOrderFromServerSelection(Character.Controlled);
            return true;
        }

        return false;
    }

    public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
    {
        msg.WriteString(_pendingIdentifier ?? "");
        msg.WriteInt32(_pendingAmount);
        _pendingIdentifier = "";
        _pendingAmount = 0;
    }

    // This component is mainly client->server, but on dedicated servers the engine may still
    // route a server->client component event. Provide a no-op payload to keep network parsing stable.
    public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
    {
    }

    public void ClientEventRead(IReadMessage msg, float sendingTime)
    {
    }

    public void ServerEventRead(IReadMessage msg, Client c)
    {
        if (!IsServerAuthority) { return; }
        string identifier = msg.ReadString();
        int amount = msg.ReadInt32();
        if (string.IsNullOrWhiteSpace(identifier) || amount <= 0)
        {
            ModFileLog.Write(
                "Fabricator",
                $"{DatabaseIOTest.Constants.LogPrefix} net order ignored by='{c?.Name ?? c?.Character?.Name ?? "unknown"}' target='{identifier ?? ""}' amount={amount} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }
        ModFileLog.Write(
            "Fabricator",
            $"{DatabaseIOTest.Constants.LogPrefix} net order request by='{c?.Name ?? c?.Character?.Name ?? "unknown"}' target='{identifier}' amount={amount} db='{_resolvedDatabaseId}' itemId={item?.ID}");
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

        var recipe = FabricatorRecipeResolver.FindRecipeByIdentifier(_fabricator, targetIdentifier);
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
        foreach (var required in FabricatorRecipeResolver.GetRequiredItems(recipe))
        {
            reqIndex++;
            int perCraft = Math.Max(0, required.Amount);
            int need = perCraft * batchAmount;
            if (need <= 0) { continue; }

            var allowedIds = FabricatorRecipeResolver.GetAllowedIdentifiers(required);
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

        if (!FabricatorRecipeResolver.TryResolveSelectedRecipe(_fabricator, isServerAuthority: true, out var selected) ||
            selected == null ||
            string.IsNullOrWhiteSpace(selected.Identifier))
        {
            DebugConsole.NewMessage(
                $"{DatabaseIOTest.Constants.LogPrefix} DB Fill failed: could not resolve current fabricator selection.",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write(
                "Fabricator",
                $"{DatabaseIOTest.Constants.LogPrefix} db fill resolve failed db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        HandleOrderServer(selected.Identifier, selected.Amount, user);
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


