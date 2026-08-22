using System.Collections.Generic;
using HarmonyLib;

namespace FineDining;

[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
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
            "fd",
            "Admin only. FineDining diet commands: rerollchef, clearrecent, printstate.",
            RootCommand,
            optionsFetcher: GetRootOptions,
            alwaysRefreshTabOptions: false,
            onlyAdmin: true);
        new Terminal.ConsoleCommand(
            "fd_rerollchef",
            "Admin only. Rerolls the local player's Chef collection.",
            RerollChef,
            onlyAdmin: true);
        new Terminal.ConsoleCommand(
            "fd_clearrecent",
            "Admin only. Clears the local player's recent food history.",
            ClearRecent,
            onlyAdmin: true);
        new Terminal.ConsoleCommand(
            "fd_printstate",
            "Admin only. Prints the local player's FineDining diet state.",
            PrintState,
            onlyAdmin: true);
    }

    private static List<string> GetRootOptions() =>
        new() { "rerollchef", "clearrecent", "printstate" };

    private static void RootCommand(Terminal.ConsoleEventArgs args)
    {
        if (args.Length < 2)
        {
            args.Context?.AddString("FineDining diet commands: rerollchef, clearrecent, printstate");
            return;
        }

        switch (args[1].ToLowerInvariant())
        {
            case "rerollchef":
                RerollChef(args);
                break;
            case "clearrecent":
                ClearRecent(args);
                break;
            case "printstate":
                PrintState(args);
                break;
            default:
                args.Context?.AddString(
                    $"FineDining: Unknown diet subcommand '{args[1]}'.");
                break;
        }
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
            $"  Recent ({state.Recent.Count}/{DietConfig.GetRecentHistorySize()}): {FormatRecent(state)}");
        args.Context?.AddString(
            $"  Chef ({state.Chef.Count}/{DietConfig.GetChefCollectionSize()}): {FormatChef(state)}");
        args.Context?.AddString($"  Queue ({state.ChefQueue.Count}): {FormatQueue(state)}");
        args.Context?.AddString($"  Active ({state.Active.Count}): {FormatActive(state)}");
        args.Context?.AddString($"  FullStraightActive: {FoodRules.IsFullStraightActive(player)}");
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

    private static string FormatQueue(PlayerFoodStateData state) =>
        state.ChefQueue.Count == 0 ? "(empty)" : string.Join(" | ", state.ChefQueue);

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
