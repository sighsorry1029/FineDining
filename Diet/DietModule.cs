using System;
using BepInEx.Configuration;
using Jotunn.Managers;
using ServerSync;

namespace FineDining;

internal static class DietModule
{
    private static bool _initialized;
    private static bool _dietReconcileRequested;
    private static bool _chefReconcileRequested;

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
        ItemManager.OnItemsRegistered += ChefContentRegistered;
        PrefabManager.OnPrefabsRegistered += ChefContentRegistered;
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
            ItemManager.OnItemsRegistered -= ChefContentRegistered;
            PrefabManager.OnPrefabsRegistered -= ChefContentRegistered;
        }

        _initialized = false;
        _dietReconcileRequested = false;
        _chefReconcileRequested = false;
        HudFoodPanels.ResetAll();
        FoodStateStore.Reset();
        FoodSlotProgression.Reset();
        CookingStationAutoPopSystem.Reset();
        FermenterCookingBonusSystem.ResetRuntime();
        DietConfig.Shutdown();
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

    private static void ChefContentRegistered()
    {
        InvalidateChefTierCatalog();
    }

}
