using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class AlarmPanel : MonoBehaviour
{
    [Serializable]
    private class RowView
    {
        public RectTransform root;
        public Text idText;
        public Text alarmText;
        public Text timeText;
        public Text stateText;
    }

    private class AlarmEntry
    {
        public string key;
        public string flightTime;
        public string trackId;
        public string alarm;
        public string state;
    }

    [Header("Data")]
    [SerializeField] private FlightDataStreamReceiver receiver;
    [SerializeField] private bool autoFindReceiver = true;
    [SerializeField] private float refreshInterval = 0.1f;

    [Header("Layout")]
    [SerializeField] private Dropdown planeDropdown;
    [SerializeField] private RectTransform selectorRoot;
    [SerializeField] private RectTransform headerRoot;
    [SerializeField] private RectTransform contentRoot;
    [SerializeField] private RectTransform rowTemplate;
    [Tooltip("标题和数据列使用 Scene 里拖好的坐标；运行时只改文字。")]
    [SerializeField] private bool preserveSceneLayout = true;
    [SerializeField] private bool autoCreateLayout = true;
    [SerializeField] private float selectorHeight = 34f;
    [SerializeField] private float headerHeight = 32f;
    [SerializeField] private float rowHeight = 30f;
    [SerializeField] private float scrollbarWidth = 14f;
    [SerializeField] private float columnSpacing = 8f;
    [SerializeField] private float clearButtonWidth = 76f;
    [SerializeField] private Color clearButtonColor = new Color(0.62f, 0.2f, 0.2f, 0.95f);
    [Tooltip("大屏队列容量。新告警插到最前，超过后回收最早的记录；界面行也只保留这么多。")]
    [SerializeField] private int maxRows = 32;
    [SerializeField] private int maxHistoryEntries = 256;

    [Header("Style")]
    [SerializeField] private Font font;
    [SerializeField] private int headerFontSize = 13;
    [SerializeField] private int rowFontSize = 13;
    [SerializeField] private Color panelBackground = new Color(0.06f, 0.08f, 0.1f, 0.7f);
    [SerializeField] private Color headerBackground = new Color(0.08f, 0.1f, 0.13f, 0.95f);
    [SerializeField] private Color oddRowBackground = new Color(0.12f, 0.14f, 0.18f, 0.82f);
    [SerializeField] private Color evenRowBackground = new Color(0.16f, 0.18f, 0.22f, 0.82f);
    [SerializeField] private Color textColor = new Color(0.92f, 0.96f, 1f, 1f);
    [SerializeField] private Color mutedTextColor = new Color(0.66f, 0.74f, 0.82f, 1f);
    [SerializeField] private Color accentTextColor = new Color(0.43f, 0.82f, 1f, 1f);
    [SerializeField] private Color alarmNormalColor = new Color(0.4f, 0.9f, 0.4f, 1f);
    [SerializeField] private Color alarmWarningColor = new Color(1f, 0.78f, 0f, 1f);
    [SerializeField] private Color alarmDangerColor = new Color(1f, 0.3f, 0.3f, 1f);

    private readonly List<RowView> rows = new List<RowView>();
    private readonly List<AlarmEntry> alarmHistory = new List<AlarmEntry>();
    private readonly HashSet<string> alarmKeys = new HashSet<string>();
    private readonly List<string> dropdownTrackIds = new List<string>();
    private const string AllPlanesOption = "__ALL__";
    private RowView headerRow;
    private float nextRefreshTime;
    private string selectedTrackId = AllPlanesOption;

    private void Awake()
    {
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        if (autoCreateLayout && !preserveSceneLayout)
            EnsureLayoutObjects();

        if (planeDropdown != null)
        {
            planeDropdown.onValueChanged.RemoveListener(OnDropdownChanged);
            planeDropdown.onValueChanged.AddListener(OnDropdownChanged);
        }

        if (!preserveSceneLayout)
        {
            EnsureVerticalLayout(headerRoot, false);
            EnsureVerticalLayout(contentRoot, true);
            ApplyHeaderViewportInset();
        }

        if (headerRoot != null)
        {
            headerRow = FindOrCreateHeaderRow();
            SetRowTexts(headerRow, "飞机编号", "告警信息", "时间", "状态");
        }
        CollectSceneDataRows();
        EnsureClearButton();
    }

    private void CollectSceneDataRows()
    {
        if (rowTemplate == null)
            rowTemplate = PanelSceneLayout.FindRowTemplate(contentRoot, rowTemplate);
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
            return;
        nextRefreshTime = Time.unscaledTime + Mathf.Max(0.02f, refreshInterval);
        Refresh();
    }

    public void Refresh()
    {
        if (receiver == null && autoFindReceiver)
            receiver = FlightDataStreamReceiver.Instance;

        if (receiver == null)
        {
            SetActiveRowCount(0);
            return;
        }

        var states = receiver.SnapshotStates;
        states.Sort((a, b) => string.Compare(a?.trackId, b?.trackId, StringComparison.Ordinal));
        UpdateDropdownOptions(states);
        EnqueueAlarmEvents(states);

        var visibleEntries = FilteredQueue();
        int count = visibleEntries.Count;
        EnsureRowCount(count);
        SetActiveRowCount(count);
        ApplyRowLayout(count);

        for (int i = 0; i < count; i++)
        {
            var entry = visibleEntries[i];
            string id = string.IsNullOrWhiteSpace(entry.trackId) ? "unknown" : entry.trackId;
            // 告警信息列只放 alarm；状态列显示数据里的 state 原值，不要用正常/异常顶替。
            string alarm = string.IsNullOrWhiteSpace(entry.alarm) ? "-" : entry.alarm;
            bool abnormal = IsAbnormalStatus(entry.alarm, entry.state);
            string stateStr = string.IsNullOrWhiteSpace(entry.state) ? "-" : entry.state;
            string time = string.IsNullOrWhiteSpace(entry.flightTime) ? "-" : entry.flightTime;

            SetRowTexts(rows[i], id, alarm, time, stateStr);
            if (rows[i].stateText != null)
                rows[i].stateText.color = abnormal ? alarmDangerColor : alarmNormalColor;

            if (rows[i].alarmText != null)
            {
                rows[i].alarmText.color = GetAlarmColor(alarm);
            }
        }
    }

    private int QueueCapacity => Mathf.Max(1, maxRows);

    /// <summary>清空告警列表。当前这一刻已经存在的状态先记账，避免清空后立刻又冒出来。</summary>
    public void ClearAllAlarms()
    {
        alarmHistory.Clear();
        alarmKeys.Clear();

        var states = receiver != null ? receiver.SnapshotStates : null;
        if (states != null)
        {
            for (int i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (state == null)
                    continue;
                if (state.frameTimeMissing || string.IsNullOrWhiteSpace(state.flightTime))
                    continue;
                string trackId = string.IsNullOrWhiteSpace(state.trackId) ? "unknown" : state.trackId;
                string stateText = string.IsNullOrWhiteSpace(state.state) ? "-" : state.state;
                string alarm = string.IsNullOrWhiteSpace(state.alarm) ? string.Empty : state.alarm;
                if (!IsAbnormalStatus(alarm, stateText))
                    continue;
                alarmKeys.Add(state.flightTime + "|" + trackId + "|" + alarm + "|" + stateText);
            }
        }

        SetActiveRowCount(0);
        ApplyRowLayout(0);
        Refresh();
    }

    private void EnsureClearButton()
    {
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        RectTransform host = selectorRoot;
        if (host == null)
        {
            var panel = transform as RectTransform;
            if (panel == null)
                return;
            host = FindOrCreateRect(panel, "SelectorRoot");
            if (host == null)
                return;
            StretchTop(host, selectorHeight, 0f);
            selectorRoot = host;
        }

        Transform existing = host.Find("ClearAllButton");
        if (existing == null)
        {
            Transform dropdownTf = host.Find("PlaneSelector");
            if (dropdownTf is RectTransform dropdownRect)
            {
                dropdownRect.anchorMin = Vector2.zero;
                dropdownRect.anchorMax = Vector2.one;
                dropdownRect.offsetMin = new Vector2(0f, 0f);
                dropdownRect.offsetMax = new Vector2(-clearButtonWidth - 6f, 0f);
            }
            else if (planeDropdown != null && planeDropdown.transform is RectTransform ddRect)
            {
                ddRect.anchorMin = Vector2.zero;
                ddRect.anchorMax = Vector2.one;
                ddRect.offsetMin = new Vector2(0f, 0f);
                ddRect.offsetMax = new Vector2(-clearButtonWidth - 6f, 0f);
            }

            var go = new GameObject("ClearAllButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(host, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.offsetMin = new Vector2(-clearButtonWidth, 0f);
            rect.offsetMax = Vector2.zero;

            var image = go.GetComponent<Image>();
            image.color = clearButtonColor;

            var label = EnsureText(rect, "Label", textColor, TextAnchor.MiddleCenter);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;
            label.text = "清除";
            label.raycastTarget = false;

            existing = go.transform;
        }

        var button = existing.GetComponent<Button>();
        if (button == null)
            button = existing.gameObject.AddComponent<Button>();
        var graphic = existing.GetComponent<Image>();
        if (graphic != null)
            button.targetGraphic = graphic;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(ClearAllAlarms);
    }

    private List<AlarmEntry> FilteredQueue()
    {
        var result = new List<AlarmEntry>();
        for (int i = 0; i < alarmHistory.Count; i++)
        {
            var entry = alarmHistory[i];
            if (entry == null)
                continue;
            if (selectedTrackId != AllPlanesOption && entry.trackId != selectedTrackId)
                continue;
            // 只保留异常，正常的一律不显示。
            if (!IsAbnormalStatus(entry.alarm, entry.state))
                continue;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>
    /// 机场大屏队列：同一飞行时刻不重复入队；新时刻/新告警插到最前。
    /// 超过 maxRows 后丢掉最早的记录，键集合同步回收，避免无限增长。
    /// </summary>
    private void EnqueueAlarmEvents(List<FlightDataStreamReceiver.TrackTimetableState> states)
    {
        if (states == null || states.Count == 0)
            return;

        var batch = new List<AlarmEntry>();
        for (int i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (state == null)
                continue;

            string trackId = string.IsNullOrWhiteSpace(state.trackId) ? "unknown" : state.trackId;

            // 本帧没带时间：直接不做任何处理，不沿用上一帧的数据。
            if (state.frameTimeMissing || string.IsNullOrWhiteSpace(state.flightTime))
                continue;

            string stateText = string.IsNullOrWhiteSpace(state.state) ? "-" : state.state;
            string alarm = string.IsNullOrWhiteSpace(state.alarm) ? string.Empty : state.alarm;
            // 只收异常信息，正常行不显示。
            if (!IsAbnormalStatus(alarm, stateText))
                continue;

            string flightTime = state.flightTime;
            string identity = flightTime + "|" + trackId + "|" + alarm + "|" + stateText;
            if (alarmKeys.Contains(identity))
                continue;

            alarmKeys.Add(identity);
            batch.Add(new AlarmEntry
            {
                key = identity,
                flightTime = flightTime,
                trackId = trackId,
                alarm = alarm,
                state = stateText,
            });
        }

        if (batch.Count == 0)
            return;

        batch.Sort((a, b) => string.Compare(b.flightTime, a.flightTime, StringComparison.Ordinal));
        alarmHistory.InsertRange(0, batch);
        RecycleOldest();
    }

    private void RecycleOldest()
    {
        int capacity = QueueCapacity;
        while (alarmHistory.Count > capacity)
        {
            int last = alarmHistory.Count - 1;
            var tail = alarmHistory[last];
            alarmHistory.RemoveAt(last);
            if (tail != null && !string.IsNullOrEmpty(tail.key))
                alarmKeys.Remove(tail.key);
        }
    }

    private static bool IsAbnormalStatus(string alarm, string state)
    {
        if (!string.IsNullOrWhiteSpace(alarm))
        {
            string a = alarm.Trim();
            if (a != "正常" && a != "无" && a != "-" && !a.Equals("normal", StringComparison.OrdinalIgnoreCase) && !a.Equals("none", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!string.IsNullOrWhiteSpace(state))
        {
            string s = state.Trim();
            if (s.Contains("异常") || s.Contains("故障") || s.Contains("告警") ||
                s.Equals("abnormal", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("fault", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("alarm", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("error", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private int GetAlarmSeverity(string alarm)
    {
        if (string.IsNullOrWhiteSpace(alarm) || alarm == "正常")
        {
            return 0;
        }

        string value = alarm.ToLowerInvariant();
        if (alarm.Contains("被锁定") ||
            value.Contains("locked") ||
            value.Contains("danger") ||
            alarm.Contains("严重") ||
            alarm.Contains("紧急") ||
            value.Contains("critical"))
        {
            return 3;
        }

        if (alarm.Contains("左侧引擎故障") ||
            alarm.Contains("右侧引擎故障") ||
            alarm.Contains("引擎故障") ||
            alarm.Contains("雷达故障") ||
            value.Contains("engine") ||
            value.Contains("radar") ||
            value.Contains("fault") ||
            value.Contains("failure"))
        {
            return 2;
        }

        return 1;
    }

    private Color GetAlarmColor(string alarm)
    {
        int severity = GetAlarmSeverity(alarm);
        if (severity >= 3)
        {
            return alarmDangerColor;
        }

        if (severity >= 1)
        {
            return alarmWarningColor;
        }

        return alarmNormalColor;
    }

    private void OnDropdownChanged(int index)
    {
        if (index >= 0 && index < dropdownTrackIds.Count)
        {
            selectedTrackId = dropdownTrackIds[index];
            Refresh();
        }
    }

    private void UpdateDropdownOptions(List<FlightDataStreamReceiver.TrackTimetableState> states)
    {
        if (planeDropdown == null)
        {
            return;
        }

        var trackIds = new List<string> { AllPlanesOption };
        foreach (var state in states)
        {
            string trackId = state?.trackId;
            if (!string.IsNullOrWhiteSpace(trackId) && !trackIds.Contains(trackId))
            {
                trackIds.Add(trackId);
            }
        }

        bool changed = trackIds.Count != dropdownTrackIds.Count;
        if (!changed)
        {
            for (int i = 0; i < trackIds.Count; i++)
            {
                if (trackIds[i] != dropdownTrackIds[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        dropdownTrackIds.Clear();
        dropdownTrackIds.AddRange(trackIds);

        var options = new List<Dropdown.OptionData>();
        foreach (string trackId in dropdownTrackIds)
        {
            options.Add(new Dropdown.OptionData(trackId == AllPlanesOption ? "全部飞机" : trackId));
        }

        planeDropdown.ClearOptions();
        planeDropdown.AddOptions(options);

        int selectedIndex = dropdownTrackIds.IndexOf(selectedTrackId);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            selectedTrackId = AllPlanesOption;
        }

        planeDropdown.SetValueWithoutNotify(selectedIndex);
        planeDropdown.RefreshShownValue();
    }

    // ── Layout creation (mirrors PlaneDifferenceRankingPanel) ──

    private void EnsureLayoutObjects()
    {
        RectTransform panel = GetComponent<RectTransform>();
        if (panel == null) return;

        var background = GetComponent<Image>();
        if (background == null)
            background = gameObject.AddComponent<Image>();
        background.color = panelBackground;

        selectorRoot = FindOrCreateRect(panel, "SelectorRoot");
        StretchTop(selectorRoot, selectorHeight, 0f);
        planeDropdown = EnsureDropdown(selectorRoot);

        headerRoot = FindOrCreateRect(panel, "HeaderRoot");
        StretchTop(headerRoot, headerHeight, selectorHeight);
        headerRoot.offsetMax = new Vector2(-scrollbarWidth - 2f, headerRoot.offsetMax.y);

        RectTransform scrollRoot = FindOrCreateRect(panel, "Scroll View");
        StretchFillBelow(scrollRoot, selectorHeight + headerHeight);
        EnsureScrollArea(scrollRoot);

        selectorRoot.SetAsLastSibling();
    }

    private Dropdown EnsureDropdown(RectTransform root)
    {
        RectTransform dropdownRect = FindOrCreateRect(root, "PlaneSelector");
        StretchFull(dropdownRect);

        var image = dropdownRect.GetComponent<Image>();
        if (image == null)
            image = dropdownRect.gameObject.AddComponent<Image>();
        image.color = new Color(0.1f, 0.13f, 0.16f, 0.95f);

        var dropdown = dropdownRect.GetComponent<Dropdown>();
        if (dropdown == null)
            dropdown = dropdownRect.gameObject.AddComponent<Dropdown>();

        Text label = EnsureText(dropdownRect, "Label", textColor, TextAnchor.MiddleLeft);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(8f, 0f);
        label.rectTransform.offsetMax = new Vector2(-28f, 0f);
        label.text = "全部飞机";

        Text arrow = EnsureText(dropdownRect, "Arrow", mutedTextColor, TextAnchor.MiddleCenter);
        arrow.rectTransform.anchorMin = new Vector2(1f, 0f);
        arrow.rectTransform.anchorMax = new Vector2(1f, 1f);
        arrow.rectTransform.pivot = new Vector2(1f, 0.5f);
        arrow.rectTransform.offsetMin = new Vector2(-26f, 0f);
        arrow.rectTransform.offsetMax = Vector2.zero;
        arrow.text = "v";

        RectTransform template = EnsureDropdownTemplate(dropdownRect);
        dropdown.targetGraphic = image;
        dropdown.captionText = label;
        dropdown.template = template;
        dropdown.itemText = template.Find("Viewport/Content/Item/Item Label")?.GetComponent<Text>();
        return dropdown;
    }

    private RectTransform EnsureDropdownTemplate(RectTransform dropdownRect)
    {
        RectTransform template = FindOrCreateRect(dropdownRect, "Template");
        template.anchorMin = new Vector2(0f, 0f);
        template.anchorMax = new Vector2(1f, 0f);
        template.pivot = new Vector2(0.5f, 1f);
        template.offsetMin = new Vector2(0f, -Mathf.Max(120f, rowHeight * 5f));
        template.offsetMax = Vector2.zero;
        template.SetAsLastSibling();
        template.gameObject.SetActive(false);

        var templateCanvas = template.GetComponent<Canvas>();
        if (templateCanvas == null)
            templateCanvas = template.gameObject.AddComponent<Canvas>();
        templateCanvas.overrideSorting = true;
        templateCanvas.sortingOrder = 100;
        if (template.GetComponent<GraphicRaycaster>() == null)
            template.gameObject.AddComponent<GraphicRaycaster>();

        var templateImage = template.GetComponent<Image>();
        if (templateImage == null)
            templateImage = template.gameObject.AddComponent<Image>();
        templateImage.color = new Color(0.08f, 0.1f, 0.13f, 0.98f);

        var scrollRect = template.GetComponent<ScrollRect>();
        if (scrollRect == null)
            scrollRect = template.gameObject.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 24f;

        RectTransform viewport = FindOrCreateRect(template, "Viewport");
        StretchFull(viewport);
        viewport.offsetMax = new Vector2(-scrollbarWidth - 2f, 0f);
        var viewportImage = viewport.GetComponent<Image>();
        if (viewportImage == null)
            viewportImage = viewport.gameObject.AddComponent<Image>();
        viewportImage.color = new Color(1f, 1f, 1f, 0.01f);
        var mask = viewport.GetComponent<Mask>();
        if (mask == null)
            mask = viewport.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        RectTransform content = FindOrCreateRect(viewport, "Content");
        StretchContentTop(content);
        content.sizeDelta = new Vector2(0f, rowHeight);
        EnsureVerticalLayout(content, true);

        RectTransform item = FindOrCreateRect(content, "Item");
        item.anchorMin = new Vector2(0f, 1f);
        item.anchorMax = new Vector2(1f, 1f);
        item.pivot = new Vector2(0.5f, 1f);
        item.anchoredPosition = Vector2.zero;
        item.sizeDelta = new Vector2(0f, rowHeight);
        var toggle = item.GetComponent<Toggle>();
        if (toggle == null)
            toggle = item.gameObject.AddComponent<Toggle>();
        var itemImage = item.GetComponent<Image>();
        if (itemImage == null)
            itemImage = item.gameObject.AddComponent<Image>();
        itemImage.color = new Color(0.12f, 0.14f, 0.18f, 0.95f);
        toggle.targetGraphic = itemImage;

        Text itemLabel = EnsureText(item, "Item Label", textColor, TextAnchor.MiddleLeft);
        itemLabel.rectTransform.anchorMin = Vector2.zero;
        itemLabel.rectTransform.anchorMax = Vector2.one;
        itemLabel.rectTransform.offsetMin = new Vector2(8f, 0f);
        itemLabel.rectTransform.offsetMax = new Vector2(-8f, 0f);
        toggle.graphic = null;

        var itemLayout = item.GetComponent<LayoutElement>();
        if (itemLayout == null)
            itemLayout = item.gameObject.AddComponent<LayoutElement>();
        itemLayout.minHeight = rowHeight;
        itemLayout.preferredHeight = rowHeight;

        scrollRect.viewport = viewport;
        scrollRect.content = content;
        scrollRect.verticalScrollbar = EnsureVerticalScrollbar(template);
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scrollRect.verticalScrollbarSpacing = 2f;
        return template;
    }

    private Text EnsureText(RectTransform parent, string name, Color color, TextAnchor alignment)
    {
        RectTransform rect = FindOrCreateRect(parent, name);
        var text = rect.GetComponent<Text>();
        if (text == null)
            text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = rowFontSize;
        text.color = color;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    private void EnsureScrollArea(RectTransform scrollRoot)
    {
        var scrollImage = scrollRoot.GetComponent<Image>();
        if (scrollImage == null)
            scrollImage = scrollRoot.gameObject.AddComponent<Image>();
        scrollImage.color = new Color(0f, 0f, 0f, 0f);
        scrollImage.raycastTarget = true;

        RectTransform viewport = FindOrCreateRect(scrollRoot, "Viewport");
        StretchViewport(viewport);

        var viewportImage = viewport.GetComponent<Image>();
        if (viewportImage == null)
            viewportImage = viewport.gameObject.AddComponent<Image>();
        viewportImage.color = new Color(1f, 1f, 1f, 0.01f);
        viewportImage.raycastTarget = true;

        var mask = viewport.GetComponent<Mask>();
        if (mask == null)
            mask = viewport.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        contentRoot = FindOrCreateRect(viewport, "ContentRoot");
        StretchContentTop(contentRoot);

        var scrollRect = scrollRoot.GetComponent<ScrollRect>();
        if (scrollRect == null)
            scrollRect = scrollRoot.gameObject.AddComponent<ScrollRect>();
        scrollRect.viewport = viewport;
        scrollRect.content = contentRoot;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 24f;
        scrollRect.verticalScrollbar = EnsureVerticalScrollbar(scrollRoot);
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scrollRect.verticalScrollbarSpacing = 2f;
    }

    private Scrollbar EnsureVerticalScrollbar(RectTransform scrollRoot)
    {
        RectTransform scrollbarRoot = FindOrCreateRect(scrollRoot, "Vertical Scrollbar");
        scrollbarRoot.anchorMin = new Vector2(1f, 0f);
        scrollbarRoot.anchorMax = new Vector2(1f, 1f);
        scrollbarRoot.pivot = new Vector2(1f, 0.5f);
        scrollbarRoot.offsetMin = new Vector2(-scrollbarWidth, 0f);
        scrollbarRoot.offsetMax = Vector2.zero;

        var bg = scrollbarRoot.GetComponent<Image>();
        if (bg == null)
            bg = scrollbarRoot.gameObject.AddComponent<Image>();
        bg.color = new Color(0.04f, 0.05f, 0.07f, 0.65f);

        RectTransform slidingArea = FindOrCreateRect(scrollbarRoot, "Sliding Area");
        StretchFull(slidingArea);
        slidingArea.offsetMin = new Vector2(2f, 2f);
        slidingArea.offsetMax = new Vector2(-2f, -2f);

        RectTransform handle = FindOrCreateRect(slidingArea, "Handle");
        StretchFull(handle);

        var handleImage = handle.GetComponent<Image>();
        if (handleImage == null)
            handleImage = handle.gameObject.AddComponent<Image>();
        handleImage.color = new Color(0.43f, 0.82f, 1f, 0.9f);

        var scrollbar = scrollbarRoot.GetComponent<Scrollbar>();
        if (scrollbar == null)
            scrollbar = scrollbarRoot.gameObject.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        scrollbar.handleRect = handle;
        scrollbar.targetGraphic = handleImage;
        scrollbar.transition = Selectable.Transition.ColorTint;
        scrollbar.size = 1f;
        scrollbar.value = 1f;
        return scrollbar;
    }

    private void ApplyHeaderViewportInset()
    {
        if (headerRoot != null)
            headerRoot.offsetMax = new Vector2(-scrollbarWidth - 2f, headerRoot.offsetMax.y);
    }

    private static RectTransform FindOrCreateRect(RectTransform parent, string childName)
    {
        Transform existing = parent.Find(childName);
        if (existing != null && existing.TryGetComponent(out RectTransform existingRect))
            return existingRect;
        var obj = new GameObject(childName, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        return obj.GetComponent<RectTransform>();
    }

    private static void StretchTop(RectTransform rect, float height, float topOffset)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(0f, -topOffset - height);
        rect.offsetMax = new Vector2(0f, -topOffset);
    }

    private static void StretchFillBelow(RectTransform rect, float topOffset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = new Vector2(0f, -topOffset);
    }

    private static void StretchFull(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private void StretchViewport(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = new Vector2(-scrollbarWidth - 2f, 0f);
    }

    private static void StretchContentTop(RectTransform rect)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero;
    }

    private void EnsureVerticalLayout(RectTransform root, bool fitHeight)
    {
        if (root == null) return;
        var vertical = root.GetComponent<VerticalLayoutGroup>();
        if (vertical == null)
            vertical = root.gameObject.AddComponent<VerticalLayoutGroup>();
        vertical.childAlignment = TextAnchor.UpperCenter;
        vertical.childControlWidth = true;
        vertical.childControlHeight = false;
        vertical.childForceExpandWidth = true;
        vertical.childForceExpandHeight = false;
        vertical.spacing = 2f;

        var fitter = root.GetComponent<ContentSizeFitter>();
        if (fitter == null)
            fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = fitHeight ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
    }

    private void EnsureRowCount(int count)
    {
        while (rows.Count < count)
        {
            int index = rows.Count;
            Color bg = index % 2 == 0 ? oddRowBackground : evenRowBackground;
            string rowName = $"AlarmRow_{index + 1}";
            RectTransform template = PanelSceneLayout.FindRowTemplate(contentRoot, rowTemplate);
            if (template == null && rows.Count > 0)
                template = rows[0].root;
            RectTransform target = null;
            if (preserveSceneLayout && index == 0 && template != null)
            {
                target = template;
                target.gameObject.SetActive(true);
                PanelSceneLayout.FreezeRowLayout(target);
            }
            else
                target = PanelSceneLayout.GetOrCloneRow(contentRoot, rowName, preserveSceneLayout ? template : null);
            if (target != null)
                rows.Add(BindRow(target, bg, rowFontSize, false, true));
            else
                rows.Add(CreateRow(contentRoot, rowName, bg, rowFontSize));
        }
    }

    private void SetActiveRowCount(int count)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].root != null)
                rows[i].root.gameObject.SetActive(i < count);
        }
    }

    /// <summary>
    /// 场景行模板锚在左下角且宽度为 0，克隆后会叠在同一点。
    /// 按 HK132C 最初的列表方式改成顶对齐拉伸，交给 Content 的纵向布局撑开条数。
    /// </summary>
    private void ApplyRowLayout(int count)
    {
        if (contentRoot == null)
            return;

        var layout = contentRoot.GetComponent<VerticalLayoutGroup>();
        if (layout == null)
            layout = contentRoot.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.enabled = true;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.spacing = 2f;

        var fitter = contentRoot.GetComponent<ContentSizeFitter>();
        if (fitter == null)
            fitter = contentRoot.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        for (int i = 0; i < rows.Count; i++)
        {
            var rt = rows[i].root;
            if (rt == null)
                continue;
            var element = rt.GetComponent<LayoutElement>();
            if (element == null)
                element = rt.gameObject.AddComponent<LayoutElement>();
            element.ignoreLayout = false;
            element.minHeight = rowHeight;
            element.preferredHeight = rowHeight;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, rowHeight);
        }

        float height = Mathf.Max(0, count) * (rowHeight + layout.spacing);
        contentRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRoot);
    }

    private RowView FindOrCreateHeaderRow()
    {
        if (headerRoot != null)
        {
            Transform existing = headerRoot.Find("Header");
            if (existing != null && existing.TryGetComponent(out RectTransform existingRect))
                return BindRow(existingRect, headerBackground, headerFontSize, true, true);
        }
        return CreateRow(headerRoot, "Header", headerBackground, headerFontSize, true);
    }

    private RowView CreateRow(RectTransform parentRoot, string rowName, Color background, int fontSize, bool header = false)
    {
        var rowObject = new GameObject(rowName, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(parentRoot != null ? parentRoot : transform, false);
        return BindRow(rowObject.GetComponent<RectTransform>(), background, fontSize, header, false);
    }

    private RowView BindRow(RectTransform rect, Color background, int fontSize, bool header, bool preserveExistingHeaderCells)
    {
        GameObject rowObject = rect.gameObject;

        if (!preserveExistingHeaderCells)
        {
            if (header)
                StretchFull(rect);
            else
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0f, rowHeight);
            }
        }

        var image = rowObject.GetComponent<Image>();
        if (image == null)
            image = rowObject.AddComponent<Image>();
        image.color = background;

        var layoutElement = rowObject.GetComponent<LayoutElement>();
        if (layoutElement == null)
            layoutElement = rowObject.AddComponent<LayoutElement>();
        layoutElement.ignoreLayout = header;
        layoutElement.minHeight = header ? 0f : rowHeight;
        layoutElement.preferredHeight = header ? 0f : rowHeight;

        var horizontal = rowObject.GetComponent<HorizontalLayoutGroup>();
        if (preserveExistingHeaderCells || preserveSceneLayout)
        {
            PanelSceneLayout.FreezeRowLayout(rect);
        }
        else if (!preserveExistingHeaderCells)
        {
            if (horizontal == null)
                horizontal = rowObject.AddComponent<HorizontalLayoutGroup>();
            horizontal.enabled = true;
            horizontal.childAlignment = TextAnchor.MiddleCenter;
            horizontal.childControlWidth = true;
            horizontal.childControlHeight = true;
            horizontal.childForceExpandWidth = false;
            horizontal.childForceExpandHeight = true;
            horizontal.spacing = columnSpacing;
            horizontal.padding = new RectOffset(8, 8, 2, 2);
        }

        RemoveCellIfExists(rowObject.transform, "Rank");
        RemoveCellIfExists(rowObject.transform, "Battery");

        var rowView = new RowView
        {
            root = rect,
            idText = FindOrCreateCell(rowObject.transform, "PlaneId", textColor, fontSize, 88f, 0f, preserveExistingHeaderCells),
            stateText = FindOrCreateCell(rowObject.transform, "State", mutedTextColor, fontSize, 64f, 0f, preserveExistingHeaderCells),
            alarmText = FindOrCreateCell(rowObject.transform, "Alarm", textColor, fontSize, 140f, 1f, preserveExistingHeaderCells),
            timeText = FindOrCreateCell(rowObject.transform, "Time", mutedTextColor, fontSize, 100f, 0f, preserveExistingHeaderCells),
        };

        // 场景中可能残留旧列顺序，统一为：飞机编号、时间、状态、告警信息
        rowView.idText.transform.SetSiblingIndex(0);
        rowView.timeText.transform.SetSiblingIndex(1);
        rowView.stateText.transform.SetSiblingIndex(2);
        rowView.alarmText.transform.SetSiblingIndex(3);
        ConfigureTimeCell(rowView.timeText);

        return rowView;
    }

    private static void RemoveCellIfExists(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing == null)
            return;

        if (Application.isPlaying)
            Destroy(existing.gameObject);
        else
            DestroyImmediate(existing.gameObject);
    }

    private Text FindOrCreateCell(Transform parent, string name, Color color, int fontSize, float width, float flexibleWidth, bool preserveExisting)
    {
        Transform existing = parent.Find(name);
        if (existing != null && existing.TryGetComponent(out Text existingText))
        {
            if (!preserveExisting)
                ConfigureCell(existingText, color, fontSize, width, flexibleWidth);
            return existingText;
        }
        return CreateCell(parent, name, color, fontSize, width, flexibleWidth);
    }

    private Text CreateCell(Transform parent, string name, Color color, int fontSize, float width, float flexibleWidth)
    {
        var cellObject = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        cellObject.transform.SetParent(parent, false);
        var text = cellObject.GetComponent<Text>();
        ConfigureCell(text, color, fontSize, width, flexibleWidth);
        return text;
    }

    private void ConfigureCell(Text text, Color color, int fontSize, float width, float flexibleWidth)
    {
        text.font = font;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;

        var layoutElement = text.GetComponent<LayoutElement>();
        if (layoutElement == null)
            layoutElement = text.gameObject.AddComponent<LayoutElement>();
        layoutElement.minWidth = width;
        layoutElement.preferredWidth = width;
        layoutElement.flexibleWidth = flexibleWidth;
    }

    private void ConfigureTimeCell(Text text)
    {
        if (text == null)
            return;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        if (preserveSceneLayout)
            return;
        var layoutElement = text.GetComponent<LayoutElement>();
        if (layoutElement == null)
            layoutElement = text.gameObject.AddComponent<LayoutElement>();
        layoutElement.minWidth = 100f;
        layoutElement.preferredWidth = 100f;
        layoutElement.flexibleWidth = 0f;
    }

    private static string FormatAlarmTime(string time)
    {
        if (string.IsNullOrWhiteSpace(time))
            return "-";
        time = time.Trim();
        int space = time.IndexOf(' ');
        if (space > 0 && space + 1 < time.Length)
            time = time.Substring(space + 1);
        int dot = time.IndexOf('.');
        if (dot >= 0 && time.Length > dot + 4)
            time = time.Substring(0, dot + 4);
        return time;
    }

    private void SetRowTexts(RowView row, string planeId, string alarm, string time, string state)
    {
        if (row == null) return;
        if (row.idText != null)
        {
            row.idText.alignment = TextAnchor.MiddleCenter;
            row.idText.text = planeId;
        }
        if (row.alarmText != null)
        {
            // 告警内容过长时按列宽自动换行，行高随后撑开。
            row.alarmText.alignment = TextAnchor.MiddleCenter;
            row.alarmText.horizontalOverflow = HorizontalWrapMode.Wrap;
            row.alarmText.verticalOverflow = VerticalWrapMode.Overflow;
            row.alarmText.text = alarm;
        }
        if (row.timeText != null)
        {
            ConfigureTimeCell(row.timeText);
            row.timeText.text = row == headerRow ? time : FormatAlarmTime(time);
        }
        if (row.stateText != null)
        {
            row.stateText.alignment = TextAnchor.MiddleCenter;
            row.stateText.text = state;
        }
        FitRowHeightToAlarm(row, alarm);
    }

    /// <summary>按告警列实际宽度算换行后的高度，把这一行撑到够显示，其它列上下居中。</summary>
    private void FitRowHeightToAlarm(RowView row, string alarm)
    {
        if (row == null || row.root == null || row.alarmText == null || row == headerRow)
            return;

        var text = row.alarmText;
        float width = text.rectTransform.rect.width;
        if (width < 8f)
        {
            float panelWidth = contentRoot != null
                ? contentRoot.rect.width
                : (transform as RectTransform) != null ? ((RectTransform)transform).rect.width : 0f;
            width = Mathf.Max(60f, panelWidth - 340f);
        }

        var settings = text.GetGenerationSettings(new Vector2(Mathf.Max(20f, width), 0f));
        settings.horizontalOverflow = HorizontalWrapMode.Wrap;
        settings.verticalOverflow = VerticalWrapMode.Overflow;
        settings.scaleFactor = 1f;
        float needed = text.cachedTextGeneratorForLayout.GetPreferredHeight(
            string.IsNullOrEmpty(alarm) ? " " : alarm, settings) / Mathf.Max(0.01f, text.pixelsPerUnit);
        float height = Mathf.Max(rowHeight, needed + 8f);

        var element = row.root.GetComponent<LayoutElement>();
        if (element != null)
        {
            element.minHeight = height;
            element.preferredHeight = height;
        }
        if (Mathf.Abs(row.root.sizeDelta.y - height) > 0.5f)
            row.root.sizeDelta = new Vector2(row.root.sizeDelta.x, height);
    }
}
