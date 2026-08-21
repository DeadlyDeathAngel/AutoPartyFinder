using System;
using Dalamud.Configuration;

namespace AutoPartyFinder;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>0 Normal, 1 Alliance, 2 Custom Match.</summary>
    public int RecruitmentType { get; set; }

    /// <summary>AgentLookingForGroup.DutyCategory flag value.</summary>
    public uint DutyCategory { get; set; }

    public ushort DutyId { get; set; }

    /// <summary>0 None, 1 Duty Completion, 2 Practice, 3 Loot.</summary>
    public int ObjectiveIndex { get; set; }

    public bool BeginnerFriendly { get; set; }

    public string Comment { get; set; } = string.Empty;

    public int AverageItemLevel { get; set; } = 0;

    public bool AverageItemLevelEnabled { get; set; } = true;

    public bool AutoPutUpPf { get; set; }

    public int RelistAfterMinutes { get; set; } = 62;

    public bool IsConfigWindowMovable { get; set; } = true;

    public bool ShouldAutoRelist => AutoPutUpPf;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
