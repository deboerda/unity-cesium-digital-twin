using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 基准飞机：在 Inspector 或运行时 F4 面板输入经纬度，二维地图上单独落点，
/// 方便对照地标调整 GeoMapping。不进数据流，不画轨迹，不参与雷达扫描。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ReferencePlanePin : MonoBehaviour
{
    public const string MarkerName = "基准飞机";

    [Header("显示")]
    [SerializeField] private bool visible;
    [SerializeField] private string label = MarkerName;

    [Header("地理坐标")]
    [SerializeField] private double longitude = 103.9653479;
    [SerializeField] private double latitude = 30.69831289;
    [SerializeField] private float altitude = 519.11f;
    [SerializeField] private float heading;

    [Header("外观")]
    [SerializeField] [Min(0.05f)] private float iconScale = 1f;
    [SerializeField] private Color iconColor = new Color(1f, 0.92f, 0.15f, 1f);

    private PlaneDisplayController display;
    private RectTransform icon;
    private Text caption;
    private Image iconImage;
    private bool panelOpen;
    private Rect panel = new Rect(12f, 80f, 380f, 520f);
    private Vector2 scroll;
    private readonly Dictionary<string, string> numberDrafts = new Dictionary<string, string>();
    private string lonText;
    private string latText;
    private string altText;

    public bool Visible
    {
        get => visible;
        set { visible = value; Refresh(); }
    }

    public double Longitude
    {
        get => longitude;
        set { longitude = value; Refresh(); }
    }

    public double Latitude
    {
        get => latitude;
        set { latitude = value; Refresh(); }
    }

    private void Awake()
    {
        visible = false;
    }

    private void OnEnable()
    {
        SyncFieldText();
        Refresh();
    }

    private void OnDisable()
    {
        if (!Application.isPlaying)
            DestroyMarker();
    }

    private void Update()
    {
        if (Application.isPlaying && Input.GetKeyDown(KeyCode.F4))
            panelOpen = !panelOpen;

        Refresh();
    }

    private void OnValidate()
    {
        SyncFieldText();
        if (!isActiveAndEnabled)
            return;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this != null && isActiveAndEnabled)
                Refresh();
        };
