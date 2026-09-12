using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace FineDining;

// No Jotunn types may appear here: the plugin must load when Jotunn is absent.
// This adapter only observes other mods; native content has a single registration owner.
internal static class JotunnCompatibility
{
    internal const string PluginGuid = "com.jotunn.jotunn";
    private static readonly List<(EventInfo Event, Action Handler)> Subscriptions = new();
    private static readonly List<(MethodInfo Query, PropertyInfo Prefab, PropertyInfo Source)> Queries = new();

    internal static void Initialize()
    {
        Shutdown();
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) || info.Instance == null)
            return;
        Assembly assembly = info.Instance.GetType().Assembly;
        try
        {
            Type? registry = assembly.GetType("Jotunn.Utils.ModRegistry");
            foreach (var spec in new[] { ("GetPrefabs", "CustomPrefab", "Prefab"),
                         ("GetItems", "CustomItem", "ItemPrefab"), ("GetPieces", "CustomPiece", "PiecePrefab") })
            {
                MethodInfo? query = registry?.GetMethod(spec.Item1, Type.EmptyTypes);
                Type? entity = assembly.GetType("Jotunn.Entities." + spec.Item2);
                PropertyInfo? prefab = entity?.GetProperty(spec.Item3);
                PropertyInfo? source = entity?.GetProperty("SourceMod");
                if (query != null && prefab != null && source != null)
                    Queries.Add((query, prefab, source));
            }
            Subscribe(assembly, "Jotunn.Managers.ItemManager", "OnItemsRegistered");
            Subscribe(assembly, "Jotunn.Managers.PrefabManager", "OnPrefabsRegistered");
        }
        catch (Exception ex)
        {
            FineDiningPlugin.Log.LogWarning("Optional Jotunn integration is incomplete: " + ex.GetBaseException().Message);
        }
    }

    private static void Subscribe(Assembly assembly, string type, string name)
    {
        EventInfo? evt = assembly.GetType(type)?.GetEvent(name, BindingFlags.Public | BindingFlags.Static);
        if (evt?.EventHandlerType != typeof(Action)) return;
        Action handler = SpoilageContentLifecycle.Refresh;
        evt.AddEventHandler(null, handler);
        Subscriptions.Add((evt, handler));
    }

    internal static IEnumerable<(GameObject? Prefab, BepInPlugin? SourceMod)> GetOwners()
    {
        foreach (var query in Queries)
        {
            if (query.Query.Invoke(null, null) is not IEnumerable entries) continue;
            foreach (object entry in entries)
                yield return (query.Prefab.GetValue(entry) as GameObject, query.Source.GetValue(entry) as BepInPlugin);
        }
    }

    internal static void Shutdown()
    {
        foreach (var subscription in Subscriptions)
        {
            try { subscription.Event.RemoveEventHandler(null, subscription.Handler); }
            catch (Exception ex) { FineDiningPlugin.Log.LogDebug("Could not detach optional Jotunn event: " + ex.Message); }
        }
        Subscriptions.Clear();
        Queries.Clear();
    }
}
