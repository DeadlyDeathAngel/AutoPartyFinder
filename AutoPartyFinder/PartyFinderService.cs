using System;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoPartyFinder;

public enum RecruitStatus
{
    Idle,
    OpeningPartyFinder,
    OpeningCondition,
    Applying,
    ClickingRecruit,
    Confirming,
    WaitingForListing,
    Listed,
    Withdrawing,
    ConfirmingWithdraw,
    RelistWait,
    Failed,
}

internal enum ApplyPhase
{
    PartyType,
    Category,
    Duty,
    Details,
}

public sealed class PartyFinderService : IDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EndedRelistDelay = TimeSpan.FromSeconds(5);

    private readonly Plugin plugin;
    private RecruitStatus status = RecruitStatus.Idle;
    private DateTime stepStarted;
    private DateTime listedAt;
    private DateTime nextRelistAt;
    private DateTime lastRecruitClick;
    private bool clickedConditionButton;
    private bool clickedEndButton;
    private bool populatedCriteria;
    private ApplyPhase applyPhase;
    private DateTime applyPhaseStarted;
    private string? lastError;
    private bool autoRelist;
    private bool userRequestedStop;
    private bool listingPosted;
    private bool endingListing;
    private ulong knownListingId;
    private bool requestedListings;
    private volatile bool sawRecruitmentCommenced;
    private volatile bool sawRecruitmentEnded;
    private uint lastSeenListingId;

    public PartyFinderService(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.PartyFinderGui.ReceiveListing += OnReceiveListing;
    }

    public RecruitStatus Status => status;

    public string StatusText => status switch
    {
        RecruitStatus.Idle => userRequestedStop ? "Stopped" : "Idle",
        RecruitStatus.OpeningPartyFinder => "Opening Party Finder…",
        RecruitStatus.OpeningCondition => "Opening Recruit Members…",
        RecruitStatus.Applying => applyPhase switch
        {
            ApplyPhase.PartyType => "Setting party type…",
            ApplyPhase.Category => "Selecting duty category…",
            ApplyPhase.Duty => "Selecting duty…",
            ApplyPhase.Details => "Filling recruitment details…",
            _ => "Applying saved conditions…",
        },
        RecruitStatus.ClickingRecruit => "Clicking Recruit Members…",
        RecruitStatus.Confirming => "Confirming…",
        RecruitStatus.WaitingForListing => "Waiting for listing…",
        RecruitStatus.Listed => AutoRelistActive
            ? $"Listed — auto-relist in {FormatRemaining(TimeUntilRelist)}"
            : "Listed",
        RecruitStatus.Withdrawing => "Ending current listing…",
        RecruitStatus.ConfirmingWithdraw => "Confirming end recruitment…",
        RecruitStatus.RelistWait => $"Re-listing in {FormatRemaining(nextRelistAt - DateTime.UtcNow)}",
        RecruitStatus.Failed => lastError ?? "Failed",
        _ => status.ToString(),
    };

    public bool IsBusy => status is not RecruitStatus.Idle and not RecruitStatus.Listed and not RecruitStatus.Failed and not RecruitStatus.RelistWait;

    public bool AutoRelistActive => autoRelist && !userRequestedStop;

    public TimeSpan TimeUntilRelist
    {
        get
        {
            if (!AutoRelistActive || status != RecruitStatus.Listed)
                return TimeSpan.Zero;

            var remaining = listedAt.AddMinutes(Math.Max(1, plugin.Configuration.RelistAfterMinutes)) - DateTime.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    public bool HasActiveListing => OwnListingId != 0 || listingPosted;

    public uint OwnListingId
    {
        get
        {
            unsafe
            {
                var agent = AgentLookingForGroup.Instance();
                return agent == null ? 0 : agent->OwnListingId;
            }
        }
    }

    public void Dispose()
    {
        Plugin.PartyFinderGui.ReceiveListing -= OnReceiveListing;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update -= OnUpdate;
    }

    public void StartRecruit(bool enableAutoRelist)
    {
        plugin.Configuration.Save();
        userRequestedStop = false;
        autoRelist = enableAutoRelist && plugin.Configuration.ShouldAutoRelist;
        endingListing = false;
        knownListingId = 0;
        lastError = null;
        sawRecruitmentEnded = false;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            Fail("You need to be logged in to post a Party Finder listing.");
            return;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            Fail("Wait until you finish changing areas.");
            return;
        }

        plugin.Chat($"Recruiting with saved conditions{(autoRelist ? " (auto-relist on)" : string.Empty)}.");
        BeginPost();
    }

    public unsafe void Stop(bool endListing)
    {
        userRequestedStop = true;
        autoRelist = false;

        if (endListing)
        {
            endingListing = true;
            SetState(RecruitStatus.Withdrawing);
            return;
        }

        SetState(RecruitStatus.Idle);
        plugin.Chat("Auto Party Finder stopped.");
    }

    public unsafe bool TryLoadFromGame()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return false;

        ref var info = ref agent->StoredRecruitmentInfo;
        var cfg = plugin.Configuration;
        cfg.DutyCategory = (uint)info.SelectedCategory;
        cfg.DutyId = info.SelectedDutyId;
        cfg.ObjectiveIndex = ObjectiveToIndex(info.Objective);
        cfg.BeginnerFriendly = info.BeginnerFriendly != 0;
        cfg.Comment = ReadComment(ref info);
        cfg.AverageItemLevel = agent->AvgItemLv;
        cfg.AverageItemLevelEnabled = agent->AvgItemLvEnabled != 0;
        cfg.RecruitmentType = agent->GroupTypeTab switch
        {
            1 => 1,
            2 => 2,
            _ => info.NumberOfGroups >= 3 ? 1 : 0,
        };
        cfg.Save();
        return true;
    }

    private void OnUpdate(IFramework framework)
    {
        if (sawRecruitmentCommenced)
        {
            sawRecruitmentCommenced = false;
            listingPosted = true;
        }

        if (sawRecruitmentEnded)
        {
            sawRecruitmentEnded = false;
            listingPosted = false;
        }

        if (status is RecruitStatus.Idle or RecruitStatus.Failed)
            return;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            if (status != RecruitStatus.Idle)
                Fail("Logged out — recruitment stopped.");
            return;
        }

        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Party Finder recruitment failed.");
            Fail(ex.Message);
        }
    }

    private unsafe void Tick()
    {
        ConfirmYesNoIfNeeded();

        switch (status)
        {
            case RecruitStatus.OpeningPartyFinder:
                OpenPartyFinder();
                break;
            case RecruitStatus.OpeningCondition:
                OpenConditionWindow();
                break;
            case RecruitStatus.Applying:
                ApplyConditions();
                break;
            case RecruitStatus.ClickingRecruit:
                ClickRecruit();
                break;
            case RecruitStatus.Confirming:
                if (HasActiveListing || RecruitmentWindowClosed())
                    OnListed();
                else if (TimedOut())
                    Fail("Timed out waiting for the recruit confirmation.");
                break;
            case RecruitStatus.WaitingForListing:
                if (HasActiveListing || RecruitmentWindowClosed())
                    OnListed();
                else if (TimedOut())
                    Fail("The listing was not created. Check that a duty is selected and try again.");
                break;
            case RecruitStatus.Listed:
                WatchListing();
                break;
            case RecruitStatus.Withdrawing:
                WithdrawListing();
                break;
            case RecruitStatus.ConfirmingWithdraw:
                if (DetailWindowClosed())
                    AfterWithdraw();
                else if (TimedOut())
                    Fail("Could not end the current listing. Open your listing and press End.");
                break;
            case RecruitStatus.RelistWait:
                if (DateTime.UtcNow >= nextRelistAt)
                    BeginPost();
                break;
        }
    }

    private unsafe void BeginPost()
    {
        endingListing = false;
        if (TryGetConditionAddon(out _))
        {
            SetState(RecruitStatus.Applying);
            return;
        }

        SetState(RecruitStatus.OpeningPartyFinder);
    }

    private unsafe void OpenPartyFinder()
    {
        if (TryGetConditionAddon(out _))
        {
            SetState(RecruitStatus.Applying);
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            Fail("Party Finder is not available.");
            return;
        }

        if (NativeUi.TryGetAddon<AddonLookingForGroup>("LookingForGroup", out _))
        {
            SetState(RecruitStatus.OpeningCondition);
            return;
        }

        if (TimedOut())
        {
            Fail("Could not open Party Finder.");
            return;
        }

        if (!agent->IsAgentActive())
            agent->Show();
    }

    private unsafe void OpenConditionWindow()
    {
        if (TryGetConditionAddon(out _))
        {
            SetState(RecruitStatus.Applying);
            return;
        }

        if (TimedOut())
        {
            Fail("Could not open Recruit Members.");
            return;
        }

        TryOpenRecruitmentCriteria();
    }

    private static unsafe bool TryGetConditionAddon(out AddonLookingForGroupCondition* addon)
        => NativeUi.TryGetAddon("LookingForGroupCondition", out addon);

    private unsafe void ApplyConditions()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null || !TryGetConditionAddon(out var addon))
        {
            if (TimedOut())
                Fail("Recruit Members closed before conditions could be applied.");
            return;
        }

        if (TimedOut())
        {
            Fail("Recruit Members stayed locked. Select the duty in Recruitment Criteria if it is still open.");
            return;
        }

        var cfg = plugin.Configuration;
        var groupCount = plugin.Duties.GetGroupCount(cfg.DutyCategory, cfg.DutyId);
        if (groupCount is not (1 or 3 or 6))
            groupCount = cfg.RecruitmentType == 1 ? (byte)3 : (byte)1;

        if (!populatedCriteria)
        {
            WriteStoredRecruitment(agent, cfg, groupCount);
            FillConditionDetails(addon, cfg, groupCount);
            populatedCriteria = true;
        }
        else if (addon->CommentTextInput != null)
        {
            addon->CommentTextInput->SetText(cfg.Comment ?? string.Empty);
        }

        switch (applyPhase)
        {
            case ApplyPhase.PartyType:
                SelectPartyType(addon, groupCount, cfg.RecruitmentType);
                if (ApplyPhaseSettled())
                    AdvanceApplyPhase(ApplyPhase.Category);
                break;
            case ApplyPhase.Category:
                plugin.Duties.SelectNativeCategory(addon, cfg.DutyCategory);
                if (NativeUi.GetSelectedIndex(addon->DutyCategoryDropDown) == plugin.Duties.IndexOfCategory(cfg.DutyCategory)
                    && ApplyPhaseSettled())
                {
                    AdvanceApplyPhase(ApplyPhase.Duty);
                }

                break;
            case ApplyPhase.Duty:
                plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);
                if ((cfg.DutyId == 0 || plugin.Duties.IsNativeDutySelected(addon, cfg.DutyCategory, cfg.DutyId))
                    && ApplyPhaseSettled())
                {
                    AdvanceApplyPhase(ApplyPhase.Details);
                }

                break;
            case ApplyPhase.Details:
                if (addon->RecruitMembersButton != null && addon->RecruitMembersButton->IsEnabled)
                {
                    if (ApplyPhaseSettled())
                        SetState(RecruitStatus.ClickingRecruit);
                }
                else
                {
                    plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);
                }

                break;
        }
    }

    private unsafe void SelectPartyType(AddonLookingForGroupCondition* addon, byte groupCount, int recruitmentType)
    {
        var radioIndex = groupCount >= 3 ? 1 : Math.Clamp(recruitmentType, 0, 2);
        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
            agent->GroupTypeTab = (byte)radioIndex;

        var radio = addon->RecruitmentType[radioIndex].Value;
        if (radio == null || radio->IsSelected)
            return;

        radio->IsSelected = true;
        radio->SetActive();
    }

    private unsafe void WriteStoredRecruitment(AgentLookingForGroup* agent, Configuration cfg, byte groupCount)
    {
        ref var info = ref agent->StoredRecruitmentInfo;
        info.SelectedCategory = (AgentLookingForGroup.DutyCategory)cfg.DutyCategory;
        info.SelectedDutyId = cfg.DutyId;
        info.Objective = IndexToObjective(cfg.ObjectiveIndex);
        info.BeginnerFriendly = (byte)(cfg.BeginnerFriendly ? 1 : 0);
        info.CompletionStatus = AgentLookingForGroup.CompletionStatus.None;
        info.DutyFinderSettingFlags = AgentLookingForGroup.DutyFinderSetting.None;
        info.LootRule = AgentLookingForGroup.LootRule.Normal;
        info.Password = 10000;
        info.LanguageFlags = AgentLookingForGroup.Language.Japanese
                             | AgentLookingForGroup.Language.English
                             | AgentLookingForGroup.Language.German
                             | AgentLookingForGroup.Language.French;
        info.NumberOfSlotsInMainParty = 8;
        info.LimitRecruitingToWorld = 1;
        info.OnePlayerPerJob = 0;
        info.NumberOfGroups = groupCount;
        WriteComment(ref info, cfg.Comment);

        var itemLevel = (ushort)Math.Clamp(cfg.AverageItemLevel, 0, 999);
        agent->AvgItemLv = itemLevel;
        agent->AvgItemLvEnabled = (byte)(cfg.AverageItemLevelEnabled && itemLevel > 0 ? 1 : 0);
        agent->GroupTypeTab = groupCount >= 3 ? (byte)1 : (byte)Math.Clamp(cfg.RecruitmentType, 0, 2);
    }

    private unsafe void FillConditionDetails(AddonLookingForGroupCondition* addon, Configuration cfg, byte groupCount)
    {
        var itemLevel = Math.Clamp(cfg.AverageItemLevel, 0, 999);
        var ilEnabled = cfg.AverageItemLevelEnabled && itemLevel > 0;

        if (addon->CommentTextInput != null)
            addon->CommentTextInput->SetText(cfg.Comment ?? string.Empty);

        if (addon->AvgItemLevelNumericInput != null)
            addon->AvgItemLevelNumericInput->SetValue(itemLevel);

        NativeUi.SetChecked((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon, addon->AvgItemLevelCheckbox, ilEnabled);
        NativeUi.SetChecked((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon, addon->BeginnersWelcomeCheckBox, cfg.BeginnerFriendly);
    }

    private void AdvanceApplyPhase(ApplyPhase next)
    {
        applyPhase = next;
        applyPhaseStarted = DateTime.UtcNow;
    }

    private bool ApplyPhaseSettled()
        => DateTime.UtcNow - applyPhaseStarted >= TimeSpan.FromMilliseconds(250);

    private unsafe void ClickRecruit()
    {
        if (!TryGetConditionAddon(out var addon))
        {
            if (TimedOut())
                Fail("Recruit Members closed before the listing could be posted.");
            return;
        }

        var cfg = plugin.Configuration;
        var dutyReady = cfg.DutyId == 0 || plugin.Duties.IsNativeDutySelected(addon, cfg.DutyCategory, cfg.DutyId);
        if (!dutyReady)
            plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);

        var enabled = addon->RecruitMembersButton != null && addon->RecruitMembersButton->IsEnabled;
        if (!enabled)
        {
            if (TimedOut())
                Fail("Recruit Members stayed locked. The duty may not have been selected in Recruitment Criteria.");
            return;
        }

        NativeUi.Click((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon, addon->RecruitMembersButton);
        listingPosted = false;
        sawRecruitmentCommenced = false;
        SetState(RecruitStatus.Confirming);
    }

    private void WatchListing()
    {
        if (HasActiveListing)
        {
            if (OwnListingId != 0)
                lastSeenListingId = OwnListingId;
            if (AutoRelistActive && TimeUntilRelist <= TimeSpan.Zero)
            {
                plugin.Chat("Refreshing Party Finder before it expires.");
                BeginPost();
            }

            return;
        }

        if (!AutoRelistActive)
        {
            SetState(RecruitStatus.Idle);
            plugin.Chat("Party Finder listing ended.");
            return;
        }

        plugin.Chat("Party Finder ended — posting it again.");
        nextRelistAt = DateTime.UtcNow + EndedRelistDelay;
        SetState(RecruitStatus.RelistWait);
    }

    private static unsafe bool TryGetDetailAddon(out AddonLookingForGroupDetail* addon)
        => NativeUi.TryGetAddon("LookingForGroupDetail", out addon);

    private unsafe void WithdrawListing()
    {
        if (!HasActiveListing && clickedEndButton)
        {
            AfterWithdraw();
            return;
        }

        if (TimedOut())
        {
            Fail("Could not end the current listing. Open your listing and press End.");
            return;
        }

        if (!TryGetDetailAddon(out var addon))
        {
            if (NativeUi.IsPresent("LookingForGroupDetail"))
                return;

            TryOpenOwnListingDetail();
            return;
        }

        if (DateTime.UtcNow - lastRecruitClick < TimeSpan.FromMilliseconds(400))
            return;

        if (clickedEndButton)
            return;

        var endButton = FindDetailEndButton(addon);
        if (endButton == null)
            return;

        NativeUi.Click((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon, endButton);
        clickedEndButton = true;
        SetState(RecruitStatus.ConfirmingWithdraw);
    }

    private unsafe FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentButton* FindDetailEndButton(AddonLookingForGroupDetail* addon)
    {
        var unit = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon;
        var byId = unit->GetComponentButtonById(110);
        if (LooksLikeEnd(NativeUi.GetButtonText(byId)))
            return byId;
        if (LooksLikeEnd(NativeUi.GetButtonText(addon->SendTellButton)))
            return addon->SendTellButton;
        return NativeUi.FindButton(unit, LooksLikeEnd);
    }

    private unsafe void TryOpenOwnListingDetail()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            Fail("Party Finder is not available.");
            return;
        }

        RememberListingId();

        if (!NativeUi.TryGetAddon<AddonLookingForGroup>("LookingForGroup", out var pf))
        {
            if (!agent->IsAgentActive())
                agent->Show();
            return;
        }

        if (clickedConditionButton && DateTime.UtcNow - lastRecruitClick < TimeSpan.FromSeconds(1.2))
            return;

        var listingId = ResolveOwnListingId(agent);
        if (listingId != 0)
        {
            agent->OpenListing(listingId);
            clickedConditionButton = true;
            lastRecruitClick = DateTime.UtcNow;
            return;
        }

        if (!requestedListings)
        {
            agent->RequestCategoryListings(0);
            agent->RequestListingsUpdate();
            requestedListings = true;
            lastRecruitClick = DateTime.UtcNow;
            return;
        }

        if (TryClickOwnListingRow(pf, agent))
        {
            clickedConditionButton = true;
            lastRecruitClick = DateTime.UtcNow;
        }
    }

    private unsafe ulong ResolveOwnListingId(AgentLookingForGroup* agent)
    {
        RememberListingId();
        if (agent->OwnListingId != 0)
            return agent->OwnListingId;
        if (knownListingId != 0)
            return knownListingId;
        if (agent->LastViewedListing.ListingId != 0 && LeaderIsLocalPlayer(agent))
            return agent->LastViewedListing.ListingId;
        return 0;
    }

    private unsafe void RememberListingId()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        if (agent->OwnListingId != 0)
            knownListingId = agent->OwnListingId;
        else if (knownListingId == 0 && agent->LastViewedListing.ListingId != 0 && LeaderIsLocalPlayer(agent))
            knownListingId = agent->LastViewedListing.ListingId;
    }

    private unsafe bool LeaderIsLocalPlayer(AgentLookingForGroup* agent)
    {
        var player = Plugin.PlayerState;
        if (!player.IsLoaded)
            return false;

        var name = player.CharacterName;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var leader = agent->LastLeader.ToString();
        return !string.IsNullOrWhiteSpace(leader) && leader.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    private unsafe bool TryClickOwnListingRow(AddonLookingForGroup* pf, AgentLookingForGroup* agent)
    {
        var list = pf->StandardViewList != null && pf->StandardViewList->GetItemCount() > 0
            ? pf->StandardViewList
            : pf->CompactViewList;
        if (list == null)
            return false;

        if (knownListingId != 0)
        {
            var ids = agent->Listings.ListingIds;
            var count = Math.Min(ids.Length, list->GetItemCount());
            for (var i = 0; i < count; i++)
            {
                if (ids[i] != knownListingId)
                    continue;
                return NativeUi.SelectListItem(list, i);
            }
        }

        var player = Plugin.PlayerState.CharacterName;
        if (string.IsNullOrWhiteSpace(player))
            return false;

        var index = NativeUi.FindListItem(list, label => label.Contains(player, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && NativeUi.SelectListItem(list, index);
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        var player = Plugin.PlayerState;
        if (!player.IsLoaded || listing.Id == 0)
            return;

        if (listing.ContentId != 0 && listing.ContentId == player.ContentId)
        {
            knownListingId = listing.Id;
            return;
        }

        var host = listing.Name.TextValue;
        var playerName = player.CharacterName;
        if (!string.IsNullOrWhiteSpace(host)
            && !string.IsNullOrWhiteSpace(playerName)
            && (host.Equals(playerName, StringComparison.OrdinalIgnoreCase)
                || host.StartsWith(playerName, StringComparison.OrdinalIgnoreCase)))
        {
            knownListingId = listing.Id;
        }
    }

    private unsafe void TryOpenRecruitmentCriteria()
    {
        if (NativeUi.IsPresent("LookingForGroupCondition"))
            return;

        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
        {
            Fail("Party Finder is not available.");
            return;
        }

        if (!NativeUi.TryGetAddon<AddonLookingForGroup>("LookingForGroup", out var pf))
        {
            if (!agent->IsAgentActive())
                agent->Show();
            return;
        }

        var recruit = NativeUi.FindPartyFinderRecruitButton((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)pf);
        if (recruit == null)
            recruit = pf->RecruitMembersButton;
        if (recruit == null)
        {
            if (!agent->IsAgentActive())
                agent->Show();
            return;
        }

        if (clickedConditionButton)
        {
            var wait = endingListing ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1.5);
            if (DateTime.UtcNow - lastRecruitClick < wait)
                return;
        }

        if (!NativeUi.Click((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)pf, recruit))
            return;

        clickedConditionButton = true;
        lastRecruitClick = DateTime.UtcNow;
    }

    private static bool LooksLikeEnd(string text)
    {
        var label = NormalizeLabel(text);
        return label.Equals("End", StringComparison.OrdinalIgnoreCase)
               || label.Equals("End Recruitment", StringComparison.OrdinalIgnoreCase)
               || label.Equals("End Listing", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Beenden", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Terminer", StringComparison.OrdinalIgnoreCase)
               || label.Equals("終了", StringComparison.Ordinal);
    }

    private static bool LooksLikeRecruit(string text)
    {
        var label = NormalizeLabel(text);
        return label.Equals("Recruit Members", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Recruit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCancel(string text)
    {
        var label = NormalizeLabel(text);
        return label.Equals("Cancel", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Abbrechen", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Annuler", StringComparison.OrdinalIgnoreCase)
               || label.Equals("キャンセル", StringComparison.Ordinal);
    }

    private static bool LooksLikeReset(string text)
    {
        var label = NormalizeLabel(text);
        return label.Equals("Reset", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Zurücksetzen", StringComparison.OrdinalIgnoreCase)
               || label.Equals("Réinitialiser", StringComparison.OrdinalIgnoreCase)
               || label.Equals("リセット", StringComparison.Ordinal);
    }

    private static string NormalizeLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c))
                continue;
            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private void AfterWithdraw()
    {
        listingPosted = false;
        knownListingId = 0;
        lastSeenListingId = 0;
        if (userRequestedStop)
        {
            SetState(RecruitStatus.Idle);
            plugin.Chat("Party Finder listing ended.");
            return;
        }

        nextRelistAt = DateTime.UtcNow + EndedRelistDelay;
        SetState(RecruitStatus.RelistWait);
    }

    private void OnListed()
    {
        listingPosted = true;
        RememberListingId();
        listedAt = DateTime.UtcNow;
        lastSeenListingId = OwnListingId;
        SetState(RecruitStatus.Listed);
        plugin.Chat(AutoRelistActive
            ? $"Party Finder is up. Auto put up PF will post it again after {plugin.Configuration.RelistAfterMinutes} minutes, or if it ends."
            : "Party Finder is up.");
    }

    private bool RecruitmentWindowClosed()
        => DateTime.UtcNow - stepStarted >= TimeSpan.FromMilliseconds(300)
           && !NativeUi.IsPresent("LookingForGroupCondition");

    private bool DetailWindowClosed()
        => DateTime.UtcNow - stepStarted >= TimeSpan.FromMilliseconds(300)
           && !NativeUi.IsPresent("LookingForGroupDetail");

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (text.Contains("recruitment commenced", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rekrutierung begonnen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("recrutement a commencé", StringComparison.OrdinalIgnoreCase)
            || text.Contains("募集を開始", StringComparison.Ordinal))
        {
            sawRecruitmentCommenced = true;
            return;
        }

        if (text.Contains("ended party recruitment", StringComparison.OrdinalIgnoreCase)
            || text.Contains("recruitment has ended", StringComparison.OrdinalIgnoreCase)
            || text.Contains("recruitment ended", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rekrutierung beendet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("recrutement a pris fin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("募集を終了", StringComparison.Ordinal)
            || text.Contains("募集を中止", StringComparison.Ordinal))
        {
            sawRecruitmentEnded = true;
        }
    }

    private unsafe void ConfirmYesNoIfNeeded()
    {
        if (status is not (RecruitStatus.Confirming or RecruitStatus.ClickingRecruit))
            return;

        var yesno = Plugin.GameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (yesno == null || !yesno->IsReady || !yesno->IsVisible || yesno->YesButton == null)
            return;

        NativeUi.Click((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)yesno, yesno->YesButton);
    }

    private void SetState(RecruitStatus next)
    {
        status = next;
        stepStarted = DateTime.UtcNow;
        if (next == RecruitStatus.OpeningCondition || next == RecruitStatus.Withdrawing)
        {
            clickedConditionButton = false;
            clickedEndButton = false;
            requestedListings = false;
        }
        if (next == RecruitStatus.Applying)
        {
            applyPhase = ApplyPhase.PartyType;
            applyPhaseStarted = DateTime.UtcNow;
            populatedCriteria = false;
        }
    }

    private bool TimedOut()
    {
        var limit = status switch
        {
            RecruitStatus.Confirming or RecruitStatus.WaitingForListing => ConfirmTimeout,
            RecruitStatus.Withdrawing or RecruitStatus.ConfirmingWithdraw => EndTimeout,
            _ => StepTimeout,
        };
        return DateTime.UtcNow - stepStarted > limit;
    }

    private void Fail(string message)
    {
        lastError = message;
        autoRelist = false;
        SetState(RecruitStatus.Failed);
        plugin.ChatError(message);
    }

    private static AgentLookingForGroup.Objective IndexToObjective(int index) => index switch
    {
        1 => AgentLookingForGroup.Objective.DutyCompletion,
        2 => AgentLookingForGroup.Objective.Practice,
        3 => AgentLookingForGroup.Objective.Loot,
        _ => AgentLookingForGroup.Objective.None,
    };

    private static int ObjectiveToIndex(AgentLookingForGroup.Objective objective)
    {
        if (objective.HasFlag(AgentLookingForGroup.Objective.DutyCompletion))
            return 1;
        if (objective.HasFlag(AgentLookingForGroup.Objective.Practice))
            return 2;
        if (objective.HasFlag(AgentLookingForGroup.Objective.Loot))
            return 3;
        return 0;
    }

    private static unsafe void WriteComment(ref AgentLookingForGroup.RecruitmentSub info, string? comment)
    {
        Span<byte> dest = info.Comment;
        dest.Clear();
        if (string.IsNullOrEmpty(comment) || dest.Length == 0)
            return;

        var written = Encoding.UTF8.GetBytes(comment, dest[..^1]);
        dest[Math.Min(written, dest.Length - 1)] = 0;
    }

    private static unsafe string ReadComment(ref AgentLookingForGroup.RecruitmentSub info)
    {
        Span<byte> src = info.Comment;
        var length = src.IndexOf((byte)0);
        if (length < 0)
            length = src.Length;
        return Encoding.UTF8.GetString(src[..length]);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;
        return remaining.TotalHours >= 1
            ? remaining.ToString(@"h\:mm\:ss")
            : remaining.ToString(@"mm\:ss");
    }
}