#endif
    }

    private void OnGUI()
    {
        if (!Application.isPlaying || !panelOpen)
            return;
        panel = GUI.Window(9144, panel, DrawPanel, "基准飞机  F4隐藏");
    }

    private void DrawPanel(int id)
    {
        WheelScroll();
        scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.ExpandHeight(true));
        GUILayout.Label("在 Inspector 或这里输入经纬度，二维地图即时落点。");
        visible = GUILayout.Toggle(visible, "显示基准飞机");
        GUILayout.BeginHorizontal();
        GUILayout.Label("经度", GUILayout.Width(40f));
        lonText = GUILayout.TextField(lonText ?? longitude.ToString("0.########"));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("纬度", GUILayout.Width(40f));
        latText = GUILayout.TextField(latText ?? latitude.ToString("0.########"));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("高度", GUILayout.Width(40f));
        altText = GUILayout.TextField(altText ?? altitude.ToString("0.##"));
        GUILayout.EndHorizontal();

        var displayNowEarly = ResolveDisplay();
        if (displayNowEarly != null)
        {
            displayNowEarly.PlaneIconScale = NumberRow("plane", "飞机(地图)", displayNowEarly.PlaneIconScale, 0.01f, 8f);
            displayNowEarly.TrailMapWidth = NumberRow("trail2d", "二维轨迹", displayNowEarly.TrailMapWidth, 0.05f, 24f);
            displayNowEarly.TrailSampleMeters = NumberRow("trailSample", "采样间隔", displayNowEarly.TrailSampleMeters, 5f, 5000f);
            displayNowEarly.TrailMaxPoints = NumberRow("trailMaxPts", "最大点数", displayNowEarly.TrailMaxPoints, 100f, 12000f);
            GUILayout.Label($"轨迹最长覆盖 {displayNowEarly.TrailSampleMeters * displayNowEarly.TrailMaxPoints / 1000f:0.0} km");
        }

        var radar = FindAnyObjectByType<RadarDetectionSystem>();
        if (radar != null)
        {
            radar.DetectionRingRadius = NumberRow("ring", "触发环", radar.DetectionRingRadius, 0.05f, 40f);
            radar.DetectionRingThickness = NumberRow("ringThick", "环粗细", radar.DetectionRingThickness, 0.02f, 24f);
            Vector2 ringOff = radar.DetectionRingOffset;
            ringOff.x = NumberRow("ringX", "环右舷", ringOff.x, -200f, 200f);
            ringOff.y = NumberRow("ringY", "环机头", ringOff.y, -200f, 200f);
            radar.DetectionRingOffset = ringOff;
            radar.SectorLineWidth = NumberRow("line2d", "二维线宽", radar.SectorLineWidth, 0.05f, 8f);
            radar.BeamLineWidth = radar.SectorLineWidth;
        }

        var flight = FlightSceneView.Active;
        if (flight != null)
        {
            flight.TrailWidthMeters = NumberRow("trail3d", "三维轨迹", flight.TrailWidthMeters, 0.2f, 80f);
            flight.SetModelLength(NumberRow("model3d", "三维机模", flight.ModelLength, 0.2f, 80f));
            flight.RadarEdgePixels = NumberRow("line3d", "三维线宽", flight.RadarEdgePixels, 0.2f, 6f);
            flight.AxisGizmoWidth = NumberRow("axisW", "坐标轴线宽", flight.AxisGizmoWidth, 0.5f, 12f);
        }

        iconScale = NumberRow("pin", "基准点", iconScale, 0.05f, 3f);

        var zoomCtrl = display != null && display.MapRoot != null
            ? display.MapRoot.GetComponent<MapZoomPanController>()
            : FindAnyObjectByType<MapZoomPanController>();
        if (zoomCtrl != null)
        {
            zoomCtrl.MaxZoom = NumberRow("zoom", "最大放大", zoomCtrl.MaxZoom, 2f, 120f);
        }

        var displayNow = ResolveDisplay();
        if (displayNow != null)
        {
            Vector2 xy = displayNow.ConvertToAnchoredPosition((float)longitude, (float)latitude);
            GUILayout.Label($"地图坐标  x={xy.x:0.0}  y={xy.y:0.0}  高度={altitude:0.#}m");
        }
        GUILayout.EndScrollView();

        if (GUILayout.Button("保存显示大小", GUILayout.Height(24f)))
            SavePlaneScale();
        if (GUILayout.Button("定位到地图", GUILayout.Height(24f)))
            ApplyLonLat();

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }

    private float NumberRow(string key, string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(64f));
        float slid = GUILayout.HorizontalSlider(Mathf.Clamp(value, min, max), min, max);
        string control = "f4num_" + key;
        GUI.SetNextControlName(control);
        bool focused = GUI.GetNameOfFocusedControl() == control;
        if (!numberDrafts.TryGetValue(key, out var draft))
            draft = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (!focused)
        {
            if (Mathf.Abs(slid - value) > 0.00001f)
                value = slid;
            draft = value.ToString("0.###", CultureInfo.InvariantCulture);
        }
        string edited = GUILayout.TextField(draft, GUILayout.Width(72f));
        numberDrafts[key] = edited;
        if (focused && float.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed))
            value = typed;
        GUILayout.EndHorizontal();
        return value;
    }

    private void WheelScroll()
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.ScrollWheel)
            return;
        scroll.y += e.delta.y * 18f;
        e.Use();
    }

    private void HideMarker(PlaneDisplayController displayNow)
    {
        if (icon != null)
            icon.gameObject.SetActive(false);
        if (displayNow == null || displayNow.MapRoot == null)
            return;
        var existing = displayNow.MapRoot.Find(MarkerName);
        if (existing != null)
            existing.gameObject.SetActive(false);
    }

    private void ApplyLonLat()
    {
        if (double.TryParse(lonText, out var lon))
            longitude = lon;
        if (double.TryParse(latText, out var lat))
            latitude = lat;
        if (float.TryParse(altText, out var alt))
            altitude = alt;
        Refresh();
    }

    public void Refresh()
    {
        var displayNow = ResolveDisplay();
        if (!visible || displayNow == null || displayNow.MapRoot == null)
        {
            HideMarker(displayNow);
            return;
        }

        EnsureMarker(displayNow);
        Vector2 anchored = displayNow.ConvertToAnchoredPosition((float)longitude, (float)latitude);
        icon.anchoredPosition = anchored;
        icon.localRotation = Quaternion.identity;
        icon.localScale = new Vector3(iconScale, iconScale, 1f);
        icon.gameObject.SetActive(true);
        if (iconImage != null)
            iconImage.color = iconColor;
        if (caption != null)
        {
            caption.text = string.IsNullOrWhiteSpace(label) ? MarkerName : label;
            caption.rectTransform.anchoredPosition = new Vector2(0f, 22f);
        }
    }

    private PlaneDisplayController ResolveDisplay()
    {
        if (display == null)
            display = GetComponent<PlaneDisplayController>() ?? FindAnyObjectByType<PlaneDisplayController>();
        return display;
    }

    private void EnsureMarker(PlaneDisplayController displayNow)
    {
        if (icon != null)
            return;

        RectTransform parent = displayNow.MapRoot;
        Transform existing = parent.Find(MarkerName);
        if (existing != null)
        {
            var old = existing as RectTransform;
            if (old != null && (old.sizeDelta.x > 20f || old.sizeDelta.y > 20f || old.childCount > 2))
            {
                if (Application.isPlaying)
                    Destroy(old.gameObject);
                else
                    DestroyImmediate(old.gameObject);
                existing = null;
            }
        }

        GameObject go;
        if (existing != null)
        {
            go = existing.gameObject;
            icon = existing as RectTransform;
        }
        else
        {
            go = new GameObject(MarkerName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            icon = go.GetComponent<RectTransform>();
            icon.SetParent(parent, false);
        }

        go.layer = parent.gameObject.layer;
        go.hideFlags = HideFlags.DontSave;
        icon.anchorMin = new Vector2(0.5f, 0.5f);
        icon.anchorMax = new Vector2(0.5f, 0.5f);
        icon.pivot = new Vector2(0.5f, 0.5f);
        icon.sizeDelta = new Vector2(10f, 10f);
        icon.localScale = Vector3.one;
        icon.SetAsLastSibling();

        iconImage = go.GetComponent<Image>();
        if (iconImage == null)
            iconImage = go.AddComponent<Image>();
        iconImage.raycastTarget = false;
        iconImage.color = iconColor;
        EnsureCrosshair(icon);

        Transform labelTf = icon.Find("Label");
        if (labelTf == null)
        {
            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
            labelGo.hideFlags = HideFlags.DontSave;
            labelTf = labelGo.transform;
            labelTf.SetParent(icon, false);
        }

        var labelRt = labelTf as RectTransform;
        labelRt.anchorMin = new Vector2(0.5f, 0.5f);
        labelRt.anchorMax = new Vector2(0.5f, 0.5f);
        labelRt.pivot = new Vector2(0.5f, 0.5f);
        labelRt.sizeDelta = new Vector2(72f, 16f);
        labelRt.anchoredPosition = new Vector2(0f, 22f);

        caption = labelTf.GetComponent<Text>();
        caption.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        caption.fontSize = 11;
        caption.alignment = TextAnchor.MiddleCenter;
        caption.color = Color.white;
        caption.raycastTarget = false;
        caption.text = string.IsNullOrWhiteSpace(label) ? MarkerName : label;
        var outline = labelTf.GetComponent<Outline>();
        if (outline != null)
        {
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);
        }
    }

    private static void EnsureCrosshair(RectTransform parent)
    {
        if (parent.Find("Crosshair") != null)
            return;
        var go = new GameObject("Crosshair", typeof(RectTransform));
        go.hideFlags = HideFlags.DontSave;
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(16f, 16f);
        CreateBar(rt, "H", new Vector2(16f, 1.5f));
        CreateBar(rt, "V", new Vector2(1.5f, 16f));
    }

    private static void CreateBar(RectTransform parent, string name, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.hideFlags = HideFlags.DontSave;
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        var img = go.GetComponent<Image>();
        img.color = new Color(1f, 0.15f, 0.12f, 0.95f);
        img.raycastTarget = false;
    }

    private void DestroyMarker()
    {
        if (icon == null)
            return;
        if (Application.isPlaying)
            Destroy(icon.gameObject);
        else
            DestroyImmediate(icon.gameObject);
        icon = null;
        caption = null;
        iconImage = null;
    }

    private void SavePlaneScale()
    {
        var displayNow = ResolveDisplay();
        if (displayNow == null)
            return;
        var store = PanelMemory.Load();
        if (store.planeIcon == null)
            store.planeIcon = new PanelMemory.PlaneIconTune();
        store.planeIcon.saved = true;
        store.planeIcon.scale = displayNow.PlaneIconScale;
        store.planeIcon.trailWidth = displayNow.TrailMapWidth;
        store.planeIcon.trailSampleMeters = displayNow.TrailSampleMeters;
        store.planeIcon.trailMaxPoints = displayNow.TrailMaxPoints;
        var radar = FindAnyObjectByType<RadarDetectionSystem>();
        if (radar != null)
        {
            store.planeIcon.ringRadius = radar.DetectionRingRadius;
            store.planeIcon.hasRingOffset = true;
            store.planeIcon.ringOffsetX = radar.DetectionRingOffset.x;
            store.planeIcon.ringOffsetY = radar.DetectionRingOffset.y;
            store.planeIcon.ringThickness = radar.DetectionRingThickness;
            store.planeIcon.radarLine2D = radar.SectorLineWidth;
        }
        if (FlightSceneView.Active != null)
        {
            store.planeIcon.radarLine3D = FlightSceneView.Active.RadarEdgePixels;
            store.planeIcon.modelLength = FlightSceneView.Active.ModelLength;
            store.planeIcon.trailWidth3D = FlightSceneView.Active.TrailWidthMeters;
            store.planeIcon.axisGizmoWidth = FlightSceneView.Active.AxisGizmoWidth;
        }
        PanelMemory.Save(store);
    }

    private void SyncFieldText()
    {
        lonText = longitude.ToString("0.########");
        latText = latitude.ToString("0.########");
        altText = altitude.ToString("0.##");
    }
}
