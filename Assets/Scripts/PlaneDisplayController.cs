using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class PlaneDisplayController : MonoBehaviour
{
    [Serializable]
    public class PlaneView
    {
        public string trackId;
        public RectTransform icon;
        public Text label;
        public RadarPolylineGraphic trail;
        public readonly List<RadarPolylineGraphic> completedTrails = new List<RadarPolylineGraphic>();
        public readonly List<Vector3> trailPoints = new List<Vector3>();
        public float lastHeading;
        public bool hasLastHeading;
    }

    private class TrackLineView
    {
        public string key;
        public string trackId;
        public string planId;
        public RadarPolylineGraphic line;
        public Color color;
    }

    [Header("UI")]
    [SerializeField] private RectTransform mapRoot;
    [SerializeField] private RectTransform planeIconPrefab;
    [SerializeField] private LineRenderer trailTemplate;
    [SerializeField] private Text debugText;

    [Header("Plane Labels")]
    [SerializeField] private bool showPlaneLabels = true;
    [SerializeField] private int labelFontSize = 14;
    [SerializeField] private Color labelColor = Color.white;
    [SerializeField] private Color labelOutlineColor = new Color(0, 0, 0, 0.8f);
    [SerializeField] private Vector2 labelOffset = new Vector2(0, 28f);

    [Header("Geo Range")]
    [SerializeField] private Vector2 longitudeRange = new Vector2(100f, 110f);
    [SerializeField] private Vector2 latitudeRange = new Vector2(20f, 30f);

    [Header("Game Range")]
    [SerializeField] private Vector2 xRange = new Vector2(-400f, 400f);
    [SerializeField] private Vector2 yRange = new Vector2(-250f, 250f);

    [Header("Plane")]
    [SerializeField] private bool autoFollowDataReceiver = true;
    [SerializeField] [Min(0.05f)] [Tooltip("二维飞机在地图坐标里的大小。随地图缩放，和雷达圈、轨迹同一比例，不锁屏幕像素。")]
    private float planeIconScale = 4f;
    [Tooltip("图标机头在贴图上朝右。90 使航向 0 竖直向上，航向增大为顺时针。")]
    [SerializeField] private float planeIconZeroHeadingOffset = 90f;
    [SerializeField] [Min(0.2f)] private float trailWidth = 3f;
    [Tooltip("航迹采样间隔（米）。二维三维共用；值越小轨迹越连续，值越大越省性能。")]
    [SerializeField] private float trailSampleMeters = 50f;
    [SerializeField] private float trailPointDistanceThreshold = 4f;
    [SerializeField] private float trailBreakDistance = 28f;
    [Tooltip("航迹最多保留的点数。二维三维共用，保证覆盖公里数一致。")]
    [SerializeField] private float trailMaxPoints = 12000f;
    public float TrailMaxPoints
    {
        get => Mathf.Max(16f, trailMaxPoints);
        set => trailMaxPoints = Mathf.Max(16f, value);
    }
    private float suppressRealtimeTrailUntil;
    public bool IsSuppressingRealtimeTrails => Time.unscaledTime < suppressRealtimeTrailUntil;

    [Header("Track Lines")]
    [SerializeField] private bool showReceivedTrackLines = true;
    [SerializeField] private LineRenderer trackLineTemplate;
    [SerializeField] private float trackLineWidth = 3.5f;
    [SerializeField]
    private Color[] trackLinePalette = new Color[]
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

    [Header("Debug")]
    [SerializeField] private bool enableDebugLog = true;

    private readonly Dictionary<string, PlaneView> planes = new Dictionary<string, PlaneView>();
    private readonly Dictionary<string, TrackLineView> trackLines = new Dictionary<string, TrackLineView>();
    private readonly Dictionary<string, int> trackColorIndices = new Dictionary<string, int>();
    private readonly Dictionary<string, Color> trailColorOverride = new Dictionary<string, Color>();
    private readonly Dictionary<string, bool> trailHiddenByTrack = new Dictionary<string, bool>();
    private bool isTrailVisible = true;
    private float trailSampleUnits;
    private int nextTrackColorIndex;
    private RectTransform mapOverlayRoot;
    private RectTransform planeLayerRoot;

    // World-space canvas + plane Image (Mesh2D-Lit, ZWrite On, local Z = -2)
    // occludes UI/Default graphics unless the overlay is closer and uses ZTest Always.
    private const float MapOverlayLocalZ = 0f;

    public bool IsTrailVisible => isTrailVisible;
    public RectTransform MapRoot => mapRoot;
    public Sprite MapSprite
    {
        get
        {
            if (mapRoot == null)
                return null;
            var img = mapRoot.GetComponent<Image>();
            return img != null ? img.sprite : null;
        }
    }
    public bool TryGetGeoRange(out Vector2 lonRange, out Vector2 latRange)
    {
        lonRange = longitudeRange;
        latRange = latitudeRange;
        return Mathf.Abs(longitudeRange.y - longitudeRange.x) > 1e-6f
            && Mathf.Abs(latitudeRange.y - latitudeRange.x) > 1e-6f;
    }
    public RectTransform PlaneIconPrefab => planeIconPrefab;
    public RectTransform MapOverlayRoot => GetMapOverlayRoot();
    public float PlaneIconScale
    {
        get => planeIconScale;
        set { planeIconScale = Mathf.Max(0.001f, value); ApplyIconScreenScale(); }
    }
    public float TrailSampleMeters
    {
        get => trailSampleMeters;
        set => trailSampleMeters = Mathf.Max(1f, value);
    }
    public float TrailMapWidth
    {
        get => trailWidth;
        set
        {
            trailWidth = Mathf.Max(0.2f, value);
            ApplyTrailWidth();
        }
    }

    private void Start()
    {
        if (mapRoot == null)
            mapRoot = GetComponent<RectTransform>();
        if (mapRoot == null)
        {
            LogError("Map root is missing.");
            return;
        }

        // Create the overlay before data-driven objects so it always remains the top map layer.
        GetPlaneLayerRoot();
        GetMapOverlayRoot();
        var store = PanelMemory.Load();
        ApplyMemory(store);
        if (store.planeIcon != null && store.planeIcon.saved)
        {
            planeIconScale = Mathf.Max(0.05f, store.planeIcon.scale);
            if (store.planeIcon.trailWidth > 0.05f)
                trailWidth = store.planeIcon.trailWidth;
            if (store.planeIcon.trailSampleMeters > 0.5f)
                trailSampleMeters = store.planeIcon.trailSampleMeters;
            if (store.planeIcon.trailMaxPoints > 16f)
                trailMaxPoints = store.planeIcon.trailMaxPoints;
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

    public void ApplyMemory(PanelMemory.Store store)
    {
        if (store?.trails == null)
            return;
        foreach (var item in store.trails)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.trackId))
                continue;
            if (item.hasColor && item.color != null)
                trailColorOverride[item.trackId] = item.color.ToColor();
            trailHiddenByTrack[item.trackId] = !item.visible;
            ApplyTrailVisuals(item.trackId);
        }
    }

    public void CaptureMemory(PanelMemory.Store store)
    {
        if (store == null)
            return;
        var map = new Dictionary<string, PanelMemory.TrailItem>(StringComparer.OrdinalIgnoreCase);
        if (store.trails != null)
        {
            foreach (var old in store.trails)
            {
                if (old != null && !string.IsNullOrWhiteSpace(old.trackId))
                    map[old.trackId] = old;
            }
        }
        foreach (var kv in trailColorOverride)
        {
            if (!map.TryGetValue(kv.Key, out var item) || item == null)
                item = new PanelMemory.TrailItem { trackId = kv.Key, visible = true };
            item.hasColor = true;
            item.color = PanelMemory.Color4.From(kv.Value);
            map[kv.Key] = item;
        }
        foreach (var kv in trailHiddenByTrack)
        {
            if (!map.TryGetValue(kv.Key, out var item) || item == null)
                item = new PanelMemory.TrailItem { trackId = kv.Key };
            item.visible = !kv.Value;
            map[kv.Key] = item;
        }
        store.trails = new List<PanelMemory.TrailItem>(map.Values);
    }

    private void LateUpdate()
    {
        if (mapOverlayRoot != null)
            mapOverlayRoot.SetAsLastSibling();
        ApplyIconScreenScale();
    }

    private void Update()
    {
        if (!autoFollowDataReceiver)
            return;

        var receiver = FlightDataStreamReceiver.Instance;
        if (receiver == null)
            return;

        // Update plane icons & realtime trails
        var positions = receiver.SnapshotPositions;
        if (positions != null && positions.Count > 0)
        {
            foreach (var position in positions)
            {
                if (position == null)
                    continue;
                UpdatePlanePosition(
                    position.trackId ?? "primary",
                    (float)position.lng, (float)position.lat, position.alt,
                    position.heading, position.pitch, position.roll);
            }
        }

        // Update received track lines (plans → positions as connected lines)
        if (showReceivedTrackLines)
            UpdateReceivedTrackLines(receiver.SnapshotTrackLines);

        if (debugText != null && receiver.PrimaryPosition != null)
        {
            debugText.text = $"Track: {receiver.PrimaryTrackId}\nLon: {receiver.PrimaryPosition.lng:F5}\nLat: {receiver.PrimaryPosition.lat:F5}\nAlt: {receiver.PrimaryPosition.alt:F1}\nHeading: {receiver.PrimaryPosition.heading:F1}";
        }
    }

    public void SetLongitudeRange(float min, float max) { longitudeRange = new Vector2(min, max); }
    public void SetLatitudeRange(float min, float max) { latitudeRange = new Vector2(min, max); }
    public void SetGameRange(float minX, float maxX, float minY, float maxY) { xRange = new Vector2(minX, maxX); yRange = new Vector2(minY, maxY); }

    public void ToggleTrails()
    {
        isTrailVisible = !isTrailVisible;
        foreach (var kv in planes)
            ApplyTrailVisuals(kv.Key);
        foreach (var kv in trackLines)
            if (kv.Value.line != null)
                kv.Value.line.gameObject.SetActive(isTrailVisible && showReceivedTrackLines);
    }

    public void UpdatePlanePosition(int planeIndex, float longitude, float latitude, float altitude, string planeName = "", float heading = 0f, float pitch = 0f, float roll = 0f)
    {
        UpdatePlanePosition(planeIndex.ToString(), longitude, latitude, altitude, heading, pitch, roll, planeName);
    }

    public void UpdatePlanePosition(string trackId, float longitude, float latitude, float altitude, float heading, float pitch = 0f, float roll = 0f, string planeName = "")
    {
        if (string.IsNullOrWhiteSpace(trackId))
            trackId = "unknown";

        PlaneView view = GetOrCreatePlaneView(trackId, planeName);
        Vector2 anchored = ConvertToAnchoredPosition(longitude, latitude);
        Vector3 localPos = new Vector3(anchored.x, anchored.y, 0f);

        // 采样间隔按米换算成地图单位，保证二维和三维一致。
        if (TryGetDisplayScale(latitude, out _, out _, out float unitsPerMeter) && unitsPerMeter > 1e-9f)
            trailSampleUnits = Mathf.Max(1e-5f, trailSampleMeters * unitsPerMeter);

        if (view.icon != null)
        {
            view.icon.anchoredPosition = anchored;
            view.icon.localRotation = Quaternion.Euler(0f, 0f, ConvertHeadingToUiRotation(heading));
            view.icon.gameObject.SetActive(true);
        }

        if (view.label != null)
        {
            view.label.rectTransform.anchoredPosition = anchored + labelOffset;
            // Push label z closer to camera so it renders in front of 3D plane models
            var lp = view.label.rectTransform.localPosition;
            lp.z = -4f;
            view.label.rectTransform.localPosition = lp;
            view.label.gameObject.SetActive(showPlaneLabels);
        }

        UpdateTrail(view, localPos);
        view.lastHeading = heading;
        view.hasLastHeading = true;
        ApplyIconMapScale(view);
    }

    public float GetMapZoom()
    {
        var zoomCtrl = mapRoot != null ? mapRoot.GetComponent<MapZoomPanController>() : null;
        return zoomCtrl != null ? Mathf.Max(0.0001f, zoomCtrl.Zoom) : 1f;
    }

    public void ApplyIconScreenScale()
    {
        foreach (var view in planes.Values)
            ApplyIconMapScale(view);
    }

    private void ApplyTrailWidth()
    {
        foreach (var view in planes.Values)
        {
            if (view == null)
                continue;
            if (view.trail != null)
                view.trail.startWidth = trailWidth;
            for (int i = 0; i < view.completedTrails.Count; i++)
            {
                if (view.completedTrails[i] != null)
                    view.completedTrails[i].startWidth = trailWidth;
            }
        }
        foreach (var line in trackLines.Values)
        {
            if (line != null && line.line != null)
                line.line.startWidth = trailWidth;
        }
    }

    private void ApplyIconMapScale(PlaneView view)
    {
        if (view == null)
            return;
        // 图标大小是地图单位，随地图缩放和雷达圈、轨迹一起变，不再锁屏幕像素。
        float iconScale = 0.2f * planeIconScale;
        if (view.icon != null)
            view.icon.localScale = new Vector3(iconScale, iconScale, iconScale);
        if (view.label != null)
        {
            view.label.rectTransform.localScale = new Vector3(iconScale, iconScale, 1f);
            view.label.rectTransform.anchoredPosition = view.icon != null
                ? view.icon.anchoredPosition + labelOffset * iconScale
                : view.label.rectTransform.anchoredPosition;
        }
    }

    public Vector2 ConvertToAnchoredPosition(float longitude, float latitude)
    {
        if (mapRoot == null)
            return Vector2.zero;
        float lon01 = Mathf.InverseLerp(longitudeRange.x, longitudeRange.y, longitude);
        float lat01 = Mathf.InverseLerp(latitudeRange.x, latitudeRange.y, latitude);
        float x = Mathf.Lerp(xRange.x, xRange.y, lon01);
        float y = Mathf.Lerp(yRange.x, yRange.y, lat01);
        return new Vector2(x, y);
    }

    /// <summary>
    /// GeoMapping.json 的线性映射：1 米在东西/南北方向各对应多少地图单位。
    /// 高度用两者的几何平均，避免锥体被压扁或拉成一根针。
    /// </summary>
    public bool TryGetDisplayScale(float latitude, out float unitsPerMeterEast, out float unitsPerMeterNorth, out float unitsPerMeterUp)
    {
        unitsPerMeterEast = 0.01f;
        unitsPerMeterNorth = 0.01f;
        unitsPerMeterUp = 0.01f;
        float lonSpan = Mathf.Abs(longitudeRange.y - longitudeRange.x);
        float latSpan = Mathf.Abs(latitudeRange.y - latitudeRange.x);
        float xSpan = Mathf.Abs(xRange.y - xRange.x);
        float ySpan = Mathf.Abs(yRange.y - yRange.x);
        if (lonSpan < 1e-6f || latSpan < 1e-6f || xSpan < 1f || ySpan < 1f)
            return false;

        float metersPerDegLon = 111320f * Mathf.Cos(latitude * Mathf.Deg2Rad);
        const float metersPerDegLat = 110540f;
        if (metersPerDegLon < 1f)
            return false;

        unitsPerMeterEast = (xSpan / lonSpan) / metersPerDegLon;
        unitsPerMeterNorth = (ySpan / latSpan) / metersPerDegLat;
        unitsPerMeterUp = Mathf.Sqrt(Mathf.Max(1e-8f, unitsPerMeterEast * unitsPerMeterNorth));
        return unitsPerMeterEast > 1e-8f && unitsPerMeterNorth > 1e-8f;
    }

    /// <summary>映射窗口中心和较长边，供三维相机对准同一块地面。</summary>
    public bool TryGetMapWindow(out float centerLng, out float centerLat, out float spanUnits)
    {
        centerLng = (longitudeRange.x + longitudeRange.y) * 0.5f;
        centerLat = (latitudeRange.x + latitudeRange.y) * 0.5f;
        spanUnits = Mathf.Max(Mathf.Abs(xRange.y - xRange.x), Mathf.Abs(yRange.y - yRange.x));
        return spanUnits > 1f;
    }

    /// <summary>地图上东、北各往哪个屏幕方向。纬度范围上大下小时，北是 -Y。</summary>
    public Vector2 MapEastDir()
    {
        float sign = Mathf.Sign((xRange.y - xRange.x) * (longitudeRange.y - longitudeRange.x));
        if (Mathf.Abs(sign) < 0.5f)
            sign = 1f;
        return new Vector2(sign, 0f);
    }

    public Vector2 MapNorthDir()
    {
        float sign = Mathf.Sign((yRange.y - yRange.x) * (latitudeRange.y - latitudeRange.x));
        if (Mathf.Abs(sign) < 0.5f)
            sign = 1f;
        return new Vector2(0f, sign);
    }

    public Vector2 MapDirection(float headingDeg)
    {
        float a = headingDeg * Mathf.Deg2Rad;
        Vector2 dir = MapEastDir() * Mathf.Sin(a) + MapNorthDir() * Mathf.Cos(a);
        return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.up;
    }

    private float ConvertHeadingToUiRotation(float heading)
    {
        // 只由航向驱动。贴图机头朝右；UI 正 Z 为逆时针。
        // 航向 0 = 竖直向上，增大为顺时针。
        return planeIconZeroHeadingOffset - heading;
    }

    public void NotifyPlaybackSeek()
    {
        suppressRealtimeTrailUntil = Time.unscaledTime + 0.45f;
        foreach (var view in planes.Values)
        {
            if (view == null || view.trail == null)
                continue;
            if (view.trailPoints.Count >= 2)
            {
                view.completedTrails.Add(view.trail);
                view.trail = CreateTrailRenderer($"{view.trackId}_{view.completedTrails.Count + 1}", view.trackId);
            }
            view.trailPoints.Clear();
            view.trail.positionCount = 0;
        }
    }

    public void ClearAllTrails()
    {
        foreach (var view in planes.Values)
        {
            view.trailPoints.Clear();
            if (view.trail != null)
                view.trail.positionCount = 0;
            for (int i = 0; i < view.completedTrails.Count; i++)
            {
                if (view.completedTrails[i] != null)
                    Destroy(view.completedTrails[i].gameObject);
            }
            view.completedTrails.Clear();
        }
        FindAnyObjectByType<FlightSceneView>()?.ClearTrails();
        foreach (var view in trackLines.Values)
        {
            if (view.line != null)
                view.line.positionCount = 0;
        }
    }

    public void SetReceivedTrackLinesVisible(bool visible)
    {
        showReceivedTrackLines = visible;
        foreach (var view in trackLines.Values)
            if (view.line != null)
                view.line.gameObject.SetActive(isTrailVisible && showReceivedTrackLines);
    }

    // ── Plane icons & realtime trails ───────────────────────────

    private PlaneView GetOrCreatePlaneView(string trackId, string planeName)
    {
        if (planes.TryGetValue(trackId, out var view))
            return view;

        view = new PlaneView { trackId = trackId };
        var iconParent = GetPlaneLayerRoot();
        if (planeIconPrefab != null && iconParent != null)
        {
            RectTransform icon = Instantiate(planeIconPrefab, iconParent);
            icon.name = string.IsNullOrWhiteSpace(planeName) ? $"Plane_{trackId}" : planeName;
            DisableGraphicDepthWrite(icon.GetComponent<Graphic>());
            view.icon = icon;
        }
        view.label = CreatePlaneLabel(trackId, planeName);
        view.trail = CreateTrailRenderer(trackId, trackId);
        GetMapOverlayRoot();
        planes[trackId] = view;
        return view;
    }

    private Text CreatePlaneLabel(string trackId, string planeName)
    {
        if (mapRoot == null)
            return null;

        var go = new GameObject($"Label_{trackId}");
        var parent = GetPlaneLayerRoot();
        go.transform.SetParent(parent != null ? parent : mapRoot, false);
        go.layer = mapRoot.gameObject.layer;

        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(120f, 24f);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);

        var outline = go.AddComponent<Outline>();
        outline.effectColor = labelOutlineColor;
        outline.effectDistance = new Vector2(1.5f, -1.5f);

        var text = go.AddComponent<Text>();
        text.text = string.IsNullOrWhiteSpace(planeName) ? trackId : planeName;
        text.fontSize = labelFontSize;
        text.color = labelColor;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.raycastTarget = false;

        text.gameObject.SetActive(showPlaneLabels);
        return text;
    }

    private RadarPolylineGraphic CreateTrailRenderer(string name, string trackId)
    {
        var trailObject = new GameObject($"Trail_{name}", typeof(RectTransform), typeof(CanvasRenderer), typeof(RadarPolylineGraphic));
        trailObject.transform.SetParent(GetMapOverlayRoot(), false);
        trailObject.layer = mapRoot != null ? mapRoot.gameObject.layer : 0;

        var graphic = trailObject.GetComponent<RadarPolylineGraphic>();
        graphic.startWidth = trailWidth;
        graphic.endWidth = graphic.startWidth;
        graphic.color = GetTrailColor(trackId);
        graphic.raycastTarget = false;
        graphic.gameObject.SetActive(IsPlaneTrailVisible(trackId));
        return graphic;
    }

    public IReadOnlyList<Color> TrailPalette => trackLinePalette;

    public Color GetTrailColor(string trackId)
    {
        if (!string.IsNullOrEmpty(trackId) && trailColorOverride.TryGetValue(trackId, out var color) && color.a > 0.01f)
            return color;
        return GetTrackLineColor(trackId);
    }

    public void SetTrailColor(string trackId, Color color)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return;
        trailColorOverride[trackId] = color;
        ApplyTrailVisuals(trackId);
    }

    public bool IsPlaneTrailVisible(string trackId)
    {
        if (!isTrailVisible)
            return false;
        return !trailHiddenByTrack.TryGetValue(trackId, out bool hidden) || !hidden;
    }

    public void SetPlaneTrailVisible(string trackId, bool visible)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return;
        trailHiddenByTrack[trackId] = !visible;
        ApplyTrailVisuals(trackId);
    }

    public void ClearTrail(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId) || !planes.TryGetValue(trackId, out var view) || view == null)
            return;
        view.trailPoints.Clear();
        if (view.trail != null)
            view.trail.positionCount = 0;
        for (int i = 0; i < view.completedTrails.Count; i++)
        {
            if (view.completedTrails[i] != null)
                Destroy(view.completedTrails[i].gameObject);
        }
        view.completedTrails.Clear();
        FindAnyObjectByType<FlightSceneView>()?.ClearTrails(trackId);
    }

    private void ApplyTrailVisuals(string trackId)
    {
        if (!planes.TryGetValue(trackId, out var view) || view == null)
            return;
        Color color = GetTrailColor(trackId);
        bool visible = IsPlaneTrailVisible(trackId);
        if (view.trail != null)
        {
            view.trail.color = color;
            view.trail.gameObject.SetActive(visible);
        }
        for (int i = 0; i < view.completedTrails.Count; i++)
        {
            var segment = view.completedTrails[i];
            if (segment == null)
                continue;
            segment.color = color;
            segment.gameObject.SetActive(visible);
        }
    }

    private void StartNewRealtimeTrailSegment(PlaneView view, Vector3 newPosition)
    {
        if (view.trail != null && view.trailPoints.Count >= 2)
            view.completedTrails.Add(view.trail);
        else if (view.trail != null)
            Destroy(view.trail.gameObject);

        view.trail = CreateTrailRenderer($"{view.trackId}_{view.completedTrails.Count + 1}", view.trackId);
        view.trailPoints.Clear();
        view.trailPoints.Add(newPosition);
        view.trail.positionCount = 1;
        view.trail.SetPositions(view.trailPoints.ToArray());
    }

    private void UpdateTrail(PlaneView view, Vector3 position)
    {
        if (view.trail == null)
            return;

        var clampedPosition = ClampToMap(position);
        position = new Vector3(clampedPosition.x, clampedPosition.y, 0f);

        // Seeking/teleport must not draw a connecting segment.
        if (IsSuppressingRealtimeTrails)
        {
            view.trailPoints.Clear();
            view.trail.positionCount = 0;
            return;
        }

        if (view.trailPoints.Count == 0)
        {
            view.trailPoints.Add(position);
        }
        else
        {
            Vector3 last = view.trailPoints[view.trailPoints.Count - 1];
            float distance = Vector3.Distance(last, position);
            float sample = trailSampleUnits > 1e-6f ? trailSampleUnits : trailPointDistanceThreshold;
            if (distance >= trailBreakDistance)
            {
                StartNewRealtimeTrailSegment(view, position);
            }
            else if (distance >= sample)
            {
                view.trailPoints.Add(position);
            }
        }

        int cap = Mathf.Max(16, (int)trailMaxPoints);
        if (view.trailPoints.Count > cap)
            view.trailPoints.RemoveRange(0, view.trailPoints.Count - cap);

        view.trail.color = GetTrailColor(view.trackId);
        view.trail.gameObject.SetActive(IsPlaneTrailVisible(view.trackId));
        view.trail.positionCount = view.trailPoints.Count;
        view.trail.SetPositions(view.trailPoints.ToArray());
    }

    // ── Received track lines ────────────────────────────────────
    //
    // Each TRACK_POSITIONS frame contains: tracks → plans → positions
    // We draw each plan as a separate LineRenderer.
    // Plans under the same trackId share the same color.
    // Different trackIds get different colors from the palette.
    // ───────────────────────────────────────────────────────────

    private void UpdateReceivedTrackLines(List<FlightDataStreamReceiver.TrackLine> receivedLines)
    {
        if (receivedLines == null || receivedLines.Count == 0)
            return;

        var activeLineKeys = new HashSet<string>();

        foreach (var receivedLine in receivedLines)
        {
            if (receivedLine == null || string.IsNullOrWhiteSpace(receivedLine.trackId))
                continue;

            string trackId = receivedLine.trackId;

            if (receivedLine.plans != null && receivedLine.plans.Count > 0)
            {
                for (int i = 0; i < receivedLine.plans.Count; i++)
                {
                    var plan = receivedLine.plans[i];
                    if (plan == null)
                        continue;
                    string planId = string.IsNullOrWhiteSpace(plan.planId) ? $"plan_{i + 1}" : plan.planId;
                    DrawTrackPlanLine(trackId, planId, plan.positions, activeLineKeys);
                }
            }
            else if (receivedLine.positions.Count > 0)
            {
                DrawTrackPlanLine(trackId, "track", receivedLine.positions, activeLineKeys);
            }
        }

        // Hide lines that are no longer in the data
        foreach (var kv in trackLines)
        {
            if (!activeLineKeys.Contains(kv.Key) && kv.Value.line != null)
                kv.Value.line.gameObject.SetActive(false);
        }
    }

    private void DrawTrackPlanLine(string trackId, string planId, List<FlightDataStreamReceiver.TrackPosition> positions, HashSet<string> activeLineKeys)
    {
        if (positions == null || positions.Count < 2)
            return;

        string lineKey = $"{trackId}/{planId}";
        activeLineKeys.Add(lineKey);

        TrackLineView view = GetOrCreateTrackLineView(lineKey, trackId, planId);
        if (view.line == null)
            return;

        // Convert all positions to UI coordinates
        var points = new Vector3[positions.Count];
        int validCount = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos == null)
                continue;
            Vector2 anchored = ConvertToAnchoredPosition((float)pos.lng, (float)pos.lat);
            Vector2 clamped = ClampToMap(anchored);
            points[validCount++] = new Vector3(clamped.x, clamped.y, 0f);
        }

        if (validCount < 2)
        {
            view.line.positionCount = 0;
            return;
        }

        // Apply color (same trackId → same color)
        Color color = view.color;
        view.line.startColor = color;
        view.line.endColor = color;

        view.line.gameObject.SetActive(isTrailVisible && showReceivedTrackLines);
        view.line.startWidth = trailWidth;
        view.line.positionCount = validCount;

        if (validCount == positions.Count)
        {
            view.line.SetPositions(points);
        }
        else
        {
            var trimmed = new Vector3[validCount];
            Array.Copy(points, trimmed, validCount);
            view.line.SetPositions(trimmed);
        }

        Log($"[TrackLines] {trackId}/{planId}: {validCount} points");
    }

    private TrackLineView GetOrCreateTrackLineView(string lineKey, string trackId, string planId)
    {
        if (trackLines.TryGetValue(lineKey, out var view))
            return view;

        view = new TrackLineView
        {
            key = lineKey,
            trackId = trackId,
            planId = planId,
            color = GetTrackLineColor(trackId),
            line = CreateTrackLineRenderer(trackId, planId),
        };
        trackLines[lineKey] = view;
        return view;
    }

    private RadarPolylineGraphic CreateTrackLineRenderer(string trackId, string planId)
    {
        var lineObject = new GameObject($"TrackLine_{trackId}_{planId}", typeof(RectTransform), typeof(CanvasRenderer), typeof(RadarPolylineGraphic));
        lineObject.transform.SetParent(GetMapOverlayRoot(), false);
        lineObject.layer = mapRoot != null ? mapRoot.gameObject.layer : 0;

        var graphic = lineObject.GetComponent<RadarPolylineGraphic>();
        graphic.startWidth = trailWidth;
        graphic.endWidth = graphic.startWidth;
        graphic.raycastTarget = false;
        graphic.gameObject.SetActive(isTrailVisible && showReceivedTrackLines);
        return graphic;
    }

    private Vector2 ClampToMap(Vector2 position)
    {
        if (mapRoot == null)
            return position;

        Rect bounds = mapRoot.rect;
        return new Vector2(
            Mathf.Clamp(position.x, bounds.xMin, bounds.xMax),
            Mathf.Clamp(position.y, bounds.yMin, bounds.yMax));
    }

    private RectTransform GetPlaneLayerRoot()
    {
        if (planeLayerRoot != null)
            return planeLayerRoot;
        if (mapRoot == null)
            return transform as RectTransform;

        var existing = mapRoot.Find("PlaneIcons");
        if (existing == null)
        {
            var go = new GameObject("PlaneIcons", typeof(RectTransform));
            existing = go.transform;
            existing.SetParent(mapRoot, false);
        }

        planeLayerRoot = existing as RectTransform;
        planeLayerRoot.anchorMin = Vector2.zero;
        planeLayerRoot.anchorMax = Vector2.one;
        planeLayerRoot.offsetMin = Vector2.zero;
        planeLayerRoot.offsetMax = Vector2.zero;
        planeLayerRoot.pivot = new Vector2(0.5f, 0.5f);
        planeLayerRoot.localScale = Vector3.one;
        planeLayerRoot.localRotation = Quaternion.identity;
        var pos = planeLayerRoot.localPosition;
        pos.z = 0f;
        planeLayerRoot.localPosition = pos;
        var inset = mapRoot.Find("飞行范围插图");
        if (inset != null)
        {
            inset.SetAsFirstSibling();
            planeLayerRoot.SetSiblingIndex(1);
        }
        else
            planeLayerRoot.SetAsFirstSibling();
        return planeLayerRoot;
    }

    private RectTransform GetMapOverlayRoot()
    {
        if (mapRoot == null)
            return mapOverlayRoot != null ? mapOverlayRoot : transform as RectTransform;

        if (mapOverlayRoot == null)
        {
            var overlay = mapRoot.Find("MapOverlay");
            if (overlay == null)
            {
                var go = new GameObject("MapOverlay", typeof(RectTransform), typeof(RectMask2D));
                overlay = go.transform;
                overlay.SetParent(mapRoot, false);
                go.layer = mapRoot.gameObject.layer;
            }

            mapOverlayRoot = overlay as RectTransform;
        }

        ConfigureMapOverlay(mapOverlayRoot);
        return mapOverlayRoot;
    }

    private void ConfigureMapOverlay(RectTransform overlay)
    {
        if (overlay == null)
            return;

        overlay.anchorMin = Vector2.zero;
        overlay.anchorMax = Vector2.one;
        overlay.offsetMin = Vector2.zero;
        overlay.offsetMax = Vector2.zero;
        overlay.pivot = new Vector2(0.5f, 0.5f);
        overlay.localScale = Vector3.one;
        overlay.localRotation = Quaternion.identity;
        var pos = overlay.localPosition;
        pos.z = MapOverlayLocalZ;
        overlay.localPosition = pos;
        overlay.SetAsLastSibling();

        var canvas = overlay.GetComponent<Canvas>();
        if (canvas != null)
        {
            // Nested overrideSorting breaks parent RectMask2D, so the zoomed map
            // would leak radar/trail graphics outside the viewport. Stay in the
            // same canvas and draw on top via sibling order + ZTest Always.
            canvas.overrideSorting = false;
        }

        var overlayMask = overlay.GetComponent<RectMask2D>();
        if (overlayMask != null)
            overlayMask.enabled = false;

        var leftoverRadar = mapRoot != null ? mapRoot.Find("RadarGraphics") : null;
        if (leftoverRadar != null && leftoverRadar != overlay)
            leftoverRadar.gameObject.SetActive(false);
    }

    private static void DisableGraphicDepthWrite(Graphic graphic)
    {
        if (graphic == null)
            return;

        // Plane icon template uses Mesh2D-Lit-Default (ZWrite On). That depth
        // write occludes later UI lines in a World Space canvas. Keep the sprite
        // but force the default UI material so icons participate in UGUI sorting.
        graphic.material = null;
    }

    private Color GetTrackLineColor(string trackId)
    {
        if (trackColorIndices.TryGetValue(trackId, out int index))
            return GetPaletteColor(index);
        index = nextTrackColorIndex++;
        trackColorIndices[trackId] = index;
        return GetPaletteColor(index);
    }

    private Color GetPaletteColor(int index)
    {
        if (trackLinePalette == null || trackLinePalette.Length == 0)
            return new Color(0.05f, 0.85f, 1f, 1f);
        return trackLinePalette[Mathf.Abs(index) % trackLinePalette.Length];
    }

    // ── Logging ─────────────────────────────────────────────────

    private void Log(string message)
    {
        if (enableDebugLog)
            Debug.Log($"[PlaneDisplayController] {message}");
    }

    private void LogError(string message)
    {
        if (enableDebugLog)
            Debug.LogError($"[PlaneDisplayController] {message}");
    }
}
