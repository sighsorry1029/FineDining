namespace FineDining;

internal static class RecentHistoryService
{
    internal static HistoryEntryData? GetEntry(PlayerFoodStateData state, string key)
    {
        foreach (HistoryEntryData entry in state.Recent)
        {
            if (entry.Key == key)
            {
                return entry;
            }
        }

        return null;
    }

    internal static int GetNextStack(PlayerFoodStateData state, string key, bool isChef)
    {
        if (isChef)
        {
            return 1;
        }

        HistoryEntryData? entry = GetEntry(state, key);
        return entry == null || entry.Stack < 1 ? 1 : entry.Stack + 1;
    }

    internal static int RegisterConsumption(PlayerFoodStateData state, string key, bool isChef)
    {
        HistoryEntryData? entry = GetEntry(state, key);
        if (entry != null)
        {
            entry.Stack = GetNextStack(state, key, isChef);
            return entry.Stack;
        }

        state.Recent.Add(new HistoryEntryData { Key = key, Stack = 1 });
        Trim(state);
        return 1;
    }

    internal static void Clear(PlayerFoodStateData state) => state.Recent.Clear();

    private static void Trim(PlayerFoodStateData state)
    {
        while (state.Recent.Count > DietConfig.GetRecentHistorySize())
        {
            state.Recent.RemoveAt(0);
        }
    }
}
