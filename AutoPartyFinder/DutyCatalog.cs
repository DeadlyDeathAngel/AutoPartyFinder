using System;
using System.Collections.Generic;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoPartyFinder;

public readonly record struct DutyOption(ushort Id, string Name, uint Category, byte GroupCount = 1);

public sealed class DutyCatalog
{
    private readonly List<DutyOption> sheetDuties = [];
    private readonly Dictionary<uint, List<DutyOption>> byCategory = [];
    private readonly HashSet<uint> liveCategories = [];
    private uint? pendingLiveCategory;

    public IReadOnlyList<(uint Category, string Label)> Categories { get; } =
    [
        ((uint)AgentLookingForGroup.DutyCategory.None, "None"),
        ((uint)AgentLookingForGroup.DutyCategory.Roulette, "Duty Roulette"),
        ((uint)AgentLookingForGroup.DutyCategory.Dungeons, "Dungeons"),
        ((uint)AgentLookingForGroup.DutyCategory.GuildQuests, "Guildhests"),
        ((uint)AgentLookingForGroup.DutyCategory.Trials, "Trials"),
        ((uint)AgentLookingForGroup.DutyCategory.Raids, "Raids"),
        ((uint)AgentLookingForGroup.DutyCategory.HighEndDuty, "High-end Duty"),
        ((uint)AgentLookingForGroup.DutyCategory.PvP, "PvP"),
        ((uint)AgentLookingForGroup.DutyCategory.GoldSaucer, "Gold Saucer"),
        ((uint)AgentLookingForGroup.DutyCategory.FATEs, "FATEs"),
        ((uint)AgentLookingForGroup.DutyCategory.TreasureHunts, "Treasure Hunt"),
        ((uint)AgentLookingForGroup.DutyCategory.TheHunt, "The Hunt"),
        ((uint)AgentLookingForGroup.DutyCategory.GatheringForays, "Gathering Forays"),
        ((uint)AgentLookingForGroup.DutyCategory.DeepDungeons, "Deep Dungeons"),
        ((uint)AgentLookingForGroup.DutyCategory.FieldOperations, "Field Operations"),
        ((uint)AgentLookingForGroup.DutyCategory.VCDungeonFinder, "V&C Dungeon Finder"),
    ];

    public DutyCatalog()
    {
        LoadRoulettes();
        LoadDuties();
        RebuildFromSheets();
    }

    public IReadOnlyList<DutyOption> ForCategory(uint category)
    {
        return byCategory.TryGetValue(category, out var list)
            ? list
            : [new DutyOption(0, "None", category)];
    }

    public string GetDutyName(uint category, ushort dutyId)
    {
        foreach (var duty in ForCategory(category))
        {
            if (duty.Id == dutyId)
                return duty.Name;
        }

        return dutyId == 0 ? "None" : $"Duty #{dutyId}";
    }

    public int IndexOfCategory(uint category)
    {
        for (var i = 0; i < Categories.Count; i++)
        {
            if (Categories[i].Category == category)
                return i;
        }

        return 0;
    }

