using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoPartyFinder.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly string[] RecruitmentTypes = ["Normal", "Alliance", "Custom Match"];
    private static readonly string[] Objectives = ["None", "Duty Completion", "Practice", "Loot"];

    private readonly Plugin plugin;
    private string dutyFilter = string.Empty;

    public MainWindow(Plugin plugin)
        : base("Auto Party Finder###AutoPartyFinderMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 480),
            MaximumSize = new Vector2(800, 1200),
        };
        Size = new Vector2(460, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var changed = false;

        ImGui.TextUnformatted("Recruit Members");
        ImGui.Separator();

        var type = cfg.RecruitmentType;
        if (ImGui.Combo("Party type", ref type, RecruitmentTypes, RecruitmentTypes.Length))
        {
            cfg.RecruitmentType = type;
            changed = true;
        }

        ImGui.TextDisabled(DutyCatalog.DescribePartySize(plugin.Duties.GetGroupCount(cfg.DutyCategory, cfg.DutyId)));

        var categoryIndex = IndexOfCategory(cfg.DutyCategory);
        var categoryLabels = CategoryLabels();
        if (ImGui.Combo("Duty category", ref categoryIndex, categoryLabels, categoryLabels.Length))
        {
            cfg.DutyCategory = plugin.Duties.Categories[categoryIndex].Category;
            cfg.DutyId = 0;
            plugin.Duties.RequestLiveList(cfg.DutyCategory);
            changed = true;
        }

        if (!plugin.PartyFinder.IsBusy && plugin.PartyFinder.OwnListingId == 0)
            plugin.Duties.TickLiveList(cfg.DutyCategory);
        ImGui.TextDisabled("Duty list matches Recruitment Criteria when Party Finder is open.");

        ImGui.InputTextWithHint("##dutyfilter", "Filter duties", ref dutyFilter, 80);

        var duties = plugin.Duties.ForCategory(cfg.DutyCategory);
        var currentDutyName = plugin.Duties.GetDutyName(cfg.DutyCategory, cfg.DutyId);
        if (ImGui.BeginCombo("Duty", currentDutyName))
        {
            for (var i = 0; i < duties.Count; i++)
            {
                var duty = duties[i];
                if (!string.IsNullOrWhiteSpace(dutyFilter)
                    && duty.Name.IndexOf(dutyFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var selected = duty.Id == cfg.DutyId;
                if (ImGui.Selectable(duty.Name, selected))
                {
                    cfg.DutyId = duty.Id;
                    cfg.RecruitmentType = plugin.Duties.RecommendedRecruitmentType(cfg.DutyCategory, duty.Id);
                    changed = true;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        var objective = Math.Clamp(cfg.ObjectiveIndex, 0, Objectives.Length - 1);
        if (ImGui.Combo("Objective", ref objective, Objectives, Objectives.Length))
        {
            cfg.ObjectiveIndex = objective;
            changed = true;
        }

        var newbie = cfg.BeginnerFriendly;
        if (ImGui.Checkbox("Newbie welcome", ref newbie))
        {
            cfg.BeginnerFriendly = newbie;
            changed = true;
        }

        var comment = cfg.Comment ?? string.Empty;
        if (ImGui.InputTextMultiline("Comment", ref comment, 191, new Vector2(0, 70)))
        {
            cfg.Comment = comment;
            changed = true;
        }

        var ilEnabled = cfg.AverageItemLevelEnabled;
        if (ImGui.Checkbox("Avg. Item Lv.", ref ilEnabled))
        {
            cfg.AverageItemLevelEnabled = ilEnabled;
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        var itemLevel = cfg.AverageItemLevel;
        if (ImGui.InputInt("##itemlevel", ref itemLevel))
        {
            cfg.AverageItemLevel = Math.Clamp(itemLevel, 0, 999);
            changed = true;
        }

        ImGui.TextDisabled("0–999");

        var autoPutUp = cfg.AutoPutUpPf;
        if (ImGui.Checkbox("Auto put up PF", ref autoPutUp))
        {
            cfg.AutoPutUpPf = autoPutUp;
            changed = true;
        }

        var minutes = cfg.RelistAfterMinutes;
        if (cfg.AutoPutUpPf)
        {
            if (ImGui.SliderInt("Re-list after (minutes)", ref minutes, 1, 59))
            {
                cfg.RelistAfterMinutes = minutes;
                changed = true;
            }
        }

        if (changed)
            cfg.Save();

        ImGui.Separator();
        ImGui.TextUnformatted($"Status: {plugin.PartyFinder.StatusText}");
        if (plugin.PartyFinder.HasActiveListing)
            ImGui.TextDisabled(plugin.PartyFinder.OwnListingId != 0
                ? $"Listing ID {plugin.PartyFinder.OwnListingId}"
                : "Party Finder listing is up");

        ImGui.Spacing();

        var busy = plugin.PartyFinder.IsBusy;
        if (busy)
            ImGui.BeginDisabled();

        if (ImGui.Button("Recruit Members", new Vector2(160, 0)))
            plugin.PartyFinder.StartRecruit(enableAutoRelist: true);

        ImGui.SameLine();
        if (ImGui.Button("Load last in-game PF"))
        {
            if (plugin.PartyFinder.TryLoadFromGame())
                plugin.Chat("Loaded the previous Party Finder conditions.");
            else
                plugin.ChatError("Could not read Party Finder conditions.");
        }

        if (busy)
            ImGui.EndDisabled();

        if (ImGui.Button("Stop auto-relist"))
            plugin.PartyFinder.Stop(endListing: false);

        ImGui.SameLine();
        if (ImGui.Button("End listing"))
            plugin.PartyFinder.Stop(endListing: true);

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Conditions are saved and reused the next time you recruit. " +
            "With Auto put up PF enabled, the listing is posted again after the timer, or sooner if Party Finder ends.");
        ImGui.TextDisabled("/apf  /apf recruit  /apf stop  /apf end");
    }

    private string[] CategoryLabels()
    {
        var labels = new string[plugin.Duties.Categories.Count];
        for (var i = 0; i < labels.Length; i++)
            labels[i] = plugin.Duties.Categories[i].Label;
        return labels;
    }

    private int IndexOfCategory(uint category)
    {
        for (var i = 0; i < plugin.Duties.Categories.Count; i++)
        {
            if (plugin.Duties.Categories[i].Category == category)
                return i;
        }

        return 0;
    }
}
