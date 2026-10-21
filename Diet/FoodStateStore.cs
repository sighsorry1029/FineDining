using System;
using System.Collections.Generic;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace FineDining;

internal static class FoodStateStore
{
    private const string CustomDataKey = "sighsorry.FineDining.DietState";
    private const string StatePrefix = "v3:";
    private static readonly Dictionary<Player, PlayerFoodStateData> Cache = new();
    // Unity's serializer omitted the nested lists in actual character saves.
    // Keep the v3 JSON contract, using the managed serializer already bundled here.
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .JsonCompatible()
        .WithTypeConverter(new RoundTripFloatConverter())
        .Build();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

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
        return StatePrefix + Serializer.Serialize(state);
    }

    private static PlayerFoodStateData DeserializeState(string data)
    {
        if (!data.StartsWith(StatePrefix, StringComparison.Ordinal))
        {
            FineDiningPlugin.Log.LogWarning(
                "Unsupported FineDining diet state format; resetting the stored diet state.");
            return new PlayerFoodStateData();
        }

        string json = data.Substring(StatePrefix.Length);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new FormatException("The FineDining diet state JSON payload is empty.");
        }

        PlayerFoodStateData? state = Deserializer.Deserialize<PlayerFoodStateData>(json);
        if (state == null)
        {
            throw new FormatException("The FineDining diet state JSON payload is invalid.");
        }

        return state;
    }

    private sealed class RoundTripFloatConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(float);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) =>
            throw new NotSupportedException("This converter is used only for JSON writing.");

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            float number = (float)value!;
            // YamlDotNet's JSON writer uses G, which loses Single precision on Mono.
            bool finite = !float.IsNaN(number) && !float.IsInfinity(number);
            emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty,
                number.ToString("R", CultureInfo.InvariantCulture),
                finite ? ScalarStyle.Plain : ScalarStyle.DoubleQuoted, finite, !finite));
        }
    }

    private static void NormalizeState(Player player, PlayerFoodStateData state)
    {
        state.Recent ??= new List<HistoryEntryData>();
        state.Chef ??= new List<ChefEntryData>();
        state.Active ??= new List<ActiveFoodData>();
        NormalizeRecent(state);
        NormalizeChef(state);
        FoodSlotProgression.NormalizeState(player, state);
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

    private static void NormalizeActive(Player player, PlayerFoodStateData state)
    {
        HashSet<string> activeKeys = new(StringComparer.Ordinal);
        foreach (Player.Food food in player.GetFoods())
        {
            string key = FoodIdentity.GetCanonicalPrefabName(food);
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
            bool scaleIsValid = !float.IsNaN(scale) &&
                                !float.IsInfinity(scale) &&
                                scale >= 0f;
            if (!scaleIsValid)
            {
                scale = state.AppliedBaseSlotScale;
            }

            // A combined saved scale cannot recover individual components.
            // Do not infer them or clamp consumed factors to today's configuration.
            bool hasEffectBreakdown = scaleIsValid &&
                                      HasValidEffectBreakdown(entry);
            ActiveFoodData normalizedEntry = new()
            {
                Key = entry.Key,
                AppliedScale = scale,
                HasEffectBreakdown = hasEffectBreakdown,
                ChefMultiplier = hasEffectBreakdown ? entry.ChefMultiplier : 1f,
                FreshnessScale = hasEffectBreakdown ? entry.FreshnessScale : 1f,
                DiminishingScale = hasEffectBreakdown ? entry.DiminishingScale : 1f
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

    private static bool HasValidEffectBreakdown(ActiveFoodData entry) =>
        entry.HasEffectBreakdown &&
        !float.IsNaN(entry.ChefMultiplier) &&
        !float.IsInfinity(entry.ChefMultiplier) &&
        entry.ChefMultiplier >= 1f &&
        !float.IsNaN(entry.FreshnessScale) &&
        !float.IsInfinity(entry.FreshnessScale) &&
        entry.FreshnessScale >= 0f &&
        entry.FreshnessScale <= 1f &&
        !float.IsNaN(entry.DiminishingScale) &&
        !float.IsInfinity(entry.DiminishingScale) &&
        entry.DiminishingScale > 0f &&
        entry.DiminishingScale <= 1f;
}
