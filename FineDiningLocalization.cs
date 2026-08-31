using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Globalization;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;

namespace FineDining;

internal static class FineDiningLocalization
{
    private static BaseUnityPlugin? _plugin;
    internal static event Action? OnLocalizationComplete;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreFields()
        .Build();

    private static BaseUnityPlugin Plugin =>
        _plugin ?? throw new InvalidOperationException("FineDining localization is not initialized.");

    private static readonly List<string> fileExtensions = [".json", ".yml"];

    internal static void Initialize(BaseUnityPlugin plugin)
    {
        _plugin = plugin;
    }

    internal static void Shutdown()
    {
        OnLocalizationComplete = null;
        _plugin = null;
    }

    internal static void LoadLocalizationLater()
    {
        if (Localization.instance != null)
        {
            LoadLocalization(Localization.instance, Localization.instance.GetSelectedLanguage());
        }
    }

    internal static string LocalizeOrFallback(string token, string fallback)
    {
        Localization? localization = Localization.instance;
        if ((object?)localization == null)
        {
            return fallback;
        }

        string localized = localization.Localize(token);
        return string.IsNullOrWhiteSpace(localized) ||
               string.Equals(localized, token, StringComparison.Ordinal)
            ? fallback
            : localized;
    }

    internal static string LocalizeOrFallback(
        string token,
        string fallback,
        params string[] arguments)
    {
        string localized = Localization.instance?.Localize(token, arguments) ?? token;
        if (!string.IsNullOrWhiteSpace(localized) &&
            !string.Equals(localized, token, StringComparison.Ordinal))
        {
            return localized;
        }

        string result = fallback;
        for (int index = 0; index < arguments.Length; index++)
        {
            result = result.Replace(
                "$" + (index + 1).ToString(CultureInfo.InvariantCulture),
                arguments[index]);
        }

        return result;
    }

    internal static string FormatOrFallback(
        string token,
        string fallback,
        params object[] values)
    {
        string localizedFormat = LocalizeOrFallback(token, fallback);
        try
        {
            string localized = string.Format(
                CultureInfo.InvariantCulture,
                localizedFormat,
                values);
            if (ContainsEveryValue(localized, values))
            {
                return localized;
            }
        }
        catch (FormatException)
        {
            // Malformed translations fall through to the known English format.
        }

        return string.Format(CultureInfo.InvariantCulture, fallback, values);
    }

    private static bool ContainsEveryValue(string text, object[] values)
    {
        foreach (object value in values)
        {
            string required = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (required.Length > 0 && text.IndexOf(required, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        return true;
    }

    internal static void LoadLocalization(Localization __instance, string language)
    {
        Dictionary<string, string> localizationFiles = new();
        foreach (string file in Directory.GetFiles(Paths.PluginPath, $"{Plugin.Info.Metadata.Name}.*", SearchOption.AllDirectories).Where(f => fileExtensions.IndexOf(Path.GetExtension(f)) >= 0))
        {
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('.');
            if (parts.Length < 2)
            {
                continue;
            }

            string key = parts[1];
            if (localizationFiles.ContainsKey(key))
            {
                // Handle duplicate key
                Debug.LogWarning($"Duplicate key {key} found for {Plugin.Info.Metadata.Name}. The duplicate file found at {file} will be skipped.");
            }
            else
            {
                localizationFiles[key] = file;
            }
        }

        if (LoadTranslationFromAssembly("English") is not { } englishAssemblyData)
        {
            throw new Exception($"Found no English localizations in mod {Plugin.Info.Metadata.Name}. Expected an embedded resource translations/English.json or translations/English.yml.");
        }

        Dictionary<string, string>? localizationTexts = Deserializer.Deserialize<Dictionary<string, string>?>(Encoding.UTF8.GetString(englishAssemblyData));
        if (localizationTexts is null)
        {
            throw new Exception($"Localization for mod {Plugin.Info.Metadata.Name} failed: Localization file was empty.");
        }

        string? localizationData = null;
        if (language != "English")
        {
            if (localizationFiles.TryGetValue(language, out string? localizationFile))
            {
                localizationData = File.ReadAllText(localizationFile);
            }
            else if (LoadTranslationFromAssembly(language) is { } languageAssemblyData)
            {
                localizationData = Encoding.UTF8.GetString(languageAssemblyData);
            }
        }

        if (localizationData is null && localizationFiles.TryGetValue("English", out string? localizationFile1))
        {
            localizationData = File.ReadAllText(localizationFile1);
        }

        if (localizationData is not null)
        {
            foreach (KeyValuePair<string, string> kv in Deserializer.Deserialize<Dictionary<string, string>?>(localizationData) ?? new Dictionary<string, string>())
            {
                localizationTexts[kv.Key] = kv.Value;
            }
        }

        foreach (KeyValuePair<string, string> s in localizationTexts)
        {
            __instance.AddWord(s.Key, s.Value);
        }

        OnLocalizationComplete?.Invoke();
    }

    private static byte[]? LoadTranslationFromAssembly(string language)
    {
        foreach (string extension in fileExtensions)
        {
            if (ReadEmbeddedFileBytes("translations." + language + extension) is { } data)
            {
                return data;
            }
        }

        return null;
    }

    private static byte[]? ReadEmbeddedFileBytes(string resourceFileName)
    {
        using MemoryStream stream = new();
        Assembly containingAssembly = typeof(FineDiningLocalization).Assembly;
        if (containingAssembly.GetManifestResourceNames().FirstOrDefault(str => str.EndsWith(resourceFileName, StringComparison.Ordinal)) is { } name)
        {
            containingAssembly.GetManifestResourceStream(name)?.CopyTo(stream);
        }

        return stream.Length == 0 ? null : stream.ToArray();
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class FineDiningLocalizationLanguagePatch
{
    [HarmonyPostfix]
    private static void Postfix(Localization __instance, string language) =>
        FineDiningLocalization.LoadLocalization(__instance, language);
}

[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.SetupGui))]
internal static class FineDiningLocalizationGuiPatch
{
    [HarmonyPostfix]
    private static void Postfix() => FineDiningLocalization.LoadLocalizationLater();
}
