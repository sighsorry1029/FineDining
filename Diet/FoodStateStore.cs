using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FineDining;

internal static class FoodStateStore
{
    private const string CustomDataKey = "sighsorry.FineDining.DietState";
    private const string StateVersion = "v1";
    private static readonly Dictionary<Player, PlayerFoodStateData> Cache = new();

    internal static PlayerFoodStateData GetState(Player? player)
    {
        if (player == null)
        {
            return new PlayerFoodStateData();
        }

        if (Cache.TryGetValue(player, out PlayerFoodStateData? state))
        {
            return state;
        }

        state = LoadState(player);
        NormalizeState(player, state);
        Cache[player] = state;
        return state;
    }

    internal static void SaveState(Player? player, PlayerFoodStateData? state = null)
    {
        if (player == null)
        {
            return;
        }

        state ??= GetState(player);
        NormalizeState(player, state);
        Cache[player] = state;
        player.m_customData[CustomDataKey] = SerializeState(state);
    }

    internal static void Invalidate(Player? player)
    {
        if (!ReferenceEquals(player, null))
        {
            Cache.Remove(player!);
        }
    }

    internal static void Reset() => Cache.Clear();

    private static PlayerFoodStateData LoadState(Player player)
    {
        if (!player.m_customData.TryGetValue(CustomDataKey, out string data) ||
            string.IsNullOrWhiteSpace(data))
        {
            return new PlayerFoodStateData();
        }

        try
        {
            return DeserializeState(data);
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Failed to deserialize FineDining diet state: " + exception.Message);
            return new PlayerFoodStateData();
        }
    }

    private static string SerializeState(PlayerFoodStateData state)
    {
        StringBuilder builder = new();
        builder.Append(StateVersion);
        builder.Append("|R:");
        AppendRecent(builder, state.Recent);
        builder.Append("|C:");
        AppendChef(builder, state.Chef);
        builder.Append("|Q:");
        AppendQueue(builder, state.ChefQueue);
        builder.Append("|A:");
        AppendActive(builder, state.Active);
        return builder.ToString();
    }

    private static PlayerFoodStateData DeserializeState(string data)
    {
        PlayerFoodStateData state = new();
        string[] sections = data.Split(new[] { '|' }, StringSplitOptions.None);
        if (sections.Length == 0 || sections[0] != StateVersion)
        {
            FineDiningPlugin.Log.LogWarning(
                "Unsupported FineDining diet state format; resetting the stored diet state.");
            return state;
        }

        for (int index = 1; index < sections.Length; index++)
        {
            string section = sections[index];
            if (section.Length < 2 || section[1] != ':')
            {
                continue;
            }

            string payload = section.Length > 2 ? section.Substring(2) : string.Empty;
            switch (section[0])
            {
                case 'R':
                    state.Recent = ReadRecent(payload);
                    break;
                case 'C':
                    state.Chef = ReadChef(payload);
                    break;
                case 'Q':
                    state.ChefQueue = ReadQueue(payload);
                    break;
                case 'A':
                    state.Active = ReadActive(payload);
                    break;
            }
        }

        return state;
    }

