using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FineDining;

internal static class AzuCraftyBoxesCompatibility
{
    internal const string PluginGuid = "Azumatt.AzuCraftyBoxes";

    private const float MissingPluginRetrySeconds = 5f;

    private static MethodInfo? _getNearbyContainersDefinition;
    private static MethodInfo? _countItemInContainer;
    private static MethodInfo? _canItemBePulled;
    private static MethodInfo? _getContainerPrefabName;
    private static bool _initialized;
    private static bool _isReady;
    private static bool _broken;
    private static float _nextLookupTime;

    internal static void Initialize()
    {
        _nextLookupTime = 0f;
        TryInitialize();
    }

    internal static void Shutdown()
    {
        _getNearbyContainersDefinition = null;
        _countItemInContainer = null;
        _canItemBePulled = null;
        _getContainerPrefabName = null;
        _initialized = false;
        _isReady = false;
        _broken = false;
        _nextLookupTime = 0f;
    }

    internal static NearbyContainerQuery? CreateNearbyQuery(Component source, float range)
    {
        if (source == null || !EnsureReady())
        {
            return null;
        }

        MethodInfo? getNearbyContainersDefinition = _getNearbyContainersDefinition;
        MethodInfo? countItemInContainer = _countItemInContainer;
        MethodInfo? canItemBePulled = _canItemBePulled;
        MethodInfo? getContainerPrefabName = _getContainerPrefabName;
        if (getNearbyContainersDefinition == null
            || countItemInContainer == null
            || canItemBePulled == null
            || getContainerPrefabName == null)
        {
            return null;
        }

        try
        {
            MethodInfo getNearby = getNearbyContainersDefinition.MakeGenericMethod(source.GetType());
            object? containersObject = getNearby.Invoke(null, new object[] { source, range });
            if (containersObject is not IEnumerable containers)
            {
                return null;
            }

            List<ContainerReference> references = new();
            foreach (object? container in containers)
            {
                if (container == null)
                {
                    continue;
                }

                string prefabName = getContainerPrefabName.Invoke(
                    container,
                    Array.Empty<object>()) as string ?? string.Empty;
                references.Add(new ContainerReference(container, prefabName));
            }

            return new NearbyContainerQuery(
                references,
                countItemInContainer,
                canItemBePulled);
        }
        catch (Exception exception)
        {
            Disable("AzuCraftyBoxes nearby-container query failed.", exception);
            return null;
        }
    }

    internal static bool CanItemBePulled(string ownerPrefab, string itemPrefab)
    {
        if (string.IsNullOrEmpty(ownerPrefab)
            || string.IsNullOrEmpty(itemPrefab)
            || !EnsureReady())
        {
            return true;
        }

        MethodInfo? method = _canItemBePulled;
        if (method == null)
        {
            return true;
        }

        try
        {
            return InvokeCanItemBePulled(method, ownerPrefab, itemPrefab);
        }
        catch (Exception exception)
        {
            Disable("AzuCraftyBoxes item filter failed.", exception);
            return true;
        }
    }

    private static bool EnsureReady()
    {
        if (_isReady)
        {
            return true;
        }

        TryInitialize();
        return _isReady;
    }

    private static void TryInitialize()
    {
        if (_initialized || _broken || Time.unscaledTime < _nextLookupTime)
        {
            return;
        }

        try
        {
            Type? apiType = Type.GetType("AzuCraftyBoxes.API, AzuCraftyBoxes");
            if (apiType == null)
            {
                // FineDining does not require the optional plugin to load first. Keep
                // retrying lazily until the first station hover after all plugins load.
                _nextLookupTime = Time.unscaledTime + MissingPluginRetrySeconds;
                return;
            }

            _initialized = true;
            Type? containerType = apiType.Assembly.GetType(
                "AzuCraftyBoxes.IContainers.IContainer",
                false);
            if (containerType == null)
            {
                Disable("AzuCraftyBoxes compatibility disabled because IContainer was not found.");
                return;
            }

            const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static;
            MethodInfo? getNearbyContainersDefinition = FindGetNearbyContainers(
                apiType,
                containerType);
            MethodInfo? countItemInContainer = apiType.GetMethod(
                "CountItemInContainer",
                publicStatic,
                null,
                new[] { containerType, typeof(string) },
                null);
            MethodInfo? canItemBePulled = apiType.GetMethod(
                "CanItemBePulled",
                publicStatic,
                null,
                new[] { typeof(string), typeof(string) },
                null);
            MethodInfo? getContainerPrefabName = containerType.GetMethod(
                "GetPrefabName",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);

            if (getNearbyContainersDefinition == null
                || countItemInContainer == null
                || countItemInContainer.IsGenericMethod
                || countItemInContainer.ReturnType != typeof(int)
                || canItemBePulled == null
                || canItemBePulled.IsGenericMethod
                || canItemBePulled.ReturnType != typeof(bool)
                || getContainerPrefabName == null
                || getContainerPrefabName.IsGenericMethod
                || getContainerPrefabName.ReturnType != typeof(string))
            {
                Disable(
                    "AzuCraftyBoxes compatibility disabled because the expected API signature was not found.");
                return;
            }

            _getNearbyContainersDefinition = getNearbyContainersDefinition;
            _countItemInContainer = countItemInContainer;
            _canItemBePulled = canItemBePulled;
            _getContainerPrefabName = getContainerPrefabName;
            _isReady = true;
            FineDiningPlugin.Log.LogInfo("AzuCraftyBoxes station-hint compatibility enabled.");
        }
        catch (Exception exception)
        {
            Disable("AzuCraftyBoxes compatibility initialization failed.", exception);
        }
    }

