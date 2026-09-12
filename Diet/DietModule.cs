using System;
using BepInEx.Configuration;
using ServerSync;

namespace FineDining;

internal static class DietModule
{
    private static bool _initialized;
    private static bool _dietReconcileRequested;
    private static bool _chefReconcileRequested;
    private static Player? _chefPlayer;
    private static ObjectDB? _chefObjectDb;
    private static int _chefKnownRecipeCount = -1;
    private static int _chefKnownMaterialCount = -1;
    private static int _chefObjectDbItemCount = -1;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        if (_initialized)
        {
            return;
        }

        DietConfig.Initialize(config, configSync);
        DietConfig.MaxFoodSlots.SettingChanged += FoodStateShapeChanged;
        DietConfig.FoodStatScale.SettingChanged += FoodStateShapeChanged;
        DietConfig.RecentHistorySize.SettingChanged += FoodStateShapeChanged;
        DietConfig.ChefCollectionSize.SettingChanged += FoodStateShapeChanged;
        DietConfig.ChefMultiplierMin.SettingChanged += FoodStateShapeChanged;
        DietConfig.ChefMultiplierMax.SettingChanged += FoodStateShapeChanged;
        FineDiningLocalization.OnLocalizationComplete += HudFoodPanels.ResetAll;
        CookingStationAutoPopSystem.Reset();
        FermenterCookingBonusSystem.ResetRuntime();
        _dietReconcileRequested = true;
        _chefReconcileRequested = true;
        _initialized = true;
    }

    internal static void Tick()
    {
        if (!_initialized ||
            !_dietReconcileRequested && !_chefReconcileRequested)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        bool reconcileDiet = _dietReconcileRequested && ChefFoodTierCatalog.IsReady;
        bool reconcileChef = _chefReconcileRequested && ChefFoodTierCatalog.IsReady;
        if (!reconcileDiet && !reconcileChef)
        {
            return;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(player);
        if (reconcileDiet)
        {
            _dietReconcileRequested = false;
            HudFoodPanels.ResetAll();
            FoodSlotProgression.ReconcileConfiguration(player, state);
            FoodSlotProgression.TrimExcessFoods(player, state);
        }

        if (reconcileChef)
        {
            _chefReconcileRequested = false;
            ChefCollectionService.EnsureChefCollection(player, state);
        }

        FoodStateStore.SaveState(player, state);
        if (reconcileDiet)
        {
            PlayerFoodLogic.RefreshFoodStats(player);
        }
    }

    internal static void Shutdown()
    {
        if (_initialized)
        {
            DietConfig.MaxFoodSlots.SettingChanged -= FoodStateShapeChanged;
            DietConfig.FoodStatScale.SettingChanged -= FoodStateShapeChanged;
            DietConfig.RecentHistorySize.SettingChanged -= FoodStateShapeChanged;
            DietConfig.ChefCollectionSize.SettingChanged -= FoodStateShapeChanged;
            DietConfig.ChefMultiplierMin.SettingChanged -= FoodStateShapeChanged;
            DietConfig.ChefMultiplierMax.SettingChanged -= FoodStateShapeChanged;
            FineDiningLocalization.OnLocalizationComplete -= HudFoodPanels.ResetAll;
        }

        _initialized = false;
        _dietReconcileRequested = false;
        _chefReconcileRequested = false;
        HudFoodPanels.Shutdown();
        CookingSkillTooltipPanel.Clear();
        FoodStateStore.Reset();
        FoodSlotProgression.Reset();
        CookingStationAutoPopSystem.Reset();
        FermenterCookingBonusSystem.ResetRuntime();
        DietConfig.Shutdown();
    }

    internal static PlayerFoodStateData ReconcileChefCollectionForHud(Player player)
    {
        // Keep this reconciliation at the existing HUD call site. Moving it to
        // Tick would change when a hidden or rebuilt HUD consumes Chef rolls.
        bool refreshChefCollection = ShouldRefreshChefCollection(player);
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        if (refreshChefCollection)
        {
            if (ChefCollectionService.EnsureChefCollection(player, state))
            {
                FoodStateStore.SaveState(player, state);
            }

            RememberChefCollectionInputs(player);
        }

        return state;
    }

    internal static void ResetChefCollectionInputs()
    {
        _chefPlayer = null;
        _chefObjectDb = null;
        _chefKnownRecipeCount = -1;
        _chefKnownMaterialCount = -1;
        _chefObjectDbItemCount = -1;
    }

    private static bool ShouldRefreshChefCollection(Player player)
    {
        ObjectDB objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return false;
        }

        return _chefPlayer != player
               || _chefObjectDb != objectDb
               || _chefKnownRecipeCount != PlayerPrivateAccess.KnownRecipes(player).Count
               || _chefKnownMaterialCount != PlayerPrivateAccess.KnownMaterials(player).Count
               || _chefObjectDbItemCount != objectDb.m_items.Count;
    }

    private static void RememberChefCollectionInputs(Player player)
    {
        ObjectDB objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return;
        }

        _chefPlayer = player;
        _chefObjectDb = objectDb;
        _chefKnownRecipeCount = PlayerPrivateAccess.KnownRecipes(player).Count;
        _chefKnownMaterialCount = PlayerPrivateAccess.KnownMaterials(player).Count;
        _chefObjectDbItemCount = objectDb.m_items.Count;
    }

    private static void FoodStateShapeChanged(object sender, EventArgs e)
    {
        _dietReconcileRequested = true;
        _chefReconcileRequested = true;
    }

    internal static void InvalidateChefTierCatalog()
    {
        ChefFoodTierCatalog.Invalidate();
        ChefTierReferenceGenerator.Invalidate();
        FoodSlotProgression.Reset();
        _dietReconcileRequested = true;
        RequestChefCollectionReconcile();
    }

    internal static void RequestChefCollectionReconcile()
    {
        _chefReconcileRequested = true;
    }

    internal static void RequestDietReconcile()
    {
        _dietReconcileRequested = true;
    }


}
