using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using SharpDX;

namespace PassiveSkillTreeNotes;

public sealed class PassiveSkillTreeNotesSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    [Menu("League Start Mode", 100, CollapsedByDefault = false)]
    public EmptyNode LeagueStartModeSettings { get; set; } = new();

    [Menu(
        "Enabled",
        "Show build notes and highlight mentioned gems in purchase and quest reward windows.",
        1,
        100)]
    public ToggleNode LeagueStartMode { get; set; } = new(false);

    [Menu("Gem Border Color", "Border color for mentioned gems.", 2, 100)]
    public ColorNode GemBorderColor { get; set; } = new(Color.LimeGreen);

    [Menu("Gem Border Thickness", "Border thickness in pixels.", 3, 100)]
    public RangeNode<int> GemBorderThickness { get; set; } = new(4, 1, 10);

    [Menu(
        "Show Reward Gem Names",
        "Show the matched gem name along the bottom of quest reward borders.",
        4,
        100)]
    public ToggleNode ShowRewardGemNames { get; set; } = new(true);

    [Menu(
        "Show Vendor Gem Names",
        "Show the matched gem name along the bottom of vendor borders.",
        5,
        100)]
    public ToggleNode ShowVendorGemNames { get; set; } = new(true);

    [Menu(
        "Hide Owned Gems",
        "Do not highlight gems already socketed in equipped gear or carried in the player inventory.",
        6,
        100)]
    public ToggleNode HideOwnedGems { get; set; } = new(true);
}
