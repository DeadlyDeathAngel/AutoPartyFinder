using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoPartyFinder;

internal static unsafe class NativeUi
{
    public static bool Click(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || button->OwnerNode == null || !button->IsEnabled)
            return false;

        var registered = FindRegisteredEvent(button->OwnerNode, AtkEventType.ButtonClick);
        if (registered == null)
            registered = button->OwnerNode->AtkEventManager.Event;
        var param = registered != null ? registered->Param : 0u;

        var evt = new AtkEvent
        {
            Listener = (AtkEventListener*)addon,
            Target = (AtkEventTarget*)button->OwnerNode,
            Param = param,
            State = new AtkEventState
            {
                EventType = AtkEventType.ButtonClick,
            },
        };

        addon->ReceiveEvent(AtkEventType.ButtonClick, (int)param, &evt);
        return true;
    }

    public static bool Click(AtkUnitBase* addon, AtkComponentCheckBox* checkbox)
    {
        return checkbox != null && Click(addon, (AtkComponentButton*)checkbox);
    }

    public static bool Click(AtkUnitBase* addon, AtkComponentRadioButton* radio)
    {
        return radio != null && Click(addon, (AtkComponentButton*)radio);
    }

    public static void SetChecked(AtkUnitBase* addon, AtkComponentCheckBox* checkbox, bool value)
    {
        if (checkbox == null)
            return;

        if (checkbox->IsChecked != value)
            checkbox->IsChecked = value;
    }

    public static string GetButtonText(AtkComponentButton* button)
    {
        if (button == null)
            return string.Empty;

        if (button->ButtonTextNode != null)
        {
            var labeled = button->ButtonTextNode->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(labeled))
                return labeled.Trim();
        }

        var nodes = ((AtkComponentBase*)button)->UldManager.NodeList;
        var count = ((AtkComponentBase*)button)->UldManager.NodeListCount;
        if (nodes == null || count <= 0 || count > 32)
            return string.Empty;

        for (var i = 0; i < count; i++)
        {
            var node = nodes[i];
            if (node == null || node->Type != NodeType.Text)
                continue;

            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return string.Empty;
    }

    public static AtkComponentButton* FindButton(AtkUnitBase* addon, Func<string, bool> match)
    {
        if (addon == null || match == null)
            return null;

        for (var id = 1u; id <= 200; id++)
        {
            var button = addon->GetComponentButtonById(id);
            if (!IsPlainButton(button))
                continue;
            if (match(GetButtonText(button)))
                return button;
        }

        return null;
    }

    public static AtkComponentButton* FindButtonBetween(AtkUnitBase* addon, AtkComponentButton* left, AtkComponentButton* right, AtkComponentButton* skip)
    {
        if (addon == null || left == null || right == null || left->OwnerNode == null || right->OwnerNode == null)
            return null;

        var leftX = left->OwnerNode->ScreenX;
        var rightX = right->OwnerNode->ScreenX;
        var rowY = left->OwnerNode->ScreenY;
        var minX = Math.Min(leftX, rightX);
        var maxX = Math.Max(leftX, rightX);
        if (maxX - minX < 16)
            return null;

        AtkComponentButton* found = null;
        var best = float.MaxValue;
        for (var id = 1u; id <= 120; id++)
        {
            var button = addon->GetComponentButtonById(id);
            if (!IsPlainButton(button) || button == left || button == right || button == skip)
                continue;

            var node = (AtkResNode*)button->OwnerNode;
            var x = node->ScreenX;
            var y = node->ScreenY;
            if (x <= minX + 8 || x >= maxX - 8 || Math.Abs(y - rowY) > 20)
                continue;

            var distance = Math.Abs(x - ((minX + maxX) / 2f));
            if (distance < best)
            {
                best = distance;
                found = button;
            }
        }

        return found;
    }

    public static bool IsPlainButton(AtkComponentButton* button)
    {
        if (button == null || button->OwnerNode == null)
            return false;

        var type = ((AtkComponentBase*)button)->GetComponentType();
        return type is ComponentType.Button or ComponentType.HoldButton;
    }

    public static bool IsVisible(AtkComponentButton* button)
        => button != null && button->OwnerNode != null && button->OwnerNode->IsVisible();

    private static AtkEvent* FindRegisteredEvent(AtkComponentNode* node, AtkEventType type)
    {
        var evt = node->AtkEventManager.Event;
        for (var i = 0; evt != null && i < 16; i++, evt = evt->NextEvent)
        {
            if (evt->State.EventType == type)
                return evt;
        }

        return node->AtkEventManager.Event;
    }

    public static bool SelectDropDown(AtkComponentDropDownList* dropDown, int index, bool dispatchEvent = false)
        => SelectDropDown(null, dropDown, index, dispatchEvent, 0);

    public static bool SelectDropDown(AtkUnitBase* addon, AtkComponentDropDownList* dropDown, int index, bool dispatchEvent, int eventParamHint)
    {
        if (dropDown == null || dropDown->List == null || index < 0)
            return false;

        var count = dropDown->List->GetItemCount();
        if (index >= count)
            return false;

        if (dropDown->GetSelectedItemIndex() == index && !dispatchEvent)
            return true;

        dropDown->SelectItem(index);
        dropDown->List->SelectItem(index, true);
        dropDown->List->DispatchItemEvent(index, AtkEventType.ListItemClick);
        dropDown->List->DispatchItemEvent(index, AtkEventType.ListItemSelect);
        dropDown->List->DispatchItemEvent(index, AtkEventType.ListItemHighlight);

        if (dispatchEvent && addon != null)
            CommitDropDownSelection(addon, dropDown, index, eventParamHint);

        return dropDown->GetSelectedItemIndex() == index;
    }

    public static void CommitDropDownSelection(AtkUnitBase* addon, AtkComponentDropDownList* dropDown, int index, int eventParamHint)
    {
        if (addon == null || dropDown == null)
            return;

        var node = ((AtkComponentBase*)dropDown)->OwnerNode;
        var param = eventParamHint > 0 ? (uint)eventParamHint : GetDropdownEventParam(dropDown, 0);

        var data = new AtkEventData();
        data.ListItemData.SelectedIndex = index;
        data.ListItemData.HoveredItemIndex3 = (short)index;

        DispatchListEvent((AtkEventListener*)dropDown, node, (uint)index, index, AtkEventType.ListItemClick, &data);
        DispatchListEvent((AtkEventListener*)dropDown, node, (uint)index, index, AtkEventType.ListItemSelect, &data);
        DispatchListEvent((AtkEventListener*)addon, node, param, (int)param, AtkEventType.ListItemClick, &data);
        DispatchListEvent((AtkEventListener*)addon, node, param, (int)param, AtkEventType.ListItemSelect, &data);
        DispatchListEvent((AtkEventListener*)addon, node, param, (int)param, AtkEventType.ListItemHighlight, &data);
    }

    private static void DispatchListEvent(AtkEventListener* listener, AtkComponentNode* node, uint param, int eventParam, AtkEventType type, AtkEventData* data)
    {
        if (listener == null)
            return;

        var evt = new AtkEvent
        {
            Listener = listener,
            Target = node != null ? (AtkEventTarget*)node : null,
            Param = param,
            State = new AtkEventState
            {
                EventType = type,
            },
        };

        listener->ReceiveEvent(type, eventParam, &evt, data);
    }

    private static uint GetDropdownEventParam(AtkComponentDropDownList* dropDown, uint fallback)
    {
        var node = ((AtkComponentBase*)dropDown)->OwnerNode;
        if (node == null)
            return fallback;

        var highlight = FindRegisteredEvent(node, AtkEventType.ListItemHighlight);
        if (highlight != null)
            return highlight->Param;
        var select = FindRegisteredEvent(node, AtkEventType.ListItemSelect);
        if (select != null)
            return select->Param;
        var click = FindRegisteredEvent(node, AtkEventType.ListItemClick);
        if (click != null)
            return click->Param;
        if (node->AtkEventManager.Event != null)
            return node->AtkEventManager.Event->Param;
        return fallback;
    }

    public static int FindLabelIndex(AtkComponentDropDownList* dropDown, string name)
    {
        var labels = ReadDropDownLabels(dropDown);
        if (labels == null || string.IsNullOrWhiteSpace(name))
            return -1;

        for (var i = 0; i < labels.Count; i++)
        {
            if (string.Equals(labels[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        var needle = StripLeadingThe(name);
        for (var i = 0; i < labels.Count; i++)
        {
            if (string.Equals(StripLeadingThe(labels[i]), needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < labels.Count; i++)
        {
            if (LabelsMatch(labels[i], name))
                return i;
        }

        return -1;
    }

    public static bool LabelsMatch(string left, string right)
        => NormalizeLabel(left) == NormalizeLabel(right);

    public static bool IsNoneOrAll(string label)
    {
        var text = NormalizeLabel(label);
        return text is "none" or "all" or "なし" or "すべて" or "alle" or "tout";
    }

    public static string GetSelectedLabel(AtkComponentDropDownList* dropDown)
    {
        if (dropDown == null || dropDown->List == null)
            return string.Empty;

        var index = dropDown->GetSelectedItemIndex();
        if (index < 0)
            return string.Empty;

        var label = dropDown->List->GetItemLabel(index).ToString();
        return string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
    }

    private static string NormalizeLabel(string value)
    {
        var text = StripLeadingThe(value).Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(text.Length);
        var spaced = false;
        foreach (var c in text)
        {
            if (c is '-' or '–' or '—' or ':')
            {
                if (!spaced)
                {
                    builder.Append(' ');
                    spaced = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!spaced)
                {
                    builder.Append(' ');
                    spaced = true;
                }

                continue;
            }

            builder.Append(c);
            spaced = false;
        }

        return builder.ToString().Trim();
    }

    public static int GetSelectedIndex(AtkComponentDropDownList* dropDown)
        => dropDown == null ? -1 : dropDown->GetSelectedItemIndex();

    private static string StripLeadingThe(string value)
    {
        var text = value.Trim();
        return text.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;
    }

    public static bool SelectListItem(AtkComponentList* list, int index)
    {
        if (list == null || index < 0)
            return false;

        var count = list->GetItemCount();
        if (index >= count)
            return false;

        list->SelectItem(index, true);
        return true;
    }

    public static int FindListItem(AtkComponentList* list, Func<string, bool> match)
    {
        if (list == null || match == null)
            return -1;

        var count = list->GetItemCount();
        for (var i = 0; i < count; i++)
        {
            var label = list->GetItemLabel(i).ToString();
            if (!string.IsNullOrWhiteSpace(label) && match(label))
                return i;
        }

        return -1;
    }

    public static AtkComponentButton* FindPartyFinderRecruitButton(AtkUnitBase* addon)
    {
        if (addon == null)
            return null;

        var labeled = FindButton(addon, text =>
        {
            var label = text.Trim();
            return label.Equals("Recruit Members", StringComparison.OrdinalIgnoreCase)
                   || label.Equals("Recruitment Criteria", StringComparison.OrdinalIgnoreCase);
        });
        if (IsPlainButton(labeled) && IsVisible(labeled) && labeled->IsEnabled)
            return labeled;

        var byId = addon->GetComponentButtonById(46);
        if (IsPlainButton(byId) && IsVisible(byId) && byId->IsEnabled)
            return byId;

        return null;
    }

    public static bool TryGetAddon<T>(string name, out T* addon) where T : unmanaged
    {
        addon = null;
        var ptr = Plugin.GameGui.GetAddonByName(name);
        if (ptr.IsNull)
            return false;

        addon = (T*)ptr.Address;
        var unit = (AtkUnitBase*)ptr.Address;
        if (!unit->IsVisible)
            return false;

        return ptr.IsReady || unit->UldManager.LoadedState >= AtkLoadState.Loaded;
    }

    public static bool IsPresent(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name);
        if (ptr.IsNull)
            return false;

        return ((AtkUnitBase*)ptr.Address)->IsVisible;
    }

    public static bool CloseIfOpen(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name);
        if (ptr.IsNull)
            return false;

        var unit = (AtkUnitBase*)ptr.Address;
        if (!unit->IsReady || !unit->IsVisible)
            return false;

        unit->Close(true);
        return true;
    }

    public static List<string>? ReadDropDownLabels(AtkComponentDropDownList* dropDown)
    {
        if (dropDown == null || dropDown->List == null)
            return null;

        var count = dropDown->List->GetItemCount();
        if (count <= 0)
            return null;

        var labels = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var label = dropDown->List->GetItemLabel(i).ToString();
            labels.Add(string.IsNullOrWhiteSpace(label) ? $"#{i}" : label.Trim());
        }

        return labels;
    }
}
