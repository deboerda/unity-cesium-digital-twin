#pragma warning disable CS0414
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 数据规则配置面板 —按飞机编号分组显示飞行数据变量及规则检查。/// "全部飞机"模式：参照SensorPanel，按飞机分组，每组有分组标题。/// 单个飞机模式：参照AlarmPanel，平铺显示，首列为飞机编号。/// 添加条目：InputField 输入变量名，自动补全提示，无效变量名则提示失败。/// 规则列：InputField 可编辑（播放前），播放后只读。/// </summary>
public class DataRuleConfigPanel : MonoBehaviour
{
    private enum FieldType { Numeric, String }

    private class FieldDef
    {
        public string key;        // 协议 JSON key（唯一合法变量名）
        public string displayName; // 展示名（DATA_FIELDS 别名里的中文，兜底用 key）
        public FieldType type;
        public bool common;       // 常用变量，排前面
        public int order;         // 原始顺序（稳定排序用）
    }

    // 收到 DATA_FIELDS 之前使用的内置兜底变量表（与协议 JSON key 一致）。
    // 收到 DATA_FIELDS 后以数据端发来的 JSON key 为准（别名仅用于展示/搜索）。
    private static readonly FieldDef[] DefaultFields =
    {
        new FieldDef { key = "trackId",        displayName = "飞机编号",    type = FieldType.String  },
        new FieldDef { key = "planId",         displayName = "计划号",      type = FieldType.String  },
        new FieldDef { key = "flightTime",     displayName = "飞行时间",    type = FieldType.String  },
        new FieldDef { key = "lng",            displayName = "经度",        type = FieldType.Numeric },
        new FieldDef { key = "lat",            displayName = "纬度",        type = FieldType.Numeric },
        new FieldDef { key = "alt",            displayName = "高度",        type = FieldType.Numeric, common = true },
        new FieldDef { key = "heading",        displayName = "航向角(度)",  type = FieldType.Numeric, common = true },
        new FieldDef { key = "pitch",          displayName = "俯仰角(度)",  type = FieldType.Numeric, common = true },
        new FieldDef { key = "roll",           displayName = "横滚角(度)",  type = FieldType.Numeric, common = true },
        new FieldDef { key = "speed",          displayName = "速度(米/秒)", type = FieldType.Numeric, common = true },
        new FieldDef { key = "battery",        displayName = "电量(%)",     type = FieldType.Numeric, common = true },
        new FieldDef { key = "state",          displayName = "飞行状态",    type = FieldType.String,  common = true },
        new FieldDef { key = "alarm",          displayName = "告警信息",    type = FieldType.String,  common = true },
        new FieldDef { key = "detector_type",  displayName = "探测器类型",  type = FieldType.String  },
        new FieldDef { key = "range_type",     displayName = "范围类型",    type = FieldType.String  },
        new FieldDef { key = "detect_range",   displayName = "探测范围",    type = FieldType.Numeric },
        new FieldDef { key = "yaw_angle",      displayName = "扫描中心(度)", type = FieldType.Numeric },
        new FieldDef { key = "pitch_angle",    displayName = "俯仰中心(度)", type = FieldType.Numeric },
        new FieldDef { key = "yaw_scan_angle", displayName = "扫描方位(度)", type = FieldType.Numeric },
        new FieldDef { key = "pitch_scan_angle", displayName = "俯仰方位(度)", type = FieldType.Numeric },
        new FieldDef { key = "center_point",   displayName = "中心点",      type = FieldType.String  },
        new FieldDef { key = "center_point1",  displayName = "传感器位置",  type = FieldType.String  },
        new FieldDef { key = "radar_status",   displayName = "探测器状态",  type = FieldType.String  },
        new FieldDef { key = "cycleId",        displayName = "周期号",      type = FieldType.Numeric },
    };

    // 旧内部字段名 → 协议 JSON key（迁移历史配置用）
    private static readonly Dictionary<string, string> LegacyKeyMap = new Dictionary<string, string>
    {
        { "detectRange",  "detect_range"  },
        { "yawAngle",     "yaw_angle"     },
        { "pitchAngle",   "pitch_angle"   },
        { "detectorType", "detector_type" },
        { "rangeType",    "range_type"    },
        { "centerPoint",  "center_point"  },
        { "radarStatus",  "radar_status"  },
    };

    // 动态变量字典：来自 DATA_FIELDS 的 JSON key（未收到时用 DefaultFields 兜底）
    private readonly List<FieldDef> fieldDict = new List<FieldDef>();
    private int fieldDictSourceVersion = -1;

    private static readonly Color[] MarkerPalette =
    {
        new Color(1f, 0.22f, 0.22f, 1f),
        new Color(1f, 0.55f, 0.12f, 1f),
        new Color(1f, 0.85f, 0.15f, 1f),
        new Color(0.35f, 1f, 0.4f, 1f),
        new Color(0.2f, 0.85f, 1f, 1f),
        new Color(0.45f, 0.5f, 1f, 1f),
        new Color(0.9f, 0.35f, 1f, 1f),
        new Color(1f, 1f, 1f, 1f),
    };

    private static Color MarkerColorFor(string fieldKey)
    {
        switch (fieldKey)
        {
            case "alt": return MarkerPalette[0];
            case "heading": return MarkerPalette[1];
            case "pitch": return MarkerPalette[2];
            case "roll": return MarkerPalette[3];
            case "speed": return MarkerPalette[4];
            case "battery": return MarkerPalette[5];
            default: return MarkerPalette[0];
        }
    }

    private static Color EnsureMarkerColor(Color color, string fieldKey)
    {
        if (color.a < 0.01f)
            return MarkerColorFor(fieldKey);
        return color;
    }

    [Serializable]
    private class TrackRuleEntry
    {
        public string fieldKey;
        public string rule;
        public bool hideMarker;
        public Color markerColor;
    }

    private class RowView
    {
        public RectTransform root;
        public Text rankText;
        public Text trackIdText;
        public Text varText; // Also serves as InputField.textComponent for editable rows
        public Text valueText;
        public InputField ruleInput;
        public Text statusText;
        public Image colorSwatch;
        public Button colorButton;
        public Button showButton;
        public Text showButtonLabel;
        public Button deleteButton;
        public Text deleteButtonLabel;
        public string boundTrackId;
        public string boundFieldKey;
    }

    [Header("Data")]
    [SerializeField] private FlightDataStreamReceiver receiver;
    [SerializeField] private bool autoFindReceiver = true;
    [SerializeField] private float refreshInterval = 0.2f;
    [SerializeField] private bool enableInEditor = true; // Run Refresh in edit mode too

    [Header("Playback State")]
    [SerializeField] private bool isReadOnly = false;

    [Header("Default Rules")]
    [SerializeField] private string altRule = ">0";
    [SerializeField] private string headingRule = "0~360";
    [SerializeField] private string pitchRule = "-90~90";
    [SerializeField] private string rollRule = "-180~180";
    [SerializeField] private string speedRule = ">=0";
    [SerializeField] private string batteryRule = "0~100";

    [Header("Layout")]
    [SerializeField] private Dropdown planeDropdown;
    [SerializeField] private RectTransform selectorRoot;
    [SerializeField] private RectTransform headerRoot;
    [SerializeField] private RectTransform contentRoot;
    [SerializeField] private RectTransform rowTemplate;
    [Tooltip("标题和数据列使用 Scene 里拖好的坐标；运行时只改文字。")]
    [SerializeField] private bool preserveSceneLayout = true;
    [SerializeField] private RectTransform addButtonRoot;
    [SerializeField] private RectTransform inputBarRoot;
    [SerializeField] private bool autoCreateLayout = true;
    [SerializeField] private float selectorHeight = 34f;
    [SerializeField] private float headerHeight = 32f;
    [SerializeField] private float footerHeight = 36f;
    [SerializeField] private float rowHeight = 30f;
    [SerializeField] private float groupHeaderHeight = 28f;
    [SerializeField] private float scrollbarWidth = 14f;
    [SerializeField] private float columnSpacing = 4f;
    [SerializeField] private int maxRows = 128;

    [Header("Style")]
    [SerializeField] private Font font;
    [SerializeField] private int headerFontSize = 13;
    [SerializeField] private int rowFontSize = 13;
    [SerializeField] private int groupFontSize = 14;
    [SerializeField] private Color panelBackground = new Color(0.06f, 0.08f, 0.1f, 0.7f);
    [SerializeField] private Color headerBackground = new Color(0.08f, 0.1f, 0.13f, 0.95f);
    [SerializeField] private Color oddRowBackground = new Color(0.12f, 0.14f, 0.18f, 0.82f);
    [SerializeField] private Color evenRowBackground = new Color(0.16f, 0.18f, 0.22f, 0.82f);
    [SerializeField] private Color groupBackground = new Color(0.15f, 0.2f, 0.28f, 0.95f);
    [SerializeField] private Color textColor = new Color(0.92f, 0.96f, 1f, 1f);
    [SerializeField] private Color mutedTextColor = new Color(0.66f, 0.74f, 0.82f, 1f);
    [SerializeField] private Color accentTextColor = new Color(0.43f, 0.82f, 1f, 1f);
    [SerializeField] private Color groupTextColor = new Color(0.55f, 0.85f, 1f, 1f);
    [SerializeField] private Color normalColor = new Color(0.35f, 1f, 0.5f, 1f);
    [SerializeField] private Color violationColor = new Color(1f, 0.3f, 0.3f, 1f);
    [SerializeField] private Color warningColor = new Color(1f, 0.78f, 0f, 1f);
    [SerializeField] private Color addButtonColor = new Color(0.15f, 0.45f, 0.75f, 0.95f);
    [SerializeField] private Color inputBgColor = new Color(0.08f, 0.1f, 0.13f, 0.95f);
    [SerializeField] private Color errorColor = new Color(1f, 0.3f, 0.3f, 1f);
    [SerializeField] private Color selectedRowBackground = new Color(0.18f, 0.42f, 0.62f, 0.95f);

    private readonly List<RowView> rows = new List<RowView>();
    private readonly List<string> dropdownTrackIds = new List<string>();
    private const string AllPlanesOption = "__ALL__";
    private RowView headerRow;
    private float nextRefreshTime;
    private string selectedTrackId = AllPlanesOption;
    private string addTargetTrackId;
    private string selectedRowTrackId;
    private string selectedRowFieldKey;
    private bool batchMode;
    private Toggle batchToggle;
    private InputField addVarInput; // kept for compatibility but unused in new flow
    private Text addVarHint;
    private float hintClearTime;
    private FlightProgressController cachedProgressController;
    private readonly HashSet<string> activeViolations = new HashSet<string>();

    // Serialized storage for trackEntries (survives Play Mode enter/exit)
    [Serializable]
    private class SerializableTrackEntry
    {
        public string trackId;
        public List<TrackRuleEntry> entries;
    }

    [SerializeField] private List<SerializableTrackEntry> serializedTrackEntries = new List<SerializableTrackEntry>();

    private readonly Dictionary<string, List<TrackRuleEntry>> trackEntries = new Dictionary<string, List<TrackRuleEntry>>();

    private void SyncSerializedToDict()
    {
        // Only add entries from serialized that aren't already in dict (don't clear runtime state)
        foreach (var ste in serializedTrackEntries)
        {
            if (!string.IsNullOrEmpty(ste.trackId) && ste.entries != null && !trackEntries.ContainsKey(ste.trackId))
                trackEntries[ste.trackId] = new List<TrackRuleEntry>(ste.entries);
        }
        LoadPersistentRules();
    }

    private void SyncDictToSerialized()
    {
        serializedTrackEntries.Clear();
        foreach (var kv in trackEntries)
        {
            serializedTrackEntries.Add(new SerializableTrackEntry
            {
                trackId = kv.Key,
                entries = new List<TrackRuleEntry>(kv.Value)
            });
        }
        SavePersistentRules();
    }

    private bool persistentRulesLoaded;

