using System;
using System.Collections.Generic;

namespace FineDining;

[Serializable]
internal sealed class HistoryEntryData
{
    public string Key = string.Empty;
    public int Stack = 1;
}

[Serializable]
internal sealed class ChefEntryData
{
    public string Key = string.Empty;
    public float Multiplier = 1f;
}

[Serializable]
internal sealed class ActiveFoodData
{
    public string Key = string.Empty;
    public float AppliedScale = 1f;
}

[Serializable]
internal sealed class PlayerFoodStateData
{
    public int UnlockedFoodSlots;
    public float AppliedBaseSlotScale;
    public List<HistoryEntryData> Recent = new();
    public List<ChefEntryData> Chef = new();
    // Persisted oldest-to-newest consumption order for currently active foods.
    public List<ActiveFoodData> Active = new();
}
