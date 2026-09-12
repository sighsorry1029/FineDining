using System.Collections.Generic;
using HarmonyLib;

namespace FineDining;

[HarmonyPatch(typeof(Terminal), "InitTerminal")]
internal static class DietTerminalCommands
{
    private static bool _registered;

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        new Terminal.ConsoleCommand(
            "fd:rerollchef",
            "Admin only. Rerolls the local player's Chef collection.",
            RerollChef,
            onlyAdmin: true);
        new Terminal.ConsoleCommand(
            "fd:clearrecent",
            "Admin only. Clears the local player's recent food history.",
            ClearRecent,
            onlyAdmin: true);
        new Terminal.ConsoleCommand(
            "fd:printstate",
            "Admin only. Prints the local player's FineDining diet state.",
            PrintState,
            onlyAdmin: true);
    }

    private static void RerollChef(Terminal.ConsoleEventArgs args)
    {
        if (!TryGetLocalPlayer(args, out Player player))
        {
            return;
        }

        ChefCollectionService.RerollAll(player);
        RefreshHud(player);
        args.Context?.AddString("FineDining: Chef collection rerolled.");
    }

    private static void ClearRecent(Terminal.ConsoleEventArgs args)
    {
        if (!TryGetLocalPlayer(args, out Player player))
        {
            return;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(player);
        RecentHistoryService.Clear(state);
        FoodStateStore.SaveState(player, state);
        RefreshHud(player);
        args.Context?.AddString("FineDining: Recent food history cleared.");
    }

    private static void PrintState(Terminal.ConsoleEventArgs args)
    {
        if (!TryGetLocalPlayer(args, out Player player))
        {
            return;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(player);
        args.Context?.AddString("FineDining diet state:");
        args.Context?.AddString(
            $"  KnownFoods: {FoodSlotProgression.GetKnownFoodCount(player)}");
        args.Context?.AddString(
            $"  FoodSlots: {FoodSlotProgression.GetCurrentSlots(player, state)}/{DietConfig.GetMaxFoodSlots()}");
        args.Context?.AddString(
            $"  Recent ({state.Recent.Count}/{DietConfig.GetRecentHistorySize()}): {FormatRecent(state)}");
        args.Context?.AddString(
            $"  Chef ({state.Chef.Count}/{DietConfig.GetChefCollectionSize()}): {FormatChef(state)}");
        args.Context?.AddString($"  Active ({state.Active.Count}): {FormatActive(state)}");
        args.Context?.AddString($"  FullCourseActive: {FoodRules.IsFullCourseActive(player)}");
    }

    private static bool TryGetLocalPlayer(Terminal.ConsoleEventArgs args, out Player player)
    {
        player = Player.m_localPlayer;
        if (player != null)
        {
            return true;
        }

        args.Context?.AddString("FineDining: No local player is available.");
        return false;
    }

    private static void RefreshHud(Player player)
    {
        if (Hud.instance != null && player == Player.m_localPlayer)
        {
            HudFoodPanels.Update(Hud.instance, player);
        }
    }

    private static string FormatRecent(PlayerFoodStateData state)
    {
        if (state.Recent.Count == 0)
        {
            return "(empty)";
        }

        List<string> entries = new();
        foreach (HistoryEntryData entry in state.Recent)
        {
            entries.Add($"{entry.Key} x{entry.Stack}");
        }

        return string.Join(" | ", entries);
    }

    private static string FormatChef(PlayerFoodStateData state)
    {
        if (state.Chef.Count == 0)
        {
            return "(empty)";
        }

        List<string> entries = new();
        foreach (ChefEntryData entry in state.Chef)
        {
            entries.Add($"{entry.Key} x{entry.Multiplier:0.00}");
        }

        return string.Join(" | ", entries);
    }

    private static string FormatActive(PlayerFoodStateData state)
    {
        if (state.Active.Count == 0)
        {
            return "(empty)";
        }

        List<string> entries = new();
        foreach (ActiveFoodData entry in state.Active)
        {
            entries.Add($"{entry.Key} scale={entry.AppliedScale:0.###}");
        }

        return string.Join(" | ", entries);
    }
}
