using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;

namespace DatabaseIOTest.Services
{
    public sealed class RecipeRequiredInfo
    {
        public RecipeRequiredInfo(
            IReadOnlyCollection<string> allowedIdentifiers,
            int perCraftAmount,
            int totalAmount,
            bool useCondition,
            float minCondition,
            float maxCondition)
        {
            AllowedIdentifiers = allowedIdentifiers ?? Array.Empty<string>();
            PerCraftAmount = Math.Max(0, perCraftAmount);
            TotalAmount = Math.Max(0, totalAmount);
            UseCondition = useCondition;
            MinCondition = minCondition;
            MaxCondition = maxCondition;
        }

        public IReadOnlyCollection<string> AllowedIdentifiers { get; }
        public int PerCraftAmount { get; }
        public int TotalAmount { get; }
        public bool UseCondition { get; }
        public float MinCondition { get; }
        public float MaxCondition { get; }
    }

    public sealed class SelectedRecipeInfo
    {
        public SelectedRecipeInfo(string identifier, int amount, IReadOnlyList<RecipeRequiredInfo> requiredItems)
        {
            Identifier = identifier ?? "";
            Amount = Math.Max(1, amount);
            RequiredItems = requiredItems ?? Array.Empty<RecipeRequiredInfo>();
        }

        public string Identifier { get; }
        public int Amount { get; }
        public IReadOnlyList<RecipeRequiredInfo> RequiredItems { get; }
    }

    public static class FabricatorRecipeResolver
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo FabricationRecipesField =
            typeof(Fabricator).GetField("fabricationRecipes", BindingFlags.Instance | BindingFlags.NonPublic);
        private static double _nextPerfLogAt;
        private static int _resolveCalls;
        private static int _fastIdentifierHits;
        private static int _reflectionIdentifierHits;
        private static int _resolveFailures;
        private static int _recipeLookupCalls;
        private static int _recipeLookupMisses;
        private static int _requiredItemRows;

        private static void FlushPerfIfDue()
        {
            if (!ModFileLog.IsDebugEnabled) { return; }
            if (Timing.TotalTime < _nextPerfLogAt) { return; }
            _nextPerfLogAt = Timing.TotalTime + 1.0;
            if (_resolveCalls <= 0 && _recipeLookupCalls <= 0) { return; }

            ModFileLog.WriteDebug(
                "Perf",
                $"{DatabaseIOTest.Constants.LogPrefix} RecipeResolverPerf resolveCalls={_resolveCalls} " +
                $"fastId={_fastIdentifierHits} reflId={_reflectionIdentifierHits} resolveFail={_resolveFailures} " +
                $"recipeLookups={_recipeLookupCalls} lookupMiss={_recipeLookupMisses} reqRows={_requiredItemRows}");

            _resolveCalls = 0;
            _fastIdentifierHits = 0;
            _reflectionIdentifierHits = 0;
            _resolveFailures = 0;
            _recipeLookupCalls = 0;
            _recipeLookupMisses = 0;
            _requiredItemRows = 0;
        }

        public static bool TryResolveSelectedRecipe(Fabricator fabricator, bool isServerAuthority, out SelectedRecipeInfo info)
        {
            info = null;
            if (fabricator == null) { return false; }
            if (ModFileLog.IsDebugEnabled)
            {
                _resolveCalls++;
                FlushPerfIfDue();
            }

            string identifier = "";
            if (TryResolveSelectedIdentifierFast(fabricator, out identifier))
            {
                if (ModFileLog.IsDebugEnabled) { _fastIdentifierHits++; }
            }
            else
            {
                if (!TryResolveSelectedIdentifierByReflection(fabricator, out identifier))
                {
                    if (ModFileLog.IsDebugEnabled) { _resolveFailures++; }
                    return false;
                }
                if (ModFileLog.IsDebugEnabled) { _reflectionIdentifierHits++; }
            }

            int amount = ResolveSelectedAmountFast(fabricator);
            if (amount <= 0)
            {
                amount = ResolveSelectedAmountByReflection(fabricator);
            }
            amount = Math.Max(1, amount);

            var recipe = FindRecipeByIdentifier(fabricator, identifier);
            if (recipe == null && isServerAuthority)
            {
                // Dedicated server path is expected to be strict.
                if (ModFileLog.IsDebugEnabled) { _resolveFailures++; }
                return false;
            }
            if (recipe == null)
            {
                if (ModFileLog.IsDebugEnabled) { _resolveFailures++; }
                return false;
            }

            var required = new List<RecipeRequiredInfo>();
            foreach (var req in GetRequiredItems(recipe))
            {
                if (req == null) { continue; }
                if (ModFileLog.IsDebugEnabled) { _requiredItemRows++; }
                int perCraft = Math.Max(0, req.Amount);
                int total = perCraft * amount;
                var allowed = GetAllowedIdentifiers(req)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (allowed.Count <= 0) { continue; }

                required.Add(new RecipeRequiredInfo(
                    allowed,
                    perCraft,
                    total,
                    req.UseCondition,
                    req.MinCondition,
                    req.MaxCondition));
            }

            info = new SelectedRecipeInfo(identifier, amount, required);
            return true;
        }

        public static IEnumerable<FabricationRecipe.RequiredItem> GetRequiredItems(FabricationRecipe recipe)
        {
            if (recipe?.RequiredItems == null) { yield break; }
            foreach (var req in recipe.RequiredItems)
            {
                if (req != null) { yield return req; }
            }
        }

