using System.Collections.Generic;

namespace FineDining;

internal sealed class HistoryEntryData
{
    public string Key = string.Empty;
    public int Stack = 1;
}

internal sealed class ChefEntryData
{
    public string Key = string.Empty;
    public float Multiplier = 1f;
}

internal sealed class ActiveFoodData
{
    public string Key = string.Empty;
    public float AppliedScale = 1f;
}

internal sealed class PlayerFoodStateData
{
    public List<HistoryEntryData> Recent = new();
    public List<ChefEntryData> Chef = new();
    public List<string> ChefQueue = new();
    public List<ActiveFoodData> Active = new();
}