    private void LoadPersistentRules()
    {
        if (persistentRulesLoaded)
            return;
        persistentRulesLoaded = true;
        var store = PanelMemory.Load();
        if (store?.tracks == null)
            return;
        foreach (var track in store.tracks)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.trackId) || track.entries == null || track.entries.Count == 0)
                continue;
            var list = new List<TrackRuleEntry>();
            foreach (var item in track.entries)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.fieldKey))
                    continue;
                list.Add(new TrackRuleEntry
                {
                    fieldKey = item.fieldKey,
                    rule = item.rule ?? "",
                    hideMarker = item.hideMarker,
                    markerColor = item.color != null ? item.color.ToColor() : MarkerColorFor(item.fieldKey),
                });
            }
            if (list.Count > 0)
                trackEntries[track.trackId] = list;
        }
    }

    private void SavePersistentRules()
    {
        var store = PanelMemory.Load();
        store.tracks = new List<PanelMemory.TrackRules>();
        foreach (var kv in trackEntries)
        {
            var track = new PanelMemory.TrackRules { trackId = kv.Key };
            if (kv.Value != null)
            {
                foreach (var e in kv.Value)
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.fieldKey))
                        continue;
                    Color c = EnsureMarkerColor(e.markerColor, e.fieldKey);
                    track.entries.Add(new PanelMemory.RuleItem
                    {
                        fieldKey = e.fieldKey,
                        rule = e.rule ?? "",
                        hideMarker = e.hideMarker,
                        color = PanelMemory.Color4.From(c),
                    });
                }
            }
            store.tracks.Add(track);
        }
        PanelMemory.Save(store);
    }

    private List<TrackRuleEntry> GetDefaultEntries()
    {
        return new List<TrackRuleEntry>
        {
            new TrackRuleEntry { fieldKey = "alt",      rule = altRule,     markerColor = MarkerColorFor("alt") },
            new TrackRuleEntry { fieldKey = "heading",  rule = headingRule, markerColor = MarkerColorFor("heading") },
            new TrackRuleEntry { fieldKey = "pitch",    rule = pitchRule,   markerColor = MarkerColorFor("pitch") },
            new TrackRuleEntry { fieldKey = "roll",     rule = rollRule,    markerColor = MarkerColorFor("roll") },
            new TrackRuleEntry { fieldKey = "speed",    rule = speedRule,   markerColor = MarkerColorFor("speed") },
            new TrackRuleEntry { fieldKey = "battery",  rule = batteryRule, markerColor = MarkerColorFor("battery") },
        };
    }

    private List<TrackRuleEntry> GetEntries(string trackId)
    {
        if (!trackEntries.TryGetValue(trackId, out var entries))
        {
            entries = GetDefaultEntries();
            trackEntries[trackId] = entries;
        }
        return entries;
    }

    private bool IsAllPlanesMode => selectedTrackId == AllPlanesOption;

    // ── Variable dictionary (driven by DATA_FIELDS json keys) ──

    private void EnsureFieldDict()
    {
        int version = receiver != null ? receiver.DataFieldsVersion : -1;
        if (fieldDict.Count > 0 && version == fieldDictSourceVersion)
            return;

        fieldDictSourceVersion = version;
        fieldDict.Clear();

        int order = 0;
        var remote = receiver != null ? receiver.DataFields : null;
        if (remote != null && remote.Count > 0)
        {
            foreach (var kv in remote)
            {
                string key = kv.Key;
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                // DATA_FIELDS 的 comment 里第一个词就是给界面看的列名
                string display = receiver != null ? receiver.GetFieldDisplayName(key) : null;
                if (string.IsNullOrWhiteSpace(display))
                {
                    display = key;
                    foreach (var alias in kv.Value)
                    {
                        if (!string.IsNullOrWhiteSpace(alias) && !alias.Equals(key, StringComparison.Ordinal))
                        {
                            display = alias;
                            break;
                        }
                    }
                }
                fieldDict.Add(new FieldDef
                {
                    key = key,
                    displayName = display,
                    type = IsNumericJsonKey(key) ? FieldType.Numeric : FieldType.String,
                    common = CommonVariableKeys.Contains(key),
                    order = order++,
                });
            }
        }
        else
        {
            foreach (var d in DefaultFields)
                fieldDict.Add(new FieldDef { key = d.key, displayName = d.displayName, type = d.type, common = d.common, order = order++ });
        }

        // 常用变量排前面，组内保持收到顺序（稳定）
        fieldDict.Sort((a, b) =>
        {
            int c = b.common.CompareTo(a.common);
            return c != 0 ? c : a.order.CompareTo(b.order);
        });
    }

    private static readonly HashSet<string> CommonVariableKeys = new HashSet<string>
    {
        "alt", "hight", "heading", "pitch", "roll", "speed", "battery", "state", "alarm", "lng", "lat", "flightTime",
    };

    private static bool IsNumericJsonKey(string key)
    {
        switch (key)
        {
            case "lng": case "lat": case "alt": case "hight": case "height":
            case "heading": case "pitch":
            case "roll": case "speed": case "battery": case "detect_range":
            case "yaw_angle": case "pitch_angle": case "yaw_scan_angle":
            case "pitch_scan_angle": case "cycleId":
                return true;
            default: return false;
        }
    }

    private List<string> GetFieldAliases(string key)
    {
        var remote = receiver != null ? receiver.DataFields : null;
        if (remote != null && remote.TryGetValue(key, out var aliases))
            return aliases;
        return null;
    }

    // ── Value extraction ───────────────────────────────────────

    private List<FlightDataStreamReceiver.CycleTableRecord> currentCycleRecords;
    private string currentCycleTrackId;

    private string GetStringValue(FlightDataStreamReceiver.TrackPosition pos,
        FlightDataStreamReceiver.TrackTimetableState state,
        string key)
    {
        switch (key)
        {
            case "lng":              return LiveText(key) ?? pos?.lng.ToString("F6") ?? "-";
            case "lat":              return LiveText(key) ?? pos?.lat.ToString("F6") ?? "-";
            case "alt":
            case "hight":
            case "height":
            {
                if (receiver != null && !string.IsNullOrWhiteSpace(currentCycleTrackId) &&
                    receiver.TryGetFieldValue(currentCycleTrackId, key, out string hightText) &&
                    !string.IsNullOrWhiteSpace(hightText))
                    return hightText;
                return pos?.alt.ToString("F1") ?? "-";
            }
            case "heading":          return LiveText(key) ?? pos?.heading.ToString("F1") ?? "-";
            case "pitch":            return LiveText(key) ?? pos?.pitch.ToString("F1") ?? "-";
            case "roll":             return LiveText(key) ?? pos?.roll.ToString("F1") ?? "-";
            case "speed":            return LiveText(key) ?? state?.speed.ToString("F1") ?? "-";
            case "battery":          return LiveText(key) ?? state?.battery.ToString("F0") ?? "-";
            case "detect_range":     return state?.detectRange.ToString("F0") ?? "-";
            case "yaw_angle":        return state?.yawAngle.ToString("F1") ?? "-";
            case "pitch_angle":      return state?.pitchAngle.ToString("F1") ?? "-";
            case "yaw_scan_angle":   return state?.yawScanAngle.ToString("F1") ?? "-";
            case "pitch_scan_angle": return state?.pitchScanAngle.ToString("F1") ?? "-";
            case "cycleId":          return GetCycleIdText();
            case "state":            return state?.state ?? "-";
            case "alarm":            return state?.alarm ?? "-";
            case "detector_type":    return state?.detectorType ?? "-";
            case "range_type":       return state?.rangeType ?? "-";
            case "center_point":     return state?.centerPoint ?? "-";
            case "center_point1":    return state?.centerPoint1 ?? "-";
            case "radar_status":     return state?.radarStatus ?? "-";
            case "flightTime":       return pos?.flightTime ?? "-";
            case "planId":           return pos?.planId ?? "-";
            case "trackId":          return pos?.trackId ?? state?.trackId ?? "-";
            default:
                if (receiver != null && !string.IsNullOrWhiteSpace(currentCycleTrackId) &&
                    receiver.TryGetFieldValue(currentCycleTrackId, key, out string live) &&
                    !string.IsNullOrWhiteSpace(live))
                    return live;
                return "-";
        }
    }

    private string GetCycleIdText()
    {
        var rec = currentCycleRecords?.Find(r => r != null && r.trackId == currentCycleTrackId);
        return rec != null ? rec.cycleId.ToString() : "-";
    }

    private string LiveText(string key)
    {
        if (receiver == null || string.IsNullOrWhiteSpace(currentCycleTrackId) || string.IsNullOrWhiteSpace(key))
            return null;
        return receiver.TryGetFieldValue(currentCycleTrackId, key, out string value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private float? LiveFloat(string key)
    {
        if (receiver == null || string.IsNullOrWhiteSpace(currentCycleTrackId) || string.IsNullOrWhiteSpace(key))
            return null;
        return receiver.TryGetFieldFloat(currentCycleTrackId, key);
    }

    private float? GetNumericValue(FlightDataStreamReceiver.TrackPosition pos,
        FlightDataStreamReceiver.TrackTimetableState state, string key)
    {
        switch (key)
        {
            case "lng":              return LiveFloat(key) ?? (pos != null ? (float)pos.lng : (float?)null);
            case "lat":              return LiveFloat(key) ?? (pos != null ? (float)pos.lat : (float?)null);
            case "alt":              return LiveFloat("hight") ?? LiveFloat("alt") ?? pos?.alt;
            case "heading":          return LiveFloat(key) ?? pos?.heading;
            case "pitch":            return LiveFloat(key) ?? pos?.pitch;
            case "roll":             return LiveFloat(key) ?? pos?.roll;
            case "speed":            return LiveFloat(key) ?? state?.speed;
            case "battery":          return LiveFloat(key) ?? state?.battery;
            case "detect_range":     return state?.detectRange;
            case "yaw_angle":        return state?.yawAngle;
            case "pitch_angle":      return state?.pitchAngle;
            case "yaw_scan_angle":   return state?.yawScanAngle;
            case "pitch_scan_angle": return state?.pitchScanAngle;
            case "hight":
            case "height":
                return receiver != null && !string.IsNullOrWhiteSpace(currentCycleTrackId)
                    ? receiver.TryGetFieldFloat(currentCycleTrackId, key) ?? pos?.alt
                    : pos?.alt;
            default:
                return receiver != null && !string.IsNullOrWhiteSpace(currentCycleTrackId)
                    ? receiver.TryGetFieldFloat(currentCycleTrackId, key)
                    : null;
        }
    }

    private static bool IsNumericField(string key)
    {
        return IsNumericJsonKey(key) && key != "cycleId";
    }

    private string GetFieldDisplayName(string key)
    {
        EnsureFieldDict();
        foreach (var f in fieldDict)
            if (f.key == key) return f.displayName;
        return key;
    }

    /// <summary>把用户输入解析成合法 JSON key；匹配 key/展示名/别名（不区分大小写），旧内部名自动迁移。</summary>
    private string FindFieldKey(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        if (LegacyKeyMap.TryGetValue(input, out var migrated))
            input = migrated;

        EnsureFieldDict();
        foreach (var f in fieldDict)
            if (f.key.Equals(input, StringComparison.OrdinalIgnoreCase)) return f.key;
        foreach (var f in fieldDict)
            if (f.displayName.Equals(input, StringComparison.OrdinalIgnoreCase)) return f.key;
        foreach (var f in fieldDict)
        {
            var aliases = GetFieldAliases(f.key);
            if (aliases == null) continue;
            foreach (var alias in aliases)
                if (!string.IsNullOrWhiteSpace(alias) && alias.Equals(input, StringComparison.OrdinalIgnoreCase))
                    return f.key;
        }
        return null;
    }

    // ── Rule checking ──────────────────────────────────────────

    private enum RuleStatus { Pass, Fail, Invalid, Empty }

    private static bool TryParseFloat(string text, out float value)
    {
        text = (text ?? string.Empty).Trim().Replace("，", ".");
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private string NormalizeRule(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return string.Empty;
        rule = rule.Trim();
        rule = rule.Replace("＝", "=").Replace("～", "~").Replace("＞", ">").Replace("＜", "<");
        rule = rule.Replace("==", "=");
        // Allow "高度=0" / "alt=0" in the rule cell by stripping a leading field name.
        EnsureFieldDict();
        foreach (var field in fieldDict)
        {
            if (rule.StartsWith(field.displayName, StringComparison.OrdinalIgnoreCase))
            {
                rule = rule.Substring(field.displayName.Length).Trim();
                break;
            }
            if (rule.StartsWith(field.key, StringComparison.OrdinalIgnoreCase))
            {
                rule = rule.Substring(field.key.Length).Trim();
                break;
            }
        }
        return rule;
    }

    private RuleStatus EvaluateRule(float value, string rule)
    {
        rule = NormalizeRule(rule);
        if (string.IsNullOrWhiteSpace(rule))
            return RuleStatus.Empty;

        if (rule.Contains("~"))
        {
            var parts = rule.Split('~');
            if (parts.Length == 2 && TryParseFloat(parts[0], out float min) && TryParseFloat(parts[1], out float max))
                return value >= min && value <= max ? RuleStatus.Pass : RuleStatus.Fail;
            return RuleStatus.Invalid;
        }

        if (rule.StartsWith(">=") && TryParseFloat(rule.Substring(2), out float ge))
            return value >= ge ? RuleStatus.Pass : RuleStatus.Fail;
        if (rule.StartsWith("<=") && TryParseFloat(rule.Substring(2), out float le))
            return value <= le ? RuleStatus.Pass : RuleStatus.Fail;
        if (rule.StartsWith("!=") && TryParseFloat(rule.Substring(2), out float ne))
            return !Mathf.Approximately(value, ne) ? RuleStatus.Pass : RuleStatus.Fail;
        if (rule.StartsWith(">") && TryParseFloat(rule.Substring(1), out float gt))
            return value > gt ? RuleStatus.Pass : RuleStatus.Fail;
        if (rule.StartsWith("<") && TryParseFloat(rule.Substring(1), out float lt))
            return value < lt ? RuleStatus.Pass : RuleStatus.Fail;
        if (rule.StartsWith("=") && TryParseFloat(rule.Substring(1), out float eqPrefixed))
            return Mathf.Approximately(value, eqPrefixed) ? RuleStatus.Pass : RuleStatus.Fail;
        if (TryParseFloat(rule, out float eq))
            return Mathf.Approximately(value, eq) ? RuleStatus.Pass : RuleStatus.Fail;
        return RuleStatus.Invalid;
    }

    // ── Lifecycle ───────────────────────────────────────────────

    private void Awake()
    {
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        SyncSerializedToDict(); // Restore user-added entries from serialized storage
        if (autoCreateLayout && !preserveSceneLayout) EnsureLayoutObjects();
        if (planeDropdown != null)
        {
            planeDropdown.onValueChanged.RemoveListener(OnPlaneDropdownChanged);
            planeDropdown.onValueChanged.AddListener(OnPlaneDropdownChanged);
        }
        if (!preserveSceneLayout)
        {
            EnsureVerticalLayout(headerRoot, false);
            EnsureVerticalLayout(contentRoot, true);
        }
        if (headerRoot != null)
        {
            headerRow = FindOrCreateHeaderRow();
            SetHeaderTexts(headerRow);
        }
        CollectSceneDataRows();
        BindAddButton();
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
            SavePersistentRules();
    }

    private void OnApplicationQuit()
    {
        SavePersistentRules();
    }

    private void CollectSceneDataRows()
    {
        if (rowTemplate == null)
            rowTemplate = PanelSceneLayout.FindRowTemplate(contentRoot, rowTemplate);
    }

    private void OnEnable()
    {
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        SyncSerializedToDict(); // Restore entries on every enable (panel switch)
        if (autoCreateLayout && !preserveSceneLayout) EnsureLayoutObjects();
        if (planeDropdown != null)
        {
            planeDropdown.onValueChanged.RemoveListener(OnPlaneDropdownChanged);
            planeDropdown.onValueChanged.AddListener(OnPlaneDropdownChanged);
        }
        if (!preserveSceneLayout)
        {
            EnsureVerticalLayout(headerRoot, false);
            EnsureVerticalLayout(contentRoot, true);
        }
        if (headerRoot != null)
        {
            headerRow = FindOrCreateHeaderRow();
            SetHeaderTexts(headerRow);
        }
        BindAddButton();
        nextRefreshTime = 0f;
        Refresh(); // Force immediate refresh so data shows right away
    }

    private void Update()
    {
        if (!Application.isPlaying)
            return;

        // Clear hint after delay
        if (addVarHint != null && addVarHint.gameObject.activeSelf && Time.unscaledTime > hintClearTime)
            addVarHint.gameObject.SetActive(false);

        if (Time.unscaledTime < nextRefreshTime) return;
        nextRefreshTime = Time.unscaledTime + Mathf.Max(0.02f, refreshInterval);

        Refresh();
    }

    public void SetReadOnly(bool readOnly)
    {
        isReadOnly = readOnly;
        Refresh(); // Force immediate refresh when read-only state changes
    }

    // ── Refresh ────────────────────────────────────────────────

    private void Refresh()
    {
        // Always prefer the live singleton when autoFindReceiver is enabled,
        // even if the serialized field was previously set (it may be stale after domain reload)
        if (autoFindReceiver)
        {
            var inst = FlightDataStreamReceiver.Instance;
            if (inst != null) receiver = inst;
        }

        var positions = receiver?.SnapshotPositions;
        var states = receiver?.SnapshotStates;
        currentCycleRecords = receiver?.SnapshotCycleRecords;
        EnsureFieldDict();
        if (positions != null || states != null)
            UpdatePlaneDropdown(positions, states);

        // Build trackId list from receiver data AND from configured entries
        var trackIds = new List<string>();
        if (positions != null) foreach (var p in positions) if (p != null && !string.IsNullOrWhiteSpace(p.trackId) && !trackIds.Contains(p.trackId)) trackIds.Add(p.trackId);
        if (states != null) foreach (var s in states) if (s != null && !string.IsNullOrWhiteSpace(s.trackId) && !trackIds.Contains(s.trackId)) trackIds.Add(s.trackId);

        // Ensure every real trackId has at least default entries
        foreach (string realId in trackIds)
        {
            if (!trackEntries.ContainsKey(realId))
            {
                trackEntries[realId] = GetDefaultEntries();
                SyncDictToSerialized();
            }
        }

        // Migrate "默认飞机" entries to ALL real trackIds if data is available
        if (trackEntries.ContainsKey("默认飞机") && trackIds.Count > 0)
        {
            var sourceEntries = trackEntries["默认飞机"];
            bool migrated = false;
            foreach (string realId in trackIds)
            {
                if (!trackEntries.ContainsKey(realId))
                {
                    // Create fresh copy of source entries
                    var migratedEntries = new List<TrackRuleEntry>();
                    foreach (var e in sourceEntries)
                        migratedEntries.Add(new TrackRuleEntry { fieldKey = e.fieldKey, rule = e.rule, hideMarker = e.hideMarker, markerColor = e.markerColor });
                    trackEntries[realId] = migratedEntries;
                    migrated = true;
                }
                else
                {
                    // Merge any user-added entries from "默认飞机" that are missing in realId
                    var realEntries = trackEntries[realId];
                    var realKeys = new HashSet<string>();
                    foreach (var e in realEntries) realKeys.Add(e.fieldKey);
                    foreach (var e in sourceEntries)
                    {
                        if (!realKeys.Contains(e.fieldKey))
                        {
                            realEntries.Add(new TrackRuleEntry { fieldKey = e.fieldKey, rule = e.rule, hideMarker = e.hideMarker, markerColor = e.markerColor });
                            migrated = true;
                        }
                    }
                }
            }
            if (migrated)
            {
                trackEntries.Remove("默认飞机");
                SyncDictToSerialized();
            }
        }

        // Remove "默认飞机" from display list if real data exists
        if (trackIds.Count > 0)
            trackIds.RemoveAll(id => id == "默认飞机");

        // Also include trackIds from configured entries (so they show even without live data)
        foreach (var kv in trackEntries)
            if (!trackIds.Contains(kv.Key)) trackIds.Add(kv.Key);
        trackIds.Sort(StringComparer.Ordinal);

        if (!IsAllPlanesMode) trackIds = trackIds.FindAll(id => id == selectedTrackId);

        var displayItems = new List<DisplayItem>();
        foreach (var trackId in trackIds)
        {
            if (IsAllPlanesMode)
                displayItems.Add(new DisplayItem { type = DisplayType.GroupHeader, trackId = trackId });

            var pos = positions?.Find(p => p != null && p.trackId == trackId);
            var state = states?.Find(s => s != null && s.trackId == trackId);
            bool hasLiveData = pos != null || state != null;
            var entries = GetEntries(trackId);
            currentCycleTrackId = trackId;

            for (int i = 0; i < entries.Count; i++)
            {
                // Read back user-edited values from InputFields before refreshing
                SyncEntryFromRow(entries[i], trackId);
            }
            SyncDictToSerialized(); // Persist user edits

            for (int i = 0; i < entries.Count; i++)
            {
                bool known = !string.IsNullOrWhiteSpace(entries[i].fieldKey)
                             && FindFieldKey(entries[i].fieldKey) != null;
                string key = FindFieldKey(entries[i].fieldKey) ?? entries[i].fieldKey;
                string rule = entries[i].rule;
                bool isNum = IsNumericField(key);
                displayItems.Add(new DisplayItem
                {
                    type = DisplayType.DataRow, rank = i + 1, trackId = trackId,
                    fieldKey = key, displayName = GetFieldDisplayName(key),
                    strValue = GetStringValue(pos, state, key),
                    numValue = isNum ? GetNumericValue(pos, state, key) : null,
                    isNumeric = isNum, rule = rule,
                    hasLiveData = hasLiveData,
                    hideMarker = entries[i].hideMarker,
                    markerColor = EnsureMarkerColor(entries[i].markerColor, key),
                    fieldKnown = known,
                });
            }
        }

        EnsureRowCount(displayItems.Count);
        SetActiveRowCount(displayItems.Count);

        for (int i = 0; i < displayItems.Count; i++)
        {
            var item = displayItems[i];
            if (item.type == DisplayType.GroupHeader)
                ConfigureGroupRow(rows[i], item.trackId);
            else
                ConfigureDataRow(rows[i], item.rank, item.trackId, item.fieldKey, item.displayName, item.strValue, item.numValue, item.isNumeric, item.rule, item.hasLiveData, item.hideMarker, item.markerColor, item.fieldKnown);
        }
    }

    // ── Sync user input back to entries before Refresh overwrites ──

    private void SyncEntryFromRow(TrackRuleEntry entry, string trackId)
    {
        string wantKey = FindFieldKey(entry.fieldKey) ?? entry.fieldKey;
        if (string.IsNullOrEmpty(wantKey))
            return;

        // 只回写同一变量的行。不能按行号对齐：分组标题也占一行，新添加的变量会对到上一行（截图里反复变成电量）。
        RowView matched = null;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null || row.root == null || !row.root.gameObject.activeSelf)
                continue;
            if (string.IsNullOrEmpty(row.boundFieldKey))
                continue;
            if (row.boundTrackId != trackId && !row.root.name.StartsWith($"Row_{trackId}_"))
                continue;

            string rowKey = FindFieldKey(row.boundFieldKey) ?? row.boundFieldKey;
            if (rowKey == wantKey)
            {
                matched = row;
                break;
            }
        }

        if (matched == null)
            return;

        if (matched.varText != null)
        {
            var varInput = matched.varText.GetComponent<InputField>();
            if (varInput != null && varInput.isFocused)
            {
                string matchedKey = FindFieldKey(varInput.text);
                if (matchedKey != null)
                    entry.fieldKey = matchedKey;
            }
        }
        if (matched.ruleInput != null && matched.ruleInput.isFocused)
            entry.rule = matched.ruleInput.text;
        if (matched.colorSwatch != null)
            entry.markerColor = matched.colorSwatch.color;
        if (matched.showButtonLabel != null)
            entry.hideMarker = matched.showButtonLabel.text == "隐";
    }

    private enum DisplayType { GroupHeader, DataRow }
    private class DisplayItem
    {
        public DisplayType type;
        public string trackId;
        public int rank;
        public string fieldKey;
        public string displayName;
        public string strValue;
        public float? numValue;
        public bool isNumeric;
        public string rule;
        public bool hasLiveData;
        public bool hideMarker;
        public Color markerColor;
        public bool fieldKnown;
    }

    // ── Row configuration ───────────────────────────────────────

    private void ConfigureGroupRow(RowView row, string trackId)
    {
        if (row == null) return;
        var img = row.root.GetComponent<Image>(); if (img != null) img.color = groupBackground;
        var le = row.root.GetComponent<LayoutElement>(); if (le != null) le.preferredHeight = groupHeaderHeight;
        if (row.rankText != null) row.rankText.text = "";
        if (row.trackIdText != null)
        {
            row.trackIdText.text = trackId; row.trackIdText.color = groupTextColor;
            row.trackIdText.fontStyle = FontStyle.Bold; row.trackIdText.fontSize = groupFontSize;
        }
        if (row.varText != null)
        {
            var headerInput = row.varText.GetComponent<InputField>();
            if (headerInput != null) headerInput.SetTextWithoutNotify("");
            else row.varText.text = "";
        }
        if (row.valueText != null) row.valueText.text = "";
        if (row.ruleInput != null) { row.ruleInput.SetTextWithoutNotify(""); row.ruleInput.interactable = false; }
        row.boundFieldKey = null;
        if (row.statusText != null) row.statusText.text = "";
        if (row.colorButton != null) row.colorButton.gameObject.SetActive(false);
        if (row.showButton != null) row.showButton.gameObject.SetActive(false);
        if (row.deleteButton != null) row.deleteButton.gameObject.SetActive(false);
        row.boundTrackId = trackId;
        BindGroupHeaderClick(row, trackId);
        bool selected = !string.IsNullOrEmpty(addTargetTrackId) && addTargetTrackId == trackId;
        if (img != null && selected)
            img.color = selectedRowBackground;
    }

    private void ConfigureDataRow(RowView row, int rank, string trackId, string fieldKey, string varName, string strValue, float? numValue, bool isNumeric, string rule, bool hasLiveData, bool hideMarker, Color markerColor, bool fieldKnown = true)
    {
        if (row == null) return;
        bool sameField = row.boundFieldKey == fieldKey && row.boundTrackId == trackId;
        row.boundTrackId = trackId;
        row.boundFieldKey = fieldKey;
        int vi = row.root.GetSiblingIndex();
        var img = row.root.GetComponent<Image>(); if (img != null) img.color = vi % 2 == 0 ? oddRowBackground : evenRowBackground;
        var le = row.root.GetComponent<LayoutElement>(); if (le != null) le.preferredHeight = rowHeight;

        if (row.trackIdText != null)
        {
            row.trackIdText.fontSize = rowFontSize; row.trackIdText.fontStyle = FontStyle.Normal; row.trackIdText.color = textColor;
            row.trackIdText.text = IsAllPlanesMode ? "" : trackId;
        }
        if (row.rankText != null) row.rankText.text = rank.ToString();
        if (row.varText != null)
        {
            // Only update if text actually changed (don't wipe user input while editing)
            var input = row.varText.GetComponent<InputField>();
            if (input != null)
            {
                // Don't overwrite if InputField is currently focused (user is typing)
                if (!input.isFocused && input.text != varName)
                    input.SetTextWithoutNotify(varName);
                input.interactable = !isReadOnly;
            }
            else
            {
                if (row.varText.text != varName)
                    row.varText.text = varName;
            }
        }
        if (row.valueText != null) row.valueText.text = strValue;

        if (row.ruleInput != null)
        {
            // 同一行仍在编辑时保留输入；换绑到另一个变量时必须用该变量自己的规则，避免复用上一行（例如电量）。
            if (sameField && row.ruleInput.isFocused)
                rule = row.ruleInput.text;
            else
                row.ruleInput.SetTextWithoutNotify(rule ?? "");
            row.ruleInput.interactable = !isReadOnly;
        }
        if (row.colorButton != null)
        {
            row.colorButton.gameObject.SetActive(true);
            if (row.colorSwatch != null)
                row.colorSwatch.color = EnsureMarkerColor(markerColor, fieldKey);
        }
        if (row.showButton != null)
        {
            row.showButton.gameObject.SetActive(true);
            if (row.showButtonLabel != null)
                row.showButtonLabel.text = hideMarker ? "隐" : "显";
        }
        if (row.deleteButton != null)
            row.deleteButton.gameObject.SetActive(false);
        BindDataRowClick(row, trackId, fieldKey);
        bool selected = selectedRowTrackId == trackId && selectedRowFieldKey == fieldKey;
        if (selected && img != null)
            img.color = selectedRowBackground;
        var progress = GetProgressController();
        if (progress != null)
            progress.SetMarkerGroupVisible(ViolationKey(trackId, fieldKey), !hideMarker);
        if (row.statusText != null)
        {
            if (!fieldKnown && !string.IsNullOrWhiteSpace(fieldKey))
            {
                // 不在 DATA_FIELDS / 变量字典里的 key：提示无效，不参与规则判定
                row.statusText.text = "变量无效";
                row.statusText.color = warningColor;
            }
            else if (!isNumeric)
            {
                row.statusText.text = hasLiveData ? "正常" : "待数据";
                row.statusText.color = hasLiveData ? normalColor : warningColor;
            }
            else if (!hasLiveData || !numValue.HasValue)
            {
                row.statusText.text = "待数据";
                row.statusText.color = warningColor;
            }
            else
            {
                var status = EvaluateRule(numValue.Value, rule);
                switch (status)
                {
                    case RuleStatus.Empty:
                        row.statusText.text = "无规则";
                        row.statusText.color = mutedTextColor;
                        break;
                    case RuleStatus.Invalid:
                        row.statusText.text = "规则无效";
                        row.statusText.color = warningColor;
                        break;
                    case RuleStatus.Fail:
                        row.statusText.text = "超限";
                        row.statusText.color = violationColor;
                        if (isReadOnly && !hideMarker)
                            NotifyViolation(trackId, fieldKey, varName, markerColor);
                        break;
                    default:
                        row.statusText.text = "正常";
                        row.statusText.color = normalColor;
                        activeViolations.Remove(ViolationKey(trackId, fieldKey));
                        break;
                }
            }
        }
        row.root.name = $"Row_{trackId}_{fieldKey}";
    }

    private string ViolationKey(string trackId, string fieldKey)
    {
        string key = FindFieldKey(fieldKey) ?? (fieldKey ?? string.Empty).Trim();
        return $"{trackId}|{key}";
    }

    private void NotifyViolation(string trackId, string fieldKey, string varName, Color color)
    {
        var progressController = GetProgressController();
        if (progressController != null)
            progressController.AddViolationMarker($"{trackId} {varName}超限", color, ViolationKey(trackId, fieldKey));
    }

    /// <summary>
    /// 规则超限打点不依赖面板是否显示。切到其他页面时 GameObject 被关掉，Update 不会跑，
    /// 由进度条控制器继续调用这里。
    /// </summary>
    public void TickViolationMarkers()
    {
        if (!Application.isPlaying || !isReadOnly)
            return;
        if (autoFindReceiver)
        {
            var inst = FlightDataStreamReceiver.Instance;
            if (inst != null) receiver = inst;
        }
        if (receiver == null)
            return;

        SyncSerializedToDict();
        EnsureFieldDict();
        var positions = receiver.SnapshotPositions;
        var states = receiver.SnapshotStates;
        currentCycleRecords = receiver.SnapshotCycleRecords;
        if ((positions == null || positions.Count == 0) && (states == null || states.Count == 0))
            return;

        var trackIds = new List<string>();
        if (positions != null)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                if (p != null && !string.IsNullOrWhiteSpace(p.trackId) && !trackIds.Contains(p.trackId))
                    trackIds.Add(p.trackId);
            }
        }
        if (states != null)
        {
            for (int i = 0; i < states.Count; i++)
            {
                var s = states[i];
                if (s != null && !string.IsNullOrWhiteSpace(s.trackId) && !trackIds.Contains(s.trackId))
                    trackIds.Add(s.trackId);
            }
        }
        foreach (var kv in trackEntries)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Key != "默认飞机" && !trackIds.Contains(kv.Key))
                trackIds.Add(kv.Key);
        }

        for (int t = 0; t < trackIds.Count; t++)
        {
            string trackId = trackIds[t];
            var pos = positions?.Find(p => p != null && p.trackId == trackId);
            var state = states?.Find(s => s != null && s.trackId == trackId);
            if (pos == null && state == null)
                continue;
            currentCycleTrackId = trackId;
            var entries = GetEntries(trackId);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.hideMarker || string.IsNullOrWhiteSpace(entry.fieldKey))
                    continue;
                string key = FindFieldKey(entry.fieldKey) ?? entry.fieldKey;
                if (!IsNumericField(key))
                    continue;
                float? num = GetNumericValue(pos, state, key);
                if (!num.HasValue)
                    continue;
                if (EvaluateRule(num.Value, entry.rule) != RuleStatus.Fail)
                    continue;
                NotifyViolation(trackId, key, GetFieldDisplayName(key), EnsureMarkerColor(entry.markerColor, key));
            }
        }
    }

    public void ClearViolationState()
    {
        activeViolations.Clear();
    }

    private FlightProgressController GetProgressController()
    {
        if (cachedProgressController != null) return cachedProgressController;
        cachedProgressController = FindAnyObjectByType<FlightProgressController>();
        if (cachedProgressController == null)
        {
            var all = Resources.FindObjectsOfTypeAll<FlightProgressController>();
            if (all.Length > 0) cachedProgressController = all[0];
        }
        return cachedProgressController;
    }

    // ── Add entry via InputField ────────────────────────────────

    private void BindDataRowClick(RowView row, string trackId, string fieldKey)
    {
        if (row == null || row.root == null)
            return;
        var img = row.root.GetComponent<Image>();
        if (img != null)
            img.raycastTarget = true;
        var btn = row.root.GetComponent<Button>();
        if (btn == null)
            btn = row.root.gameObject.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.targetGraphic = img;
        btn.onClick.RemoveAllListeners();
        string capturedTrack = trackId;
        string capturedKey = fieldKey;
        btn.onClick.AddListener(() => SelectDataRow(capturedTrack, capturedKey));
    }

    private void SelectDataRow(string trackId, string fieldKey)
    {
        selectedRowTrackId = trackId;
        selectedRowFieldKey = fieldKey;
        ShowHint($"已选中 {trackId} / {fieldKey}，点删除行可移除", accentTextColor);
        Refresh();
    }

    private void OnDeleteRowClicked()
    {
        if (isReadOnly)
        {
            ShowHint("播放中不能删除", warningColor);
            return;
        }
        if (string.IsNullOrEmpty(selectedRowTrackId) || string.IsNullOrEmpty(selectedRowFieldKey))
        {
            ShowHint("请先点击要删除的数据行", warningColor);
            return;
        }
        if (batchMode)
            DeleteFieldFromAll(selectedRowFieldKey);
        else
            DeleteRow(selectedRowTrackId, selectedRowFieldKey);
        selectedRowTrackId = null;
        selectedRowFieldKey = null;
    }

    private void BindGroupHeaderClick(RowView row, string trackId)
    {
        if (row == null || row.root == null || string.IsNullOrEmpty(trackId))
            return;
        var img = row.root.GetComponent<Image>();
        if (img != null)
            img.raycastTarget = true;
        var btn = row.root.GetComponent<Button>();
        if (btn == null)
            btn = row.root.gameObject.AddComponent<Button>();
        btn.transition = Selectable.Transition.None;
        btn.targetGraphic = img;
        btn.onClick.RemoveAllListeners();
        string captured = trackId;
        btn.onClick.AddListener(() => SelectAddTarget(captured));
    }

    private void SelectAddTarget(string trackId)
    {
        addTargetTrackId = trackId;
        ShowHint($"添加行将加入 {trackId}", accentTextColor);
        Refresh();
    }

    private void OnAddEntryClicked()
    {
        // 加号 = 添加变量：弹出变量选择器，只允许 DATA_FIELDS / 内置字典里的合法 JSON key
        EnsureFieldDict();

        var matches = new List<string>();
        foreach (var f in fieldDict)
            if (f.common) matches.Add(FieldPickerLabel(f));
        foreach (var f in fieldDict)
            if (!f.common) matches.Add(FieldPickerLabel(f));

        if (matches.Count == 0)
        {
            ShowHint("变量字典为空，请先让数据端发送 DATA_FIELDS", warningColor);
            return;
        }

        bool hasRemote = receiver != null && receiver.DataFields.Count > 0;
        ShowSuggestionPanel(matches, hasRemote
            ? "选择要添加的变量（中文列名，常用在前）"
            : "尚未收到 DATA_FIELDS，先展示内置常用变量（收到后以数据端列名为准）");
    }

    /// <summary>添加行列表只显示中文注释；没有中文时退回 JSON key。</summary>
    private static string FieldPickerLabel(FieldDef f)
    {
        if (f == null)
            return string.Empty;
        if (!string.IsNullOrWhiteSpace(f.displayName) &&
            !string.Equals(f.displayName, f.key, StringComparison.OrdinalIgnoreCase))
            return f.displayName;
        return f.key;
    }

    private void AddEntryWithFieldKey(string jsonKey)
    {
        if (string.IsNullOrWhiteSpace(jsonKey))
            return;

        var targets = ResolveAddTargets();
        if (targets.Count == 0)
        {
            ShowHint("没有可添加的飞机", warningColor);
            return;
        }

        int added = 0;
        foreach (string trackId in targets)
        {
            var entries = GetEntries(trackId);
            bool exists = false;
            foreach (var e in entries)
            {
                if (FindFieldKey(e.fieldKey) == jsonKey)
                {
                    exists = true;
                    break;
                }
            }
            if (exists)
                continue;
            entries.Add(new TrackRuleEntry
            {
                fieldKey = jsonKey,
                rule = DefaultRuleForKey(jsonKey),
                markerColor = MarkerColorFor(jsonKey),
            });
            added++;
        }

        if (added == 0)
        {
            ShowHint($"变量 {jsonKey} 已存在", warningColor);
            return;
        }
        ShowHint(batchMode && targets.Count > 1
            ? $"已为 {added} 架飞机添加 {jsonKey}"
            : $"已添加变量 {jsonKey}", normalColor);
        SyncDictToSerialized();
        Refresh();
    }

    private List<string> CollectKnownTrackIds()
    {
        var ids = new List<string>();
        if (receiver != null)
        {
            var positions = receiver.SnapshotPositions;
            var states = receiver.SnapshotStates;
            if (positions != null)
            {
                foreach (var p in positions)
                {
                    if (p != null && !string.IsNullOrWhiteSpace(p.trackId) && !ids.Contains(p.trackId))
                        ids.Add(p.trackId);
                }
            }
            if (states != null)
            {
                foreach (var s in states)
                {
                    if (s != null && !string.IsNullOrWhiteSpace(s.trackId) && !ids.Contains(s.trackId))
                        ids.Add(s.trackId);
                }
            }
        }
        foreach (var kv in trackEntries)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Key != AllPlanesOption && kv.Key != "默认飞机" && !ids.Contains(kv.Key))
                ids.Add(kv.Key);
        }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    private List<string> ResolveAddTargets()
    {
        var all = CollectKnownTrackIds();
        if (batchMode)
            return all;
        string one = ResolveTargetTrackId();
        if (string.IsNullOrWhiteSpace(one) || one == AllPlanesOption)
            return all.Count > 0 ? new List<string> { all[0] } : new List<string>();
        return new List<string> { one };
    }

    private string ResolveTargetTrackId()
    {
        // Determine target trackId: use selected plane, or first available from receiver, or from trackEntries
        if (selectedTrackId != AllPlanesOption && !string.IsNullOrWhiteSpace(selectedTrackId))
            return selectedTrackId;
        if (!string.IsNullOrWhiteSpace(addTargetTrackId) && addTargetTrackId != AllPlanesOption)
            return addTargetTrackId;

        string trackId = "";

        // Try to get a real trackId from receiver
        if (string.IsNullOrEmpty(trackId))
        {
            var receiver = FlightDataStreamReceiver.Instance;
            if (receiver != null)
            {
                var positions = receiver.SnapshotPositions;
                if (positions != null && positions.Count > 0)
                {
                    foreach (var p in positions)
                    {
                        if (p != null && !string.IsNullOrWhiteSpace(p.trackId))
                        {
                            trackId = p.trackId;
                            break;
                        }
                    }
                }
            }
        }

        // If still no trackId, try from trackEntries
        if (string.IsNullOrEmpty(trackId) && trackEntries.Count > 0)
        {
            foreach (var kv in trackEntries)
            {
                if (kv.Key != AllPlanesOption)
                {
                    trackId = kv.Key;
                    break;
                }
            }
        }

        // Last resort: use a default
        if (string.IsNullOrEmpty(trackId))
        {
            trackId = "默认飞机";
            if (!dropdownTrackIds.Contains(trackId))
                dropdownTrackIds.Add(trackId);
        }

        return trackId;
    }

    private static string DefaultRuleForKey(string key)
    {
        switch (key)
        {
            case "alt":     return ">0";
            case "heading": return "0~360";
            case "pitch":   return "-90~90";
            case "roll":    return "-180~180";
            case "speed":   return ">=0";
            case "battery": return "0~100";
            default:        return "";
        }
    }

    private void ShowHint(string msg, Color color)
    {
    }

    // ── Plane dropdown ─────────────────────────────────────────

    private void OnPlaneDropdownChanged(int index)
    {
        if (index >= 0 && index < dropdownTrackIds.Count)
        {
            selectedTrackId = dropdownTrackIds[index];
            // Update header visibility
            if (headerRow != null && headerRow.trackIdText != null)
                headerRow.trackIdText.gameObject.SetActive(!IsAllPlanesMode);
            Refresh();
        }
    }

    private void UpdatePlaneDropdown(List<FlightDataStreamReceiver.TrackPosition> positions, List<FlightDataStreamReceiver.TrackTimetableState> states)
    {
        if (planeDropdown == null) return;
        var tids = new List<string> { AllPlanesOption };
        if (positions != null) foreach (var p in positions) { string id = p?.trackId; if (!string.IsNullOrWhiteSpace(id) && !tids.Contains(id)) tids.Add(id); }
        if (states != null) foreach (var s in states) { string id = s?.trackId; if (!string.IsNullOrWhiteSpace(id) && !tids.Contains(id)) tids.Add(id); }
        // Also include trackIds from configured entries
        foreach (var kv in trackEntries)
            if (!tids.Contains(kv.Key)) tids.Add(kv.Key);

        bool changed = tids.Count != dropdownTrackIds.Count;
        if (!changed) for (int i = 0; i < tids.Count; i++) if (tids[i] != dropdownTrackIds[i]) { changed = true; break; }
        if (!changed) return;

        dropdownTrackIds.Clear(); dropdownTrackIds.AddRange(tids);
        var opts = new List<Dropdown.OptionData>();
        foreach (string id in dropdownTrackIds) opts.Add(new Dropdown.OptionData(id == AllPlanesOption ? "全部飞机" : id));
        planeDropdown.ClearOptions(); planeDropdown.AddOptions(opts);
        int idx = dropdownTrackIds.IndexOf(selectedTrackId);
        if (idx < 0) { idx = 0; selectedTrackId = AllPlanesOption; }
        planeDropdown.SetValueWithoutNotify(idx); planeDropdown.RefreshShownValue();
    }

    // ── Layout creation ────────────────────────────────────────

    private void EnsureLayoutObjects()
    {
        RectTransform panel = GetComponent<RectTransform>();
        if (panel == null) return;

        RectTransform container = FindOrCreateRect(panel, "DataRuleConfigRoot");
        if (container.GetComponent<Image>() == null) container.gameObject.AddComponent<Image>();
        container.GetComponent<Image>().color = panelBackground;
        bool firstCreate = container.childCount == 0;
        if (firstCreate) StretchFull(container);

        selectorRoot = FindOrCreateRect(container, "SelectorRoot");
        StretchTop(selectorRoot, selectorHeight, 0f);
        planeDropdown = EnsureDropdown(selectorRoot);

        headerRoot = FindOrCreateRect(container, "HeaderRoot");
        StretchTop(headerRoot, headerHeight, selectorHeight);
        headerRoot.offsetMax = new Vector2(-scrollbarWidth - 2f, headerRoot.offsetMax.y);

        RectTransform scrollRoot = FindOrCreateRect(container, "Scroll View");
        StretchFillBelowWithFooter(scrollRoot, selectorHeight + headerHeight, footerHeight);
        EnsureScrollArea(scrollRoot);

        // Input bar + Add button at bottom
        inputBarRoot = FindOrCreateRect(container, "InputBarRoot");
        StretchBottom(inputBarRoot, footerHeight);
        EnsureInputBar(inputBarRoot);

        selectorRoot.SetAsLastSibling();
        container.SetAsLastSibling();
    }

    private void EnsureInputBar(RectTransform root)
    {
        var hlg = root.GetComponent<HorizontalLayoutGroup>();
        if (hlg == null) hlg = root.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.childAlignment = TextAnchor.MiddleRight;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = true;
        hlg.spacing = 6f; hlg.padding = new RectOffset(8, 8, 2, 2);

        // Add button (find or create)
        RectTransform btnRect = FindOrCreateRect(root, "AddBtn");
        var ble = btnRect.GetComponent<LayoutElement>();
        if (ble == null) ble = btnRect.gameObject.AddComponent<LayoutElement>();
        ble.minWidth = 100f; ble.preferredWidth = 100f; ble.flexibleWidth = 0f;
        var bImg = btnRect.GetComponent<Image>();
        if (bImg == null) bImg = btnRect.gameObject.AddComponent<Image>();
        bImg.color = addButtonColor;
        var btn = btnRect.GetComponent<Button>();
        if (btn == null) btn = btnRect.gameObject.AddComponent<Button>();
        btn.targetGraphic = bImg;
        btn.onClick.RemoveListener(OnAddEntryClicked);
        btn.onClick.AddListener(OnAddEntryClicked);

        RectTransform bTextRect = FindOrCreateRect(btnRect, "Label");
        bTextRect.anchorMin = Vector2.zero; bTextRect.anchorMax = Vector2.one;
        bTextRect.offsetMin = Vector2.zero; bTextRect.offsetMax = Vector2.zero;
        var bt = bTextRect.GetComponent<Text>();
        if (bt == null) bt = bTextRect.gameObject.AddComponent<Text>();
        bt.font = font; bt.fontSize = rowFontSize; bt.color = Color.white; bt.alignment = TextAnchor.MiddleCenter;
        bt.text = "+ 添加变量";

        // Hint text (find or create)
        RectTransform hintRect = FindOrCreateRect(root, "Hint");
        var hle = hintRect.GetComponent<LayoutElement>();
        if (hle == null) hle = hintRect.gameObject.AddComponent<LayoutElement>();
        hle.flexibleWidth = 1f; hle.minWidth = 50f;
        addVarHint = hintRect.GetComponent<Text>();
        if (addVarHint == null) addVarHint = hintRect.gameObject.AddComponent<Text>();
        addVarHint.font = font; addVarHint.fontSize = rowFontSize; addVarHint.color = normalColor;
        addVarHint.alignment = TextAnchor.MiddleLeft; addVarHint.horizontalOverflow = HorizontalWrapMode.Overflow;
        hintRect.gameObject.SetActive(false);
    }

    private Dropdown EnsureDropdown(RectTransform root)
    {
        RectTransform dd = FindOrCreateRect(root, "PlaneSelector");
        StretchFull(dd);
        var img = dd.GetComponent<Image>();
        if (img == null) img = dd.gameObject.AddComponent<Image>();
        img.color = new Color(0.1f, 0.13f, 0.16f, 0.95f);
        var dropdown = dd.GetComponent<Dropdown>();
        if (dropdown == null) dropdown = dd.gameObject.AddComponent<Dropdown>();

        Text label = EnsureText(dd, "Label", textColor, TextAnchor.MiddleLeft);
        label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(8f, 0f); label.rectTransform.offsetMax = new Vector2(-28f, 0f);
        label.text = "全部飞机";

        Text arrow = EnsureText(dd, "Arrow", mutedTextColor, TextAnchor.MiddleCenter);
        arrow.rectTransform.anchorMin = new Vector2(1f, 0f); arrow.rectTransform.anchorMax = new Vector2(1f, 1f);
        arrow.rectTransform.pivot = new Vector2(1f, 0.5f);
        arrow.rectTransform.offsetMin = new Vector2(-26f, 0f); arrow.rectTransform.offsetMax = Vector2.zero;
        arrow.text = "v";

        RectTransform template = EnsureDropdownTemplate(dd);
        dropdown.targetGraphic = img; dropdown.captionText = label;
        dropdown.template = template;
        dropdown.itemText = template.Find("Viewport/Content/Item/Item Label")?.GetComponent<Text>();
        return dropdown;
    }

    private RectTransform EnsureDropdownTemplate(RectTransform dd)
    {
        RectTransform t = FindOrCreateRect(dd, "Template");
        t.anchorMin = new Vector2(0f, 0f); t.anchorMax = new Vector2(1f, 0f);
        t.pivot = new Vector2(0.5f, 1f);
        t.offsetMin = new Vector2(0f, -Mathf.Max(120f, rowHeight * 5f));
        t.offsetMax = Vector2.zero; t.SetAsLastSibling(); t.gameObject.SetActive(false);

        var tc = t.GetComponent<Canvas>();
        if (tc == null) tc = t.gameObject.AddComponent<Canvas>();
        tc.overrideSorting = true; tc.sortingOrder = 100;
        if (t.GetComponent<GraphicRaycaster>() == null) t.gameObject.AddComponent<GraphicRaycaster>();

        var ti = t.GetComponent<Image>();
        if (ti == null) ti = t.gameObject.AddComponent<Image>();
        ti.color = new Color(0.08f, 0.1f, 0.13f, 0.98f);

        var sr = t.GetComponent<ScrollRect>();
        if (sr == null) sr = t.gameObject.AddComponent<ScrollRect>();
        sr.horizontal = false; sr.vertical = true; sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 24f;

        RectTransform vp = FindOrCreateRect(t, "Viewport");
        StretchFull(vp); vp.offsetMax = new Vector2(-scrollbarWidth - 2f, 0f);
        var vpi = vp.GetComponent<Image>();
        if (vpi == null) vpi = vp.gameObject.AddComponent<Image>();
        vpi.color = new Color(1f, 1f, 1f, 0.01f);
        var mask = vp.GetComponent<Mask>();
        if (mask == null) mask = vp.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        RectTransform content = FindOrCreateRect(vp, "Content");
        StretchContentTop(content); EnsureVerticalLayout(content, true);

        RectTransform item = FindOrCreateRect(content, "Item");
        item.anchorMin = new Vector2(0f, 1f); item.anchorMax = new Vector2(1f, 1f);
        item.pivot = new Vector2(0.5f, 1f); item.anchoredPosition = Vector2.zero;
        item.sizeDelta = new Vector2(0f, rowHeight);
        var toggle = item.GetComponent<Toggle>();
        if (toggle == null) toggle = item.gameObject.AddComponent<Toggle>();
        var ii = item.GetComponent<Image>();
        if (ii == null) ii = item.gameObject.AddComponent<Image>();
        ii.color = new Color(0.12f, 0.14f, 0.18f, 0.95f);
        toggle.targetGraphic = ii;

        Text il = EnsureText(item, "Item Label", textColor, TextAnchor.MiddleLeft);
        il.rectTransform.anchorMin = Vector2.zero; il.rectTransform.anchorMax = Vector2.one;
        il.rectTransform.offsetMin = new Vector2(8f, 0f); il.rectTransform.offsetMax = new Vector2(-8f, 0f);
        toggle.graphic = null;

        var ilm = item.GetComponent<LayoutElement>();
        if (ilm == null) ilm = item.gameObject.AddComponent<LayoutElement>();
        ilm.minHeight = rowHeight; ilm.preferredHeight = rowHeight;

        sr.viewport = vp; sr.content = content;
        sr.verticalScrollbar = EnsureVerticalScrollbar(t);
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        sr.verticalScrollbarSpacing = 2f;
        return t;
    }

    private Text EnsureText(RectTransform parent, string name, Color color, TextAnchor alignment)
    {
        RectTransform r = FindOrCreateRect(parent, name);
        var t = r.GetComponent<Text>();
        if (t == null) t = r.gameObject.AddComponent<Text>();
        t.font = font; t.fontSize = rowFontSize; t.color = color; t.alignment = alignment;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Truncate;
        t.raycastTarget = false;
        return t;
    }

    private void EnsureScrollArea(RectTransform sr)
    {
        var si = sr.GetComponent<Image>();
        if (si == null) si = sr.gameObject.AddComponent<Image>();
        si.color = new Color(0f, 0f, 0f, 0f); si.raycastTarget = true;

        RectTransform vp = FindOrCreateRect(sr, "Viewport");
        StretchViewport(vp);
        var vpi = vp.GetComponent<Image>();
        if (vpi == null) vpi = vp.gameObject.AddComponent<Image>();
        vpi.color = new Color(1f, 1f, 1f, 0.01f); vpi.raycastTarget = true;
        var mask = vp.GetComponent<Mask>();
        if (mask == null) mask = vp.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        contentRoot = FindOrCreateRect(vp, "ContentRoot");
        StretchContentTop(contentRoot);

        var s = sr.GetComponent<ScrollRect>();
        if (s == null) s = sr.gameObject.AddComponent<ScrollRect>();
        s.viewport = vp; s.content = contentRoot; s.horizontal = false; s.vertical = true;
        s.movementType = ScrollRect.MovementType.Clamped; s.scrollSensitivity = 24f;
        s.verticalScrollbar = EnsureVerticalScrollbar(sr);
        s.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        s.verticalScrollbarSpacing = 2f;
    }

    private Scrollbar EnsureVerticalScrollbar(RectTransform sr)
    {
        RectTransform sb = FindOrCreateRect(sr, "Vertical Scrollbar");
        sb.anchorMin = new Vector2(1f, 0f); sb.anchorMax = new Vector2(1f, 1f);
        sb.pivot = new Vector2(1f, 0.5f);
        sb.offsetMin = new Vector2(-scrollbarWidth, 0f); sb.offsetMax = Vector2.zero;
        var bg = sb.GetComponent<Image>();
        if (bg == null) bg = sb.gameObject.AddComponent<Image>();
        bg.color = new Color(0.04f, 0.05f, 0.07f, 0.65f);

        RectTransform sa = FindOrCreateRect(sb, "Sliding Area");
        StretchFull(sa); sa.offsetMin = new Vector2(2f, 2f); sa.offsetMax = new Vector2(-2f, -2f);
        RectTransform h = FindOrCreateRect(sa, "Handle");
        StretchFull(h);
        var hi = h.GetComponent<Image>();
        if (hi == null) hi = h.gameObject.AddComponent<Image>();
        hi.color = new Color(0.43f, 0.82f, 1f, 0.9f);

        var s = sb.GetComponent<Scrollbar>();
        if (s == null) s = sb.gameObject.AddComponent<Scrollbar>();
        s.direction = Scrollbar.Direction.BottomToTop; s.handleRect = h;
        s.targetGraphic = hi; s.transition = Selectable.Transition.ColorTint;
        s.size = 1f; s.value = 1f;
        return s;
    }

    // ── Layout helpers ──────────────────────────────────────────

    private void StretchViewport(RectTransform r)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = Vector2.zero; r.sizeDelta = Vector2.zero;
        r.offsetMin = Vector2.zero; r.offsetMax = new Vector2(-scrollbarWidth - 2f, 0f);
    }
    private static void StretchTop(RectTransform r, float h, float off)
    {
        r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0.5f, 1f);
        r.offsetMin = new Vector2(0f, -off - h); r.offsetMax = new Vector2(0f, -off);
    }
    private static void StretchFillBelowWithFooter(RectTransform r, float topOff, float footerH)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 0.5f);
        r.offsetMin = new Vector2(0f, footerH); r.offsetMax = new Vector2(0f, -topOff);
    }
    private static void StretchBottom(RectTransform r, float h)
    {
        r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 0f); r.pivot = new Vector2(0.5f, 0f);
        r.offsetMin = new Vector2(0f, 0f); r.offsetMax = new Vector2(0f, h);
    }
    private static void StretchFull(RectTransform r)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0.5f, 0.5f);
        r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
    }
    private static void StretchContentTop(RectTransform r)
    {
        r.anchorMin = new Vector2(0f, 1f); r.anchorMax = new Vector2(1f, 1f); r.pivot = new Vector2(0.5f, 1f);
        r.anchoredPosition = Vector2.zero; r.sizeDelta = Vector2.zero;
    }
    private void EnsureVerticalLayout(RectTransform r, bool fit)
    {
        if (r == null) return;
        var v = r.GetComponent<VerticalLayoutGroup>();
        if (v == null) v = r.gameObject.AddComponent<VerticalLayoutGroup>();
        v.childAlignment = TextAnchor.UpperCenter; v.childControlWidth = true; v.childControlHeight = false;
        v.childForceExpandWidth = true; v.childForceExpandHeight = false; v.spacing = 2f;
        var f = r.GetComponent<ContentSizeFitter>();
        if (f == null) f = r.gameObject.AddComponent<ContentSizeFitter>();
        f.verticalFit = fit ? ContentSizeFitter.FitMode.PreferredSize : ContentSizeFitter.FitMode.Unconstrained;
    }

    // ── Row pooling ─────────────────────────────────────────────

    private void EnsureRowCount(int count)
    {
        while (rows.Count < count)
        {
            int i = rows.Count;
            string rowName = $"Row_{i + 1}";
            Color bg = i % 2 == 0 ? oddRowBackground : evenRowBackground;
            RectTransform template = PanelSceneLayout.FindRowTemplate(contentRoot, rowTemplate);
            if (template == null && rows.Count > 0)
                template = rows[0].root;
            RectTransform target = null;
            if (preserveSceneLayout && i == 0 && template != null)
            {
                target = template;
                target.gameObject.SetActive(true);
                PanelSceneLayout.FreezeRowLayout(target);
            }
            else
                target = PanelSceneLayout.GetOrCloneRow(contentRoot, rowName, preserveSceneLayout ? template : null);
            if (target != null)
                rows.Add(BindRow(target, bg, rowFontSize, false));
            else
                rows.Add(CreateRow(contentRoot, rowName, bg, rowFontSize));
        }
    }
    private void SetActiveRowCount(int count)
    {
        for (int i = 0; i < rows.Count; i++) if (rows[i].root != null) rows[i].root.gameObject.SetActive(i < count);
    }
    private static RectTransform FindOrCreateRect(RectTransform parent, string name)
    {
        Transform e = parent.Find(name);
        if (e != null && e.TryGetComponent(out RectTransform er)) return er;
        var o = new GameObject(name, typeof(RectTransform));
        o.transform.SetParent(parent, false);
        return o.GetComponent<RectTransform>();
    }

    private RowView FindOrCreateHeaderRow()
    {
        if (headerRoot != null)
        {
            Transform e = headerRoot.Find("Header");
            if (e != null && e.TryGetComponent(out RectTransform er)) return BindRow(er, headerBackground, headerFontSize, true);
        }
        return CreateRow(headerRoot, "Header", headerBackground, headerFontSize, true);
    }

    private RowView CreateRow(RectTransform parent, string name, Color bg, int fontSize, bool header = false)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent != null ? parent : transform, false);
        return BindRow(go.GetComponent<RectTransform>(), bg, fontSize, header);
    }

    private RowView BindRow(RectTransform rect, Color bg, int fontSize, bool header)
    {
        GameObject go = rect.gameObject;
        if (!preserveSceneLayout)
        {
            if (header) StretchFull(rect);
            else
            {
                rect.anchorMin = new Vector2(0f, 1f); rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f); rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0f, rowHeight);
            }
        }
        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.color = bg;
        var le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.ignoreLayout = header; le.minHeight = header ? 0f : rowHeight; le.preferredHeight = header ? 0f : rowHeight;

        if (preserveSceneLayout)
        {
            PanelSceneLayout.FreezeRowLayout(rect);
        }
        else
        {
            var h = go.GetComponent<HorizontalLayoutGroup>();
            if (h == null) h = go.AddComponent<HorizontalLayoutGroup>();
            h.childAlignment = TextAnchor.MiddleCenter; h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = true; h.spacing = columnSpacing; h.padding = new RectOffset(8, 8, 2, 2);
        }

        var rv = new RowView { root = rect };
        rv.rankText = FindOrCreateCell(go.transform, "Rank", header ? mutedTextColor : accentTextColor, fontSize, 22f);
        rv.trackIdText = FindOrCreateCell(go.transform, "TrackId", textColor, fontSize, 64f);
        rv.varText = FindOrCreateVarInput(go.transform, "VarName", textColor, fontSize, 52f);
        rv.valueText = FindOrCreateCell(go.transform, "Value", mutedTextColor, fontSize, 58f);
        rv.ruleInput = FindOrCreateRuleInput(go.transform, "Rule", textColor, fontSize, 62f);
        rv.statusText = FindOrCreateCell(go.transform, "Status", textColor, fontSize, 40f);
        BindMarkerControls(rv, go.transform, header);
        return rv;
    }

    private void BindMarkerControls(RowView rv, Transform parent, bool header)
    {
        rv.colorButton = FindOrCreateColorButton(parent, "MarkerColor", 28f);
        rv.colorSwatch = rv.colorButton != null ? rv.colorButton.GetComponent<Image>() : null;
        if (rv.colorButton != null)
        {
            rv.colorButton.onClick.RemoveAllListeners();
            if (!header)
                rv.colorButton.onClick.AddListener(() => CycleMarkerColor(rv));
        }

        rv.showButton = FindOrCreateShowButton(parent, "MarkerShow", 28f, out rv.showButtonLabel);
        if (rv.showButton != null)
        {
            rv.showButton.onClick.RemoveAllListeners();
            if (!header)
                rv.showButton.onClick.AddListener(() => ToggleMarkerVisible(rv));
        }

        rv.deleteButton = parent.Find("MarkerDelete") != null
            ? parent.Find("MarkerDelete").GetComponent<Button>()
            : null;
        if (rv.deleteButton != null)
            rv.deleteButton.gameObject.SetActive(false);

        if (header)
        {
            if (rv.colorButton != null)
            {
                rv.colorButton.gameObject.SetActive(true);
                rv.colorButton.interactable = false;
                if (rv.colorSwatch != null)
                    rv.colorSwatch.color = headerBackground;
                var colorLabel = EnsureText(rv.colorButton.GetComponent<RectTransform>(), "Label", mutedTextColor, TextAnchor.MiddleCenter);
                colorLabel.text = "颜色";
                colorLabel.fontSize = headerFontSize;
                colorLabel.raycastTarget = false;
            }
            if (rv.showButton != null) rv.showButton.gameObject.SetActive(true);
            if (rv.showButtonLabel != null) rv.showButtonLabel.text = "显示";
            if (rv.deleteButton != null)
                rv.deleteButton.gameObject.SetActive(false);
        }
    }

    private void CycleMarkerColor(RowView row)
    {
        if (row == null || row.colorSwatch == null)
            return;
        int best = 0;
        float bestDist = float.MaxValue;
        Color current = row.colorSwatch.color;
        for (int i = 0; i < MarkerPalette.Length; i++)
        {
            Color d = MarkerPalette[i] - current;
            float dist = d.r * d.r + d.g * d.g + d.b * d.b;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }
        row.colorSwatch.color = MarkerPalette[(best + 1) % MarkerPalette.Length];
        var progress = GetProgressController();
        if (progress != null)
            progress.SetMarkerGroupColor(ViolationKey(row.boundTrackId, row.boundFieldKey), row.colorSwatch.color);
        PersistRowVisuals(row);
    }

    private void ToggleMarkerVisible(RowView row)
    {
        if (row == null || row.showButtonLabel == null)
            return;
        bool hidden = row.showButtonLabel.text == "隐";
        bool show = hidden;
        row.showButtonLabel.text = show ? "显" : "隐";
        var progress = GetProgressController();
        if (progress != null)
            progress.SetMarkerGroupVisible(ViolationKey(row.boundTrackId, row.boundFieldKey), show);
        PersistRowVisuals(row);
    }

    private void PersistRowVisuals(RowView row)
    {
        if (row == null || string.IsNullOrEmpty(row.boundTrackId) || string.IsNullOrEmpty(row.boundFieldKey))
            return;
        var entries = GetEntries(row.boundTrackId);
        for (int i = 0; i < entries.Count; i++)
        {
            string key = FindFieldKey(entries[i].fieldKey) ?? entries[i].fieldKey;
            if (key != row.boundFieldKey)
                continue;
            if (row.colorSwatch != null)
                entries[i].markerColor = row.colorSwatch.color;
            if (row.showButtonLabel != null)
                entries[i].hideMarker = row.showButtonLabel.text == "隐";
            break;
        }
        SyncDictToSerialized();
    }

    private void DeleteRow(RowView row)
    {
        if (row == null)
            return;
        DeleteRow(row.boundTrackId, row.boundFieldKey);
    }

    private void DeleteRow(string trackId, string fieldKey)
    {
        if (isReadOnly || string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(fieldKey))
            return;
        string want = FindFieldKey(fieldKey) ?? fieldKey;
        RemoveFieldFromTrack(trackId, fieldKey);
        ShowHint($"已删除 {want}", mutedTextColor);
        SyncDictToSerialized();
        Refresh();
    }

    private void DeleteFieldFromAll(string fieldKey)
    {
        if (isReadOnly || string.IsNullOrEmpty(fieldKey))
            return;
        var ids = CollectKnownTrackIds();
        int n = 0;
        foreach (string id in ids)
        {
            if (RemoveFieldFromTrack(id, fieldKey))
                n++;
        }
        ShowHint($"已从 {n} 架飞机删除 {FindFieldKey(fieldKey) ?? fieldKey}", mutedTextColor);
        SyncDictToSerialized();
        Refresh();
    }

    private bool RemoveFieldFromTrack(string trackId, string fieldKey)
    {
        if (string.IsNullOrEmpty(trackId) || string.IsNullOrEmpty(fieldKey))
            return false;
        var entries = GetEntries(trackId);
        string want = FindFieldKey(fieldKey) ?? fieldKey;
        bool removed = false;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            string key = FindFieldKey(entries[i].fieldKey) ?? entries[i].fieldKey;
            if (key == want)
            {
                entries.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    private Button FindOrCreateDeleteButton(Transform parent, string name, Button showButton, out Text label)
    {
        Transform existing = parent.Find(name);
        GameObject go;
        if (existing != null)
            go = existing.gameObject;
        else
        {
            go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0f, 0.5f);
            if (showButton != null)
            {
                var showRt = showButton.GetComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(showRt.anchoredPosition.x + showRt.sizeDelta.x + 4f, showRt.anchoredPosition.y);
                rt.sizeDelta = showRt.sizeDelta.sqrMagnitude > 1f ? showRt.sizeDelta : new Vector2(28f, 20f);
            }
            else
            {
                rt.anchoredPosition = new Vector2(330f, 0f);
                rt.sizeDelta = new Vector2(28f, 20f);
            }
        }

        var img = go.GetComponent<Image>();
        if (img == null)
            img = go.AddComponent<Image>();
        img.color = new Color(0.55f, 0.18f, 0.18f, 0.95f);
        var btn = go.GetComponent<Button>();
        if (btn == null)
            btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        label = EnsureText(go.GetComponent<RectTransform>(), "Label", Color.white, TextAnchor.MiddleCenter);
        if (label != null)
        {
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;
            label.fontSize = 11;
            label.raycastTarget = false;
            if (string.IsNullOrEmpty(label.text) || label.text == "Label")
                label.text = "删";
        }
        return btn;
    }

    private Button FindOrCreateColorButton(Transform parent, string name, float width)
    {
        Transform existing = parent.Find(name);
        GameObject go;
        if (existing != null)
            go = existing.gameObject;
        else
        {
            go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(Button));
            go.transform.SetParent(parent, false);
        }

        if (!preserveSceneLayout)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.flexibleWidth = 0f;
        }

        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        var btn = go.GetComponent<Button>();
        if (btn == null) btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        return btn;
    }

    private Button FindOrCreateShowButton(Transform parent, string name, float width, out Text label)
    {
        Transform existing = parent.Find(name);
        GameObject go;
        if (existing != null)
            go = existing.gameObject;
        else
        {
            go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(Button));
            go.transform.SetParent(parent, false);
        }

        var le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width; le.minWidth = width; le.flexibleWidth = 0f;

        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.color = new Color(0.18f, 0.22f, 0.28f, 0.95f);
        var btn = go.GetComponent<Button>();
        if (btn == null) btn = go.AddComponent<Button>();
        btn.targetGraphic = img;

        label = EnsureText(go.GetComponent<RectTransform>(), "Label", textColor, TextAnchor.MiddleCenter);
        if (label != null)
        {
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = Vector2.zero;
            label.rectTransform.offsetMax = Vector2.zero;
            label.fontSize = 12;
            label.raycastTarget = false;
        }
        return btn;
    }

    private Text FindOrCreateVarInput(Transform parent, string name, Color color, int fontSize, float width)
    {
        Transform existing = parent.Find(name);
        if (existing != null)
        {
            var existingText = existing.GetComponent<Text>();
            if (existingText != null)
            {
                if (!preserveSceneLayout)
                    ConfigureCell(existingText, color, fontSize, width);
                existingText.raycastTarget = true;
                var input = existing.GetComponent<InputField>();
                if (input == null)
                {
                    input = existing.gameObject.AddComponent<InputField>();
                    input.targetGraphic = existingText;
                    input.textComponent = existingText;
                }
                // Ensure autocomplete is wired
                input.onValueChanged.RemoveListener(OnVarNameInputChanged);
                input.onValueChanged.AddListener(OnVarNameInputChanged);
                return existingText;
            }
        }

        var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        ConfigureCell(t, color, fontSize, width);
        var input2 = go.AddComponent<InputField>();
        input2.targetGraphic = t;
        input2.textComponent = t;
        input2.onValueChanged.AddListener(OnVarNameInputChanged);
        return t;
    }

    // ── Autocomplete for VarName InputField ────────────────────

    private Dropdown suggestionDropdown;
    private bool isSelectingSuggestion;
    private bool suppressSuggestion;
    private InputField activeVarInput; // The InputField that triggered the suggestion; null = picker mode

    private void OnVarNameInputChanged(string text)
    {
        if (suppressSuggestion)
            return;

        // Store which InputField triggered this
        var eventSys = UnityEngine.EventSystems.EventSystem.current;
        if (eventSys != null && eventSys.currentSelectedGameObject != null)
        {
            var input = eventSys.currentSelectedGameObject.GetComponent<InputField>();
            if (input != null)
                activeVarInput = input;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            HideSuggestionPanel();
            return;
        }

        string lower = text.Trim().ToLowerInvariant();
        EnsureFieldDict();

        // 已输入完整合法 JSON key 时不再弹提示
        string exact = FindFieldKey(text);
        if (exact != null && exact.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            HideSuggestionPanel();
            return;
        }

        var matches = new List<string>();
        foreach (var f in fieldDict)
        {
            bool hit = f.key.ToLowerInvariant().Contains(lower) || f.displayName.Contains(text);
            if (!hit)
            {
                var aliases = GetFieldAliases(f.key);
                if (aliases != null)
                {
                    foreach (var alias in aliases)
                    {
                        if (!string.IsNullOrWhiteSpace(alias) && alias.Contains(text))
                        {
                            hit = true;
                            break;
                        }
                    }
                }
            }
            if (hit)
                matches.Add(FieldPickerLabel(f));
        }

        if (matches.Count == 0)
        {
            HideSuggestionPanel();
            return;
        }

        ShowSuggestionPanel(matches, "候选变量（中文列名）— 点击选择");
    }

    private void BindAddButton()
    {
        Button addBtn = null;
        Transform footer = null;
        var buttons = GetComponentsInChildren<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            if (b == null)
                continue;
            string n = b.gameObject.name;
            var label = b.GetComponentInChildren<Text>(true);
            string t = label != null ? label.text : "";
            if (n == "DeleteBtn" || t.Contains("删除行"))
                continue;
            if (n == "AddBtn" || n.Contains("添加") || t.Contains("添加行") || t.Contains("添加变量") || t.Contains("添加"))
            {
                addBtn = b;
                footer = b.transform.parent;
                break;
            }
        }

        if (addBtn != null)
        {
            addBtn.onClick.RemoveListener(OnAddEntryClicked);
            addBtn.onClick.AddListener(OnAddEntryClicked);
            addBtn.interactable = true;
            var addRt = addBtn.GetComponent<RectTransform>();
            if (addRt != null)
            {
                addRt.anchorMin = new Vector2(0f, 0.5f);
                addRt.anchorMax = new Vector2(0f, 0.5f);
                addRt.pivot = new Vector2(0f, 0.5f);
                addRt.anchoredPosition = new Vector2(8f, 0f);
                addRt.sizeDelta = new Vector2(100f, 28f);
            }
            if (footer != null)
            {
                var hlg = footer.GetComponent<HorizontalLayoutGroup>();
                if (hlg != null)
                    hlg.enabled = false;
            }
        }

        if (footer == null)
            footer = addButtonRoot != null ? addButtonRoot : (inputBarRoot != null ? inputBarRoot : transform);

        Button deleteBtn = null;
        var existing = footer.Find("DeleteBtn");
        if (existing != null)
            deleteBtn = existing.GetComponent<Button>();
        if (deleteBtn == null)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                var label = buttons[i].GetComponentInChildren<Text>(true);
                if (label != null && label.text.Contains("删除行"))
                {
                    deleteBtn = buttons[i];
                    break;
                }
            }
        }
        if (deleteBtn == null)
        {
            var go = new GameObject("DeleteBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(footer, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.55f, 0.18f, 0.18f, 0.95f);
            deleteBtn = go.GetComponent<Button>();
            deleteBtn.targetGraphic = img;
            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelGo.transform.SetParent(go.transform, false);
            var lrt = labelGo.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var lt = labelGo.GetComponent<Text>();
            if (font != null) lt.font = font;
            lt.fontSize = rowFontSize; lt.color = Color.white;
            lt.alignment = TextAnchor.MiddleCenter;
            lt.text = "删除行";
            lt.raycastTarget = false;
        }

        var delRt = deleteBtn.GetComponent<RectTransform>();
        delRt.anchorMin = new Vector2(1f, 0.5f);
        delRt.anchorMax = new Vector2(1f, 0.5f);
        delRt.pivot = new Vector2(1f, 0.5f);
        delRt.anchoredPosition = new Vector2(-8f, 0f);
        delRt.sizeDelta = new Vector2(88f, 28f);
        deleteBtn.onClick.RemoveListener(OnDeleteRowClicked);
        deleteBtn.onClick.AddListener(OnDeleteRowClicked);
        deleteBtn.interactable = true;
        var delLabel = deleteBtn.GetComponentInChildren<Text>(true);
        if (delLabel != null)
            delLabel.text = "删除行";

        EnsureBatchToggle(footer);
        HideHint(footer);
    }

    private static void HideHint(Transform footer)
    {
        if (footer == null)
            return;
        for (int i = footer.childCount - 1; i >= 0; i--)
        {
            var child = footer.GetChild(i);
            if (child != null && child.name.StartsWith("Hint"))
                child.gameObject.SetActive(false);
        }
    }

    private void OnBatchToggleChanged(bool on)
    {
        batchMode = on;
        ShowHint(on ? "批量：所有飞机同时添加/删除" : "单架：仅当前飞机添加/删除", accentTextColor);
    }

    private void EnsureBatchToggle(Transform footer)
    {
        if (footer == null)
            return;
        Transform existing = footer.Find("BatchToggle");
        GameObject go = existing != null ? existing.gameObject : new GameObject("BatchToggle", typeof(RectTransform), typeof(Image), typeof(Toggle));
        if (existing == null)
            go.transform.SetParent(footer, false);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-102f, 0f);
        rt.sizeDelta = new Vector2(88f, 24f);

        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.color = new Color(0.1f, 0.13f, 0.16f, 0.95f);

        var toggle = go.GetComponent<Toggle>();
        if (toggle == null) toggle = go.AddComponent<Toggle>();
        batchToggle = toggle;
        toggle.targetGraphic = img;

        Transform checkT = go.transform.Find("Checkmark");
        GameObject checkGo = checkT != null ? checkT.gameObject : new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
        if (checkT == null)
            checkGo.transform.SetParent(go.transform, false);
        var checkRt = checkGo.GetComponent<RectTransform>();
        checkRt.anchorMin = new Vector2(0f, 0.5f);
        checkRt.anchorMax = new Vector2(0f, 0.5f);
        checkRt.pivot = new Vector2(0f, 0.5f);
        checkRt.anchoredPosition = new Vector2(6f, 0f);
        checkRt.sizeDelta = new Vector2(14f, 14f);
        var checkImg = checkGo.GetComponent<Image>();
        if (checkImg == null) checkImg = checkGo.AddComponent<Image>();
        checkImg.color = accentTextColor;
        toggle.graphic = checkImg;

        var label = EnsureText(rt, "Label", textColor, TextAnchor.MiddleLeft);
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = new Vector2(24f, 0f);
        label.rectTransform.offsetMax = new Vector2(-4f, 0f);
        label.text = "批量";
        label.fontSize = rowFontSize;
        label.raycastTarget = false;

        toggle.onValueChanged.RemoveAllListeners();
        toggle.SetIsOnWithoutNotify(batchMode);
        toggle.onValueChanged.AddListener(OnBatchToggleChanged);
    }

    private void ShowSuggestionPanel(List<string> matches, string title)
    {
        RectTransform container = transform.Find("DataRuleConfigRoot") as RectTransform;
        if (container == null)
            container = GetComponent<RectTransform>();
        if (container == null)
            return;

        // Find or create suggestion overlay
        RectTransform overlay = FindOrCreateRect(container, "SuggestionOverlay");
        if (overlay.GetComponent<Image>() == null)
            overlay.gameObject.AddComponent<Image>();
        overlay.GetComponent<Image>().color = new Color(0.05f, 0.07f, 0.1f, 0.98f);
        overlay.anchorMin = new Vector2(0f, 0f);
        overlay.anchorMax = new Vector2(1f, 1f);
        overlay.pivot = new Vector2(0.5f, 0.5f);
        overlay.offsetMin = Vector2.zero;
        overlay.offsetMax = Vector2.zero;
        overlay.SetAsLastSibling();

        // Build clickable buttons for each match instead of Dropdown
        // Clear old buttons
        for (int i = overlay.childCount - 1; i >= 0; i--)
        {
            var child = overlay.GetChild(i);
            if (child.name.StartsWith("MatchBtn_"))
                DestroyImmediate(child.gameObject);
        }

        // Remove old Title and Dropdown
        var oldTitle = overlay.Find("Title");
        if (oldTitle != null) DestroyImmediate(oldTitle.gameObject);
        var oldDD = overlay.Find("SuggestionDropdown");
        if (oldDD != null) DestroyImmediate(oldDD.gameObject);
        var oldClose = overlay.Find("CloseBtn");
        if (oldClose != null) DestroyImmediate(oldClose.gameObject);

        // Title
        RectTransform titleRect = FindOrCreateRect(overlay, "Title");
        var titleText = EnsureText(titleRect, "Label", mutedTextColor, TextAnchor.MiddleCenter);
        titleText.rectTransform.anchorMin = new Vector2(0f, 1f);
        titleText.rectTransform.anchorMax = new Vector2(1f, 1f);
        titleText.rectTransform.pivot = new Vector2(0.5f, 1f);
        titleText.rectTransform.sizeDelta = new Vector2(0f, 24f);
        titleText.rectTransform.anchoredPosition = Vector2.zero;
        titleText.text = title;

        // Close button
        RectTransform closeRect = FindOrCreateRect(overlay, "CloseBtn");
        closeRect.anchorMin = new Vector2(1f, 1f); closeRect.anchorMax = new Vector2(1f, 1f);
        closeRect.pivot = new Vector2(1f, 1f); closeRect.sizeDelta = new Vector2(60f, 24f);
        closeRect.offsetMin = new Vector2(-60f, -24f); closeRect.offsetMax = Vector2.zero;
        var cImg = closeRect.GetComponent<Image>();
        if (cImg == null) cImg = closeRect.gameObject.AddComponent<Image>();
        cImg.color = new Color(0.6f, 0.2f, 0.2f, 0.9f);
        var cBtn = closeRect.GetComponent<Button>();
        if (cBtn == null) cBtn = closeRect.gameObject.AddComponent<Button>();
        cBtn.targetGraphic = cImg;
        cBtn.onClick.RemoveAllListeners();
        cBtn.onClick.AddListener(HideSuggestionPanel);
        var cText = EnsureText(closeRect, "Label", Color.white, TextAnchor.MiddleCenter);
        cText.rectTransform.anchorMin = Vector2.zero; cText.rectTransform.anchorMax = Vector2.one;
        cText.rectTransform.offsetMin = Vector2.zero; cText.rectTransform.offsetMax = Vector2.zero;
        cText.text = "关闭";

        // Create clickable buttons for each match
        float btnHeight = rowHeight + 4f;
        float startY = -30f; // below title
        for (int i = 0; i < matches.Count; i++)
        {
            string match = matches[i];
            string key = FindFieldKey(match) ?? ExtractKeyFromMatch(match);

            RectTransform btnRect = FindOrCreateRect(overlay, $"MatchBtn_{i}");
            btnRect.anchorMin = new Vector2(0.1f, 1f);
            btnRect.anchorMax = new Vector2(0.9f, 1f);
            btnRect.pivot = new Vector2(0.5f, 1f);
            btnRect.sizeDelta = new Vector2(0f, btnHeight);
            btnRect.anchoredPosition = new Vector2(0f, startY - i * btnHeight);

            var btnImg = btnRect.GetComponent<Image>();
            if (btnImg == null) btnImg = btnRect.gameObject.AddComponent<Image>();
            btnImg.color = new Color(0.12f, 0.14f, 0.18f, 0.95f);

            var btn = btnRect.GetComponent<Button>();
            if (btn == null) btn = btnRect.gameObject.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            btn.onClick.RemoveAllListeners();
            string capturedKey = key;
            btn.onClick.AddListener(() => OnSuggestionButtonClicked(capturedKey));

            var btnLabel = EnsureText(btnRect, "Label", textColor, TextAnchor.MiddleLeft);
            btnLabel.rectTransform.anchorMin = Vector2.zero; btnLabel.rectTransform.anchorMax = Vector2.one;
            btnLabel.rectTransform.offsetMin = new Vector2(10f, 0f); btnLabel.rectTransform.offsetMax = new Vector2(-10f, 0f);
            btnLabel.text = match;
        }

        overlay.gameObject.SetActive(true);
    }

    private static string ExtractKeyFromMatch(string match)
    {
        if (string.IsNullOrEmpty(match)) return match;
        int idx = match.IndexOf('（');
        return idx > 0 ? match.Substring(0, idx).Trim() : match.Trim();
    }

    private void OnSuggestionButtonClicked(string key)
    {
        if (activeVarInput != null)
        {
            suppressSuggestion = true;
            activeVarInput.text = key;
            suppressSuggestion = false;

            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
            {
                es.SetSelectedGameObject(activeVarInput.gameObject);
                activeVarInput.ActivateInputField();
            }

            Debug.Log($"[DataRuleConfigPanel] Filled '{key}' into InputField");
        }
        else
        {
            // 加号选变量模式：没有正在编辑的输入框 → 直接添加一行该变量的规则
            AddEntryWithFieldKey(key);
        }

        activeVarInput = null;
        HideSuggestionPanel();
    }

    private void HideSuggestionPanel()
    {
        RectTransform container = transform.Find("DataRuleConfigRoot") as RectTransform;
        if (container == null) return;
        var overlay = container.Find("SuggestionOverlay");
        if (overlay != null)
            overlay.gameObject.SetActive(false);
    }

    private Text FindOrCreateCell(Transform parent, string name, Color color, int fontSize, float width)
    {
        Transform existing = parent.Find(name);
        if (existing != null && existing.TryGetComponent(out Text existingText))
        {
            if (!preserveSceneLayout)
                ConfigureCell(existingText, color, fontSize, width);
            return existingText;
        }
        return CreateCell(parent, name, color, fontSize, width);
    }

    private InputField FindOrCreateRuleInput(Transform parent, string name, Color color, int fontSize, float width)
    {
        CleanupDuplicateChildren(parent, name);
        Transform existing = parent.Find(name);
        if (existing != null && existing.TryGetComponent(out InputField existingInput))
        {
            CollapseRuleGraphics(existingInput);
            return existingInput;
        }

        if (existing != null && existing.TryGetComponent(out Text existingText))
        {
            existingText.raycastTarget = true;
            existingText.supportRichText = false;
            var nested = existing.GetComponentInChildren<InputField>(true);
            if (nested != null && nested.gameObject != existing.gameObject)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(nested.gameObject);
                else UnityEngine.Object.DestroyImmediate(nested.gameObject);
            }
            var input = existing.gameObject.AddComponent<InputField>();
            input.textComponent = existingText;
            input.targetGraphic = existingText;
            input.contentType = InputField.ContentType.Standard;
            input.lineType = InputField.LineType.SingleLine;
            CollapseRuleGraphics(input);
            return input;
        }

        var created = CreateRuleInput(parent, name, color, fontSize, width);
        CollapseRuleGraphics(created);
        return created;
    }

    private static void CleanupDuplicateChildren(Transform parent, string name)
    {
        if (parent == null || string.IsNullOrEmpty(name))
            return;
        Transform keep = null;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform child = parent.GetChild(i);
            if (child == null || (child.name != name && !child.name.StartsWith(name + " ")))
                continue;
            if (keep == null)
            {
                keep = child;
                keep.name = name;
                continue;
            }
            if (Application.isPlaying) UnityEngine.Object.Destroy(child.gameObject);
            else UnityEngine.Object.DestroyImmediate(child.gameObject);
        }
    }

    private static void CollapseRuleGraphics(InputField input)
    {
        if (input == null)
            return;
        var selfText = input.GetComponent<Text>();
        if (selfText != null && input.textComponent != null && input.textComponent != selfText)
            selfText.enabled = false;
        if (input.textComponent != null)
        {
            input.textComponent.supportRichText = false;
            input.textComponent.raycastTarget = input.textComponent == selfText || input.targetGraphic == input.textComponent;
        }
    }

    private Text CreateCell(Transform parent, string name, Color color, int fontSize, float width)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        ConfigureCell(t, color, fontSize, width);
        return t;
    }

    private void ConfigureCell(Text t, Color color, int fontSize, float width)
    {
        t.font = font; t.fontSize = fontSize; t.color = color; t.alignment = TextAnchor.MiddleLeft;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Truncate;
        var le = t.GetComponent<LayoutElement>();
        if (le == null) le = t.gameObject.AddComponent<LayoutElement>();
        le.minWidth = width; le.preferredWidth = width; le.flexibleWidth = 0f;
    }

    private InputField CreateRuleInput(Transform parent, string name, Color color, int fontSize, float width)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var le = go.GetComponent<LayoutElement>();
        le.minWidth = width; le.preferredWidth = width; le.flexibleWidth = 0f;

        var bgGo = new GameObject("BG", typeof(RectTransform), typeof(Image));
        bgGo.transform.SetParent(go.transform, false);
        var bgRT = bgGo.GetComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;
        bgGo.GetComponent<Image>().color = inputBgColor;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(bgGo.transform, false);
        var textRT = textGo.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = new Vector2(4f, 0f); textRT.offsetMax = new Vector2(-4f, 0f);
        var t = textGo.GetComponent<Text>();
        t.font = font; t.fontSize = fontSize; t.color = color; t.alignment = TextAnchor.MiddleLeft;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.supportRichText = false;

        var input = go.AddComponent<InputField>();
        input.targetGraphic = bgGo.GetComponent<Image>();
        input.textComponent = t; input.text = "";
        return input;
    }

    private void SetHeaderTexts(RowView row)
    {
        if (row == null) return;
        if (row.rankText != null) row.rankText.text = "#";
        if (row.trackIdText != null) { row.trackIdText.text = "飞机编号"; row.trackIdText.gameObject.SetActive(true); }
        if (row.varText != null)
        {
            var input = row.varText.GetComponent<InputField>();
            if (input != null) { input.interactable = false; input.text = "变量名"; }
            else row.varText.text = "变量名";
        }
        if (row.valueText != null) row.valueText.text = "当前值";
        if (row.ruleInput != null) { row.ruleInput.interactable = false; row.ruleInput.text = "规则"; }
        if (row.statusText != null) row.statusText.text = "状态";
        if (row.showButtonLabel != null) row.showButtonLabel.text = "显示";
    }
}