    private static MethodInfo? FindGetNearbyContainers(Type apiType, Type containerType)
    {
        MethodInfo? match = null;
        foreach (MethodInfo method in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != "GetNearbyContainers"
                || !method.IsGenericMethodDefinition
                || method.GetGenericArguments().Length != 1)
            {
                continue;
            }

            Type genericArgument = method.GetGenericArguments()[0];
            Type[] constraints = genericArgument.GetGenericParameterConstraints();
            ParameterInfo[] parameters = method.GetParameters();
            Type returnType = method.ReturnType;
            if (constraints.Length != 1
                || constraints[0] != typeof(Component)
                || parameters.Length != 2
                || parameters[0].ParameterType != genericArgument
                || parameters[1].ParameterType != typeof(float)
                || !returnType.IsGenericType
                || returnType.GetGenericTypeDefinition() != typeof(List<>)
                || returnType.GetGenericArguments()[0] != containerType)
            {
                continue;
            }

            if (match != null)
            {
                return null;
            }

            match = method;
        }

        return match;
    }

    private static bool InvokeCanItemBePulled(
        MethodInfo method,
        string ownerPrefab,
        string itemPrefab)
    {
        object? result = method.Invoke(null, new object[] { ownerPrefab, itemPrefab });
        return result is bool value
            ? value
            : throw new InvalidOperationException(
                "AzuCraftyBoxes CanItemBePulled returned an unexpected value.");
    }

    private static void Disable(string message, Exception? exception = null)
    {
        _isReady = false;
        _broken = true;
        _getNearbyContainersDefinition = null;
        _countItemInContainer = null;
        _canItemBePulled = null;
        _getContainerPrefabName = null;

        FineDiningPlugin.Log.LogWarning(
            exception == null
                ? message
                : $"{message} {exception.GetBaseException().Message}");
    }

    internal readonly struct ContainerReference
    {
        internal ContainerReference(object container, string prefabName)
        {
            Container = container;
            PrefabName = prefabName;
        }

        internal object Container { get; }
        internal string PrefabName { get; }
    }

    internal sealed class NearbyContainerQuery
    {
        private readonly IReadOnlyList<ContainerReference> _containers;
        private readonly MethodInfo _countItemInContainer;
        private readonly MethodInfo _canItemBePulled;

        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        private bool _failed;

        internal NearbyContainerQuery(
            IReadOnlyList<ContainerReference> containers,
            MethodInfo countItemInContainer,
            MethodInfo canItemBePulled)
        {
            _containers = containers;
            _countItemInContainer = countItemInContainer;
            _canItemBePulled = canItemBePulled;
        }

        internal int CountAvailable(string itemPrefab, string sharedName)
        {
            if (_failed || string.IsNullOrEmpty(itemPrefab) || string.IsNullOrEmpty(sharedName))
            {
                return 0;
            }

            string cacheKey = itemPrefab + "\n" + sharedName;
            if (_counts.TryGetValue(cacheKey, out int cached))
            {
                return cached;
            }

            try
            {
                long total = 0L;
                foreach (ContainerReference reference in _containers)
                {
                    if (!string.IsNullOrEmpty(reference.PrefabName)
                        && !InvokeCanItemBePulled(
                            _canItemBePulled,
                            reference.PrefabName,
                            itemPrefab))
                    {
                        continue;
                    }

                    object? countObject = _countItemInContainer.Invoke(
                        null,
                        new[] { reference.Container, sharedName });
                    if (countObject is int count && count > 0)
                    {
                        total = Math.Min(int.MaxValue, total + count);
                    }
                }

                int result = (int)total;
                _counts[cacheKey] = result;
                return result;
            }
            catch (Exception exception)
            {
                _failed = true;
                Disable("AzuCraftyBoxes nearby count failed.", exception);
                return 0;
            }
        }
    }
}
