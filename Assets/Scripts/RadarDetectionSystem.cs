using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 传感器扫描检测系统 —— 10 变量模型（完全替换旧版 扇形/锥形 + detectRange + centerPoint 逻辑）。
///
/// 扫描区域由 10 个变量共同叠加决定：
///   飞机位置: lng / lat / alt
///   飞机姿态: heading(航向角,北偏东) / pitch(俯仰角,上正) / roll(横滚角,默认右压杆为正,可 invertRollSign 翻转)
///   传感器:   yaw_angle(左右偏转角,左正右负) / pitch_angle(上下偏转角,上正下负)
///             yaw_scan_angle(左右扫描总范围,如 40 => 左20右20) / pitch_scan_angle(上下扫描总范围,如 40 => 上20下20)
///
/// 波束指向: 方位 = heading − yaw_angle（左偏为正，故取减号）；俯仰 = pitch + pitch_angle（上偏为正）。
///           横滚角绕波束轴扭转整个扫描锥（使二维投影外弧不对称、改变探测判定）。
/// 扫描距离不在 10 变量内，由 maxDetectRangeMeters 统一充当二维投影半径与检测距离门限。
///
/// 应用：1) 扫描锥的二维(地图)投影显示；2) 飞机扫描其他飞机的触发（波束线 + 扫描状态环）。
/// </summary>
public class RadarDetectionSystem : MonoBehaviour
{
    private struct DetectionKey : IEquatable<DetectionKey>
    {
        public string sourceTrackId;
        public string targetTrackId;

