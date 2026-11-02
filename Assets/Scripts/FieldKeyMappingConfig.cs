using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 字段名映射配置 — 支持外部 JSON 文件修改数据端发来的字段名到内部 key 的映射。
/// 配置文件路径：Assets/StreamingAssets/FieldKeyMapping.json
/// 
/// 例如：数据端发来的 key 是 "经度"，配置映射 "经度" → "lng"，系统自动识别。
/// </summary>
public class FieldKeyMappingConfig : MonoBehaviour
{
    [Serializable]
    public class KeyAlias
    {
        public string internalKey;
        public string[] aliases;
    }

    [Serializable]
    public class MappingConfig
    {
        // 经纬高坐标
        public KeyAlias lng = new KeyAlias { internalKey = "lng", aliases = new[] { "lng", "lon", "longitude", "经度" } };
        public KeyAlias lat = new KeyAlias { internalKey = "lat", aliases = new[] { "lat", "latitude", "纬度" } };
        public KeyAlias alt = new KeyAlias { internalKey = "alt", aliases = new[] { "alt", "altitude", "height", "高度" } };
        public KeyAlias heading = new KeyAlias { internalKey = "heading", aliases = new[] { "heading", "yaw", "航向", "航向角" } };
        public KeyAlias pitch = new KeyAlias { internalKey = "pitch", aliases = new[] { "pitch", "俯仰", "俯仰角" } };
        public KeyAlias roll = new KeyAlias { internalKey = "roll", aliases = new[] { "roll", "横滚", "横滚角" } };
        
        // 状态数据
        public KeyAlias speed = new KeyAlias { internalKey = "speed", aliases = new[] { "speed", "速度" } };
        public KeyAlias battery = new KeyAlias { internalKey = "battery", aliases = new[] { "battery", "电量" } };
        public KeyAlias detectRange = new KeyAlias { internalKey = "detectRange", aliases = new[] { "detect_range", "detectRange", "range", "探测范围" } };
        public KeyAlias yawAngle = new KeyAlias { internalKey = "yawAngle", aliases = new[] { "yaw_angle", "yawAngle", "扫描中心(度)", "扫描中心", "传感器左右偏转角", "左右偏转角" } };
        public KeyAlias pitchAngle = new KeyAlias { internalKey = "pitchAngle", aliases = new[] { "pitch_angle", "pitchAngle", "俯仰中心(度)", "俯仰中心", "传感器上下偏转角", "上下偏转角" } };
        public KeyAlias yawScanAngle = new KeyAlias { internalKey = "yawScanAngle", aliases = new[] { "yaw_scan_angle", "yawScanAngle", "扫描方位(度)", "扫描方位", "传感器左右扫描范围" } };
        public KeyAlias pitchScanAngle = new KeyAlias { internalKey = "pitchScanAngle", aliases = new[] { "pitch_scan_angle", "pitchScanAngle", "俯仰方位(度)", "俯仰方位", "传感器上下扫描范围" } };
        public KeyAlias state = new KeyAlias { internalKey = "state", aliases = new[] { "state", "flightState", "状态" } };
        public KeyAlias alarm = new KeyAlias { internalKey = "alarm", aliases = new[] { "alarm", "warning", "告警" } };
        public KeyAlias detectorType = new KeyAlias { internalKey = "detectorType", aliases = new[] { "detector_type", "detectorType", "detector_name", "detectorName", "探测器类型", "探测器名称" } };
        public KeyAlias rangeType = new KeyAlias { internalKey = "rangeType", aliases = new[] { "range_type", "rangeType", "范围类型" } };
        public KeyAlias centerPoint = new KeyAlias { internalKey = "centerPoint", aliases = new[] { "center_point", "centerPoint", "中心点" } };
        public KeyAlias radarStatus = new KeyAlias { internalKey = "radarStatus", aliases = new[] { "radar_status", "radarStatus", "雷达状态" } };
        public KeyAlias flightTime = new KeyAlias { internalKey = "flightTime", aliases = new[] { "flightTime", "time", "飞行时间" } };
        public KeyAlias trackId = new KeyAlias { internalKey = "trackId", aliases = new[] { "trackId", "id", "aircraftId", "planeId", "飞机编号" } };
        public KeyAlias planId = new KeyAlias { internalKey = "planId", aliases = new[] { "planId", "计划号" } };
        public KeyAlias cycleId = new KeyAlias { internalKey = "cycleId", aliases = new[] { "cycleId", "cycle_id", "周期号" } };
    }

