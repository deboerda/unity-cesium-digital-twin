using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class PlaneDifferenceRankingPanel : MonoBehaviour
{
    [Serializable]
    private class RowView
    {
        public RectTransform root;
        public Text rankText;
        public Text idText;
        public Text distanceText;
        public Text altitudeText;
        public Text headingText;
        public Text pitchText;
    }

    [Header("Data")]
    [SerializeField] private FlightDataStreamReceiver receiver;
    [SerializeField] private bool autoFindReceiver = true;
    [SerializeField] private float refreshInterval = 0.1f;

    [Header("Layout")]
    [SerializeField] private Dropdown planeDropdown;
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
    [SerializeField] private int maxRows = 32;

    [Header("Style")]
    [SerializeField] private Font font;
    [SerializeField] private int selectorFontSize = 13;
    [SerializeField] private int headerFontSize = 13;
    [SerializeField] private int rowFontSize = 13;
    [SerializeField] private Color panelBackground = new Color(0.06f, 0.08f, 0.1f, 0.7f);
    [SerializeField] private Color headerBackground = new Color(0.08f, 0.1f, 0.13f, 0.95f);
    [SerializeField] private Color oddRowBackground = new Color(0.12f, 0.14f, 0.18f, 0.82f);
    [SerializeField] private Color evenRowBackground = new Color(0.16f, 0.18f, 0.22f, 0.82f);
    [SerializeField] private Color textColor = new Color(0.92f, 0.96f, 1f, 1f);
    [SerializeField] private Color mutedTextColor = new Color(0.66f, 0.74f, 0.82f, 1f);
    [SerializeField] private Color accentTextColor = new Color(0.43f, 0.82f, 1f, 1f);

    private const float EarthRadiusMeters = 6371000f;

    /// <summary>表头可点击排序的列。</summary>
    private enum SortColumn
    {
        Distance,
        TrackId,
        Altitude,
        Azimuth,
        Pitch
    }

    private readonly List<RowView> rows = new List<RowView>();
    private readonly List<string> dropdownTrackIds = new List<string>();
    private RowView headerRow;
    private float nextRefreshTime;
    private string selectedTrackId;
    private SortColumn sortColumn = SortColumn.Distance;
    private bool sortAscending = true;
    public string SelectedTrackId => selectedTrackId;

    private void Awake()
    {
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        if (autoCreateLayout && !preserveSceneLayout)
        {
            EnsureLayoutObjects();
        }

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
            SetRowTexts(headerRow, "#", "飞机编号", "距离差", "高度差", "方位角差", "俯仰角差");
            SetupHeaderSort();
            UpdateHeaderLabels();
        }
        CollectSceneDataRows();
    }

    private void CollectSceneDataRows()
    {
        if (rowTemplate == null)
            rowTemplate = PanelSceneLayout.FindRowTemplate(contentRoot, rowTemplate);
    }

    private void Update()
    {
        if (Time.unscaledTime < nextRefreshTime)
        {
            return;
        }

        nextRefreshTime = Time.unscaledTime + Mathf.Max(0.02f, refreshInterval);
        Refresh();
    }

    public void Refresh()
    {
        if (receiver == null && autoFindReceiver)
        {
            receiver = FlightDataStreamReceiver.Instance;
        }

        if (receiver == null)
        {
            SetActiveRowCount(0);
            return;
        }

        List<FlightDataStreamReceiver.TrackPosition> positions = receiver.SnapshotPositions;
        positions.Sort((a, b) => string.Compare(a?.trackId, b?.trackId, StringComparison.Ordinal));
        UpdateDropdownOptions(positions);

        FlightDataStreamReceiver.TrackPosition selected = positions.Find(p => p != null && p.trackId == selectedTrackId);
        if (selected == null && positions.Count > 0)
        {
            selected = positions[0];
            selectedTrackId = selected.trackId;
        }

        if (selected == null)
        {
            SetActiveRowCount(0);
            return;
        }

        var comparisons = new List<FlightDataStreamReceiver.TrackPosition>();
        foreach (var pos in positions)
        {
            if (pos == null || pos.trackId == selected.trackId)
            {
                continue;
            }

            comparisons.Add(pos);
        }

        comparisons.Sort((a, b) => CompareBy(selected, a, b));

        int count = Mathf.Min(maxRows, comparisons.Count);
        EnsureRowCount(count);
        SetActiveRowCount(count);

        for (int i = 0; i < count; i++)
        {
            var pos = comparisons[i];
            float distance = CalculateDistanceMeters(selected, pos);
            float altitudeDelta = pos.alt - selected.alt;
            float headingDelta = RelativeAzimuthDeg(selected, pos);
            float pitchDelta = Mathf.DeltaAngle(selected.pitch, pos.pitch);
            SetRowTexts(
                rows[i],
                (i + 1).ToString(),
                string.IsNullOrWhiteSpace(pos.trackId) ? "unknown" : pos.trackId,
                FormatDistance(distance),
                FormatSigned(altitudeDelta, "m"),
                FormatSigned(headingDelta, "deg"),
                FormatSigned(pitchDelta, "deg"));
        }
    }

    private void OnDropdownChanged(int index)
    {
        if (index >= 0 && index < dropdownTrackIds.Count)
        {
            selectedTrackId = dropdownTrackIds[index];
            Refresh();
        }
    }

    private void UpdateDropdownOptions(List<FlightDataStreamReceiver.TrackPosition> positions)
    {
        if (planeDropdown == null)
        {
            return;
        }

        bool changed = positions.Count != dropdownTrackIds.Count;
        if (!changed)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                string trackId = positions[i]?.trackId ?? string.Empty;
                if (dropdownTrackIds[i] != trackId)
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
        planeDropdown.ClearOptions();

        var options = new List<Dropdown.OptionData>();
        foreach (var pos in positions)
        {
            string trackId = string.IsNullOrWhiteSpace(pos?.trackId) ? "unknown" : pos.trackId;
            dropdownTrackIds.Add(trackId);
            options.Add(new Dropdown.OptionData(trackId));
        }

        planeDropdown.AddOptions(options);
        int selectedIndex = Mathf.Max(0, dropdownTrackIds.IndexOf(selectedTrackId));
        if (dropdownTrackIds.Count > 0)
        {
            selectedTrackId = dropdownTrackIds[selectedIndex];
            planeDropdown.SetValueWithoutNotify(selectedIndex);
            planeDropdown.RefreshShownValue();
        }
    }

    private int CompareDistance(FlightDataStreamReceiver.TrackPosition selected, FlightDataStreamReceiver.TrackPosition a, FlightDataStreamReceiver.TrackPosition b)
    {
        return CalculateDistanceMeters(selected, a).CompareTo(CalculateDistanceMeters(selected, b));
    }

    /// <summary>按当前排序列比较；相同值时按距离、再按编号兜底，保证顺序稳定。</summary>
    private int CompareBy(FlightDataStreamReceiver.TrackPosition selected, FlightDataStreamReceiver.TrackPosition a, FlightDataStreamReceiver.TrackPosition b)
    {
        int cmp;
        switch (sortColumn)
        {
            case SortColumn.TrackId:
                cmp = string.Compare(a?.trackId, b?.trackId, StringComparison.Ordinal);
                break;
            case SortColumn.Altitude:
                cmp = a.alt.CompareTo(b.alt);
                break;
            case SortColumn.Azimuth:
                cmp = RelativeAzimuthDeg(selected, a).CompareTo(RelativeAzimuthDeg(selected, b));
                break;
            case SortColumn.Pitch:
                cmp = Mathf.DeltaAngle(selected.pitch, a.pitch).CompareTo(Mathf.DeltaAngle(selected.pitch, b.pitch));
                break;
            default:
                cmp = CompareDistance(selected, a, b);
                break;
        }

        if (!sortAscending)
            cmp = -cmp;
        if (cmp == 0)
            cmp = CompareDistance(selected, a, b);
        if (cmp == 0)
            cmp = string.Compare(a?.trackId, b?.trackId, StringComparison.Ordinal);
        return cmp;
    }

    // ── 表头点击排序 ──────────────────────────────────────────────

    private void SetupHeaderSort()
    {
        if (headerRow == null)
            return;
        BindHeaderCell(headerRow.idText, "飞机编号", SortColumn.TrackId);
        BindHeaderCell(headerRow.distanceText, "距离差", SortColumn.Distance);
        BindHeaderCell(headerRow.altitudeText, "高度差", SortColumn.Altitude);
        BindHeaderCell(headerRow.headingText, "方位角差", SortColumn.Azimuth);
        BindHeaderCell(headerRow.pitchText, "俯仰角差", SortColumn.Pitch);
    }

    private void BindHeaderCell(Text cell, string label, SortColumn column)
    {
        if (cell == null)
            return;
        cell.raycastTarget = true;
        var button = cell.GetComponent<Button>();
        if (button == null)
            button = cell.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(() => OnHeaderClicked(column));
    }

    private void OnHeaderClicked(SortColumn column)
    {
        if (sortColumn == column)
            sortAscending = !sortAscending;
        else
        {
            sortColumn = column;
            sortAscending = true;
        }

        UpdateHeaderLabels();
        Refresh();
    }

    private void UpdateHeaderLabels()
    {
        if (headerRow == null)
            return;
        SetHeaderLabel(headerRow.idText, "飞机编号", SortColumn.TrackId);
        SetHeaderLabel(headerRow.distanceText, "距离差", SortColumn.Distance);
        SetHeaderLabel(headerRow.altitudeText, "高度差", SortColumn.Altitude);
        SetHeaderLabel(headerRow.headingText, "方位角差", SortColumn.Azimuth);
        SetHeaderLabel(headerRow.pitchText, "俯仰角差", SortColumn.Pitch);
    }

    private void SetHeaderLabel(Text cell, string label, SortColumn column)
    {
        if (cell == null)
            return;
        cell.text = sortColumn == column ? $"{label} {(sortAscending ? "↑" : "↓")}" : label;
    }

    private static float CalculateDistanceMeters(FlightDataStreamReceiver.TrackPosition a, FlightDataStreamReceiver.TrackPosition b)
    {
        float lat1 = (float)a.lat * Mathf.Deg2Rad;
        float lat2 = (float)b.lat * Mathf.Deg2Rad;
        float dLat = (float)(b.lat - a.lat) * Mathf.Deg2Rad;
        float dLng = (float)(b.lng - a.lng) * Mathf.Deg2Rad;
        float sinLat = Mathf.Sin(dLat * 0.5f);
        float sinLng = Mathf.Sin(dLng * 0.5f);
        float h = sinLat * sinLat + Mathf.Cos(lat1) * Mathf.Cos(lat2) * sinLng * sinLng;
        float c = 2f * Mathf.Atan2(Mathf.Sqrt(h), Mathf.Sqrt(Mathf.Max(0f, 1f - h)));
        return EarthRadiusMeters * c;
    }

    /// <summary>对方相对本机机头的方位：0 正前，正值右舷，负值左舷。</summary>
    private static float RelativeAzimuthDeg(FlightDataStreamReceiver.TrackPosition from, FlightDataStreamReceiver.TrackPosition to)
    {
        return Mathf.DeltaAngle(from.heading, BearingDeg(from, to));
    }

    private static float BearingDeg(FlightDataStreamReceiver.TrackPosition from, FlightDataStreamReceiver.TrackPosition to)
    {
        float lat1 = (float)from.lat * Mathf.Deg2Rad;
        float lat2 = (float)to.lat * Mathf.Deg2Rad;
        float dLng = (float)(to.lng - from.lng) * Mathf.Deg2Rad;
        float y = Mathf.Sin(dLng) * Mathf.Cos(lat2);
        float x = Mathf.Cos(lat1) * Mathf.Sin(lat2) - Mathf.Sin(lat1) * Mathf.Cos(lat2) * Mathf.Cos(dLng);
        return Mathf.Atan2(y, x) * Mathf.Rad2Deg;
    }

    private static string FormatDistance(float meters)
    {
        return meters >= 1000f ? $"{meters / 1000f:F2} km" : $"{meters:F1} m";
    }

    private static string FormatSigned(float value, string unit)
    {
        return $"{value:+0.0;-0.0;0.0} {unit}";
    }

    private void EnsureLayoutObjects()
    {
        RectTransform panel = GetComponent<RectTransform>();
        if (panel == null)
        {
            return;
        }

        var background = GetComponent<Image>();
        if (background == null)
        {
            background = gameObject.AddComponent<Image>();
        }
        background.color = panelBackground;

        RectTransform selectorRoot = FindOrCreateRect(panel, "SelectorRoot");
        StretchTop(selectorRoot, selectorHeight, 0f);
        planeDropdown = EnsureDropdown(selectorRoot);

        headerRoot = FindOrCreateRect(panel, "HeaderRoot");
        StretchTop(headerRoot, headerHeight, selectorHeight);
        headerRoot.offsetMax = new Vector2(-scrollbarWidth - 2f, headerRoot.offsetMax.y);

        RectTransform scrollRoot = FindOrCreateRect(panel, "Scroll View");
        StretchFillBelow(scrollRoot, selectorHeight + headerHeight);
        EnsureScrollArea(scrollRoot);
    }

    private void EnsureScrollArea(RectTransform scrollRoot)
    {
        var scrollImage = scrollRoot.GetComponent<Image>();
        if (scrollImage == null)
        {
            scrollImage = scrollRoot.gameObject.AddComponent<Image>();
        }
        scrollImage.color = new Color(0f, 0f, 0f, 0f);
        scrollImage.raycastTarget = true;

        RectTransform viewport = FindOrCreateRect(scrollRoot, "Viewport");
        StretchViewport(viewport);

        var viewportImage = viewport.GetComponent<Image>();
        if (viewportImage == null)
        {
            viewportImage = viewport.gameObject.AddComponent<Image>();
        }
        viewportImage.color = new Color(1f, 1f, 1f, 0.01f);
        viewportImage.raycastTarget = true;

        var mask = viewport.GetComponent<Mask>();
        if (mask == null)
        {
            mask = viewport.gameObject.AddComponent<Mask>();
        }
        mask.showMaskGraphic = false;

        contentRoot = FindOrCreateRect(viewport, "ContentRoot");
        StretchContentTop(contentRoot);

        var scrollRect = scrollRoot.GetComponent<ScrollRect>();
        if (scrollRect == null)
        {
            scrollRect = scrollRoot.gameObject.AddComponent<ScrollRect>();
        }
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

    private Dropdown EnsureDropdown(RectTransform root)
    {
        RectTransform dropdownRect = FindOrCreateRect(root, "PlaneSelector");
        StretchFull(dropdownRect);

        var image = dropdownRect.GetComponent<Image>();
        if (image == null)
        {
            image = dropdownRect.gameObject.AddComponent<Image>();
        }
        image.color = new Color(0.1f, 0.13f, 0.16f, 0.95f);

        var dropdown = dropdownRect.GetComponent<Dropdown>();
        if (dropdown == null)
        {
            dropdown = dropdownRect.gameObject.AddComponent<Dropdown>();
        }

        Text label = EnsureText(dropdownRect, "Label", textColor, selectorFontSize, TextAnchor.MiddleLeft);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(8f, 0f);
        label.rectTransform.offsetMax = new Vector2(-28f, 0f);
        label.text = "选择飞机";

        Text arrow = EnsureText(dropdownRect, "Arrow", mutedTextColor, selectorFontSize, TextAnchor.MiddleCenter);
        arrow.rectTransform.anchorMin = new Vector2(1f, 0f);
        arrow.rectTransform.anchorMax = new Vector2(1f, 1f);
        arrow.rectTransform.pivot = new Vector2(1f, 0.5f);
        arrow.rectTransform.offsetMin = new Vector2(-26f, 0f);
        arrow.rectTransform.offsetMax = Vector2.zero;
        arrow.text = "v";

        dropdown.targetGraphic = image;
        dropdown.captionText = label;
        RectTransform template = EnsureDropdownTemplate(dropdownRect);
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
        template.gameObject.SetActive(false);

        var templateImage = template.GetComponent<Image>();
        if (templateImage == null)
        {
            templateImage = template.gameObject.AddComponent<Image>();
        }
        templateImage.color = new Color(0.08f, 0.1f, 0.13f, 0.98f);

        var scrollRect = template.GetComponent<ScrollRect>();
        if (scrollRect == null)
        {
            scrollRect = template.gameObject.AddComponent<ScrollRect>();
        }
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.scrollSensitivity = 24f;

        RectTransform viewport = FindOrCreateRect(template, "Viewport");
        StretchFull(viewport);
        viewport.offsetMax = new Vector2(-scrollbarWidth - 2f, 0f);
        var viewportImage = viewport.GetComponent<Image>();
        if (viewportImage == null)
        {
            viewportImage = viewport.gameObject.AddComponent<Image>();
        }
        viewportImage.color = new Color(1f, 1f, 1f, 0.01f);

        var mask = viewport.GetComponent<Mask>();
        if (mask == null)
        {
            mask = viewport.gameObject.AddComponent<Mask>();
        }
        mask.showMaskGraphic = false;

        RectTransform content = FindOrCreateRect(viewport, "Content");
        StretchContentTop(content);

        var vertical = content.GetComponent<VerticalLayoutGroup>();
        if (vertical == null)
        {
            vertical = content.gameObject.AddComponent<VerticalLayoutGroup>();
        }
        vertical.childControlHeight = false;
        vertical.childControlWidth = true;
        vertical.childForceExpandHeight = false;
        vertical.childForceExpandWidth = true;
        vertical.spacing = 0f;

        var fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter == null)
        {
            fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        }
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RectTransform item = FindOrCreateRect(content, "Item");
        item.anchorMin = new Vector2(0f, 1f);
        item.anchorMax = new Vector2(1f, 1f);
        item.pivot = new Vector2(0.5f, 1f);
        item.sizeDelta = new Vector2(0f, rowHeight);

        var itemLayout = item.GetComponent<LayoutElement>();
        if (itemLayout == null)
        {
            itemLayout = item.gameObject.AddComponent<LayoutElement>();
        }
        itemLayout.minHeight = rowHeight;
        itemLayout.preferredHeight = rowHeight;

        var toggle = item.GetComponent<Toggle>();
        if (toggle == null)
        {
            toggle = item.gameObject.AddComponent<Toggle>();
        }

        var itemBackground = item.GetComponent<Image>();
        if (itemBackground == null)
        {
            itemBackground = item.gameObject.AddComponent<Image>();
        }
        itemBackground.color = evenRowBackground;
        toggle.targetGraphic = itemBackground;

        Text itemLabel = EnsureText(item, "Item Label", textColor, selectorFontSize, TextAnchor.MiddleLeft);
        itemLabel.rectTransform.anchorMin = Vector2.zero;
        itemLabel.rectTransform.anchorMax = Vector2.one;
        itemLabel.rectTransform.offsetMin = new Vector2(8f, 0f);
        itemLabel.rectTransform.offsetMax = new Vector2(-8f, 0f);

        scrollRect.viewport = viewport;
        scrollRect.content = content;
        scrollRect.verticalScrollbar = EnsureVerticalScrollbar(template);
        scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        scrollRect.verticalScrollbarSpacing = 2f;
        return template;
    }

    private Text EnsureText(RectTransform parent, string name, Color color, int fontSize, TextAnchor alignment)
    {
        RectTransform rect = FindOrCreateRect(parent, name);
        var text = rect.GetComponent<Text>();
        if (text == null)
        {
            text = rect.gameObject.AddComponent<Text>();
        }
        text.font = font;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = alignment;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;
        return text;
    }

    private Scrollbar EnsureVerticalScrollbar(RectTransform scrollRoot)
    {
        RectTransform scrollbarRoot = FindOrCreateRect(scrollRoot, "Vertical Scrollbar");
        scrollbarRoot.anchorMin = new Vector2(1f, 0f);
        scrollbarRoot.anchorMax = new Vector2(1f, 1f);
        scrollbarRoot.pivot = new Vector2(1f, 0.5f);
        scrollbarRoot.offsetMin = new Vector2(-scrollbarWidth, 0f);
        scrollbarRoot.offsetMax = Vector2.zero;

        var background = scrollbarRoot.GetComponent<Image>();
        if (background == null)
        {
            background = scrollbarRoot.gameObject.AddComponent<Image>();
        }
        background.color = new Color(0.04f, 0.05f, 0.07f, 0.65f);
        background.raycastTarget = true;

        RectTransform slidingArea = FindOrCreateRect(scrollbarRoot, "Sliding Area");
        StretchFull(slidingArea);
        slidingArea.offsetMin = new Vector2(2f, 2f);
        slidingArea.offsetMax = new Vector2(-2f, -2f);

        RectTransform handle = FindOrCreateRect(slidingArea, "Handle");
        StretchFull(handle);

        var handleImage = handle.GetComponent<Image>();
        if (handleImage == null)
        {
            handleImage = handle.gameObject.AddComponent<Image>();
        }
        handleImage.color = new Color(0.43f, 0.82f, 1f, 0.9f);
        handleImage.raycastTarget = true;

        var scrollbar = scrollbarRoot.GetComponent<Scrollbar>();
        if (scrollbar == null)
        {
            scrollbar = scrollbarRoot.gameObject.AddComponent<Scrollbar>();
        }
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
        if (headerRoot == null || headerRoot == contentRoot)
        {
            return;
        }

        headerRoot.offsetMax = new Vector2(-scrollbarWidth - 2f, headerRoot.offsetMax.y);
    }

    private static RectTransform FindOrCreateRect(RectTransform parent, string childName)
    {
        Transform existing = parent.Find(childName);
        if (existing != null && existing.TryGetComponent(out RectTransform existingRect))
        {
            return existingRect;
        }

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
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
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
        rect.sizeDelta = new Vector2(0f, 0f);
    }

    private void EnsureVerticalLayout(RectTransform root, bool fitHeight)
    {
        if (root == null)
        {
            return;
        }

        var vertical = root.GetComponent<VerticalLayoutGroup>();
        if (vertical == null)
        {
            vertical = root.gameObject.AddComponent<VerticalLayoutGroup>();
        }
        vertical.childAlignment = TextAnchor.UpperCenter;
        vertical.childControlWidth = true;
        vertical.childControlHeight = false;
        vertical.childForceExpandWidth = true;
        vertical.childForceExpandHeight = false;
        vertical.spacing = 2f;

        var fitter = root.GetComponent<ContentSizeFitter>();
        if (fitter == null)
        {
            fitter = root.gameObject.AddComponent<ContentSizeFitter>();
        }
        fitter.verticalFit = fitHeight ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
    }

    private void EnsureRowCount(int count)
    {
        while (rows.Count < count)
        {
            int index = rows.Count;
            Color bg = index % 2 == 0 ? oddRowBackground : evenRowBackground;
            string rowName = $"PlaneDifferenceRow_{index + 1}";
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
                rows.Add(CreateRow(contentRoot, rowName, bg, rowFontSize, false));
        }
    }

    private void SetActiveRowCount(int count)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].root != null)
            {
                rows[i].root.gameObject.SetActive(i < count);
            }
        }
    }

    private RowView FindOrCreateHeaderRow()
    {
        if (headerRoot != null)
        {
            Transform existing = headerRoot.Find("Header");
            if (existing != null && existing.TryGetComponent(out RectTransform existingRect))
            {
                return BindRow(existingRect, headerBackground, headerFontSize, true, true);
            }
        }

        return CreateRow(headerRoot, "Header", headerBackground, headerFontSize, true);
    }

    private RowView CreateRow(RectTransform parentRoot, string rowName, Color background, int fontSize, bool header)
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
            {
                StretchFull(rect);
            }
            else
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0f, rowHeight);
                rect.offsetMin = new Vector2(0f, rect.offsetMin.y);
                rect.offsetMax = new Vector2(0f, rect.offsetMax.y);
            }
        }

        var image = rowObject.GetComponent<Image>();
        if (image == null)
        {
            image = rowObject.AddComponent<Image>();
        }
        image.color = background;

        var layoutElement = rowObject.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = rowObject.AddComponent<LayoutElement>();
        }
        layoutElement.ignoreLayout = header;
        layoutElement.minHeight = header ? 0f : rowHeight;
        layoutElement.preferredHeight = header ? 0f : rowHeight;

        var horizontal = rowObject.GetComponent<HorizontalLayoutGroup>();
        if (preserveExistingHeaderCells || preserveSceneLayout)
        {
            PanelSceneLayout.FreezeRowLayout(rect);
        }
        else
        {
            if (horizontal == null)
            {
                horizontal = rowObject.AddComponent<HorizontalLayoutGroup>();
            }
            horizontal.enabled = true;
            horizontal.childAlignment = TextAnchor.MiddleCenter;
            horizontal.childControlWidth = true;
            horizontal.childControlHeight = true;
            horizontal.childForceExpandWidth = false;
            horizontal.childForceExpandHeight = true;
            horizontal.spacing = columnSpacing;
            horizontal.padding = new RectOffset(8, 8, 2, 2);
        }

        return new RowView
        {
            root = rect,
            rankText = FindOrCreateCell(rowObject.transform, "Rank", header ? mutedTextColor : accentTextColor, fontSize, 42f, 0f, preserveExistingHeaderCells),
            idText = FindOrCreateCell(rowObject.transform, "PlaneId", textColor, fontSize, 112f, 0f, preserveExistingHeaderCells),
            distanceText = FindOrCreateCell(rowObject.transform, "DistanceDelta", textColor, fontSize, 86f, 0f, preserveExistingHeaderCells),
            altitudeText = FindOrCreateCell(rowObject.transform, "AltitudeDelta", textColor, fontSize, 78f, 0f, preserveExistingHeaderCells),
            headingText = FindOrCreateCell(rowObject.transform, "HeadingDelta", textColor, fontSize, 86f, 0f, preserveExistingHeaderCells),
            pitchText = FindOrCreateCell(rowObject.transform, "PitchDelta", textColor, fontSize, 82f, 0f, preserveExistingHeaderCells),
        };
    }

    private Text FindOrCreateCell(Transform parent, string name, Color color, int fontSize, float width, float flexibleWidth, bool preserveExisting)
    {
        Transform existing = parent.Find(name);
        if (existing != null && existing.TryGetComponent(out Text existingText))
        {
            if (!preserveExisting)
            {
                ConfigureCell(existingText, name, color, fontSize, width, flexibleWidth);
            }
            return existingText;
        }

        return CreateCell(parent, name, color, fontSize, width, flexibleWidth);
    }

    private Text CreateCell(Transform parent, string name, Color color, int fontSize, float width, float flexibleWidth)
    {
        var cellObject = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        cellObject.transform.SetParent(parent, false);

        var text = cellObject.GetComponent<Text>();
        ConfigureCell(text, name, color, fontSize, width, flexibleWidth);
        return text;
    }

    private void ConfigureCell(Text text, string name, Color color, int fontSize, float width, float flexibleWidth)
    {
        text.font = font;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;

        var layoutElement = text.GetComponent<LayoutElement>();
        if (layoutElement == null)
        {
            layoutElement = text.gameObject.AddComponent<LayoutElement>();
        }
        layoutElement.minWidth = width;
        layoutElement.preferredWidth = width;
        layoutElement.flexibleWidth = flexibleWidth;
    }

    private void SetRowTexts(RowView row, string rank, string planeId, string distance, string altitude, string heading, string pitch)
    {
        if (row == null)
        {
            return;
        }

        if (row.rankText != null) row.rankText.text = rank;
        if (row.idText != null) row.idText.text = planeId;
        if (row.distanceText != null) row.distanceText.text = distance;
        if (row.altitudeText != null) row.altitudeText.text = altitude;
        if (row.headingText != null) row.headingText.text = heading;
        if (row.pitchText != null) row.pitchText.text = pitch;
    }
}
