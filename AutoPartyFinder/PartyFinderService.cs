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
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(25);
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
    private volatile bool closePfOnQueueRequested;
    private uint lastSeenListingId;
    private DateTime lastPfShow;

    public PartyFinderService(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnUpdate;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.PartyFinderGui.ReceiveListing += OnReceiveListing;
        Plugin.Condition.ConditionChange += OnConditionChange;
        Plugin.ClientState.Login += OnLogin;
        Plugin.ClientState.Logout += OnLogout;
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
        RecruitStatus.RelistWait => DutyQueueBlocksPartyFinder
            ? "Waiting to leave duty queue before re-listing…"
            : $"Re-listing in {FormatRemaining(nextRelistAt - DateTime.UtcNow)}",
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
        Plugin.ClientState.Login -= OnLogin;
        Plugin.ClientState.Logout -= OnLogout;
        Plugin.Condition.ConditionChange -= OnConditionChange;
        Plugin.PartyFinderGui.ReceiveListing -= OnReceiveListing;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (!value || !plugin.Configuration.ClosePfOnQueue)
            return;

        if (flag is ConditionFlag.InDutyQueue
            or ConditionFlag.WaitingForDutyFinder
            or ConditionFlag.WaitingForDuty)
        {
            closePfOnQueueRequested = true;
        }
    }

    private void HandleDutyQueue()
    {
        if (!plugin.Configuration.ClosePfOnQueue)
            return;

        if (ClosePartyFinderWindows())
            plugin.Chat("Closed Party Finder because you queued for a duty.");

        if (status is RecruitStatus.OpeningPartyFinder
            or RecruitStatus.OpeningCondition
            or RecruitStatus.Applying
            or RecruitStatus.ClickingRecruit
            or RecruitStatus.Confirming
            or RecruitStatus.WaitingForListing
            or RecruitStatus.Withdrawing
            or RecruitStatus.ConfirmingWithdraw)
        {
            if (HasActiveListing)
            {
                listedAt = listedAt == default ? DateTime.UtcNow : listedAt;
                SetState(RecruitStatus.Listed);
            }
            else if (AutoRelistActive)
            {
                nextRelistAt = DateTime.UtcNow + EndedRelistDelay;
                SetState(RecruitStatus.RelistWait);
            }
            else
            {
                SetState(RecruitStatus.Idle);
            }
        }
    }

    private static bool ClosePartyFinderWindows()
    {
        var closed = NativeUi.CloseIfOpen("LookingForGroupCondition");
        closed |= NativeUi.CloseIfOpen("LookingForGroupDetail");
        closed |= NativeUi.CloseIfOpen("LookingForGroup");
        return closed;
    }

    private bool DutyQueueBlocksPartyFinder
        => plugin.Configuration.ClosePfOnQueue && IsQueuedOrInDuty();

    private static bool IsQueuedOrInDuty()
        => Plugin.Condition[ConditionFlag.InDutyQueue]
           || Plugin.Condition[ConditionFlag.WaitingForDutyFinder]
           || Plugin.Condition[ConditionFlag.WaitingForDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty]
           || Plugin.Condition[ConditionFlag.BoundByDuty56]
           || Plugin.Condition[ConditionFlag.BoundByDuty95];

    public void StartRecruit(bool enableAutoRelist)
    {
        plugin.Configuration.Save();
        userRequestedStop = false;
        autoRelist = enableAutoRelist && plugin.Configuration.ShouldAutoRelist;
        endingListing = false;
        knownListingId = 0;
        listingPosted = false;
        clickedConditionButton = false;
        clickedEndButton = false;
        requestedListings = false;
        lastError = null;
        sawRecruitmentEnded = false;
        sawRecruitmentCommenced = false;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            Fail("You need to be logged in to post a Party Finder listing.");
            return;
        }

        if (plugin.Configuration.ClosePfOnQueue && IsQueuedOrInDuty())
        {
            Fail("Can't post Party Finder while queued or in a duty.");
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

        if (closePfOnQueueRequested)
        {
            closePfOnQueueRequested = false;
            HandleDutyQueue();
        }

        if (DutyQueueBlocksPartyFinder && status is RecruitStatus.RelistWait)
            return;

        if (status is RecruitStatus.Idle or RecruitStatus.Failed)
            return;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            HandleLoggedOut();
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
                if (clickedEndButton && (RecruitmentWindowClosed() || DetailWindowClosed() || !HasActiveListing))
                    AfterWithdraw();
                else if (TimedOut())
                    Fail("Could not end the current listing. Open Party Finder → Recruitment Criteria → End.");
                break;
            case RecruitStatus.RelistWait:
                if (DateTime.UtcNow >= nextRelistAt)
                    BeginPost();
                break;
        }
    }

    private unsafe void BeginPost()
    {
        if (DutyQueueBlocksPartyFinder)
        {
            nextRelistAt = DateTime.UtcNow + EndedRelistDelay;
            SetState(RecruitStatus.RelistWait);
            return;
        }

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
            SetState(endingListing ? RecruitStatus.Withdrawing : RecruitStatus.Applying);
            return;
        }

        if (WorldNotReady())
        {
            if (TimedOut())
                Fail("Wait until you finish logging in, then try again.");
            return;
        }

        if (EnsurePartyFinderVisible())
        {
            SetState(endingListing ? RecruitStatus.Withdrawing : RecruitStatus.OpeningCondition);
            return;
        }

        if (TimedOut())
            Fail("Could not open Party Finder.");
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

        if (WorldNotReady())
            return;

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
            WriteStoredRecruitment(agent, cfg, groupCount, includeDutySelection: false);
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
                if (NativeUi.GetSelectedIndex(addon->DutyCategoryDropDown) != plugin.Duties.IndexOfCategory(cfg.DutyCategory))
                    plugin.Duties.SelectNativeCategory(addon, cfg.DutyCategory, dispatchEvent: true);
                if (NativeUi.GetSelectedIndex(addon->DutyCategoryDropDown) == plugin.Duties.IndexOfCategory(cfg.DutyCategory)
                    && plugin.Duties.NativeDutyListContains(addon, cfg.DutyCategory, cfg.DutyId)
                    && ApplyPhaseSettled())
                {
                    AdvanceApplyPhase(ApplyPhase.Duty);
                }

                break;
            case ApplyPhase.Duty:
                if (!plugin.Duties.IsNativeDutySelected(addon, cfg.DutyCategory, cfg.DutyId))
                    plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);

                if (plugin.Duties.IsNativeDutySelected(addon, cfg.DutyCategory, cfg.DutyId)
                    && ApplyPhaseSettled())
                {
                    WriteStoredRecruitment(agent, cfg, groupCount, includeDutySelection: true);
                    if (NativeDutyCommitted(agent, cfg.DutyId))
                    {
                        agent->PopulateRecruitmentCriteriaPopup(false, false);
                        SelectPartyType(addon, groupCount, cfg.RecruitmentType);
                        FillConditionDetails(addon, cfg, groupCount);
                    }

                    AdvanceApplyPhase(ApplyPhase.Details);
                }

                break;
            case ApplyPhase.Details:
                if (!plugin.Duties.IsNativeDutySelected(addon, cfg.DutyCategory, cfg.DutyId))
                {
                    plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);
                    break;
                }

                WriteStoredRecruitment(agent, cfg, groupCount, includeDutySelection: true);
                if (addon->RecruitMembersButton != null && addon->RecruitMembersButton->IsEnabled
                    && NativeDutyCommitted(agent, cfg.DutyId)
                    && ApplyPhaseSettled())
                {
                    SetState(RecruitStatus.ClickingRecruit);
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

    private unsafe void WriteStoredRecruitment(AgentLookingForGroup* agent, Configuration cfg, byte groupCount, bool includeDutySelection = true)
    {
        ref var info = ref agent->StoredRecruitmentInfo;
        if (includeDutySelection)
            WriteSelectedDuty(agent, cfg.DutyCategory, cfg.DutyId);
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

    private static unsafe void WriteSelectedDuty(AgentLookingForGroup* agent, uint category, ushort dutyId)
    {
        ref var info = ref agent->StoredRecruitmentInfo;
        info.SelectedCategory = (AgentLookingForGroup.DutyCategory)category;
        info.SelectedDutyId = dutyId;
        SetSelectedDutyType(agent, DutyTypeFor(category, dutyId));

        if (dutyId == 0)
            return;

        if (category == (uint)AgentLookingForGroup.DutyCategory.Roulette)
            return;

        if (!agent->ContentUI.LoadByContentFinderConditionId(dutyId))
            return;

        agent->PartyContent = agent->ContentUI.PartyContent;
        var lookupType = (ushort)agent->ContentUI.LookupInfo.ContentType;
        if (lookupType != 0)
            SetSelectedDutyType(agent, lookupType);
    }

    private static ushort DutyTypeFor(uint category, ushort dutyId)
    {
        if (dutyId == 0)
            return 0;
        if (category == (uint)AgentLookingForGroup.DutyCategory.Roulette)
            return 1;
        return 2;
    }

    private static unsafe ushort GetSelectedDutyType(AgentLookingForGroup* agent)
        => *(ushort*)((byte*)&agent->StoredRecruitmentInfo + 0x12);

    private static unsafe void SetSelectedDutyType(AgentLookingForGroup* agent, ushort dutyType)
        => *(ushort*)((byte*)&agent->StoredRecruitmentInfo + 0x12) = dutyType;

    private static unsafe bool NativeDutyCommitted(AgentLookingForGroup* agent, ushort dutyId)
        => dutyId == 0
           || (agent != null
               && agent->StoredRecruitmentInfo.SelectedDutyId != 0
               && GetSelectedDutyType(agent) != 0);

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
        => DateTime.UtcNow - applyPhaseStarted >= TimeSpan.FromMilliseconds(400);

    private unsafe void ClickRecruit()
    {
        if (!TryGetConditionAddon(out var addon))
        {
            if (TimedOut())
                Fail("Recruit Members closed before the listing could be posted.");
            return;
        }

        var cfg = plugin.Configuration;
        if (!plugin.Duties.IsNativeDutySelected(addon, cfg.DutyCategory, cfg.DutyId))
        {
            plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);
            if (TimedOut())
                Fail("Recruit Members stayed locked. The duty may not have been selected in Recruitment Criteria.");
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
        {
            var groupCount = plugin.Duties.GetGroupCount(cfg.DutyCategory, cfg.DutyId);
            if (groupCount is not (1 or 3 or 6))
                groupCount = cfg.RecruitmentType == 1 ? (byte)3 : (byte)1;
            WriteStoredRecruitment(agent, cfg, groupCount, includeDutySelection: true);
            if (!NativeDutyCommitted(agent, cfg.DutyId))
            {
                plugin.Duties.SelectNativeDuty(addon, cfg.DutyCategory, cfg.DutyId, dispatchEvent: true);
                if (TimedOut())
                    Fail("The duty did not stick in Recruitment Criteria. Select The Occult Crescent (or your duty) once, then try again.");
                return;
            }

            Plugin.Log.Information($"APF posting duty id={agent->StoredRecruitmentInfo.SelectedDutyId} type={GetSelectedDutyType(agent)} category={agent->StoredRecruitmentInfo.SelectedCategory}");
        }

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
                if (DutyQueueBlocksPartyFinder)
                    return;

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

        if (DutyQueueBlocksPartyFinder)
        {
            nextRelistAt = DateTime.UtcNow + EndedRelistDelay;
            SetState(RecruitStatus.RelistWait);
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
        if (clickedEndButton && !HasActiveListing)
        {
            AfterWithdraw();
            return;
        }

        if (WorldNotReady())
        {
            if (TimedOut())
                Fail("Wait until you finish logging in, then end the listing.");
            return;
        }

        if (TimedOut())
        {
            Fail("Could not end the current listing. Open Party Finder → Recruitment Criteria → End.");
            return;
        }

        if (DateTime.UtcNow - lastRecruitClick < TimeSpan.FromMilliseconds(400))
            return;

        if (clickedEndButton)
            return;

        if (TryGetConditionAddon(out var condition))
        {
            var endButton = FindConditionEndButton(condition);
            if (endButton != null)
            {
                if (!NativeUi.Click((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)condition, endButton))
                    return;

                clickedEndButton = true;
                SetState(RecruitStatus.ConfirmingWithdraw);
                return;
            }

            if (LooksLikeRecruit(NativeUi.GetButtonText(condition->RecruitMembersButton)))
            {
                NativeUi.CloseIfOpen("LookingForGroupCondition");
                clickedConditionButton = false;
                lastRecruitClick = DateTime.UtcNow;
            }

            return;
        }

        if (TryGetDetailAddon(out var detail))
        {
            var detailEnd = FindDetailEndButton(detail);
            if (detailEnd != null)
            {
                if (!NativeUi.Click((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)detail, detailEnd))
                    return;

                clickedEndButton = true;
                SetState(RecruitStatus.ConfirmingWithdraw);
                return;
            }
        }

        if (!EnsurePartyFinderVisible())
            return;

        if (endingListing && !requestedListings)
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent != null)
            {
                agent->RequestListingsUpdate();
                requestedListings = true;
            }
        }

        TryOpenRecruitmentCriteria();
    }

    private unsafe FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentButton* FindConditionEndButton(AddonLookingForGroupCondition* addon)
    {
        if (addon == null)
            return null;

        if (LooksLikeEnd(NativeUi.GetButtonText(addon->RecruitMembersButton)))
            return addon->RecruitMembersButton;

        return NativeUi.FindButton((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon, LooksLikeEnd);
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

        if (!EnsurePartyFinderVisible())
            return;

        if (!NativeUi.TryGetAddon<AddonLookingForGroup>("LookingForGroup", out var pf))
            return;

        var recruit = NativeUi.FindPartyFinderRecruitButton((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)pf);
        if (recruit == null)
            recruit = pf->RecruitMembersButton;
        if (recruit == null || !recruit->IsEnabled)
            return;

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
        if (status is not (RecruitStatus.Confirming or RecruitStatus.ClickingRecruit or RecruitStatus.ConfirmingWithdraw or RecruitStatus.Withdrawing))
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
            RecruitStatus.OpeningPartyFinder or RecruitStatus.OpeningCondition => OpenTimeout,
            RecruitStatus.Applying => TimeSpan.FromSeconds(20),
            RecruitStatus.Confirming or RecruitStatus.WaitingForListing => ConfirmTimeout,
            RecruitStatus.Withdrawing or RecruitStatus.ConfirmingWithdraw => EndTimeout,
            _ => StepTimeout,
        };
        return DateTime.UtcNow - stepStarted > limit;
    }

    private void OnLogin()
    {
        listingPosted = false;
        knownListingId = 0;
        lastSeenListingId = 0;
        clickedConditionButton = false;
        clickedEndButton = false;
        requestedListings = false;
    }

    private void OnLogout(int type, int code) => HandleLoggedOut();

    private void HandleLoggedOut()
    {
        listingPosted = false;
        knownListingId = 0;
        lastSeenListingId = 0;
        clickedConditionButton = false;
        clickedEndButton = false;
        requestedListings = false;
        if (status is RecruitStatus.Idle or RecruitStatus.Failed or RecruitStatus.RelistWait)
            return;

        if (AutoRelistActive)
        {
            nextRelistAt = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            SetState(RecruitStatus.RelistWait);
            return;
        }

        SetState(RecruitStatus.Idle);
    }

    private static bool WorldNotReady()
        => !Plugin.ClientState.IsLoggedIn
           || !Plugin.PlayerState.IsLoaded
           || Plugin.Condition[ConditionFlag.BetweenAreas]
           || Plugin.Condition[ConditionFlag.BetweenAreas51];

    private unsafe bool EnsurePartyFinderVisible()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return false;

        if (NativeUi.TryGetAddon<AddonLookingForGroup>("LookingForGroup", out _))
            return true;

        if (!agent->IsActivatable())
            return false;

        if (DateTime.UtcNow - lastPfShow < TimeSpan.FromMilliseconds(750))
            return false;

        lastPfShow = DateTime.UtcNow;
        if (!agent->IsAgentActive())
            agent->Show();
        else if (!agent->IsAddonShown())
            agent->ShowAddon();
        else
            agent->Show();

        return NativeUi.TryGetAddon<AddonLookingForGroup>("LookingForGroup", out _);
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