    public int IndexOfDuty(uint category, ushort dutyId)
    {
        var list = ForCategory(category);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == dutyId)
                return i;
        }

        return 0;
    }

    public byte GetGroupCount(uint category, ushort dutyId)
    {
        if (dutyId == 0)
            return 1;

        foreach (var duty in sheetDuties)
        {
            if (duty.Id == dutyId && duty.GroupCount is 1 or 3 or 6)
                return duty.GroupCount;
        }

        return 1;
    }

    public static string DescribePartySize(byte groupCount) => groupCount switch
    {
        6 => "48 players — Alliance A–F",
        3 => "24 players — Alliance A–C",
        _ => "8 players — Normal",
    };

    public int RecommendedRecruitmentType(uint category, ushort dutyId)
        => GetGroupCount(category, dutyId) >= 3 ? 1 : 0;

    public void RequestLiveList(uint category)
    {
        pendingLiveCategory = category;
        liveCategories.Remove(category);
    }

    public unsafe void TickLiveList(uint category)
    {
        var target = pendingLiveCategory ?? category;
        // Never change native dropdowns here. Selecting category resets Duty to None
        // and turns Recruitment Criteria' End button back into Recruit Members.
        if (TryReadRecruitmentCriteria(target, selectCategory: false))
            pendingLiveCategory = null;
    }

    public unsafe bool SelectNativeCategory(AddonLookingForGroupCondition* addon, uint category, bool dispatchEvent = false)
    {
        return addon != null && NativeUi.SelectDropDown(
            (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon,
            addon->DutyCategoryDropDown,
            IndexOfCategory(category),
            dispatchEvent,
            7);
    }

    public unsafe bool SelectNativeDuty(AddonLookingForGroupCondition* addon, uint category, ushort dutyId, bool dispatchEvent = false)
    {
        if (addon == null)
            return false;

        var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon;
        if (dutyId == 0)
            return NativeUi.SelectDropDown(unit, addon->DutyDropDown, 0, dispatchEvent, 8);

        var name = GetDutyName(category, dutyId);
        var index = NativeUi.FindLabelIndex(addon->DutyDropDown, name);
        if (index <= 0)
            return false;

        return NativeUi.SelectDropDown(unit, addon->DutyDropDown, index, dispatchEvent, 8);
    }

    public unsafe bool IsNativeDutySelected(AddonLookingForGroupCondition* addon, uint category, ushort dutyId)
    {
        if (addon == null || addon->DutyDropDown == null)
            return false;

        var selectedLabel = NativeUi.GetSelectedLabel(addon->DutyDropDown);
        if (dutyId == 0)
            return NativeUi.IsNoneOrAll(selectedLabel);

        if (NativeUi.IsNoneOrAll(selectedLabel))
            return false;

        return NativeUi.LabelsMatch(selectedLabel, GetDutyName(category, dutyId));
    }

    public unsafe bool NativeDutyListContains(AddonLookingForGroupCondition* addon, uint category, ushort dutyId)
    {
        if (addon == null || dutyId == 0)
            return true;

        return NativeUi.FindLabelIndex(addon->DutyDropDown, GetDutyName(category, dutyId)) > 0;
    }

    private void RebuildFromSheets()
    {
        byCategory.Clear();
        liveCategories.Clear();

        foreach (var (category, _) in Categories)
        {
            var list = new List<DutyOption>();
            foreach (var duty in sheetDuties)
            {
                if (duty.Category == category)
                    list.Add(duty);
            }

            list.Sort(ComparePfOrder);
            list.Insert(0, new DutyOption(0, "None", category));
            byCategory[category] = list;
        }
    }

    private unsafe bool TryReadRecruitmentCriteria(uint category, bool selectCategory)
    {
        if (!NativeUi.TryGetAddon<AddonLookingForGroupCondition>("LookingForGroupCondition", out var addon)
            || addon->DutyDropDown == null)
            return false;

        var categoryIndex = IndexOfCategory(category);
        if (selectCategory && addon->DutyCategoryDropDown != null)
        {
            if (addon->DutyCategoryDropDown->GetSelectedItemIndex() != categoryIndex)
            {
                addon->DutyCategoryDropDown->SelectItem(categoryIndex);
                return false;
            }
        }
        else if (addon->DutyCategoryDropDown != null
                 && addon->DutyCategoryDropDown->GetSelectedItemIndex() != categoryIndex)
        {
            return false;
        }

        var labels = NativeUi.ReadDropDownLabels(addon->DutyDropDown);
        if (labels == null || labels.Count == 0)
            return false;

        var rebuilt = new List<DutyOption>(labels.Count);
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var id = i == 0 || IsNoneLabel(label) ? (ushort)0 : ResolveDutyId(category, label);
            rebuilt.Add(new DutyOption(id, label, category, GetGroupCount(category, id)));
        }

        byCategory[category] = rebuilt;
        liveCategories.Add(category);
        Plugin.Log.Debug($"Loaded {rebuilt.Count} Recruitment Criteria duties for {GetCategoryName(category)}.");
        return true;
    }

    private ushort ResolveDutyId(uint category, string label)
    {
        foreach (var duty in sheetDuties)
        {
            if (duty.Category != category)
                continue;
            if (NamesMatch(duty.Name, label))
                return duty.Id;
        }

        foreach (var duty in sheetDuties)
        {
            if (NamesMatch(duty.Name, label))
                return duty.Id;
        }

        return 0;
    }

    private void LoadRoulettes()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ContentRoulette>();
        if (sheet == null)
            return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                continue;

            var name = DisplayName(row.Name.ToString());
            if (string.IsNullOrEmpty(name))
                continue;

            var category = (uint)AgentLookingForGroup.DutyCategory.Roulette;
            if (row.IsPvP)
                category = (uint)AgentLookingForGroup.DutyCategory.PvP;
            else if (row.IsGoldSaucer)
                category = (uint)AgentLookingForGroup.DutyCategory.GoldSaucer;

            sheetDuties.Add(new DutyOption((ushort)row.RowId, name, category, ResolveGroupCount(row.ContentMemberType)));
        }
    }

    private void LoadDuties()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
        if (sheet == null)
            return;

        foreach (var row in sheet)
        {
            if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                continue;

            var name = DisplayName(row.Name.ToString());
            if (string.IsNullOrEmpty(name))
                continue;

            var category = MapCategory(row);
            if (category == (uint)AgentLookingForGroup.DutyCategory.None)
                continue;

            sheetDuties.Add(new DutyOption((ushort)row.RowId, name, category, ResolveGroupCount(row.ContentMemberType)));
        }
    }

    private static byte ResolveGroupCount(RowRef<ContentMemberType> memberType)
    {
        if (!memberType.IsValid)
            return 1;

        var type = memberType.Value;
        if (type.AlliancePartyCount is > 0 and <= 6)
            return type.AlliancePartyCount;

        return type.PartyCount switch
        {
            3 => 3,
            6 => 6,
            _ => 1,
        };
    }

    private static uint MapCategory(ContentFinderCondition row)
    {
        if (row.HighEndDuty)
            return (uint)AgentLookingForGroup.DutyCategory.HighEndDuty;

        if (row.PvP)
            return (uint)AgentLookingForGroup.DutyCategory.PvP;

        return row.ContentType.RowId switch
        {
            2 => (uint)AgentLookingForGroup.DutyCategory.Dungeons,
            3 => (uint)AgentLookingForGroup.DutyCategory.GuildQuests,
            4 => (uint)AgentLookingForGroup.DutyCategory.Trials,
            5 => (uint)AgentLookingForGroup.DutyCategory.Raids,
            6 => (uint)AgentLookingForGroup.DutyCategory.PvP,
            8 => (uint)AgentLookingForGroup.DutyCategory.FATEs,
            9 => (uint)AgentLookingForGroup.DutyCategory.TreasureHunts,
            16 => (uint)AgentLookingForGroup.DutyCategory.GatheringForays,
            19 => (uint)AgentLookingForGroup.DutyCategory.GoldSaucer,
            21 => (uint)AgentLookingForGroup.DutyCategory.DeepDungeons,
            23 => (uint)AgentLookingForGroup.DutyCategory.GatheringForays,
            26 => (uint)AgentLookingForGroup.DutyCategory.FieldOperations,
            27 => (uint)AgentLookingForGroup.DutyCategory.GoldSaucer,
            28 => (uint)AgentLookingForGroup.DutyCategory.HighEndDuty,
            29 => (uint)AgentLookingForGroup.DutyCategory.FieldOperations,
            30 => (uint)AgentLookingForGroup.DutyCategory.VCDungeonFinder,
            31 => (uint)AgentLookingForGroup.DutyCategory.GatheringForays,
            32 => (uint)AgentLookingForGroup.DutyCategory.GoldSaucer,
            33 => (uint)AgentLookingForGroup.DutyCategory.TheHunt,
            35 => (uint)AgentLookingForGroup.DutyCategory.GoldSaucer,
            37 => (uint)AgentLookingForGroup.DutyCategory.HighEndDuty,
            38 => (uint)AgentLookingForGroup.DutyCategory.FieldOperations,
            _ => (uint)AgentLookingForGroup.DutyCategory.None,
        };
    }

    private static int ComparePfOrder(DutyOption left, DutyOption right)
    {
        var byId = right.Id.CompareTo(left.Id);
        if (byId != 0)
            return byId;
        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return string.Empty;

        return char.ToUpper(trimmed[0], CultureInfo.InvariantCulture) + trimmed[1..];
    }

    private static bool IsNoneLabel(string label)
        => NativeUi.IsNoneOrAll(label);

    private static bool NamesMatch(string left, string right)
    {
        return NormalizeName(left) == NormalizeName(right);
    }

    private static string NormalizeName(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            text = text[4..];
        return text.ToLowerInvariant();
    }

    public string GetCategoryName(uint category)
    {
        foreach (var (value, label) in Categories)
        {
            if (value == category)
                return label;
        }

        return "None";
    }
}