    [Header("Current Mapping")]
    [SerializeField] private MappingConfig mapping = new MappingConfig();

    [Header("Auto Apply")]
    [SerializeField] private bool applyOnStart = true;
    [SerializeField] private bool autoReloadOnChange = true;

    [Header("Config File")]
    [SerializeField] private string configFileName = "FieldKeyMapping.json";

    private string configPath;
    private DateTime lastFileWrite;

    // Runtime lookup: external key → internal key
    private readonly Dictionary<string, string> keyLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static FieldKeyMappingConfig Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        configPath = Path.Combine(Application.streamingAssetsPath, configFileName);
        LoadFromFile();
        if (applyOnStart) RebuildLookup();
    }

    private void Update()
    {
        if (autoReloadOnChange && File.Exists(configPath))
        {
            var writeTime = File.GetLastWriteTime(configPath);
            if (writeTime != lastFileWrite)
            {
                LoadFromFile();
                RebuildLookup();
            }
        }
    }

    public void RebuildLookup()
    {
        keyLookup.Clear();
        var fields = typeof(MappingConfig).GetFields();
        foreach (var field in fields)
        {
            if (field.GetValue(mapping) is KeyAlias ka)
            {
                if (!string.IsNullOrEmpty(ka.internalKey))
                    keyLookup[ka.internalKey] = ka.internalKey;
                if (ka.aliases != null)
                    foreach (var alias in ka.aliases)
                        if (!string.IsNullOrEmpty(alias))
                            keyLookup[alias] = ka.internalKey;
            }
        }
        Debug.Log($"[FieldKeyMappingConfig] Lookup table built: {keyLookup.Count} entries");
    }

    /// <summary>
    /// Resolve an external key name to internal key.
    /// Returns the internal key if found, otherwise returns the input as-is.
    /// </summary>
    public string ResolveKey(string externalKey)
    {
        if (string.IsNullOrEmpty(externalKey)) return externalKey;
        if (keyLookup.TryGetValue(externalKey, out var internalKey))
            return internalKey;
        return externalKey;
    }

    /// <summary>
    /// Get all alias names for a given internal key (for GetString/GetFloat fallback matching).
    /// </summary>
    public string[] GetAliases(string internalKey)
    {
        var fields = typeof(MappingConfig).GetFields();
        foreach (var field in fields)
        {
            if (field.GetValue(mapping) is KeyAlias ka && ka.internalKey == internalKey)
                return ka.aliases ?? new[] { internalKey };
        }
        return new[] { internalKey };
    }

    public void LoadFromFile()
    {
        if (string.IsNullOrEmpty(configPath))
            configPath = Path.Combine(Application.streamingAssetsPath, configFileName);

        if (!File.Exists(configPath))
        {
            SaveToFile();
            return;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var loaded = JsonUtility.FromJson<MappingConfig>(json);
            if (loaded != null)
            {
                mapping = loaded;
                lastFileWrite = File.GetLastWriteTime(configPath);
                Debug.Log($"[FieldKeyMappingConfig] Loaded from {configPath}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[FieldKeyMappingConfig] Failed to load: {e.Message}");
        }
    }

    public void SaveToFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonUtility.ToJson(mapping, true);
            File.WriteAllText(configPath, json);
            lastFileWrite = File.GetLastWriteTime(configPath);
            Debug.Log($"[FieldKeyMappingConfig] Saved to {configPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[FieldKeyMappingConfig] Failed to save: {e.Message}");
        }
    }

    [ContextMenu("Reload Config")]
    public void Reload()
    {
        LoadFromFile();
        RebuildLookup();
    }

    [ContextMenu("Save Current as Default")]
    public void SaveCurrent()
    {
        SaveToFile();
    }
}
