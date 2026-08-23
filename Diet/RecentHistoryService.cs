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
        for (int index = 0; index < state.Recent.Count; index++)
        {
            HistoryEntryData entry = state.Recent[index];
            if (entry.Key != key)
            {
                continue;
            }

            entry.Stack = isChef
                ? 1
                : entry.Stack < 1
                    ? 1
                    : entry.Stack + 1;

            // Re-consumption makes this the newest history entry. Moving it to
            // the tail also ensures trimming removes the genuinely oldest food.
            if (index != state.Recent.Count - 1)
            {
                state.Recent.RemoveAt(index);
                state.Recent.Add(entry);
            }

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