        public static HashSet<string> GetAllowedIdentifiers(FabricationRecipe.RequiredItem required)
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
                // Ignore resolver exceptions and fallback to FirstMatchingPrefab.
            }

            if (result.Count == 0 && required.FirstMatchingPrefab?.Identifier != null)
            {
                result.Add(required.FirstMatchingPrefab.Identifier.Value);
            }

            return result;
        }

        public static FabricationRecipe FindRecipeByIdentifier(Fabricator fabricator, string identifier)
        {
            if (fabricator == null || string.IsNullOrWhiteSpace(identifier)) { return null; }
            if (ModFileLog.IsDebugEnabled) { _recipeLookupCalls++; }
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

            if (ModFileLog.IsDebugEnabled) { _recipeLookupMisses++; }
            return null;
        }

        public static IEnumerable<FabricationRecipe> GetFabricatorRecipes(Fabricator fabricator)
        {
            if (fabricator == null) { yield break; }

            var value = FabricationRecipesField?.GetValue(fabricator);
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

        private static bool TryResolveSelectedIdentifierFast(Fabricator fabricator, out string identifier)
        {
            identifier = "";
            if (fabricator == null) { return false; }

            try
            {
                Identifier selected = fabricator.SelectedItemIdentifier;
                if (!selected.IsEmpty)
                {
                    identifier = selected.Value?.Trim() ?? "";
                }
            }
            catch
            {
                identifier = "";
            }

            return !string.IsNullOrWhiteSpace(identifier);
        }

        private static int ResolveSelectedAmountFast(Fabricator fabricator)
        {
            if (fabricator == null) { return 0; }
            try
            {
                return Math.Max(1, fabricator.AmountToFabricate);
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryResolveSelectedIdentifierByReflection(Fabricator fabricator, out string identifier)
        {
            identifier = "";
            if (fabricator == null) { return false; }

            bool TryReadIdentifierFromValue(object value, out string id)
            {
                id = "";
                if (value == null) { return false; }

                if (value is Identifier baroId)
                {
                    if (baroId.IsEmpty) { return false; }
                    id = baroId.Value;
                    return !string.IsNullOrWhiteSpace(id);
                }

                if (value is string str)
                {
                    id = str?.Trim() ?? "";
                    return !string.IsNullOrWhiteSpace(id);
                }

                if (value is FabricationRecipe recipe && recipe.TargetItem?.Identifier != null)
                {
                    id = recipe.TargetItem.Identifier.Value;
                    return !string.IsNullOrWhiteSpace(id);
                }

                return false;
            }

            foreach (string memberName in new[] { "SelectedItemIdentifier", "selectedItemIdentifier" })
            {
                var prop = fabricator.GetType().GetProperty(memberName, AnyInstance);
                if (prop != null && TryReadIdentifierFromValue(prop.GetValue(fabricator), out identifier))
                {
                    return true;
                }

                var field = fabricator.GetType().GetField(memberName, AnyInstance);
                if (field != null && TryReadIdentifierFromValue(field.GetValue(fabricator), out identifier))
                {
                    return true;
                }
            }

            foreach (string memberName in new[] { "SelectedRecipe", "selectedRecipe", "SelectedItem", "selectedItem" })
            {
                var prop = fabricator.GetType().GetProperty(memberName, AnyInstance);
                if (prop != null && TryReadIdentifierFromValue(prop.GetValue(fabricator), out identifier))
                {
                    return true;
                }

                var field = fabricator.GetType().GetField(memberName, AnyInstance);
                if (field != null && TryReadIdentifierFromValue(field.GetValue(fabricator), out identifier))
                {
                    return true;
                }
            }

            int selectedIndex = -1;
            foreach (string memberName in new[] { "SelectedItemIndex", "selectedItemIndex", "selectedIndex" })
            {
                var prop = fabricator.GetType().GetProperty(memberName, AnyInstance);
                if (prop != null)
                {
                    object value = prop.GetValue(fabricator);
                    if (value != null && int.TryParse(value.ToString(), out selectedIndex))
                    {
                        break;
                    }
                }

                var field = fabricator.GetType().GetField(memberName, AnyInstance);
                if (field != null)
                {
                    object value = field.GetValue(fabricator);
                    if (value != null && int.TryParse(value.ToString(), out selectedIndex))
                    {
                        break;
                    }
                }
            }

            if (selectedIndex >= 0)
            {
                var recipes = GetFabricatorRecipes(fabricator).ToList();
                if (selectedIndex < recipes.Count)
                {
                    identifier = recipes[selectedIndex]?.TargetItem?.Identifier.Value ?? "";
                }
            }

            identifier = identifier?.Trim() ?? "";
            return !string.IsNullOrWhiteSpace(identifier);
        }

        private static int ResolveSelectedAmountByReflection(Fabricator fabricator)
        {
            if (fabricator == null) { return 1; }

            foreach (string memberName in new[] { "AmountToFabricate", "amountToFabricate", "SelectedAmount", "selectedAmount" })
            {
                var prop = fabricator.GetType().GetProperty(memberName, AnyInstance);
                if (prop != null)
                {
                    object value = prop.GetValue(fabricator);
                    if (value != null && int.TryParse(value.ToString(), out int parsed))
                    {
                        return Math.Max(1, parsed);
                    }
                }

                var field = fabricator.GetType().GetField(memberName, AnyInstance);
                if (field != null)
                {
                    object value = field.GetValue(fabricator);
                    if (value != null && int.TryParse(value.ToString(), out int parsed))
                    {
                        return Math.Max(1, parsed);
                    }
                }
            }

            return 1;
        }
    }
}