    private static void AppendRecent(StringBuilder builder, List<HistoryEntryData> recent)
    {
        bool first = true;
        foreach (HistoryEntryData entry in recent)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            AppendSeparator(builder, ref first);
            builder.Append(EncodeKey(entry.Key));
            builder.Append(',');
            builder.Append(Math.Max(1, entry.Stack).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendChef(StringBuilder builder, List<ChefEntryData> chef)
    {
        bool first = true;
        foreach (ChefEntryData entry in chef)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            AppendSeparator(builder, ref first);
            builder.Append(EncodeKey(entry.Key));
            builder.Append(',');
            builder.Append(entry.Multiplier.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendQueue(StringBuilder builder, List<string> queue)
    {
        bool first = true;
        foreach (string key in queue)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            AppendSeparator(builder, ref first);
            builder.Append(EncodeKey(key));
        }
    }

    private static void AppendActive(StringBuilder builder, List<ActiveFoodData> active)
    {
        bool first = true;
        foreach (ActiveFoodData entry in active)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            AppendSeparator(builder, ref first);
            builder.Append(EncodeKey(entry.Key));
            builder.Append(',');
            builder.Append(entry.AppliedScale.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendSeparator(StringBuilder builder, ref bool first)
    {
        if (!first)
        {
            builder.Append(';');
        }

        first = false;
    }

    private static List<HistoryEntryData> ReadRecent(string payload)
    {
        List<HistoryEntryData> recent = new();
        foreach (string token in SplitEntries(payload))
        {
            string[] parts = token.Split(',');
            if (parts.Length != 2)
            {
                continue;
            }

            string key = DecodeKey(parts[0]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                recent.Add(new HistoryEntryData
                {
                    Key = key,
                    Stack = ParseInt(parts[1], 1)
                });
            }
        }

        return recent;
    }

    private static List<ChefEntryData> ReadChef(string payload)
    {
        List<ChefEntryData> chef = new();
        foreach (string token in SplitEntries(payload))
        {
            string[] parts = token.Split(',');
            if (parts.Length != 2)
            {
                continue;
            }

            string key = DecodeKey(parts[0]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                chef.Add(new ChefEntryData
                {
                    Key = key,
                    Multiplier = ParseFloat(parts[1], 1f)
                });
            }
        }

        return chef;
    }

    private static List<string> ReadQueue(string payload)
    {
        List<string> queue = new();
        foreach (string token in SplitEntries(payload))
        {
            string key = DecodeKey(token);
            if (!string.IsNullOrWhiteSpace(key))
            {
                queue.Add(key);
            }
        }

        return queue;
    }

    private static List<ActiveFoodData> ReadActive(string payload)
    {
        List<ActiveFoodData> active = new();
        foreach (string token in SplitEntries(payload))
        {
            string[] parts = token.Split(',');
            if (parts.Length != 2)
            {
                continue;
            }

            string key = DecodeKey(parts[0]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                active.Add(new ActiveFoodData
                {
                    Key = key,
                    AppliedScale = ParseFloat(parts[1], DietConfig.GetBaseSlotScale())
                });
            }
        }

        return active;
    }

    private static IEnumerable<string> SplitEntries(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            yield break;
        }

        foreach (string entry in payload.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return entry;
        }
    }

    private static string EncodeKey(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DecodeKey(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    private static float ParseFloat(string value, float fallback)
    {
        return float.TryParse(
                   value,
                   NumberStyles.Float | NumberStyles.AllowThousands,
                   CultureInfo.InvariantCulture,
                   out float parsed) &&
               !float.IsNaN(parsed) &&
               !float.IsInfinity(parsed)
            ? parsed
            : fallback;
    }

    private static void NormalizeState(Player player, PlayerFoodStateData state)
    {
        state.Recent ??= new List<HistoryEntryData>();
        state.Chef ??= new List<ChefEntryData>();
        state.ChefQueue ??= new List<string>();
        state.Active ??= new List<ActiveFoodData>();
        NormalizeRecent(state);
        NormalizeChef(state);
        NormalizeQueue(state);
        NormalizeActive(player, state);
    }

    private static void NormalizeRecent(PlayerFoodStateData state)
    {
        List<HistoryEntryData> normalized = new();
        Dictionary<string, HistoryEntryData> byKey = new(StringComparer.Ordinal);
        foreach (HistoryEntryData entry in state.Recent)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            if (byKey.TryGetValue(entry.Key, out HistoryEntryData? existing))
            {
                existing.Stack += Math.Max(1, entry.Stack);
                continue;
            }

            HistoryEntryData normalizedEntry = new()
            {
                Key = entry.Key,
                Stack = Math.Max(1, entry.Stack)
            };
            normalized.Add(normalizedEntry);
            byKey[entry.Key] = normalizedEntry;
        }

        while (normalized.Count > DietConfig.GetRecentHistorySize())
        {
            normalized.RemoveAt(0);
        }

        state.Recent = normalized;
    }

    private static void NormalizeChef(PlayerFoodStateData state)
    {
        List<ChefEntryData> normalized = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        float minimum = DietConfig.GetChefMultiplierMin();
        float maximum = DietConfig.GetChefMultiplierMax();
        foreach (ChefEntryData entry in state.Chef)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Key) || !seen.Add(entry.Key))
            {
                continue;
            }

            float multiplier = entry.Multiplier;
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f)
            {
                multiplier = minimum;
            }

            normalized.Add(new ChefEntryData
            {
                Key = entry.Key,
                Multiplier = Math.Max(minimum, Math.Min(maximum, multiplier))
            });
        }

        while (normalized.Count > DietConfig.GetChefCollectionSize())
        {
            normalized.RemoveAt(normalized.Count - 1);
        }

        state.Chef = normalized;
    }

    private static void NormalizeQueue(PlayerFoodStateData state)
    {
        List<string> normalized = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string key in state.ChefQueue)
        {
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
            {
                normalized.Add(key);
            }
        }

        state.ChefQueue = normalized;
    }

    private static void NormalizeActive(Player player, PlayerFoodStateData state)
    {
        HashSet<string> activeKeys = new(StringComparer.Ordinal);
        foreach (Player.Food food in player.GetFoods())
        {
            string key = FoodKeys.GetKey(food);
            if (!string.IsNullOrWhiteSpace(key))
            {
                activeKeys.Add(key);
            }
        }

        List<ActiveFoodData> normalized = new();
        Dictionary<string, int> indexByKey = new(StringComparer.Ordinal);
        foreach (ActiveFoodData entry in state.Active)
        {
            if (entry == null ||
                string.IsNullOrWhiteSpace(entry.Key) ||
                !activeKeys.Contains(entry.Key))
            {
                continue;
            }

            float scale = entry.AppliedScale;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f)
            {
                scale = DietConfig.GetBaseSlotScale();
            }

            ActiveFoodData normalizedEntry = new()
            {
                Key = entry.Key,
                AppliedScale = Math.Min(25f, scale)
            };

            if (indexByKey.TryGetValue(entry.Key, out int existingIndex))
            {
                normalized[existingIndex] = normalizedEntry;
            }
            else
            {
                indexByKey[entry.Key] = normalized.Count;
                normalized.Add(normalizedEntry);
            }
        }

        state.Active = normalized;
    }
}
