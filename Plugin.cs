using System.Reflection;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;

namespace FineDining;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(JotunnCompatibility.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(
    AzuExtendedPlayerInventoryCompatibility.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(
    InventorySlotsCompatibility.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(
    AzuCraftyBoxesCompatibility.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(
    ValheimCuisineCompatibility.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("expand_world_data", BepInDependency.DependencyFlags.SoftDependency)]
[BepInIncompatibility("sighsorry.BeingSpoiled")]
[BepInIncompatibility("blizz.GourmetsDiet")]
[BepInIncompatibility("sighsorry.InputHoverHints")]
public sealed class FineDiningPlugin : BaseUnityPlugin
{
    internal const string ModName = "FineDining";
    internal const string ModVersion = "1.1.0";
    internal const string Author = "sighsorry";
    internal const string ModGUID = Author + "." + ModName;
    internal const bool DefaultConfigurationLock = true;

    internal static ManualLogSource Log = null!;

    internal static string ConfigDirectoryPath => Path.Combine(Paths.ConfigPath, ModName);

    internal static bool IsRuntimeReferenceAuthority =>
        ConfigSync.IsSourceOfTruth &&
        ZNet.instance != null &&
        ZNet.instance.IsServer();

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
        Log = Logger;
        FineDiningLocalization.Initialize(this);
        ConfigSync.AddLockingConfigEntry(BindConfigurationLock(Config));
        PreservationConfig.Initialize(Config, ConfigSync);
        FreshnessRuntime.Initialize(Config, ConfigSync);
        SpoilagePolicy.Initialize(Config, ConfigSync);
        ChefResourceMapPolicy.Initialize(ConfigSync);
        IceboxSubsystem.Initialize(this);
        GeneratedPrefabRegistry.Initialize();
        DietModule.Initialize(Config, ConfigSync);
        StationModule.Initialize(Config, ConfigSync);
        JotunnCompatibility.Initialize();
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        AzuExtendedPlayerInventoryCompatibility.TryInstall(_harmony);
        InventorySlotsCompatibility.TryInstall();
    }

    internal static ConfigEntry<bool> BindConfigurationLock(ConfigFile config) =>
        config.Bind(
            ConfigPresentation.General.Name,
            "Lock Server Configuration",
            DefaultConfigurationLock,
            ConfigPresentation.Synced(
                "Lock synchronized gameplay settings to the server. Server administrators remain exempt.",
                ConfigPresentation.General,
                500));

    private void Update()
    {
        PreservationConfig.Tick();
        SpoilagePolicy.RefreshAuthority();
        ChefResourceMapPolicy.RefreshAuthority();
        ChefFoodTierCatalog.Tick();
        SpoilageReferenceGenerator.Tick();
        ChefTierReferenceGenerator.Tick();
        DecayRuntime.Tick();
        IceboxSubsystem.Tick();
        DietModule.Tick();
    }

    private void OnDestroy()
    {
        JotunnCompatibility.Shutdown();
        StationModule.Shutdown();
        DietModule.Shutdown();
        ChefResourceMapPolicy.Shutdown();
        FreshnessRuntime.Shutdown();
        PreservationConfig.Shutdown();
        SpoilagePolicy.Shutdown();
        GeneratedPrefabRegistry.Shutdown();
        IceboxSubsystem.Shutdown();
        AzuExtendedPlayerInventoryCompatibility.Shutdown();
        FineDiningLocalization.Shutdown();
        _harmony.UnpatchSelf();
        DecayRuntime.Reset();
        FoodClassifier.Invalidate();
        SpoilageReferenceGenerator.Reset();
        ChefFoodTierCatalog.Reset();
        ChefTierReferenceGenerator.Reset();
    }
}
