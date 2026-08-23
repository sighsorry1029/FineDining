using System;
using BepInEx.Configuration;
using ServerSync;

namespace FineDining;

internal static class DietModule
{
    private static bool _initialized;
    private static bool _reconcileRequested;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        if (_initialized)
        {
            return;
        }

        DietConfig.Initialize(config, configSync);
        DietConfig.MaxFoodSlots.SettingChanged += FoodStateShapeChanged;
        DietConfig.RecentHistorySize.SettingChanged += FoodStateShapeChanged;
        DietConfig.ChefCollectionSize.SettingChanged += FoodStateShapeChanged;
        FineDiningLocalization.OnLocalizationComplete += HudFoodPanels.ResetAll;
        _reconcileRequested = true;
        _initialized = true;
    }

    internal static void Tick()
    {
        if (!_initialized || !_reconcileRequested)
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        _reconcileRequested = false;
        HudFoodPanels.ResetAll();
        TrimExcessFoods(player);
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        ChefCollectionService.EnsureChefCollection(player, state);
        FoodStateStore.SaveState(player, state);
        PlayerFoodLogic.RefreshFoodStats(player);
    }

    internal static void Shutdown()
    {
        if (_initialized)
        {
            DietConfig.MaxFoodSlots.SettingChanged -= FoodStateShapeChanged;
            DietConfig.RecentHistorySize.SettingChanged -= FoodStateShapeChanged;
            DietConfig.ChefCollectionSize.SettingChanged -= FoodStateShapeChanged;
            FineDiningLocalization.OnLocalizationComplete -= HudFoodPanels.ResetAll;
        }

        _initialized = false;
        _reconcileRequested = false;
        HudFoodPanels.ResetAll();
        FoodStateStore.Reset();
        DietConfig.Shutdown();
    }

    private static void FoodStateShapeChanged(object sender, EventArgs e)
    {
        _reconcileRequested = true;
    }

    private static void TrimExcessFoods(Player player)
    {
        var foods = player.GetFoods();
        int maximum = DietConfig.GetMaxFoodSlots();
        while (foods.Count > maximum)
        {
            int mostDepletedIndex = 0;
            for (int index = 1; index < foods.Count; index++)
            {
                if (foods[index].m_time < foods[mostDepletedIndex].m_time)
                {
                    mostDepletedIndex = index;
                }
            }

            foods.RemoveAt(mostDepletedIndex);
        }
    }
}
