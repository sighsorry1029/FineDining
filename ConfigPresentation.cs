using BepInEx.Configuration;

namespace FineDining;

/// <summary>
/// Defines the public configuration schema and its ConfigManager presentation.
/// Gameplay owners keep their own entries; this class only keeps section names,
/// scope labels, and ordering consistent across those owners.
/// </summary>
internal static class ConfigPresentation
{
    internal readonly struct SectionDefinition
    {
        internal SectionDefinition(string name, int categoryOrder)
        {
            Name = name;
            CategoryOrder = categoryOrder;
        }

        internal string Name { get; }
        internal int CategoryOrder { get; }
    }

    internal static readonly SectionDefinition General =
        new("1 - General", 500);
    internal static readonly SectionDefinition ClientSection =
        new("2 - Client", 400);
    internal static readonly SectionDefinition Diet =
        new("3 - Diet", 300);
    internal static readonly SectionDefinition ChefChoice =
        new("4 - Chef Choice", 200);
    internal static readonly SectionDefinition Spoilage =
        new("5 - Spoilage", 100);

    internal static ConfigDescription Synced(
        string description,
        SectionDefinition section,
        int order,
        AcceptableValueBase? acceptableValues = null) =>
        Create(description, "[Synced with Server]", section, order, acceptableValues);

    internal static ConfigDescription Client(
        string description,
        SectionDefinition section,
        int order,
        AcceptableValueBase? acceptableValues = null) =>
        Create(description, "[Client Only]", section, order, acceptableValues);

    private static ConfigDescription Create(
        string description,
        string scope,
        SectionDefinition section,
        int order,
        AcceptableValueBase? acceptableValues) =>
        new(
            description + " " + scope,
            acceptableValues,
            new object[]
            {
                new ConfigurationManagerAttributes
                {
                    CategoryOrder = section.CategoryOrder,
                    Order = order
                }
            });
}

/// <summary>
/// Optional ConfigManager metadata. ConfigManager discovers this shape by type
/// name and reflection, so FineDining has no compile-time or runtime dependency
/// on a particular ConfigManager build.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    public int? CategoryOrder { get; set; }
    public int? Order { get; set; }
}
