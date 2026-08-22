using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace FineDining;

internal static class InventorySlotsCompatibility
{
    internal const string PluginGuid = "sighsorry.InventorySlots";

    private const string ApiTypeName = "InventorySlots.InventorySlotsApi";

    internal static void TryInstall()
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out BepInEx.PluginInfo pluginInfo))
        {
            return;
        }

        try
        {
            bool supportedVersion = pluginInfo.Metadata.Version.CompareTo(new System.Version(1, 3, 6)) >= 0;
            if (!supportedVersion)
            {
                FineDiningPlugin.Log.LogWarning(
                    "InventorySlots " + pluginInfo.Metadata.Version +
                    " predates signed spoilage-clock compatibility; update it to 1.3.6 or newer.");
            }

            Type? apiType = pluginInfo.Instance?.GetType().Assembly.GetType(ApiTypeName, throwOnError: false);
            MethodInfo? register = apiType?.GetMethod(
                "RegisterStackMetadataPolicy",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                new[] { typeof(string), typeof(Func<string, string, string>) },
                modifiers: null);
            if (register == null)
            {
                FineDiningPlugin.Log.LogWarning(
                    "InventorySlots is installed, but its stack metadata API was not found. " +
                    "Its built-in FineDining clock fallback will be used.");
                return;
            }

            Func<string?, string?, string?> clockMerger = DecayRuntime.ComposeStackClockValues;
            Func<string?, string?, string?> lifetimeMerger = FreshnessRuntime.ComposeAssignedLifetimeValues;
            bool registeredClock = Register(register, DecayRuntime.ExpiryDataKey, clockMerger);
            bool registeredLifetime = Register(
                register,
                FreshnessRuntime.AssignedLifetimeDataKey,
                lifetimeMerger);
            if (registeredClock || registeredLifetime)
            {
                FineDiningPlugin.Log.LogInfo(
                    "Registered FineDining spoilage-clock and freshness metadata mergers with InventorySlots.");
            }
            else if (supportedVersion)
            {
                FineDiningPlugin.Log.LogInfo(
                    "InventorySlots kept an existing FineDining stack clock merger.");
            }
            else
            {
                FineDiningPlugin.Log.LogWarning(
                    "The installed InventorySlots version did not accept FineDining's signed clock merger; " +
                    "mixed running and paused stacks may compose incorrectly until InventorySlots is updated.");
            }
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not register FineDining's stack clock merger with InventorySlots. " +
                "Its built-in compatibility fallback will remain active. " + exception);
        }
    }

    private static bool Register(
        MethodInfo register,
        string key,
        Func<string?, string?, string?> merger) =>
        register.Invoke(null, new object?[] { key, merger }) as bool? ?? false;
}
