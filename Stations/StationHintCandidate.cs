using UnityEngine;

namespace FineDining;

internal sealed class StationHintCandidate
{
    internal StationHintCandidate(string displayName, Sprite? icon, string statusText = "")
    {
        DisplayName = displayName;
        Icon = icon;
        StatusText = statusText;
    }

    internal string DisplayName { get; }

    internal Sprite? Icon { get; }

    internal string StatusText { get; }
}
