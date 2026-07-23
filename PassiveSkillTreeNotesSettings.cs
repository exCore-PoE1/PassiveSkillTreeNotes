using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace PassiveSkillTreeNotes;

public sealed class PassiveSkillTreeNotesSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);
}