        public bool Equals(DetectionKey other) => sourceTrackId == other.sourceTrackId && targetTrackId == other.targetTrackId;
        public override bool Equals(object obj) => obj is DetectionKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked { return ((sourceTrackId != null ? sourceTrackId.GetHashCode() : 0) * 397) ^ (targetTrackId != null ? targetTrackId.GetHashCode() : 0); }
        }
    }

    // ── Public detection result (for UI consumers) ─────────────

    public struct DetectionResult
    {
        public string sourceTrackId;
        public string targetTrackId;
        public string detectorType;
        public float distance;           // 米
        public float offAzimuth;         // 目标相对波束中心的方位偏角(度)
        public float offElevation;       // 目标相对波束中心的俯仰偏角(度)
        public float boresightAzimuth;   // 波束中心方位角(度,北偏东)
        public float boresightElevation; // 波束中心俯仰角(度,上正)
        public float sourceLat;
        public float sourceLng;
        public float sourceAlt;
        public float sourceHeading;
        public float targetLat;
        public float targetLng;
        public float targetAlt;
    }

    public struct SensorInfo
    {
        public string trackId;
        public string detectorType;
        public string radarStatus;
        public float lat;
        public float lng;
        public float alt;
        public float heading;         // 航向角(度)
        public float pitch;           // 俯仰角(度,上正)
        public float roll;            // 横滚角(度,符号见 invertRollSign)
        public float yawDeflection;   // yaw_angle 传感器左右偏转角(左正右负)
        public float pitchDeflection; // pitch_angle 传感器上下偏转角(上正下负)
        public float yawScanHalf;     // yaw_scan_angle / 2
        public float pitchScanHalf;   // pitch_scan_angle / 2
        public float boresightAzimuth;
        public float boresightElevation;
        public float maxRangeMeters;
        public int mountSign;        // 传感器位置：前=+1，中=0，后=-1
        public bool spherical;       // 球形雷达：全向球体，半径 = maxRangeMeters
        public string rangeType;
    }

    public List<DetectionResult> CurrentDetections { get; private set; } = new List<DetectionResult>();
    public List<SensorInfo> ActiveSensors { get; private set; } = new List<SensorInfo>();

    // ── Config ──────────────────────────────────────────────────

    [Header("Data")]
    [SerializeField] private FlightDataStreamReceiver receiver;
    [SerializeField] private bool autoFindReceiver = true;
    [SerializeField] private float checkInterval = 0.2f;

    [Header("Geometry — 10-variable model")]
    [Tooltip("扫描/检测距离门限(米)。10 变量不包含距离，此值统一作为二维投影半径与检测截止距离。")]
    [SerializeField] private float maxDetectRangeMeters = 50000f;
    [Tooltip("横滚角符号：勾选后数据按“左压杆为正”处理（默认右压杆为正）。")]
    [SerializeField] private bool invertRollSign = false;
    [Tooltip("二维投影采样网格：波束方位 x 俯仰方向的采样数")]
    [SerializeField] private int frustumAzSamples = 48;
    [SerializeField] private int frustumElSamples = 9;
    [Tooltip("传感器位置为“前/后”时，扇面和波束起点相对飞机图标中心沿机头/机尾方向的像素偏移。中部保持在图标中心。")]
    [SerializeField] private float bodyStationOffsetPixels = 26f;

    [Header("Output")]
    [SerializeField] private bool enableDetectionLog = true;
    [SerializeField] private bool logLostDetection = false;

    /// <summary>默认探测距离门限(米)。未单独配置的传感器用此值。</summary>
    public float MaxDetectRangeMeters => Mathf.Max(0f, maxDetectRangeMeters);

    private readonly Dictionary<string, float> sensorRangeMeters = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置默认探测距离门限(米)。</summary>
    public void SetMaxDetectRangeMeters(float meters)
    {
        float clamped = Mathf.Clamp(meters, 0f, 10000000f);
        if (Mathf.Approximately(clamped, maxDetectRangeMeters))
            return;
        maxDetectRangeMeters = clamped;
        Debug.Log($"[RadarDetectionSystem] Max detect range set to {maxDetectRangeMeters:F0}m");
    }

    public float GetSensorRangeMeters(string trackId, string detectorType)
    {
        string key = MakeSensorKey(trackId, detectorType);
        if (!string.IsNullOrEmpty(key) && sensorRangeMeters.TryGetValue(key, out float meters))
            return Mathf.Max(0f, meters);
        return MaxDetectRangeMeters;
    }

    public void SetSensorRangeMeters(string trackId, string detectorType, float meters)
    {
        string key = MakeSensorKey(trackId, detectorType);
        if (string.IsNullOrEmpty(key))
            return;
        float clamped = Mathf.Clamp(meters, 0f, 10000000f);
        sensorRangeMeters[key] = clamped;
    }

    // ── UI Visualization ────────────────────────────────────────

    [Header("Detection UI")]
    [SerializeField] private PlaneDisplayController mapController;
    [SerializeField] private bool showDetectionSectors = true;
    [SerializeField] private bool showDetectionBeams = true;
    [SerializeField] private float sectorLineWidth = 1.5f;
    [SerializeField] private float beamLineWidth = 2.5f;
    public float SectorLineWidth
    {
        get => sectorLineWidth;
        set => sectorLineWidth = Mathf.Clamp(value, 0.05f, 12f);
    }
    public float BeamLineWidth
    {
        get => beamLineWidth;
        set => beamLineWidth = Mathf.Clamp(value, 0.05f, 12f);
    }
    [SerializeField] private int sectorSortingOrder = 6;
    [SerializeField] private int beamSortingOrder = 7;
    [SerializeField] private Color sectorColor = new Color(0f, 1f, 0.5f, 0.4f);
    [SerializeField] private Color sectorEdgeColor = new Color(0f, 1f, 0.5f, 0.8f);
    [SerializeField] private Color beamColor = new Color(1f, 0.2f, 0.2f, 0.9f);

    [Header("Sensor Visual Config")]
    [SerializeField] private Color[] sensorPalette = new Color[]
    {
        new Color(0.05f, 0.85f, 1f, 1f),
        new Color(1f, 0.82f, 0.12f, 1f),
        new Color(0.35f, 1f, 0.35f, 1f),
        new Color(1f, 0.32f, 0.32f, 1f),
        new Color(0.72f, 0.45f, 1f, 1f),
        new Color(1f, 0.48f, 0.08f, 1f),
        new Color(0.1f, 0.45f, 1f, 1f),
        new Color(1f, 0.22f, 0.75f, 1f),
    };

    [Header("Scan Status Rings")]
    [SerializeField] private bool showDetectionRings = true;
    [SerializeField] private float ringRadius = 16f;
    public float DetectionRingRadius
    {
        get => ringRadius;
        set
        {
            float next = Mathf.Clamp(value, 0.15f, 80f);
            if (Mathf.Abs(next - ringRadius) < 0.01f)
                return;
            ringRadius = next;
            ClearRings();
        }
    }

    private void ClearRings()
    {
        foreach (var kv in rings)
        {
            var ring = kv.Value;
            if (ring == null)
                continue;
            for (int i = 0; i < ring.arcs.Count; i++)
            {
                if (ring.arcs[i] != null)
                    Destroy(ring.arcs[i].gameObject);
            }
            ring.arcs.Clear();
            ring.arcSensors.Clear();
        }
    }
    [SerializeField] private float ringLineWidth = 2.5f;
    [SerializeField] private int ringSortingOrder = 8;
    [SerializeField] private Color ringIdleColor = new Color(0.55f, 0.62f, 0.7f, 0.9f);
    [SerializeField] private Vector2 ringOffset = Vector2.zero;
    public Vector2 DetectionRingOffset
    {
        get => ringOffset;
        set => ringOffset = value;
    }
    public float DetectionRingThickness
    {
        get => ringLineWidth;
        set
        {
            float next = Mathf.Clamp(value, 0.02f, 40f);
            if (Mathf.Abs(next - ringLineWidth) < 0.001f)
                return;
            ringLineWidth = next;
            ClearRings();
        }
    }
    [SerializeField] private int ringArcSegments = 14;
    [SerializeField] private float ringArcGapDegrees = 6f;

    // ── Per-sensor visual settings ──────────────────────────────

    private struct SensorVisualState
    {
        public Color colorOverride; // alpha≈0 表示未手动指定，使用默认跳色
        public bool visible;
    }

    private readonly Dictionary<string, SensorVisualState> sensorVisuals = new Dictionary<string, SensorVisualState>(StringComparer.OrdinalIgnoreCase);

    public static string MakeSensorKey(string trackId, string detectorType) => $"{trackId}|{detectorType}";

    public static bool IsSphericalRadar(string rangeType)
    {
        if (string.IsNullOrWhiteSpace(rangeType))
            return false;
        return rangeType.IndexOf("球", StringComparison.Ordinal) >= 0
            || rangeType.IndexOf("sphere", StringComparison.OrdinalIgnoreCase) >= 0
            || rangeType.IndexOf("omni", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool IsSphericalRadar(FlightDataStreamReceiver.TrackTimetableState s)
    {
        return s != null && IsSphericalRadar(s.rangeType);
    }

    public static bool IsSphericalRadar(SensorInfo sensor) => sensor.spherical;

    public void TransferSensorKey(string trackId, string oldDetector, string newDetector)
    {
        string oldKey = MakeSensorKey(trackId, oldDetector);
        string newKey = MakeSensorKey(trackId, newDetector);
        if (string.IsNullOrEmpty(oldKey) || string.IsNullOrEmpty(newKey) || oldKey == newKey)
            return;
        if (sensorRangeMeters.TryGetValue(oldKey, out float meters))
        {
            sensorRangeMeters[newKey] = meters;
            sensorRangeMeters.Remove(oldKey);
        }
        if (sensorVisuals.TryGetValue(oldKey, out var visual))
        {
            sensorVisuals[newKey] = visual;
            sensorVisuals.Remove(oldKey);
        }
        if (sectorLines.TryGetValue(oldKey, out var sector))
        {
            sectorLines.Remove(oldKey);
            if (sector != null)
                sector.gameObject.SetActive(false);
        }
    }

    public IReadOnlyList<Color> SensorPalette => sensorPalette;

    /// <summary>传感器当前生效颜色：手动指定优先，否则按排序自动跳色。</summary>
    public Color GetSensorColor(string trackId, string detectorType)
    {
        string key = MakeSensorKey(trackId, detectorType);
        if (sensorVisuals.TryGetValue(key, out var state) && state.colorOverride.a > 0.01f)
            return state.colorOverride;
        return GetAutoSensorColor(key);
    }

    public bool IsSensorVisible(string trackId, string detectorType)
    {
        return !sensorVisuals.TryGetValue(MakeSensorKey(trackId, detectorType), out var state) || state.visible;
    }

    public void SetSensorColor(string trackId, string detectorType, Color color)
    {
        string key = MakeSensorKey(trackId, detectorType);
        var state = GetOrCreateVisualState(key);
        state.colorOverride = color;
        sensorVisuals[key] = state;
    }

    public void SetSensorVisible(string trackId, string detectorType, bool visible)
    {
        string key = MakeSensorKey(trackId, detectorType);
        var state = GetOrCreateVisualState(key);
        state.visible = visible;
        sensorVisuals[key] = state;

        if (!visible && sectorLines.TryGetValue(key, out var sector) && sector != null)
            sector.gameObject.SetActive(false);
    }

    private SensorVisualState GetOrCreateVisualState(string key)
    {
        if (sensorVisuals.TryGetValue(key, out var state))
            return state;
        return new SensorVisualState { colorOverride = new Color(0f, 0f, 0f, 0f), visible = true };
    }

    public void ApplyMemory(PanelMemory.Store store)
    {
        if (store == null)
            return;
        if (store.defaultDetectRange >= 0f)
            SetMaxDetectRangeMeters(store.defaultDetectRange);
        if (store.sensors == null)
            return;
        foreach (var item in store.sensors)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.key))
                continue;
            if (item.range >= 0f)
                sensorRangeMeters[item.key] = item.range;
            var vis = GetOrCreateVisualState(item.key);
            vis.visible = item.visible;
            if (item.hasColor && item.color != null)
                vis.colorOverride = item.color.ToColor();
            sensorVisuals[item.key] = vis;
        }
    }

    public void CaptureMemory(PanelMemory.Store store)
    {
        if (store == null)
            return;
        store.defaultDetectRange = MaxDetectRangeMeters;
        var map = new Dictionary<string, PanelMemory.SensorItem>(StringComparer.OrdinalIgnoreCase);
        if (store.sensors != null)
        {
            foreach (var old in store.sensors)
            {
                if (old != null && !string.IsNullOrWhiteSpace(old.key))
                    map[old.key] = old;
            }
        }
        foreach (var kv in sensorRangeMeters)
        {
            if (!map.TryGetValue(kv.Key, out var item) || item == null)
                item = new PanelMemory.SensorItem { key = kv.Key, visible = true };
            item.range = kv.Value;
            map[kv.Key] = item;
        }
        foreach (var kv in sensorVisuals)
        {
            if (!map.TryGetValue(kv.Key, out var item) || item == null)
                item = new PanelMemory.SensorItem { key = kv.Key, range = -1f };
            item.visible = kv.Value.visible;
            if (kv.Value.colorOverride.a > 0.01f)
            {
                item.hasColor = true;
                item.color = PanelMemory.Color4.From(kv.Value.colorOverride);
            }
            map[kv.Key] = item;
        }
        store.sensors = new List<PanelMemory.SensorItem>(map.Values);
    }

    private Color GetAutoSensorColor(string key)
    {
        if (sensorPalette == null || sensorPalette.Length == 0)
            return sectorEdgeColor;

        // 按传感器 key 稳定跳色，不随激活列表增减把已选颜色冲回青色。
        int hash = 0;
        if (!string.IsNullOrEmpty(key))
        {
            for (int i = 0; i < key.Length; i++)
                hash = unchecked(hash * 16777619 ^ char.ToLowerInvariant(key[i]));
        }
        int idx = Mathf.Abs(hash) % sensorPalette.Length;
        return sensorPalette[idx];
    }

    // ── Runtime state ───────────────────────────────────────────

    private readonly HashSet<DetectionKey> activeDetections = new HashSet<DetectionKey>();
    private float nextCheckTime;

    // Sector graphics keyed by trackId|detectorType
    private readonly Dictionary<string, RadarPolylineGraphic> sectorLines = new Dictionary<string, RadarPolylineGraphic>();
    // Beam graphics keyed by "source→target"
    private readonly Dictionary<string, RadarPolylineGraphic> beamLines = new Dictionary<string, RadarPolylineGraphic>();
    // Scan status rings keyed by target trackId
    private readonly Dictionary<string, RingView> rings = new Dictionary<string, RingView>();
    private RectTransform radarGraphicRoot;

    private class RingView
    {
        public readonly List<Graphic> arcs = new List<Graphic>();
        // 与 arcs 平行；空字符串表示空闲段（无人扫描，空心描边）
        public readonly List<string> arcSensors = new List<string>();
        public Vector2 anchoredPosition;
        public float spin;
    }

    private void Start()
    {
        if (mapController == null)
            mapController = FindAnyObjectByType<PlaneDisplayController>();
        var store = PanelMemory.Load();
        ApplyMemory(store);
        if (store.planeIcon != null && store.planeIcon.saved && store.planeIcon.ringRadius > 0.05f)
            ringRadius = store.planeIcon.ringRadius;
        if (store.planeIcon != null && store.planeIcon.saved && store.planeIcon.hasRingOffset)
            ringOffset = new Vector2(store.planeIcon.ringOffsetX, store.planeIcon.ringOffsetY);
        if (store.planeIcon != null && store.planeIcon.saved && store.planeIcon.ringThickness > 0.01f)
            ringLineWidth = Mathf.Clamp(store.planeIcon.ringThickness, 0.02f, 40f);
        if (store.planeIcon != null && store.planeIcon.saved && store.planeIcon.radarLine2D > 0.01f)
        {
            sectorLineWidth = store.planeIcon.radarLine2D;
            beamLineWidth = store.planeIcon.radarLine2D;
        }
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
            return;
        var store = PanelMemory.Load();
        CaptureMemory(store);
        PanelMemory.Save(store);
    }

    private void Update()
    {
        if (Time.unscaledTime < nextCheckTime)
            return;

        nextCheckTime = Time.unscaledTime + Mathf.Max(0.02f, checkInterval);
        EvaluateDetections();

        if (mapController != null)
        {
            if (showDetectionSectors)
                UpdateSectorVisuals();
            if (showDetectionBeams)
                UpdateBeamVisuals();
            if (showDetectionRings)
                UpdateRingVisuals();
        }
    }

    // ── Detection logic (10-variable model) ─────────────────────

    private void EvaluateDetections()
    {
        CurrentDetections.Clear();
        ActiveSensors.Clear();

        if (receiver == null && autoFindReceiver)
            receiver = FlightDataStreamReceiver.Instance;
        if (receiver == null)
        {
            ClearDetections();
            return;
        }

        var positions = receiver.SnapshotPositions;
        var states = receiver.SnapshotSensors;
        if ((states == null || states.Count == 0) && !receiver.HasSensorEdits)
            states = receiver.SnapshotStates;
        if (positions == null || states == null || positions.Count == 0 || states.Count == 0)
        {
            ClearDetections();
            return;
        }

        var positionsByTrack = new Dictionary<string, FlightDataStreamReceiver.TrackPosition>();
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            if (p != null && !string.IsNullOrWhiteSpace(p.trackId))
                positionsByTrack[p.trackId] = p;
        }

        // Collect active sensors — 每架飞机一个扫描锥，完全由 10 变量决定
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (!IsSensorEnabled(s) || !positionsByTrack.TryGetValue(s.trackId, out var srcPos))
                continue;

            float roll = invertRollSign ? -srcPos.roll : srcPos.roll;
            bool spherical = IsSphericalRadar(s);
            ComputeBoresight(srcPos.heading, srcPos.pitch, s.yawAngle, s.pitchAngle, out float boresightAz, out float boresightEl);

            ActiveSensors.Add(new SensorInfo
            {
                trackId = s.trackId,
                detectorType = s.detectorType,
                radarStatus = s.radarStatus,
                lat = (float)srcPos.lat,
                lng = (float)srcPos.lng,
                alt = srcPos.alt,
                heading = srcPos.heading,
                pitch = srcPos.pitch,
                roll = roll,
                yawDeflection = s.yawAngle,
                pitchDeflection = s.pitchAngle,
                yawScanHalf = spherical ? 180f : Mathf.Clamp(s.yawScanAngle, 0f, 720f) * 0.5f,
                pitchScanHalf = spherical ? 180f : Mathf.Clamp(s.pitchScanAngle, 0f, 360f) * 0.5f,
                boresightAzimuth = boresightAz,
                boresightElevation = boresightEl,
                maxRangeMeters = GetSensorRangeMeters(s.trackId, s.detectorType),
                mountSign = spherical ? 0 : ClassifySensorMount(s.centerPoint1, s.centerPoint),
                spherical = spherical,
                rangeType = s.rangeType,
            });
        }

        // Run detections
        var currentDetections = new HashSet<DetectionKey>();
        for (int i = 0; i < ActiveSensors.Count; i++)
        {
            var sensor = ActiveSensors[i];
            var sourcePosition = positionsByTrack[sensor.trackId];

            foreach (var kv in positionsByTrack)
            {
                string targetTrackId = kv.Key;
                if (targetTrackId == sensor.trackId)
                    continue;

                var targetPosition = kv.Value;
                if (!TryDetect(sensor, targetPosition, out float distance, out float offAz, out float offEl))
                    continue;

                var key = new DetectionKey { sourceTrackId = sensor.trackId, targetTrackId = targetTrackId };
                currentDetections.Add(key);

                CurrentDetections.Add(new DetectionResult
                {
                    sourceTrackId = sensor.trackId,
                    targetTrackId = targetTrackId,
                    detectorType = sensor.detectorType,
                    distance = distance,
                    offAzimuth = offAz,
                    offElevation = offEl,
                    boresightAzimuth = sensor.boresightAzimuth,
                    boresightElevation = sensor.boresightElevation,
                    sourceLat = (float)sourcePosition.lat,
                    sourceLng = (float)sourcePosition.lng,
                    sourceAlt = sourcePosition.alt,
                    sourceHeading = sourcePosition.heading,
                    targetLat = (float)targetPosition.lat,
                    targetLng = (float)targetPosition.lng,
                    targetAlt = targetPosition.alt,
                });

                if (!activeDetections.Contains(key))
                {
                    LogDetection(sensor, targetTrackId, distance, offAz, offEl);
                    MarkDetectionOnProgress(sensor, targetTrackId);
                }
            }
        }

        if (logLostDetection)
        {
            foreach (var old in activeDetections)
            {
                if (!currentDetections.Contains(old))
                    Debug.Log($"[RadarDetectionSystem] LOST source={old.sourceTrackId} target={old.targetTrackId}");
            }
        }

        activeDetections.Clear();
        foreach (var d in currentDetections)
            activeDetections.Add(d);
    }

    private void ClearDetections()
    {
        if (logLostDetection && activeDetections.Count > 0)
        {
            foreach (var old in activeDetections)
                Debug.Log($"[RadarDetectionSystem] LOST source={old.sourceTrackId} target={old.targetTrackId}");
        }
        activeDetections.Clear();
    }

    // ── 10-variable geometry ────────────────────────────────────
    // 坐标系: x=东, y=上, z=北（与 GeoToLocalMeters 一致）；方位角自北顺时针。

    private struct SensorFrame
    {
        public Vector3 boresight; // 波束中心
        public Vector3 right;     // 波束右方（含横滚扭转）
        public Vector3 up;        // 波束上方（含横滚扭转）
    }

    /// <summary>波束指向叠加：方位 = heading − yaw_angle（左正），俯仰 = pitch + pitch_angle（上正）。</summary>
    private static void ComputeBoresight(float headingDeg, float pitchDeg, float yawDeflectionDeg, float pitchDeflectionDeg, out float azimuthDeg, out float elevationDeg)
    {
        azimuthDeg = Mathf.Repeat(headingDeg - yawDeflectionDeg + 180f, 360f) - 180f;
        elevationDeg = Mathf.Clamp(pitchDeg + pitchDeflectionDeg, -89.9f, 89.9f);
    }

    private static Vector3 DirFromAzEl(float azimuthDeg, float elevationDeg)
    {
        float a = azimuthDeg * Mathf.Deg2Rad;
        float e = elevationDeg * Mathf.Deg2Rad;
        float horiz = Mathf.Cos(e);
        return new Vector3(Mathf.Sin(a) * horiz, Mathf.Sin(e), Mathf.Cos(a) * horiz);
    }

    /// <summary>构建波束坐标系：横滚角绕波束轴扭转（正值为右压杆 → 扫描锥“上方向”倒向右翼）。</summary>
    private static SensorFrame BuildSensorFrame(SensorInfo s)
    {
        Vector3 b = DirFromAzEl(s.boresightAzimuth, s.boresightElevation);

        Vector3 r0 = Vector3.Cross(Vector3.up, b);
        if (r0.sqrMagnitude < 1e-6f)
            r0 = Vector3.right; // 波束接近竖直时的退化保护
        r0.Normalize();

        Vector3 u0 = Vector3.Cross(b, r0);

        float t = s.roll * Mathf.Deg2Rad;
        float cos = Mathf.Cos(t);
        float sin = Mathf.Sin(t);
        return new SensorFrame
        {
            boresight = b,
            right = r0 * cos - u0 * sin,
            up = u0 * cos + r0 * sin,
        };
    }

    /// <summary>波束坐标系内 (方位偏角, 俯仰偏角) 对应的世界方向（角度空间参数化）。</summary>
    private static Vector3 FrameDirection(SensorFrame f, float azOffDeg, float elOffDeg)
    {
        float a = azOffDeg * Mathf.Deg2Rad;
        float e = elOffDeg * Mathf.Deg2Rad;
        float cosE = Mathf.Cos(e);
        return (f.boresight * (cosE * Mathf.Cos(a)) + f.right * (cosE * Mathf.Sin(a)) + f.up * Mathf.Sin(e)).normalized;
    }

    public static Vector3 GeoToLocalMeters(double lng, double lat, float alt, float originLat, float originLng)
    {
        float latRad = originLat * Mathf.Deg2Rad;
        float metersPerDegLon = 111320f * Mathf.Cos(latRad);
        const float metersPerDegLat = 110540f;
        return new Vector3(((float)lng - originLng) * metersPerDegLon, alt, ((float)lat - originLat) * metersPerDegLat);
    }

    private static Vector3 GeoToLocalMeters(FlightDataStreamReceiver.TrackPosition pos, float originLat, float originLng)
    {
        return GeoToLocalMeters(pos.lng, pos.lat, pos.alt, originLat, originLng);
    }

    /// <summary>与三维扫描体、二维扇面共用的判定。目标在锥内且距离不超过射程才算扫到。</summary>
    public static bool IsInsideScanVolume(SensorInfo sensor, Vector3 toTargetMeters, out float distance, out float offAzimuth, out float offElevation)
    {
        return TryDetectCore(sensor, toTargetMeters, out distance, out offAzimuth, out offElevation);
    }

    public static Vector3 ScanRayDirection(SensorInfo sensor, float azOffDeg, float elOffDeg)
    {
        return FrameDirection(BuildSensorFrame(sensor), azOffDeg, elOffDeg);
    }

    public static float ConeHalfAngleDeg(SensorInfo sensor)
    {
        return Mathf.Max(sensor.yawScanHalf, sensor.pitchScanHalf);
    }

    /// <summary>底面椭圆：az=0 沿左右（yaw），az=90° 沿上下（pitch）。</summary>
    public static float EllipticalHalfRad(SensorInfo sensor, float azRad)
    {
        float yaw = Mathf.Max(0.5f, sensor.yawScanHalf) * Mathf.Deg2Rad;
        float pitch = Mathf.Max(0.5f, sensor.pitchScanHalf) * Mathf.Deg2Rad;
        float c = Mathf.Cos(azRad);
        float s = Mathf.Sin(azRad);
        float denom = (c * c) / (yaw * yaw) + (s * s) / (pitch * pitch);
        return 1f / Mathf.Sqrt(Mathf.Max(1e-8f, denom));
    }

    public static Vector3 ConeBoresight(SensorInfo sensor)
    {
        return ScanRayDirection(sensor, 0f, 0f);
    }

    public static void ConeBasis(SensorInfo sensor, out Vector3 axis, out Vector3 side, out Vector3 up)
    {
        axis = ConeBoresight(sensor);
        side = Vector3.Cross(axis, Vector3.up);
        if (side.sqrMagnitude < 1e-6f)
            side = Vector3.Cross(axis, Vector3.right);
        side.Normalize();
        up = Vector3.Cross(side, axis);
    }

    /// <summary>
    /// 圆锥判定：偏离波束中心的夹角不超过扫描半角，且距离不超过射程。
    /// 半角取左右/上下扫描半角中较大的一个，和三维圆锥一致。
    /// </summary>
    private static bool TryDetectCore(SensorInfo sensor, Vector3 toTargetMeters, out float distance, out float offAzimuth, out float offElevation)
    {
        distance = 0f; offAzimuth = 0f; offElevation = 0f;

        if (sensor.maxRangeMeters <= 0f)
            return false;

        distance = toTargetMeters.magnitude;
        if (distance <= 0.001f || distance > sensor.maxRangeMeters)
            return false;

        if (sensor.spherical)
            return true;

        Vector3 d = toTargetMeters / distance;
        SensorFrame f = BuildSensorFrame(sensor);
        float onBoresight = Vector3.Dot(d, f.boresight);
        if (onBoresight <= 0.0001f)
            return false;

        offAzimuth = Mathf.Atan2(Vector3.Dot(d, f.right), onBoresight) * Mathf.Rad2Deg;
        offElevation = Mathf.Asin(Mathf.Clamp(Vector3.Dot(d, f.up), -1f, 1f)) * Mathf.Rad2Deg;
        float yawLimit = Mathf.Max(0.5f, sensor.yawScanHalf);
        float pitchLimit = Mathf.Max(0.5f, sensor.pitchScanHalf);
        float nx = offAzimuth / yawLimit;
        float ny = offElevation / pitchLimit;
        return nx * nx + ny * ny <= 1f;
    }

    private bool TryDetect(SensorInfo sensor, FlightDataStreamReceiver.TrackPosition target, out float distance, out float offAzimuth, out float offElevation)
    {
        Vector3 sourceMeters = new Vector3(0f, sensor.alt, 0f) + StationOffsetMeters(sensor);
        Vector3 targetMeters = GeoToLocalMeters(target, sensor.lat, sensor.lng);
        return TryDetectCore(sensor, targetMeters - sourceMeters, out distance, out offAzimuth, out offElevation);
    }

    private static bool IsSensorEnabled(FlightDataStreamReceiver.TrackTimetableState s)
    {
        if (s == null || string.IsNullOrWhiteSpace(s.trackId)) return false;

        // 探测器状态仍作为显式开关：无效/关闭/故障时不扫描
        string status = NormalizeText(s.radarStatus);
        if (status.Contains("无效") || status.Contains("invalid") || status.Contains("off") ||
            status.Contains("disabled") || status.Contains("关闭") || status.Contains("故障") || status.Contains("fault"))
            return false;

        if (IsSphericalRadar(s))
            return true;

        // 新模型：左右/上下扫描范围 > 0 即存在传感器（替代旧 detectorType 白名单）
        return s.yawScanAngle > 0.01f && s.pitchScanAngle > 0.01f;
    }

    private static string NormalizeText(string v) => string.IsNullOrWhiteSpace(v) ? string.Empty : v.Trim().ToLowerInvariant();

    private FlightProgressController progressMarkers;

    private void MarkDetectionOnProgress(SensorInfo sensor, string targetTrackId)
    {
        if (progressMarkers == null)
        {
            progressMarkers = FindAnyObjectByType<FlightProgressController>();
            if (progressMarkers == null)
            {
                var all = Resources.FindObjectsOfTypeAll<FlightProgressController>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].gameObject.scene.IsValid())
                    {
                        progressMarkers = all[i];
                        break;
                    }
                }
            }
        }
        if (progressMarkers == null)
            return;

        Color color = GetSensorColor(sensor.trackId, sensor.detectorType);
        string detector = string.IsNullOrWhiteSpace(sensor.detectorType) ? "传感器" : sensor.detectorType;
        string info = $"{sensor.trackId} {detector}扫描到 {targetTrackId}";
        string key = $"scan|{sensor.trackId}|{detector}|{targetTrackId}";
        progressMarkers.AddViolationMarker(info, color, key);
    }

    private void LogDetection(SensorInfo sensor, string target, float distance, float offAz, float offEl)
    {
        if (!enableDetectionLog) return;
        Debug.Log($"[RadarDetectionSystem] DETECTED detector={sensor.detectorType} source={sensor.trackId} target={target} " +
                  $"distance={distance:F1}m maxRange={sensor.maxRangeMeters:F0}m " +
                  $"boresightAz={sensor.boresightAzimuth:F1} boresightEl={sensor.boresightElevation:F1} " +
                  $"offAz={offAz:+0.0;-0.0;0.0} offEl={offEl:+0.0;-0.0;0.0} " +
                  $"(yawScan±{sensor.yawScanHalf:F1} pitchScan±{sensor.pitchScanHalf:F1} roll={sensor.roll:F1})");
    }

    /// <summary>传感器位置：前=+1（机头），中/空=0，后=-1（机尾）。优先读 centerPoint1（传感器位置）。</summary>
    private static int ClassifySensorMount(string position, string fallback)
    {
        // centerPoint1 常被默认写成「中」，不能挡住 centerPoint 里的前/后。
        int signA = 0;
        int signB = 0;
        bool hasA = TryClassifyMount(position, out signA);
        bool hasB = TryClassifyMount(fallback, out signB);
        if (hasA && signA != 0)
            return signA;
        if (hasB && signB != 0)
            return signB;
        if (hasA)
            return signA;
        if (hasB)
            return signB;
        return 0;
    }

    private static bool TryClassifyMount(string text, out int sign)
    {
        sign = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.IndexOf("前", StringComparison.Ordinal) >= 0
            || text.IndexOf("机头", StringComparison.Ordinal) >= 0
            || text.IndexOf("nose", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            sign = 1;
            return true;
        }
        if (text.IndexOf("后", StringComparison.Ordinal) >= 0
            || text.IndexOf("机尾", StringComparison.Ordinal) >= 0
            || text.IndexOf("tail", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("rear", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("aft", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            sign = -1;
            return true;
        }
        if (text.IndexOf("中", StringComparison.Ordinal) >= 0
            || text.IndexOf("mid", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("center", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    /// <summary>前=沿机头，后=沿机尾，中=图标中心。扇面、探测线和扫描判定共用这个起点。</summary>
    private Vector2 SensorDrawCenter(SensorInfo sensor)
    {
        Vector2 center = mapController.ConvertToAnchoredPosition(sensor.lng, sensor.lat);
        return center + StationPixelOffset(sensor);
    }

    private Vector2 StationPixelOffset(SensorInfo sensor)
    {
        if (mapController == null)
            return Vector2.zero;
        float tuned = RadarStationTuner.Instance != null
            ? RadarStationTuner.Instance.Offset2D(sensor.mountSign)
            : bodyStationOffsetPixels * sensor.mountSign;
        float along = tuned + 10f * mapController.PlaneIconScale * sensor.mountSign;
        if (Mathf.Abs(along) < 0.01f)
            return Vector2.zero;
        return mapController.MapDirection(sensor.heading) * along;
    }

    /// <summary>三维显示模型长约 18 单位，前/后只偏到机头或机尾，不再用二维图标的像素偏移。</summary>
    public const float VisualNoseOffsetUnits = 8f;

    public Vector3 StationOffsetMeters(SensorInfo sensor)
    {
        if (mapController == null)
            return Vector3.zero;
        if (!mapController.TryGetDisplayScale(sensor.lat, out _, out _, out float uniform) || uniform < 1e-6f)
            return Vector3.zero;
        float alongUnits = RadarStationTuner.Instance != null
            ? RadarStationTuner.Instance.Offset3D(sensor.mountSign)
            : (FlightSceneView.Active != null ? FlightSceneView.Active.ModelLength * 0.45f : VisualNoseOffsetUnits) * sensor.mountSign;
        if (Mathf.Abs(alongUnits) < 0.01f)
            return Vector3.zero;
        float meters = alongUnits / uniform;
        float headingRad = sensor.heading * Mathf.Deg2Rad;
        return new Vector3(Mathf.Sin(headingRad) * meters, 0f, Mathf.Cos(headingRad) * meters);
    }

    private Vector2 BeamSourceCenter(DetectionResult det)
    {
        for (int i = 0; i < ActiveSensors.Count; i++)
        {
            var sensor = ActiveSensors[i];
            if (sensor.trackId == det.sourceTrackId && sensor.detectorType == det.detectorType)
                return SensorDrawCenter(sensor);
        }
        return mapController.ConvertToAnchoredPosition(det.sourceLng, det.sourceLat);
    }

    // ── UI: 2D projection of the scan frustum ───────────────────
    //
    // 把三维扫描锥投影到二维地图：锥内每个方向 d 的水平射程 = maxRange·cos(世界仰角)，
    // 按世界方位角分箱取最大射程 → 得到外弧半径随横滚/俯仰不对称收缩的楔形轮廓。
    // 无横滚时退化为以波束方位为中心、半角 yaw_scan_angle/2 的普通扇形。
    // ─────────────────────────────────────────────────────────────

    private void UpdateSectorVisuals()
    {
        var activeKeys = new HashSet<string>();

        foreach (var sensor in ActiveSensors)
        {
            if (sensor.maxRangeMeters <= 0f)
                continue;

            string key = MakeSensorKey(sensor.trackId, sensor.detectorType);
            if (!IsSensorVisible(sensor.trackId, sensor.detectorType))
                continue; // 隐藏的传感器不绘制，陈旧图形在下方统一关闭

            activeKeys.Add(key);

            if (!sectorLines.TryGetValue(key, out var lr))
            {
                lr = CreateLineRenderer($"RadarSector_{key}", sectorSortingOrder, sectorLineWidth);
                sectorLines[key] = lr;
            }

            Color edge = GetSensorColor(sensor.trackId, sensor.detectorType);
            lr.startWidth = sectorLineWidth;
            lr.endWidth = sectorLineWidth;
            lr.startColor = edge;
            lr.endColor = edge;

            Vector2 center = SensorDrawCenter(sensor);
            var points = BuildSectorPolyline(sensor, center);
            if (points.Count < 4)
            {
                lr.gameObject.SetActive(false);
                continue;
            }

            lr.positionCount = points.Count;
            lr.SetPositions(points.ToArray());
            lr.gameObject.SetActive(true);
        }

        // Hide stale sectors
        foreach (var kv in sectorLines)
        {
            if (!activeKeys.Contains(kv.Key) && kv.Value != null)
                kv.Value.gameObject.SetActive(false);
        }
    }

    /// <summary>二维扇面用圆锥地面轮廓，东西南北同比例，避免地图经纬拉伸把扇面拉扁。</summary>
    private List<Vector3> BuildSectorPolyline(SensorInfo sensor, Vector2 center)
    {
        if (mapController == null)
        {
            return new List<Vector3>
            {
                new Vector3(center.x, center.y, 0f),
                new Vector3(center.x, center.y + 12f, 0f),
                new Vector3(center.x, center.y, 0f)
            };
        }

        if (!mapController.TryGetDisplayScale(sensor.lat, out _, out _, out float units))
            units = 0.01f;
        Vector2 east = mapController.MapEastDir();
        Vector2 north = mapController.MapNorthDir();
        if (sensor.spherical)
        {
            const int circleSeg = 64;
            var circle = new List<Vector3>(circleSeg + 1);
            float circleRadius = sensor.maxRangeMeters * units;
            for (int i = 0; i <= circleSeg; i++)
            {
                float ang = Mathf.PI * 2f * i / circleSeg;
                Vector2 dir = east * Mathf.Sin(ang) + north * Mathf.Cos(ang);
                circle.Add(new Vector3(center.x + dir.x * circleRadius, center.y + dir.y * circleRadius, 0f));
            }
            return circle;
        }
        ConeBasis(sensor, out Vector3 axis, out Vector3 side, out Vector3 up);
        float rangeMeters = sensor.maxRangeMeters;

        const int seg = 64;
        var cloud = new List<Vector2>(seg + 1) { center };
        for (int i = 0; i < seg; i++)
        {
            float ang = Mathf.PI * 2f * i / seg;
            // 与三维锥体、触发判定同一套椭圆底：左右半角管左右，上下半角管上下。
            float halfI = EllipticalHalfRad(sensor, ang);
            float along = Mathf.Cos(halfI) * rangeMeters;
            float radius = Mathf.Sin(halfI) * rangeMeters;
            Vector3 offset = axis * along + (side * Mathf.Cos(ang) + up * Mathf.Sin(ang)) * radius;
            Vector2 dir = east * offset.x + north * offset.z;
            cloud.Add(center + dir * units);
        }

        var hull = ConvexHull(cloud);
        if (hull.Count < 3)
        {
            return new List<Vector3>
            {
                new Vector3(center.x, center.y, 0f),
                new Vector3(center.x, center.y + 12f, 0f),
                new Vector3(center.x, center.y, 0f)
            };
        }

        var points = new List<Vector3>(hull.Count + 1);
        for (int i = 0; i < hull.Count; i++)
            points.Add(new Vector3(hull[i].x, hull[i].y, 0f));
        points.Add(points[0]);
        return points;
    }

    private static List<Vector2> ConvexHull(List<Vector2> points)
    {
        var src = new List<Vector2>(points);
        src.Sort((a, b) =>
        {
            int c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.y.CompareTo(b.y);
        });
        var hull = new List<Vector2>(src.Count);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            for (int i = 0; i < src.Count; i++)
            {
                Vector2 p = pass == 0 ? src[i] : src[src.Count - 1 - i];
                while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    /// <summary>圆锥在地图上有水平投影的方位跨度。返回 false 表示几乎没有地面投影。</summary>
    private static bool TryConeFootprintSpan(Vector3 axis, float cosLimit, out float centerAz, out float halfSpan)
    {
        float horiz = Mathf.Sqrt(axis.x * axis.x + axis.z * axis.z);
        centerAz = horiz > 1e-4f ? Mathf.Atan2(axis.x, axis.z) : 0f;
        float ay2 = axis.y * axis.y;
        float need = cosLimit * cosLimit - ay2;
        if (horiz < 1e-4f)
        {
            halfSpan = need <= 0f ? Mathf.PI : 0f;
            return need <= 0f;
        }

        float cos2 = need / (horiz * horiz);
        if (cos2 <= 0f)
        {
            halfSpan = Mathf.PI;
            return true;
        }
        if (cos2 >= 1f)
        {
            halfSpan = 0f;
            return false;
        }
        halfSpan = Mathf.Acos(Mathf.Sqrt(cos2));
        return halfSpan > 0.01f;
    }

    /// <summary>该方位上仍落在圆锥内的最大水平距离，闭式解，采样不会跳。</summary>
    private static float MaxHorizontalReach(Vector3 axis, float azimuth, float cosLimit, float rangeMeters)
    {
        float h = Mathf.Sin(azimuth) * axis.x + Mathf.Cos(azimuth) * axis.z;
        float amp = Mathf.Sqrt(h * h + axis.y * axis.y);
        if (amp < cosLimit + 1e-5f)
            return 0f;

        float delta = Mathf.Acos(Mathf.Clamp(cosLimit / amp, -1f, 1f));
        float alpha = Mathf.Atan2(axis.y, h);
        float lo = alpha - delta;
        float hi = alpha + delta;
        float bestCos = lo <= 0f && hi >= 0f
            ? 1f
            : Mathf.Max(Mathf.Cos(lo), Mathf.Cos(hi));
        return Mathf.Max(0f, bestCos) * rangeMeters;
    }

    /// <summary>测量地图上 1 米对应的像素数（沿纬度方向微偏移法，沿用旧实现）。</summary>
    private float PixelsPerMeter(float lng, float lat)
    {
        const float metersPerDegreeLat = 110540f;
        float smallOffsetDeg = 1f / metersPerDegreeLat;
        Vector2 p0 = mapController.ConvertToAnchoredPosition(lng, lat);
        Vector2 p1 = mapController.ConvertToAnchoredPosition(lng, lat + smallOffsetDeg);
        return Vector2.Distance(p0, p1);
    }

    // ── UI: Detection beams ─────────────────────────────────────
    //
    // Draws a line from source to target for each active detection.
    // ─────────────────────────────────────────────────────────────

    private void UpdateBeamVisuals()
    {
        if (mapController != null && mapController.IsSuppressingRealtimeTrails)
        {
            foreach (var kv in beamLines)
                if (kv.Value != null)
                    kv.Value.gameObject.SetActive(false);
            return;
        }

        var activeKeys = new HashSet<string>();

        foreach (var det in CurrentDetections)
        {
            if (!IsSensorVisible(det.sourceTrackId, det.detectorType))
                continue; // 隐藏的传感器不绘制探测波束

            string key = $"{det.sourceTrackId}→{det.targetTrackId}";
            activeKeys.Add(key);

            if (!beamLines.TryGetValue(key, out var lr))
            {
                lr = CreateLineRenderer($"RadarBeam_{key}", beamSortingOrder, beamLineWidth);
                beamLines[key] = lr;
            }

            Color beam = GetSensorColor(det.sourceTrackId, det.detectorType);
            lr.startWidth = beamLineWidth;
            lr.endWidth = beamLineWidth;
            lr.startColor = beam;
            lr.endColor = beam;

            Vector2 src = BeamSourceCenter(det);
            Vector2 tgt = mapController.ConvertToAnchoredPosition(det.targetLng, det.targetLat);

            lr.positionCount = 2;
            lr.SetPosition(0, new Vector3(src.x, src.y, 0));
            lr.SetPosition(1, new Vector3(tgt.x, tgt.y, 0));
            lr.gameObject.SetActive(true);
        }

        // Hide stale beams
        foreach (var kv in beamLines)
        {
            if (!activeKeys.Contains(kv.Key) && kv.Value != null)
                kv.Value.gameObject.SetActive(false);
        }
    }

    // ── UI: Scan status rings ───────────────────────────────────
    //
    // 每架飞机一个跟随运动的空心圆环：未被扫描时空圆（中性色）；
    // 被 N 个可见传感器扫描到时，圆环等分为 N 段弧，各段显示对应传感器颜色。
    // ─────────────────────────────────────────────────────────────

    private void UpdateRingVisuals()
    {
        if (receiver == null)
            return;

        var positions = receiver.SnapshotPositions;
        if (positions == null || positions.Count == 0)
        {
            foreach (var kv in rings)
                SetRingVisible(kv.Value, false);
            return;
        }

        // 目标飞机 → 当前扫描到它的可见传感器键（稳定排序）
        var detectionsByTarget = new Dictionary<string, List<string>>();
        foreach (var det in CurrentDetections)
        {
            if (!IsSensorVisible(det.sourceTrackId, det.detectorType))
                continue;

            if (!detectionsByTarget.TryGetValue(det.targetTrackId, out var keys))
            {
                keys = new List<string>();
                detectionsByTarget[det.targetTrackId] = keys;
            }

            string sensorKey = MakeSensorKey(det.sourceTrackId, det.detectorType);
            if (!keys.Contains(sensorKey))
                keys.Add(sensorKey);
        }

        var activeTargets = new HashSet<string>();
        foreach (var pos in positions)
        {
            if (pos == null || string.IsNullOrWhiteSpace(pos.trackId))
                continue;

            string target = pos.trackId;
            activeTargets.Add(target);

            if (!rings.TryGetValue(target, out var ring))
            {
                ring = new RingView();
                rings[target] = ring;
            }

            detectionsByTarget.TryGetValue(target, out var keys);
            keys?.Sort(StringComparer.Ordinal);
            SyncRingPartition(ring, keys);

            Vector2 center = mapController.ConvertToAnchoredPosition((float)pos.lng, (float)pos.lat);
            Vector2 nose = mapController.MapDirection(pos.heading);
            if (nose.sqrMagnitude < 1e-6f)
                nose = Vector2.up;
            Vector2 right = new Vector2(nose.y, -nose.x);
            ring.anchoredPosition = center + right * ringOffset.x + nose * ringOffset.y;
            ring.spin = -Mathf.Atan2(nose.x, nose.y) * Mathf.Rad2Deg;

            for (int i = 0; i < ring.arcs.Count; i++)
            {
                var arc = ring.arcs[i];
                if (arc == null)
                    continue;

                string sensorKey = i < ring.arcSensors.Count ? ring.arcSensors[i] : string.Empty;
                if (string.IsNullOrEmpty(sensorKey))
                    arc.color = ringIdleColor;
                else
                {
                    Color fill = ColorFromSensorKey(sensorKey);
                    fill.a = Mathf.Max(fill.a, 0.85f);
                    arc.color = fill;
                    if (arc is RadarArcGraphic wedge)
                        wedge.SetFilled();
                }
                arc.rectTransform.anchoredPosition = ring.anchoredPosition;
                arc.rectTransform.localRotation = Quaternion.Euler(0f, 0f, ring.spin);
                arc.gameObject.SetActive(true);
            }
        }

        foreach (var kv in rings)
        {
            if (!activeTargets.Contains(kv.Key))
                SetRingVisible(kv.Value, false);
        }
    }

    private void SyncRingPartition(RingView ring, List<string> sensorKeys)
    {
        bool scanned = sensorKeys != null && sensorKeys.Count > 0;
        int wantCount = scanned ? sensorKeys.Count : 1;
        if (ring.arcs.Count == wantCount)
        {
            bool same = true;
            for (int i = 0; i < wantCount && same; i++)
            {
                string existing = i < ring.arcSensors.Count ? ring.arcSensors[i] : string.Empty;
                string want = scanned ? sensorKeys[i] : string.Empty;
                same = existing == want;
            }
            if (same)
                return;
        }

        foreach (var arc in ring.arcs)
            if (arc != null)
                Destroy(arc.gameObject);
        ring.arcs.Clear();
        ring.arcSensors.Clear();

        float span = 360f / wantCount;
        float gap = wantCount > 1 ? Mathf.Min(ringArcGapDegrees, span * 0.3f) : 0f;

        for (int i = 0; i < wantCount; i++)
        {
            float startDeg = i * span + gap * 0.5f;
            float endDeg = (i + 1) * span - gap * 0.5f;
            string sensorKey = scanned ? sensorKeys[i] : string.Empty;

            Graphic arc;
            if (scanned)
            {
                // 被扫描：填充色块（楔形），清楚表达"这一份来自哪个传感器"
                EnsureRadarGraphicRoot(GetMapRoot());
                var go = new GameObject($"Ring_{i}_{sensorKey}", typeof(RectTransform), typeof(CanvasRenderer), typeof(RadarArcGraphic));
                go.transform.SetParent(radarGraphicRoot != null ? radarGraphicRoot : transform, false);
                go.layer = GetMapRoot() != null ? GetMapRoot().gameObject.layer : 0;
                var wedge = go.GetComponent<RadarArcGraphic>();
                wedge.sortingOrder = ringSortingOrder;
                wedge.SetArc(startDeg, endDeg, ringRadius, ringArcSegments, 0f);
                wedge.color = ringIdleColor;
                arc = wedge;
            }
            else
            {
                // 空闲：空心描边圆
                var polyline = CreateLineRenderer($"Ring_{i}_idle", ringSortingOrder, ringLineWidth);
                int seg = Mathf.Max(4, ringArcSegments);
                var points = new List<Vector3>(seg + 1);
                for (int p = 0; p <= seg; p++)
                {
                    float angle = Mathf.Lerp(startDeg, endDeg, (float)p / seg) * Mathf.Deg2Rad;
                    points.Add(new Vector3(ringRadius * Mathf.Sin(angle), ringRadius * Mathf.Cos(angle), 0f));
                }
                polyline.SetPositions(points.ToArray());
                polyline.color = ringIdleColor;
                arc = polyline;
            }

            ring.arcs.Add(arc);
            ring.arcSensors.Add(sensorKey);
        }
    }

    private Color ColorFromSensorKey(string sensorKey)
    {
        int sep = sensorKey.IndexOf('|');
        if (sep < 0)
            return GetAutoSensorColor(sensorKey);
        return GetSensorColor(sensorKey.Substring(0, sep), sensorKey.Substring(sep + 1));
    }

    private static void SetRingVisible(RingView ring, bool visible)
    {
        if (ring == null)
            return;
        foreach (var arc in ring.arcs)
            if (arc != null)
                arc.gameObject.SetActive(visible);
    }

    private void EnsureRadarGraphicRoot(RectTransform mapRoot)
    {
        if (mapController != null)
        {
            var overlay = mapController.MapOverlayRoot;
            if (overlay != null)
            {
                radarGraphicRoot = overlay;
                return;
            }
        }

        var existing = mapRoot.Find("MapOverlay");
        if (existing == null)
            existing = mapRoot.Find("RadarGraphics");
        var go = existing != null ? existing.gameObject : new GameObject("MapOverlay", typeof(RectTransform));
        radarGraphicRoot = go.GetComponent<RectTransform>();
        radarGraphicRoot.SetParent(mapRoot, false);
        radarGraphicRoot.anchorMin = Vector2.zero;
        radarGraphicRoot.anchorMax = Vector2.one;
        radarGraphicRoot.offsetMin = Vector2.zero;
        radarGraphicRoot.offsetMax = Vector2.zero;
        radarGraphicRoot.pivot = new Vector2(0.5f, 0.5f);
        var pos = radarGraphicRoot.localPosition;
        pos.z = 0f;
        radarGraphicRoot.localPosition = pos;
        radarGraphicRoot.SetAsLastSibling();
    }

    private RadarPolylineGraphic CreateLineRenderer(string name, int sortingOrder, float width)
    {
        var mapRoot = GetMapRoot();
        EnsureRadarGraphicRoot(mapRoot);
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RadarPolylineGraphic));
        go.transform.SetParent(radarGraphicRoot != null ? radarGraphicRoot : mapRoot != null ? mapRoot : transform, false);
        go.layer = mapRoot != null ? mapRoot.gameObject.layer : 0;

        var graphic = go.GetComponent<RadarPolylineGraphic>();
        graphic.startWidth = width;
        graphic.endWidth = width;
        graphic.sortingOrder = sortingOrder;
        graphic.raycastTarget = false;
        return graphic;
    }

    private RectTransform GetMapRoot()
    {
        if (mapController == null)
            return null;
        if (mapController.MapRoot != null)
            return mapController.MapRoot;
        var field = typeof(PlaneDisplayController).GetField("mapRoot",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return field?.GetValue(mapController) as RectTransform;
    }

    private Rect GetMapBounds()
    {
        var rt = GetMapRoot();
        if (rt == null)
            return new Rect(-10000, -10000, 20000, 20000);
        var rect = rt.rect;
        return rect;
    }

    private static Vector3 ClampToMap(Vector3 p, Rect bounds)
    {
        float halfW = bounds.width * 0.5f;
        float halfH = bounds.height * 0.5f;
        p.x = Mathf.Clamp(p.x, -halfW, halfW);
        p.y = Mathf.Clamp(p.y, -halfH, halfH);
        return p;
    }
}
