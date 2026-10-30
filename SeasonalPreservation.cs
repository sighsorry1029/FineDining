using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;

namespace FineDining;

// Optional adapters only observe the provider's accepted season. They never
// change weather, biome configuration, global keys or the provider's calendar.
internal static class SeasonalPreservation
{
    internal const string SeasonalityGuid = "RustyMods.Seasonality";
    internal const string SeasonsGuid = "shudnal.Seasons";
    private static ConfigEntry<bool>? _enabled;
    private static MethodInfo? _season;
    private static MethodInfo? _day;
    private static MethodInfo? _active;
    private static FieldInfo? _state;
    private static FieldInfo? _sync;
    private static MethodInfo? _syncReady;
    private static object? _winter;
    private static bool _available;
    private static bool _warned;
    internal static bool Enabled => _enabled?.Value == true;

    internal static void Initialize(ConfigFile config, ConfigSync sync)
    {
        _enabled = config.Bind(ConfigPresentation.Spoilage.Name,
            "Pause Spoilage In Winter", false,
            ConfigPresentation.Synced(
                "Pause the world spoilage clock during Seasonality or Seasons winter, including offline players " +
                "and unloaded food after its clock is upgraded to the world-aware format. " +
                "Existing timers are adopted when first processed; earlier winters are not refunded. " +
                "Requires the same FineDining build on every peer.",
                ConfigPresentation.Spoilage, 550));
        sync.AddConfigEntry(_enabled).SynchronizedConfig = true;
        _enabled.SettingChanged += Changed;
    }

    internal static void Install(Harmony harmony)
    {
        bool seasonality = Chainloader.PluginInfos.TryGetValue(SeasonalityGuid, out var a) && a.Instance != null;
        bool seasons = Chainloader.PluginInfos.TryGetValue(SeasonsGuid, out var b) && b.Instance != null;
        if (!seasonality && !seasons) return;
        try
        {
            if (seasonality && seasons) throw new InvalidOperationException("Both incompatible season providers are active.");
            Assembly assembly = (seasonality ? a : b).Instance.GetType().Assembly;
            MethodInfo transition;
            if (seasonality)
            {
                Type time = RequiredType(assembly, "Seasonality.SeasonTime");
                _season = RequiredMethod(time, "GetCurrentSeason", true);
                _sync = RequiredType(assembly, "Seasonality.ConfigManager").GetField("ConfigSync", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingFieldException("Seasonality.ConfigManager.ConfigSync");
                _syncReady = RequiredMethod(_sync.FieldType, "get_IsSourceOfTruth", false, typeof(bool));
                transition = RequiredMethod(RequiredType(assembly, "Seasonality.SeasonalityPlugin"), "OnSeasonChange", true, typeof(void));
            }
            else
            {
                Type state = RequiredType(assembly, "Seasons.SeasonState");
                _state = RequiredType(assembly, "Seasons.Seasons").GetField("seasonState", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingFieldException("Seasons.Seasons.seasonState");
                if (_state.FieldType != state) throw new InvalidOperationException("Unexpected Seasons state type.");
                _season = RequiredMethod(state, "GetCurrentSeason", false);
                _day = RequiredMethod(state, "GetCurrentDay", false, typeof(int));
                _active = RequiredMethod(state, "get_IsActive", true, typeof(bool));
                transition = RequiredMethod(state, "OnSeasonDayChange", true, typeof(void));
            }
            if (!_season.ReturnType.IsEnum || !Enum.IsDefined(_season.ReturnType, "Winter"))
                throw new InvalidOperationException("The provider has no Winter season.");
            _winter = Enum.Parse(_season.ReturnType, "Winter");
            harmony.Patch(transition, finalizer: new HarmonyMethod(typeof(SeasonalPreservation), nameof(TransitionFinished)));
            _available = true;
            FineDiningPlugin.Log.LogInfo("Winter spoilage adapter ready: " + assembly.GetName().Name);
        }
        catch (Exception error) { Disable(error); }
    }

    private static Type RequiredType(Assembly assembly, string name) => assembly.GetType(name, true)!;
    private static MethodInfo RequiredMethod(Type type, string name, bool isStatic, Type? result = null)
    {
        MethodInfo? method = type.GetMethod(name, BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance),
            null, Type.EmptyTypes, null);
        if (method == null || method.IsStatic != isStatic || (result != null && method.ReturnType != result))
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    // Only the server queries external provider state. Clients receive its clock.
    internal static bool TryGetWinter(out bool winter)
    {
        winter = false;
        if (!Enabled || !_available) return true;
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null) return false;
        try
        {
            object? instance = null;
            if (_state != null)
            {
                instance = _state.GetValue(null);
                if (instance == null || !Equals(_active!.Invoke(null, null), true) || (int)_day!.Invoke(instance, null) <= 0)
                    return false;
            }
            else
            {
                object? sync = _sync!.GetValue(null);
                if (sync == null || !Equals(_syncReady!.Invoke(sync, null), true)) return false;
            }
            winter = Equals(_season!.Invoke(instance, null), _winter);
            return true;
        }
        catch (Exception error)
        {
            Disable(error);
            return true; // Disable only the unavailable adapter, retain the world clock.
        }
    }

    private static Exception? TransitionFinished(Exception? __exception)
    {
        try { WorldSpoilageClock.RefreshSeason(); }
        catch (Exception error) { FineDiningPlugin.Log.LogWarning("Could not observe the season transition: " + error.Message); }
        return __exception;
    }
    private static void Changed(object sender, EventArgs args) => TransitionFinished(null);
    private static void Disable(Exception error)
    {
        _available = false;
        if (_warned) return;
        _warned = true;
        FineDiningPlugin.Log.LogWarning("Winter spoilage adapter disabled; ordinary preservation remains active: " + error.GetBaseException().Message);
    }
    internal static void Shutdown()
    {
        if (_enabled != null) _enabled.SettingChanged -= Changed;
        _enabled = null;
        _season = _day = _active = _syncReady = null;
        _state = _sync = null;
        _winter = null;
        _available = _warned = false;
    }
}
