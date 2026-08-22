using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace FineDining;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(
    AzuExtendedPlayerInventoryCompatibility.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(
    InventorySlotsCompatibility.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("expand_world_data", BepInDependency.DependencyFlags.SoftDependency)]
[BepInIncompatibility("sighsorry.BeingSpoiled")]
[BepInIncompatibility("blizz.GourmetsDiet")]
[BepInIncompatibility("sighsorry.InputHoverHints")]
public sealed class FineDiningPlugin : BaseUnityPlugin
{
    internal const string ModName = "FineDining";
    internal const string ModVersion = "1.0.0";
    internal const string Author = "sighsorry";
    internal const string ModGUID = Author + "." + ModName;
    internal const bool DefaultConfigurationLock = true;

    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);

    internal static readonly ConfigSync ConfigSync = new(ModGUID)
    {
        DisplayName = ModName,
        CurrentVersion = ModVersion,
        MinimumRequiredVersion = ModVersion,
        ModRequired = true
    };

    private readonly Harmony _harmony = new(ModGUID);

    private void Awake()
    {
        LocalizationManager.Localizer.Load();
        ConfigSync.AddLockingConfigEntry(BindConfigurationLock(Config));
        FreshnessRuntime.Initialize(Config, ConfigSync);
        PreservationConfig.Initialize(Config, ConfigSync);
        SpoilagePolicy.Initialize(ConfigSync);
        IceboxSubsystem.Initialize(this);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        AzuExtendedPlayerInventoryCompatibility.TryInstall(_harmony);
        InventorySlotsCompatibility.TryInstall();
    }

    internal static ConfigEntry<bool> BindConfigurationLock(ConfigFile config) =>
        config.Bind(
            "00 - Server",
            "Lock Configuration",
            DefaultConfigurationLock,
            new ConfigDescription(
                "Lock synchronized gameplay settings to the server. Server administrators remain exempt."));

    private void Update()
    {
        PreservationConfig.Tick();
        SpoilagePolicy.RefreshAuthority();
        SpoilageReferenceGenerator.Tick();
        DecayRuntime.Tick();
        IceboxSubsystem.Tick();
    }

    private void OnDestroy()
    {
        FreshnessRuntime.Shutdown();
        PreservationConfig.Shutdown();
        SpoilagePolicy.Shutdown();
        IceboxSubsystem.Shutdown();
        AzuExtendedPlayerInventoryCompatibility.Shutdown();
        _harmony.UnpatchSelf();
        DecayRuntime.Reset();
        FoodClassifier.Invalidate();
        SpoilageReferenceGenerator.Reset();
    }
}
