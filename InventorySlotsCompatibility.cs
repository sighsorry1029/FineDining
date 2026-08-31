using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace FineDining;

internal static class InventorySlotsCompatibility
{
    internal const string PluginGuid = "sighsorry.InventorySlots";
    internal static readonly System.Version MinimumSupportedVersion = new(1, 3, 7);

    private const string ApiTypeName = "InventorySlots.InventorySlotsApi";

    internal static void TryInstall()
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out BepInEx.PluginInfo pluginInfo))
        {
            return;
        }

        try
        {
            if (pluginInfo.Metadata.Version.CompareTo(MinimumSupportedVersion) < 0)
            {
                FineDiningPlugin.Log.LogWarning(
                    "InventorySlots " + pluginInfo.Metadata.Version +
                    " predates FineDining stack-metadata compatibility; update it to " +
                    MinimumSupportedVersion + " or newer. FineDining metadata was not registered.");
                return;
            }

            Type? apiType = pluginInfo.Instance?.GetType().Assembly.GetType(ApiTypeName, throwOnError: false);
            MethodInfo? guardedRegister = apiType?.GetMethod(
                "RegisterStackMetadataPolicy",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                new[]
                {
                    typeof(string),
                    typeof(Func<string, string, string>),
                    typeof(Func<string, string, bool>)
                },
                modifiers: null);
            if (guardedRegister == null)
            {
                FineDiningPlugin.Log.LogWarning(
                    "InventorySlots is installed, but its guarded stack metadata API was not found. " +
                    "FineDining spoilage metadata was not registered.");
                return;
            }

            Func<string?, string?, string?> clockMerger = DecayRuntime.ComposeStackClockValues;
            Func<string?, string?, bool> clockCanMerge = DecayRuntime.CanMergeStackClockValues;
            Func<string?, string?, string?> lifetimeMerger = FreshnessRuntime.ComposeAssignedLifetimeValues;
            Func<string?, string?, bool> lifetimeCanMerge =
                FreshnessRuntime.CanMergeAssignedLifetimeValues;
            Func<string?, string?, string?> spoiledMerger = SpoilageClock.ComposeSpoiledValues;
            Func<string?, string?, bool> spoiledCanMerge = SpoilageClock.CanMergeSpoiledValues;
            RegisterAndLog(
                guardedRegister,
                SpoilageClock.ExpiryDataKey,
                "spoilage-clock",
                clockMerger,
                clockCanMerge);
            RegisterAndLog(
                guardedRegister,
                FreshnessRuntime.AssignedLifetimeDataKey,
                "assigned-lifetime",
                lifetimeMerger,
                lifetimeCanMerge);
            RegisterAndLog(
                guardedRegister,
                SpoilageClock.SpoiledDataKey,
                "spoiled-state",
                spoiledMerger,
                spoiledCanMerge);
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not inspect InventorySlots' stack metadata API. " +
                "FineDining spoilage metadata may remain unregistered. " + exception);
        }
    }

    private static void RegisterAndLog(
        MethodInfo register,
        string key,
        string label,
        Func<string?, string?, string?> merger,
        Func<string?, string?, bool> canMerge)
    {
        try
        {
            bool registered = Register(register, key, merger, canMerge);
            if (registered)
            {
                FineDiningPlugin.Log.LogInfo(
                    "Registered FineDining " + label +
                    " metadata merger with InventorySlots with fail-closed validation.");
                return;
            }

            FineDiningPlugin.Log.LogWarning(
                "InventorySlots did not accept FineDining's " + label + " metadata merger for '" +
                key + "'. Another first-wins policy may already own that key; " +
                "FineDining cannot verify the active policy.");
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not register FineDining's " + label + " metadata merger for '" + key +
                "' with InventorySlots. " + exception);
        }
    }

    private static bool Register(
        MethodInfo register,
        string key,
        Func<string?, string?, string?> merger,
        Func<string?, string?, bool> canMerge)
    {
        return register.Invoke(null, new object?[] { key, merger, canMerge }) is bool registered &&
               registered;
    }
}
