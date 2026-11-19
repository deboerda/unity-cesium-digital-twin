using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 飞行视图：用二维同一套经纬度、姿态、航迹和扫描锥。
/// 雷达是立体锥，只有目标真的在锥内才画扫描线。
/// </summary>
public class FlightSceneView : MonoBehaviour
{
    private const int FlightLayer = 3;

    private Camera flightCamera;
    private RawImage viewImage;
    private Transform root;
    private bool userHasControl;
    private bool flying;
    private bool panning;
    private bool orbiting;
    private bool zoomDragging;
    private float flySpeed = 8f;
    private float orbitDistance = 80f;
    private Vector3 orbitPivot;
    private float yaw;
    private float pitch;
    private Material skyMaterial;
    private Transform globe;
    private Transform mapPatch;
    private Transform hdPatch;
    private Material globeMaterial;
    private Material mapGlobeMaterial;
    private Material hdGlobeMaterial;
    private Texture groundTexture;
    private Texture hdTexture;
    private GameObject modelTemplate;
    private bool hasOrigin;
    private float originLat;
    private float originLng;
    private float nextRefresh;
    private readonly Dictionary<string, Transform> planes = new Dictionary<string, Transform>();
    private readonly Dictionary<string, Transform> followRigs = new Dictionary<string, Transform>();
    private readonly Dictionary<string, Camera> chaseCams = new Dictionary<string, Camera>();
    private readonly Dictionary<string, float> planeHeadings = new Dictionary<string, float>();
    private readonly List<string> followDropdownIds = new List<string>();
    private Dropdown followDropdown;
    private Text followHint;
    private bool followLocked;
    private string followTrackId;
    private bool ignoreFollowDropdown;
    private bool followSnap = true;
    private Vector3 followSmoothVel;
    private float followFovVel;
    private Vector3 unlockedPos;
    private float unlockedYaw;
    private float unlockedPitch;
    private float unlockedOrbitDistance = 80f;
    private Vector3 unlockedPivot;
    private readonly Dictionary<string, LineRenderer> tracks = new Dictionary<string, LineRenderer>();
    private readonly Dictionary<string, MeshFilter> volumes = new Dictionary<string, MeshFilter>();
    private readonly Dictionary<string, Vector3> volumeApexBuilt = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, string> volumeTrackId = new Dictionary<string, string>();
    private readonly Dictionary<string, LineRenderer> hits = new Dictionary<string, LineRenderer>();
    private readonly Dictionary<string, LineRenderer> volumeEdges = new Dictionary<string, LineRenderer>();
    private readonly List<VolumeOutlineJob> outlineJobs = new List<VolumeOutlineJob>();

    private struct VolumeOutlineJob
    {
        public string key;
        public RadarDetectionSystem.SensorInfo sensor;
        public Vector3 originMeters;
        public Color color;
    }
    private readonly Dictionary<string, List<Vector3>> liveTrails = new Dictionary<string, List<Vector3>>();
    private readonly Dictionary<string, float> stableAltitude = new Dictionary<string, float>();
    private readonly Dictionary<string, float> altitudeTime = new Dictionary<string, float>();
    private readonly Dictionary<string, Vector3> planeCenters = new Dictionary<string, Vector3>();
    private readonly Dictionary<string, TextMesh> labels = new Dictionary<string, TextMesh>();
    private Vector3 unitsPerMeter = Vector3.one;
    private static bool labelsVisible = true;

    public static FlightSceneView Active { get; private set; }
    public float RadarEdgePixels = 1.15f;
    public float RadarFillAlpha = 0.16f;
    public float TrailWidthMeters = 12f;
    public float AxisGizmoWidth = 3.5f;
    public float ModelLength { get; private set; } = 0.02f;
    public float ModelLengthMeters { get; set; } = 16f;
    private float appliedModelLength = 0.02f;

    public bool LabelsVisible => labelsVisible;

    public void SetModelLength(float length)
    {
        ApplyModelScale();
    }

    public void ApplyModelScale()
    {
        float meters = Mathf.Clamp(ModelLengthMeters, 1f, 80f);
        ModelLengthMeters = meters;
        float visual = meters * Mathf.Max(1e-8f, unitsPerMeter.x, unitsPerMeter.z);
        visual = Mathf.Max(1e-5f, visual);
        float ratio = visual / Mathf.Max(1e-8f, appliedModelLength);
        ModelLength = visual;
        appliedModelLength = visual;
        if (Mathf.Abs(ratio - 1f) < 0.001f)
            return;
        foreach (var kv in planes)
        {
            if (kv.Value != null)
                kv.Value.localScale *= ratio;
        }
    }

    public void ReleasePointerCapture()
    {
        flying = false;
        panning = false;
        orbiting = false;
        zoomDragging = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void SetLabelsVisible(bool visible)
    {
        labelsVisible = visible;
        foreach (var kv in labels)
        {
            if (kv.Value != null)
                kv.Value.gameObject.SetActive(visible);
        }
    }

    public void ToggleLabels()
    {
        SetLabelsVisible(!labelsVisible);
    }

    public void Bind(Camera camera, RawImage image = null, bool reframe = false)
    {
        Active = this;
        flightCamera = camera;
        var saved = PanelMemory.Load();
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.radarLine3D > 0.01f)
            RadarEdgePixels = saved.planeIcon.radarLine3D;
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.radarFillAlpha > 0.001f)
            RadarFillAlpha = Mathf.Clamp(saved.planeIcon.radarFillAlpha, 0.01f, 0.9f);
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.modelLength > 0.5f)
            ModelLengthMeters = Mathf.Clamp(saved.planeIcon.modelLength, 1f, 80f);
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.trailWidth3D > 0.2f)
            TrailWidthMeters = saved.planeIcon.trailWidth3D;
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.axisGizmoWidth > 0.4f)
            AxisGizmoWidth = saved.planeIcon.axisGizmoWidth;
        if (image != null)
            viewImage = image;
        if (reframe)
            FrameCamera(lastFocus, lastHeading, Mathf.Max(40f, lastSpan), true);
        else if (!RestoreCameraView())
            ApplyDefaultCameraIfNeeded();
        EnsureRoot();
        skyMaterial = null;
        EnsureSky();
        EnsureGlobe();
        RefreshDisplayScale();
        RefreshGlobe();
        if (flightCamera != null)
        {
            flightCamera.cullingMask = 1 << FlightLayer;
            flightCamera.clearFlags = CameraClearFlags.Skybox;
            var sky = flightCamera.GetComponent<Skybox>();
            if (sky == null)
                sky = flightCamera.gameObject.AddComponent<Skybox>();
            sky.material = skyMaterial;
            EnsureFlightLight();
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.globalTextureMipmapLimit = 0;
            ApplyClipPlanes(flightCamera);
            Vector3 euler = flightCamera.transform.eulerAngles;
            yaw = euler.y;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }
        EnsureFollowDropdown();
    }

    private void Update()
    {
        if (flightCamera == null || !flightCamera.enabled)
            return;
        bool showFollowUi = viewImage != null && viewImage.gameObject.activeInHierarchy;
        if (followDropdown != null)
            followDropdown.gameObject.SetActive(showFollowUi);
        if (followHint != null)
            followHint.gameObject.SetActive(showFollowUi);
        HandleFollowHotkey();
        if (!followLocked)
            Navigate();
        TickAircraftPoses();
        ApplyFollowCamera();
        PinTrailsToAircraft();
        FaceLabels();
        StickRadarToAircraft();
        UpdateVolumeOutlines();
        if (!followLocked)
            PersistCameraIfNeeded();
        if (Time.unscaledTime < nextRefresh)
            return;
        nextRefresh = Time.unscaledTime + 0.2f;
        Refresh();
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
            SaveCameraView();
    }

    private void OnApplicationQuit()
    {
        if (Application.isPlaying)
            SaveCameraView();
    }

    private void EnsureRoot()
    {
        if (root != null)
            return;
        var existing = GameObject.Find("FlightSceneRoot");
        root = existing != null ? existing.transform : new GameObject("FlightSceneRoot").transform;
        SetLayer(root.gameObject);
        var placeholder = GameObject.Find("FlightViewPlaceholder");
        if (placeholder != null)
            placeholder.SetActive(false);
    }

    private void EnsureSky()
    {
        var shader = Shader.Find("HK132/FlightSkybox");
        if (shader == null)
            shader = Shader.Find("Skybox/Procedural");
        if (shader == null)
            return;
        skyMaterial = new Material(shader);
        skyMaterial.SetColor("_SkyZenith", new Color(0.16f, 0.46f, 0.92f));
        skyMaterial.SetColor("_SkyHorizon", new Color(0.62f, 0.80f, 0.94f));
        skyMaterial.SetColor("_Ground", new Color(0.18f, 0.24f, 0.16f));
        skyMaterial.SetColor("_SunColor", new Color(1f, 0.96f, 0.86f));
        skyMaterial.SetVector("_SunDir", new Vector4(0.38f, 0.62f, 0.55f, 0f));
        skyMaterial.SetFloat("_Exposure", 1.12f);
    }

    private static readonly Color GlobeGreen = new Color(0.16f, 0.22f, 0.14f, 1f);

    private void EnsureGlobe()
    {
        EnsureRoot();
        if (root == null)
            return;
        var leftover = root.Find("MapGround");
        if (leftover != null)
            leftover.gameObject.SetActive(false);

        if (globe == null)
        {
            var existing = root.Find("EarthGlobe");
            globe = existing != null ? existing : new GameObject("EarthGlobe").transform;
            globe.SetParent(root, false);
            if (globe.GetComponent<MeshFilter>() == null)
                globe.gameObject.AddComponent<MeshFilter>().sharedMesh = GlobeSphereMesh();
            if (globe.GetComponent<MeshRenderer>() == null)
                globe.gameObject.AddComponent<MeshRenderer>();
        }
        if (globeMaterial == null)
        {
            var sh = Shader.Find("Unlit/Color");
            globeMaterial = new Material(sh != null ? sh : Shader.Find("Sprites/Default"));
            globeMaterial.color = GlobeGreen;
        }
        var globeR = globe.GetComponent<MeshRenderer>();
        globeR.sharedMaterial = globeMaterial;
        globeR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        globeR.receiveShadows = false;
        SetLayer(globe.gameObject);
        globe.gameObject.SetActive(true);

        if (mapPatch == null)
        {
            var existing = root.Find("MapGlobePatch");
            mapPatch = existing != null ? existing : new GameObject("MapGlobePatch").transform;
            mapPatch.SetParent(root, false);
            if (mapPatch.GetComponent<MeshFilter>() == null)
                mapPatch.gameObject.AddComponent<MeshFilter>();
            if (mapPatch.GetComponent<MeshRenderer>() == null)
                mapPatch.gameObject.AddComponent<MeshRenderer>();
        }
        if (mapGlobeMaterial == null)
        {
            var sh = Shader.Find("HK132/MapGlobe");
            if (sh == null)
                sh = Shader.Find("Unlit/Texture");
            mapGlobeMaterial = new Material(sh != null ? sh : Shader.Find("Sprites/Default"));
            mapGlobeMaterial.SetColor("_EdgeColor", GlobeGreen);
            mapGlobeMaterial.SetFloat("_EdgeSoft", 0.055f);
            mapGlobeMaterial.color = Color.white;
        }
        var patchR = mapPatch.GetComponent<MeshRenderer>();
        patchR.sharedMaterial = mapGlobeMaterial;
        patchR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        patchR.receiveShadows = false;
        SetLayer(mapPatch.gameObject);
        mapPatch.gameObject.SetActive(true);

        if (hdPatch == null)
        {
            var existing = root.Find("MapGlobeHdPatch");
            hdPatch = existing != null ? existing : new GameObject("MapGlobeHdPatch").transform;
            hdPatch.SetParent(root, false);
            if (hdPatch.GetComponent<MeshFilter>() == null)
                hdPatch.gameObject.AddComponent<MeshFilter>();
            if (hdPatch.GetComponent<MeshRenderer>() == null)
                hdPatch.gameObject.AddComponent<MeshRenderer>();
        }
        if (hdGlobeMaterial == null)
        {
            var sh = Shader.Find("HK132/MapGlobe");
            if (sh == null)
                sh = Shader.Find("Unlit/Texture");
            hdGlobeMaterial = new Material(sh != null ? sh : Shader.Find("Sprites/Default"));
            hdGlobeMaterial.SetColor("_EdgeColor", GlobeGreen);
            hdGlobeMaterial.SetFloat("_EdgeSoft", 0.02f);
            hdGlobeMaterial.color = Color.white;
        }
        var hdR = hdPatch.GetComponent<MeshRenderer>();
        hdR.sharedMaterial = hdGlobeMaterial;
        hdR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        hdR.receiveShadows = false;
        SetLayer(hdPatch.gameObject);
        hdPatch.gameObject.SetActive(true);
    }

    private static Mesh GlobeSphereMesh()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(go);
        return mesh;
    }

    private void RefreshGlobe()
    {
        EnsureGlobe();
        var map = ResolveMap();
        Vector2 lonRange = new Vector2(97.2f, 110.5f);
        Vector2 latRange = new Vector2(26.2f, 34.9f);
        if (map != null)
            map.TryGetGeoRange(out lonRange, out latRange);
        float lat0 = hasOrigin ? originLat : (latRange.x + latRange.y) * 0.5f;
        float lng0 = hasOrigin ? originLng : (lonRange.x + lonRange.y) * 0.5f;
        Vector3 sw = ToVisual(RadarDetectionSystem.GeoToLocalMeters(lonRange.x, latRange.x, 0f, lat0, lng0));
        Vector3 se = ToVisual(RadarDetectionSystem.GeoToLocalMeters(lonRange.y, latRange.x, 0f, lat0, lng0));
        Vector3 nw = ToVisual(RadarDetectionSystem.GeoToLocalMeters(lonRange.x, latRange.y, 0f, lat0, lng0));
        Vector3 ne = ToVisual(RadarDetectionSystem.GeoToLocalMeters(lonRange.y, latRange.y, 0f, lat0, lng0));
        Vector3 center = (sw + se + nw + ne) * 0.25f;
        float width = Mathf.Max(1f, Vector3.Distance(new Vector3(sw.x, 0f, sw.z), new Vector3(se.x, 0f, se.z)));
        float depth = Mathf.Max(1f, Vector3.Distance(new Vector3(sw.x, 0f, sw.z), new Vector3(nw.x, 0f, nw.z)));
        float span = Mathf.Max(width, depth);
        float radius = Mathf.Max(span * 28f, 20000f);

        globe.position = new Vector3(center.x, -radius, center.z);
        globe.localScale = Vector3.one * (radius * 2f);
        globe.rotation = Quaternion.identity;

        var filter = mapPatch.GetComponent<MeshFilter>();
        filter.sharedMesh = BuildGlobePatchMesh(center, width, depth, radius, 0.02f);
        mapPatch.position = Vector3.zero;
        mapPatch.rotation = Quaternion.identity;
        mapPatch.localScale = Vector3.one;

        if (groundTexture == null)
            groundTexture = LoadMapTexture(map);
        if (mapGlobeMaterial != null)
        {
            if (groundTexture != null)
            {
                SharpenMapTexture(groundTexture);
                mapGlobeMaterial.mainTexture = groundTexture;
                mapGlobeMaterial.SetTexture("_MainTex", groundTexture);
            }
            mapGlobeMaterial.SetColor("_EdgeColor", GlobeGreen);
            mapGlobeMaterial.SetFloat("_EdgeSoft", 0.055f);
        }
        var inset = FindAnyObjectByType<MapInsetOverlay>(FindObjectsInactive.Include);
        if (inset != null)
        {
            hdTexture = inset.OverlayTexture ?? hdTexture;
            Vector3 hdSw = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)inset.LonMin, (float)inset.LatMin, 0f, lat0, lng0));
            Vector3 hdSe = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)inset.LonMax, (float)inset.LatMin, 0f, lat0, lng0));
            Vector3 hdNw = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)inset.LonMin, (float)inset.LatMax, 0f, lat0, lng0));
            Vector3 hdNe = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)inset.LonMax, (float)inset.LatMax, 0f, lat0, lng0));
            Vector3 hdCenter = (hdSw + hdSe + hdNw + hdNe) * 0.25f;
            float hdW = Mathf.Max(1f, Vector3.Distance(new Vector3(hdSw.x, 0f, hdSw.z), new Vector3(hdSe.x, 0f, hdSe.z)));
            float hdD = Mathf.Max(1f, Vector3.Distance(new Vector3(hdSw.x, 0f, hdSw.z), new Vector3(hdNw.x, 0f, hdNw.z)));
            var hdFilter = hdPatch.GetComponent<MeshFilter>();
            hdFilter.sharedMesh = BuildGlobePatchMesh(hdCenter, hdW, hdD, radius, 0.12f);
            hdPatch.position = Vector3.zero;
            hdPatch.rotation = Quaternion.identity;
            hdPatch.localScale = Vector3.one;
            if (hdGlobeMaterial != null && hdTexture != null)
            {
                SharpenMapTexture(hdTexture);
                hdGlobeMaterial.mainTexture = hdTexture;
                hdGlobeMaterial.SetTexture("_MainTex", hdTexture);
                hdGlobeMaterial.SetFloat("_EdgeSoft", 0.006f);
            }
            hdPatch.gameObject.SetActive(true);
        }
        else if (hdPatch != null)
            hdPatch.gameObject.SetActive(false);

        BuildMapLayerPatches(lat0, lng0, radius);

        if (flightCamera != null)
            flightCamera.farClipPlane = Mathf.Max(flightCamera.farClipPlane, radius * 20f, 4000000f);
    }

    private sealed class LayerPatch
    {
        public Transform transform;
        public Material material;
    }

    private readonly List<LayerPatch> layerPatches = new List<LayerPatch>();
    private float layerPatchRadius = -1f;
    private float layerPatchLat0;
    private float layerPatchLng0;
    private int layerPatchCount = -1;

    private LayerPatch GetLayerPatch(int index)
    {
        while (layerPatches.Count <= index)
        {
            int i = layerPatches.Count;
            var existing = root.Find("MapGlobeLayer_" + i);
            var t = existing != null ? existing : new GameObject("MapGlobeLayer_" + i).transform;
            t.SetParent(root, false);
            if (t.GetComponent<MeshFilter>() == null)
                t.gameObject.AddComponent<MeshFilter>();
            if (t.GetComponent<MeshRenderer>() == null)
                t.gameObject.AddComponent<MeshRenderer>();
            var sh = Shader.Find("HK132/MapGlobe");
            if (sh == null)
                sh = Shader.Find("Unlit/Texture");
            var mat = new Material(sh != null ? sh : Shader.Find("Sprites/Default"));
            mat.SetColor("_EdgeColor", GlobeGreen);
            mat.SetFloat("_EdgeSoft", 0.01f);
            mat.color = Color.white;
            var r = t.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            SetLayer(t.gameObject);
            layerPatches.Add(new LayerPatch { transform = t, material = mat });
        }
        return layerPatches[index];
    }

    /// <summary>三维同样叠 MapLayers.json 的每一层，大范围在下、高清在上。</summary>
    private void BuildMapLayerPatches(float lat0, float lng0, float radius)
    {
        var layers = MapLayerCatalog.Layers;
        // RefreshGlobe 每 0.2 秒跑一次，几何没变就别重建网格。
        if (layerPatchCount == layers.Count
            && Mathf.Approximately(layerPatchRadius, radius)
            && Mathf.Approximately(layerPatchLat0, lat0)
            && Mathf.Approximately(layerPatchLng0, lng0))
        {
            for (int i = 0; i < layerPatches.Count; i++)
            {
                if (layerPatches[i]?.transform != null)
                    layerPatches[i].transform.gameObject.SetActive(i < layers.Count);
            }
            return;
        }
        layerPatchCount = layers.Count;
        layerPatchRadius = radius;
        layerPatchLat0 = lat0;
        layerPatchLng0 = lng0;

        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var def = layer?.def;
            if (def == null || layer.texture == null)
                continue;

            var patch = GetLayerPatch(i);
            Vector3 sw = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)def.lonMin, (float)def.latMin, 0f, lat0, lng0));
            Vector3 se = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)def.lonMax, (float)def.latMin, 0f, lat0, lng0));
            Vector3 nw = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)def.lonMin, (float)def.latMax, 0f, lat0, lng0));
            Vector3 ne = ToVisual(RadarDetectionSystem.GeoToLocalMeters((float)def.lonMax, (float)def.latMax, 0f, lat0, lng0));
            Vector3 center = (sw + se + nw + ne) * 0.25f;
            float w = Mathf.Max(1f, Vector3.Distance(new Vector3(sw.x, 0f, sw.z), new Vector3(se.x, 0f, se.z)));
            float d = Mathf.Max(1f, Vector3.Distance(new Vector3(sw.x, 0f, sw.z), new Vector3(nw.x, 0f, nw.z)));
            // 层间留够间距，远距离下深度精度不足会 z-fight。
            float lift = 0.04f + 0.03f * i;

            patch.transform.GetComponent<MeshFilter>().sharedMesh = BuildGlobePatchMesh(center, w, d, radius, lift);
            patch.transform.position = Vector3.zero;
            patch.transform.rotation = Quaternion.identity;
            patch.transform.localScale = Vector3.one;
            SharpenMapTexture(layer.texture);
            patch.material.mainTexture = layer.texture;
            patch.material.SetTexture("_MainTex", layer.texture);
            patch.transform.gameObject.SetActive(true);
        }

        for (int i = layers.Count; i < layerPatches.Count; i++)
        {
            if (layerPatches[i]?.transform != null)
                layerPatches[i].transform.gameObject.SetActive(false);
        }
    }

    private static Mesh BuildGlobePatchMesh(Vector3 center, float width, float depth, float radius, float lift)
    {
        const int n = 48;
        var verts = new Vector3[n * n];
        var uvs = new Vector2[n * n];
        var norms = new Vector3[n * n];
        Vector3 sphereC = new Vector3(center.x, -radius, center.z);
        for (int iz = 0; iz < n; iz++)
        {
            float v = iz / (n - 1f);
            float z = center.z + Mathf.Lerp(-depth * 0.5f, depth * 0.5f, v);
            for (int ix = 0; ix < n; ix++)
            {
                float u = ix / (n - 1f);
                float x = center.x + Mathf.Lerp(-width * 0.5f, width * 0.5f, u);
                Vector3 fromC = new Vector3(x, 0f, z) - sphereC;
                float mag = fromC.magnitude;
                Vector3 on = mag > 1e-4f ? fromC * ((radius + lift) / mag) : Vector3.up * (radius + lift);
                int i = iz * n + ix;
                verts[i] = sphereC + on;
                uvs[i] = new Vector2(u, v);
                norms[i] = on.normalized;
            }
        }
        var tris = new int[(n - 1) * (n - 1) * 6];
        int t = 0;
        for (int iz = 0; iz < n - 1; iz++)
        {
            for (int ix = 0; ix < n - 1; ix++)
            {
                int a = iz * n + ix;
                int b = a + 1;
                int c = a + n;
                int d = c + 1;
                tris[t++] = a; tris[t++] = c; tris[t++] = b;
                tris[t++] = c; tris[t++] = d; tris[t++] = b;
            }
        }
        var mesh = new Mesh { name = "MapGlobePatch" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.normals = norms;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Texture LoadMapTexture(PlaneDisplayController map)
    {
        if (map != null && map.MapSprite != null && map.MapSprite.texture != null)
            return map.MapSprite.texture;
        if (map != null && map.MapRoot != null)
        {
            var images = map.MapRoot.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] != null && images[i].sprite != null && images[i].sprite.texture != null
                    && images[i].gameObject.name != "飞行范围插图")
                    return images[i].sprite.texture;
            }
        }
        return Resources.Load<Texture2D>("成都");
    }

    private static void SharpenMapTexture(Texture tex)
    {
        if (tex == null)
            return;
        tex.filterMode = FilterMode.Bilinear;
        tex.anisoLevel = 16;
        tex.mipMapBias = 0f;
        tex.wrapMode = TextureWrapMode.Clamp;
    }

    private PlaneDisplayController ResolveMap()
    {
        return FindAnyObjectByType<PlaneDisplayController>(FindObjectsInactive.Include);
    }

    private void Refresh()
    {
        EnsureRoot();
        var receiver = FlightDataStreamReceiver.Instance;
        var radar = FindAnyObjectByType<RadarDetectionSystem>();
        if (receiver == null)
        {
            FrameCamera(Vector3.zero, 0f, 40f);
            return;
        }

        var positions = receiver.SnapshotPositions;
        if (positions == null || positions.Count == 0)
        {
            FrameCamera(Vector3.zero, 0f, 40f);
            return;
        }

        if (!hasOrigin)
        {
            originLat = (float)positions[0].lat;
            originLng = (float)positions[0].lng;
            hasOrigin = true;
        }
        RefreshDisplayScale();

        var usedPlanes = TickAircraftPoses();
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var kv in planeCenters)
        {
            if (!usedPlanes.Contains(kv.Key))
                continue;
            sum += kv.Value;
            n++;
        }
        HideUnused(planes, usedPlanes);
        HideUnused(followRigs, usedPlanes);
        HideUnused(chaseCams, usedPlanes);
        RefreshFollowDropdown(usedPlanes);
        DrawDifferenceLabels(positions);
        HideUnused(labels, usedPlanes);

        RefreshGlobe();
        DrawTracks(receiver);
        DrawVolumes(radar, positions);
        if (n > 0 && !followLocked)
            FrameToMappedWindow(sum / n, MeanHeading(positions), FormationSpan(positions));
    }

    private HashSet<string> TickAircraftPoses()
    {
        var used = new HashSet<string>();
        var receiver = FlightDataStreamReceiver.Instance;
        if (receiver == null)
            return used;
        var positions = receiver.SnapshotPositions;
        if (positions == null || positions.Count == 0)
            return used;
        if (!hasOrigin)
        {
            originLat = (float)positions[0].lat;
            originLng = (float)positions[0].lng;
            hasOrigin = true;
            RefreshDisplayScale();
        }
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos == null || string.IsNullOrWhiteSpace(pos.trackId))
                continue;
            float alt = StableAltitude(pos.trackId, pos.alt);
            Vector3 world = ToVisual(pos.lng, pos.lat, alt);
            var rig = GetFollowRig(pos.trackId);
            if (!rig.gameObject.activeSelf)
                rig.gameObject.SetActive(true);
            rig.SetPositionAndRotation(world, Quaternion.AngleAxis(pos.heading, Vector3.up));
            var plane = GetPlane(pos.trackId);
            if (!plane.gameObject.activeSelf)
                plane.gameObject.SetActive(true);
            plane.localRotation = Quaternion.Inverse(rig.rotation) * FlightAttitude(pos.heading, pos.pitch, pos.roll);
            plane.localPosition = Vector3.zero;
            Vector3 center = RendererCenter(plane.gameObject);
            plane.localPosition += rig.InverseTransformVector(world - center);
            LiftModelAboveGround(plane);
            planeCenters[pos.trackId] = world;
            planeHeadings[pos.trackId] = pos.heading;
            PlaceChaseOnRig(pos.trackId);
            AppendLiveTrail(pos.trackId, world);
            used.Add(pos.trackId);
        }
        return used;
    }

    private void StickRadarToAircraft()
    {
        for (int i = 0; i < outlineJobs.Count; i++)
        {
            var job = outlineJobs[i];
            float alt = StableAltitude(job.sensor.trackId, job.sensor.alt);
            Vector3 apex = AircraftApex(job.sensor, alt);
            job.originMeters = FromVisual(apex);
            outlineJobs[i] = job;
            if (volumes.TryGetValue(job.key, out var filter) && filter != null
                && volumeApexBuilt.TryGetValue(job.key, out var built))
            {
                filter.transform.position = apex - built;
            }
        }

        var radar = FindAnyObjectByType<RadarDetectionSystem>();
        if (radar == null)
            return;
        var detections = radar.CurrentDetections;
        for (int d = 0; d < detections.Count; d++)
        {
            var det = detections[d];
            string hitKey = RadarDetectionSystem.MakeSensorKey(det.sourceTrackId, det.detectorType) + "->" + det.targetTrackId;
            if (!hits.TryGetValue(hitKey, out var beam) || beam == null)
                continue;
            Vector3 from = planeCenters.TryGetValue(det.sourceTrackId, out var src) ? src : beam.GetPosition(0);
            Vector3 to = planeCenters.TryGetValue(det.targetTrackId, out var dst)
                ? dst
                : ToVisual(RadarDetectionSystem.GeoToLocalMeters(det.targetLng, det.targetLat, det.targetAlt, originLat, originLng));
            beam.SetPosition(0, from);
            beam.SetPosition(1, to);
        }
    }

    private void RefreshDisplayScale()
    {
        var map = ResolveMap();
        if (map != null && map.TryGetDisplayScale(hasOrigin ? originLat : 30.55f, out _, out _, out float uniform))
        {
            uniform = Mathf.Max(1e-6f, uniform);
            unitsPerMeter = new Vector3(uniform, uniform, uniform);
            ApplyModelScale();
            ApplyClipPlanes(flightCamera);
        }
    }

    private void ApplyClipPlanes(Camera cam)
    {
        if (cam == null)
            return;
        // 近裁剪过小会毁掉远处深度，地平线会被远裁剪平面切成一条硬边。
        float near = Mathf.Clamp(Mathf.Max(0.004f, ModelLength * 0.1f), 0.004f, 1.5f);
        cam.nearClipPlane = near;
        cam.farClipPlane = Mathf.Max(cam.farClipPlane, 4000000f);
    }

    /// <summary>只抬机模，不改雷达/轨迹高度，避免锥体跟着形变。</summary>
    private void LiftModelAboveGround(Transform plane)
    {
        if (plane == null)
            return;
        float clearance = 0.5f * Mathf.Max(1e-8f, unitsPerMeter.y);
        var renderers = plane.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            if (plane.position.y < clearance)
                plane.position += Vector3.up * (clearance - plane.position.y);
            return;
        }
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        if (bounds.min.y < clearance)
            plane.position += Vector3.up * (clearance - bounds.min.y);
    }

    private Vector3 ToVisual(double lng, double lat, float alt)
    {
        return ToVisual(RadarDetectionSystem.GeoToLocalMeters(lng, lat, alt, originLat, originLng));
    }

    private Vector3 ToVisual(Vector3 eastUpNorthMeters)
    {
        return Vector3.Scale(eastUpNorthMeters, unitsPerMeter);
    }

    private Vector3 FromVisual(Vector3 visual)
    {
        return new Vector3(
            visual.x / Mathf.Max(1e-6f, unitsPerMeter.x),
            visual.y / Mathf.Max(1e-6f, unitsPerMeter.y),
            visual.z / Mathf.Max(1e-6f, unitsPerMeter.z));
    }

    private Vector3 AircraftApex(RadarDetectionSystem.SensorInfo sensor, float alt)
    {
        Vector3 body = planeCenters.TryGetValue(sensor.trackId, out var center)
            ? center
            : ToVisual(sensor.lng, sensor.lat, alt);
        Vector3 nose = -(FlightAttitude(sensor.heading, sensor.pitch, sensor.roll) * Vector3.up);
        if (nose.sqrMagnitude < 1e-4f)
            return body;
        float alongMeters = RadarStationTuner.Instance != null
            ? RadarStationTuner.Instance.Offset3D(sensor.mountSign)
            : sensor.mountSign * 8f;
        float upMeters = RadarStationTuner.Instance != null
            ? RadarStationTuner.Instance.OffsetUp(sensor.mountSign)
            : 0f;
        float scale = Mathf.Max(unitsPerMeter.x, unitsPerMeter.z);
        return body + nose.normalized * (alongMeters * scale) + Vector3.up * (upMeters * unitsPerMeter.y);
    }

    private void FrameToMappedWindow(Vector3 aircraftFocus, float heading, float aircraftSpan)
    {
        var map = FindAnyObjectByType<PlaneDisplayController>();
        if (map != null && map.TryGetMapWindow(out float centerLng, out float centerLat, out float span))
        {
            Vector3 focus = ToVisual(centerLng, centerLat, 0f);
            FrameCamera(focus, heading, Mathf.Max(span, aircraftSpan));
            return;
        }
        FrameCamera(aircraftFocus, heading, aircraftSpan);
    }

    private static float MeanHeading(List<FlightDataStreamReceiver.TrackPosition> positions)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos == null)
                continue;
            float rad = pos.heading * Mathf.Deg2Rad;
            sum += new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }
        if (sum.sqrMagnitude < 0.001f)
            return 0f;
        return Mathf.Atan2(sum.x, sum.y) * Mathf.Rad2Deg;
    }

    public static Quaternion FlightAttitude(float heading, float pitch, float roll)
    {
        // 航向北偏东、俯仰上正。上一版多转了 180°，机头和雷达锥相反；模型网格机头在 +Y，只用 -90° 放平且机背朝上。
        Quaternion yaw = Quaternion.AngleAxis(heading, Vector3.up);
        Quaternion noseUp = Quaternion.AngleAxis(-pitch, yaw * Vector3.right);
        Quaternion bank = Quaternion.AngleAxis(-roll, noseUp * yaw * Vector3.forward);
        return bank * noseUp * yaw * Quaternion.Euler(-90f, 0f, 0f);
    }

    private static Vector3 RendererCenter(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
            return go.transform.position;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds.center;
    }

    private float StableAltitude(string trackId, float raw)
    {
        float now = Time.unscaledTime;
        if (!stableAltitude.TryGetValue(trackId, out float prev))
        {
            stableAltitude[trackId] = raw;
            altitudeTime[trackId] = now;
            return raw;
        }
        float dt = Mathf.Max(0.05f, now - altitudeTime[trackId]);
        altitudeTime[trackId] = now;
        // test_data 后段高度每秒跳几十米，但俯仰只有零点几度，不能当真实爬升。
        if (raw <= 1f && prev > 50f)
            return prev;
        if (Mathf.Abs(raw - prev) / dt > 25f)
            return prev;
        stableAltitude[trackId] = raw;
        return raw;
    }

    private PlaneDisplayController trailSettingsSource;

    /// <summary>航迹采样间隔跟二维共用同一个值（米），降采样省性能。</summary>
    private float ResolveTrailSampleMeters()
    {
        if (trailSettingsSource == null)
            trailSettingsSource = FindAnyObjectByType<PlaneDisplayController>();
        return trailSettingsSource != null ? Mathf.Max(1f, trailSettingsSource.TrailSampleMeters) : 50f;
    }

    /// <summary>点数上限也跟二维一致，保证两边覆盖的公里数相同。</summary>
    private int ResolveTrailMaxPoints()
    {
        if (trailSettingsSource == null)
            trailSettingsSource = FindAnyObjectByType<PlaneDisplayController>();
        return trailSettingsSource != null ? (int)trailSettingsSource.TrailMaxPoints : 12000;
    }

    private void AppendLiveTrail(string trackId, Vector3 world)
    {
        if (!liveTrails.TryGetValue(trackId, out var trail))
        {
            trail = new List<Vector3>();
            liveTrails[trackId] = trail;
        }
        float step = Mathf.Max(1e-4f, ResolveTrailSampleMeters() * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
        if (trail.Count == 0 || (trail[trail.Count - 1] - world).sqrMagnitude > step * step)
            trail.Add(world);
        // 全程保留，只留一个防爆上限，不再按时间去截断。
        int cap = ResolveTrailMaxPoints();
        if (trail.Count > cap)
            trail.RemoveRange(0, trail.Count - cap);
    }

    public void ClearTrails(string trackId = null)
    {
        bool all = string.IsNullOrWhiteSpace(trackId);
        var drop = new List<string>();
        foreach (var kv in liveTrails)
        {
            if (!all && kv.Key != trackId)
                continue;
            kv.Value.Clear();
            drop.Add(kv.Key);
        }
        for (int i = 0; i < drop.Count; i++)
            liveTrails.Remove(drop[i]);

        foreach (var kv in tracks)
        {
            if (kv.Value == null)
                continue;
            if (!all && kv.Key != trackId && kv.Key != "live_" + trackId)
                continue;
            kv.Value.positionCount = 0;
            kv.Value.gameObject.SetActive(false);
        }
    }

    private Transform GetPlane(string trackId)
    {
        if (planes.TryGetValue(trackId, out var existing) && existing != null)
            return existing;
        var go = Instantiate(GetTemplate());
        go.name = "FlightPlane_" + trackId;
        go.transform.SetParent(GetFollowRig(trackId), false);
        foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour != null)
                behaviour.enabled = false;
        }
        MakePlaneVisible(go);
        foreach (var collider in go.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null)
                Destroy(collider);
        }
        ApplyModelScale();
        NormalizeLength(go, ModelLength);
        appliedModelLength = ModelLength;
        SetLayer(go);
        planes[trackId] = go.transform;
        GetChaseCamera(trackId);
        return go.transform;
    }

    private const float EarthRadiusMeters = 6371000f;

    private void DrawDifferenceLabels(List<FlightDataStreamReceiver.TrackPosition> positions)
    {
        if (!labelsVisible || positions == null)
        {
            foreach (var kv in labels)
            {
                if (kv.Value != null)
                    kv.Value.gameObject.SetActive(false);
            }
            return;
        }

        var live = new List<FlightDataStreamReceiver.TrackPosition>();
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos == null || string.IsNullOrWhiteSpace(pos.trackId))
                continue;
            if (!planeCenters.ContainsKey(pos.trackId))
                continue;
            live.Add(pos);
        }
        live.Sort((a, b) => string.Compare(a.trackId, b.trackId, System.StringComparison.Ordinal));

        var tuner = DifferenceLabelTuner.Instance;
        float cycle = tuner != null ? Mathf.Max(1f, tuner.cycleSeconds) : 4f;
        int peerSlot = live.Count > 1
            ? Mathf.FloorToInt(Time.unscaledTime / cycle)
            : 0;
        Color color = tuner != null ? tuner.textColor : new Color(0.92f, 0.96f, 1f, 1f);

        for (int i = 0; i < live.Count; i++)
        {
            var pos = live[i];
            var mesh = GetLabel(pos.trackId);
            mesh.gameObject.SetActive(true);
            mesh.color = color;
            mesh.text = BuildCycledDifferenceLabel(pos, live, peerSlot);
            PlaceDifferenceLabel(pos.trackId, mesh);
        }
    }

    private void FaceLabels()
    {
        if (!labelsVisible || flightCamera == null)
            return;
        foreach (var kv in labels)
            PlaceDifferenceLabel(kv.Key, kv.Value);
    }

    private void PlaceDifferenceLabel(string trackId, TextMesh mesh)
    {
        if (mesh == null || flightCamera == null || !mesh.gameObject.activeInHierarchy)
            return;
        var tuner = DifferenceLabelTuner.Instance;
        float right = tuner != null ? tuner.offsetRight : 0f;
        float up = tuner != null ? tuner.offsetUp : 12f;
        float forward = tuner != null ? tuner.offsetForward : 0f;
        Vector3 camRight = flightCamera.transform.right;
        Vector3 camForward = flightCamera.transform.forward;
        camRight.y = 0f;
        if (camRight.sqrMagnitude > 0.0001f)
            camRight.Normalize();
        camForward.y = 0f;
        if (camForward.sqrMagnitude > 0.0001f)
            camForward.Normalize();
        Vector3 world = TrailTip(trackId);
        float px = PixelWidth(world, 1f);
        mesh.transform.position = world + camRight * (right * px) + Vector3.up * (up * px) + camForward * (forward * px);
        FaceLabel(mesh);
    }

    private void FaceLabel(TextMesh mesh)
    {
        if (mesh == null || flightCamera == null || !mesh.gameObject.activeInHierarchy)
            return;
        Vector3 toCam = mesh.transform.position - flightCamera.transform.position;
        if (toCam.sqrMagnitude > 0.0001f)
            mesh.transform.rotation = Quaternion.LookRotation(toCam, Vector3.up);
        var tuner = DifferenceLabelTuner.Instance;
        float mul = tuner != null ? Mathf.Max(0.02f, tuner.textScale) : 1f;
        float world = PixelWidth(mesh.transform.position, 26f * mul);
        float s = world / 0.22f;
        mesh.transform.localScale = new Vector3(s, s, s);
    }

    private static string BuildCycledDifferenceLabel(
        FlightDataStreamReceiver.TrackPosition pos,
        List<FlightDataStreamReceiver.TrackPosition> live,
        int peerSlot)
    {
        string id = string.IsNullOrWhiteSpace(pos.trackId) ? "unknown" : pos.trackId;
        if (live == null || live.Count <= 1)
            return id;

        var others = new List<FlightDataStreamReceiver.TrackPosition>(live.Count - 1);
        for (int i = 0; i < live.Count; i++)
        {
            if (live[i].trackId != pos.trackId)
                others.Add(live[i]);
        }
        if (others.Count == 0)
            return id;

        var peer = others[Mathf.Abs(peerSlot) % others.Count];
        string peerId = string.IsNullOrWhiteSpace(peer.trackId) ? "unknown" : peer.trackId;
        float distance = HorizontalDistanceMeters(pos, peer);
        float altitude = peer.alt - pos.alt;
        float azimuth = RelativeAzimuthDeg(pos, peer);
        float pitchDelta = Mathf.DeltaAngle(pos.pitch, peer.pitch);
        return id
            + "\n相对 " + peerId
            + "\n距离差 " + FormatDistance(distance)
            + "\n高度差 " + FormatSigned(altitude, "m")
            + "\n方位角差 " + FormatSigned(azimuth, "deg")
            + "\n俯仰角差 " + FormatSigned(pitchDelta, "deg");
    }

    private static float HorizontalDistanceMeters(FlightDataStreamReceiver.TrackPosition a, FlightDataStreamReceiver.TrackPosition b)
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

    private TextMesh GetLabel(string trackId)
    {
        if (labels.TryGetValue(trackId, out var existing) && existing != null)
            return existing;
        var go = new GameObject("DiffLabel_" + trackId);
        go.transform.SetParent(root, false);
        var mesh = go.AddComponent<TextMesh>();
        mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        mesh.fontSize = 48;
        mesh.characterSize = 0.22f;
        mesh.anchor = TextAnchor.LowerCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.color = new Color(0.92f, 0.96f, 1f, 1f);
        mesh.text = trackId;
        var renderer = go.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
        SetLayer(go);
        labels[trackId] = mesh;
        return mesh;
    }

    private GameObject GetTemplate()
    {
        if (modelTemplate != null)
            return modelTemplate;
        var all = Resources.FindObjectsOfTypeAll<Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null || !t.gameObject.scene.IsValid())
                continue;
            if (t.name != "J35")
                continue;
            if (t.parent != null && t.parent.name == "FlightSceneRoot")
                continue;
            modelTemplate = t.gameObject;
            break;
        }
        if (modelTemplate == null)
        {
            modelTemplate = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            modelTemplate.name = "FlightPlaneTemplate";
            modelTemplate.transform.localScale = new Vector3(2f, 6f, 2f);
            modelTemplate.SetActive(false);
        }
        return modelTemplate;
    }

    private Light flightSun;

    private void EnsureFlightLight()
    {
        EnsureRoot();
        if (flightSun != null)
            return;
        var existing = root != null ? root.Find("FlightSun") : null;
        if (existing != null)
            flightSun = existing.GetComponent<Light>();
        if (flightSun == null)
        {
            var go = new GameObject("FlightSun");
            if (root != null)
                go.transform.SetParent(root, false);
            flightSun = go.AddComponent<Light>();
        }
        flightSun.type = LightType.Directional;
        flightSun.color = new Color(1f, 0.96f, 0.88f);
        flightSun.intensity = 1.35f;
        flightSun.shadows = LightShadows.None;
        flightSun.cullingMask = 1 << FlightLayer;
        flightSun.transform.rotation = Quaternion.Euler(38f, -42f, 0f);
        SetLayer(flightSun.gameObject);
    }

    private static Shader planeUnlitShader;

    private static Shader PlaneUnlitShader()
    {
        if (planeUnlitShader != null)
            return planeUnlitShader;
        planeUnlitShader = Shader.Find("HK132/PlaneUnlit");
        if (planeUnlitShader == null)
            planeUnlitShader = Shader.Find("Unlit/Texture");
        if (planeUnlitShader == null)
            planeUnlitShader = Shader.Find("Sprites/Default");
        return planeUnlitShader;
    }

    private static void MakePlaneVisible(GameObject go)
    {
        if (go == null)
            return;
        var shader = PlaneUnlitShader();
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
                continue;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var mats = renderer.materials;
            if (mats == null)
                continue;
            for (int m = 0; m < mats.Length; m++)
            {
                var src = mats[m];
                if (src == null)
                    continue;
                Texture tex = src.mainTexture;
                if (tex == null && src.HasProperty("_MainTex"))
                    tex = src.GetTexture("_MainTex");
                if (tex == null && src.HasProperty("_BaseMap"))
                    tex = src.GetTexture("_BaseMap");
                Color col = Color.white;
                if (src.HasProperty("_Color"))
                    col = src.GetColor("_Color");
                else if (src.HasProperty("_BaseColor"))
                    col = src.GetColor("_BaseColor");
                if (col.maxColorComponent < 0.08f)
                    col = new Color(0.72f, 0.75f, 0.78f, 1f);
                if (shader != null)
                {
                    var next = new Material(shader);
                    if (tex != null)
                        next.mainTexture = tex;
                    next.color = tex != null ? Color.white : col;
                    next.SetColor("_Color", tex != null ? Color.white : col);
                    mats[m] = next;
                }
                else
                {
                    if (src.HasProperty("_Metallic"))
                        src.SetFloat("_Metallic", 0f);
                    if (src.HasProperty("_Glossiness"))
                        src.SetFloat("_Glossiness", 0.25f);
                    if (src.HasProperty("_Color"))
                        src.SetColor("_Color", col);
                }
            }
            renderer.materials = mats;
        }
    }

    private static Color TrailColor(string trackId)
    {
        var map = FindAnyObjectByType<PlaneDisplayController>();
        if (map == null)
            return new Color(0.05f, 0.85f, 1f, 0.9f);
        Color color = map.GetTrailColor(trackId);
        color.a = Mathf.Max(color.a, 0.9f);
        return color;
    }

    private static bool TrailVisible(string trackId)
    {
        var map = FindAnyObjectByType<PlaneDisplayController>();
        return map == null || map.IsPlaneTrailVisible(trackId);
    }

    private static void ApplyTrailColor(LineRenderer lr, Color color)
    {
        lr.startColor = lr.endColor = color;
        lr.colorGradient = new Gradient
        {
            colorKeys = new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            alphaKeys = new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) }
        };
    }

    private void DrawTracks(FlightDataStreamReceiver receiver)
    {
        var lines = receiver.SnapshotTrackLines;
        var used = new HashSet<string>();
        if (lines != null)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null || line.positions == null || line.positions.Count < 2)
                    continue;
                string key = string.IsNullOrWhiteSpace(line.trackId) ? "track_" + i : line.trackId;
                float planWidth = Mathf.Max(1e-6f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
                var lr = GetLine(tracks, key, planWidth, TrailColor(line.trackId));
                lr.widthMultiplier = planWidth;
                int count = 0;
                var buffer = new Vector3[line.positions.Count];
                for (int p = 0; p < line.positions.Count; p++)
                {
                    var pos = line.positions[p];
                    if (pos == null)
                        continue;
                    buffer[count++] = ToVisual(pos.lng, pos.lat, pos.alt);
                }
                if (count < 2)
                {
                    lr.gameObject.SetActive(false);
                    continue;
                }
                lr.positionCount = count;
                for (int p = 0; p < count; p++)
                    lr.SetPosition(p, buffer[p]);
                ApplyTrailColor(lr, TrailColor(line.trackId));
                lr.gameObject.SetActive(TrailVisible(line.trackId));
                used.Add(key);
            }
        }
        float liveWidth = Mathf.Max(1e-6f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
        foreach (var kv in liveTrails)
        {
            if (kv.Value.Count < 1)
                continue;
            var lr = GetLine(tracks, "live_" + kv.Key, liveWidth, TrailColor(kv.Key));
            lr.widthMultiplier = liveWidth;
            ApplyLiveTrailPositions(lr, kv.Key, kv.Value);
            ApplyTrailColor(lr, TrailColor(kv.Key));
            lr.gameObject.SetActive(TrailVisible(kv.Key));
            used.Add("live_" + kv.Key);
        }
        HideUnused(tracks, used);
    }

    private Vector3 TrailTip(string trackId)
    {
        if (planes.TryGetValue(trackId, out var plane) && plane != null)
            return RendererCenter(plane.gameObject);
        if (planeCenters.TryGetValue(trackId, out var center))
            return center;
        return Vector3.zero;
    }

    private void ApplyLiveTrailPositions(LineRenderer lr, string trackId, List<Vector3> samples)
    {
        if (lr == null || samples == null || samples.Count < 1)
            return;
        Vector3 tip = TrailTip(trackId);
        int n = samples.Count;
        bool extra = (samples[n - 1] - tip).sqrMagnitude > 1e-10f;
        int count = extra ? n + 1 : n;
        if (lr.positionCount != count)
        {
            var arr = new Vector3[count];
            samples.CopyTo(arr);
            arr[count - 1] = tip;
            lr.positionCount = count;
            lr.SetPositions(arr);
        }
        else
            lr.SetPosition(count - 1, tip);
    }

    private void PinTrailsToAircraft()
    {
        float liveWidth = Mathf.Max(1e-6f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
        foreach (var kv in liveTrails)
        {
            if (kv.Value == null || kv.Value.Count < 1 || !TrailVisible(kv.Key))
                continue;
            var lr = GetLine(tracks, "live_" + kv.Key, liveWidth, TrailColor(kv.Key));
            lr.widthMultiplier = liveWidth;
            ApplyLiveTrailPositions(lr, kv.Key, kv.Value);
            if (!lr.gameObject.activeSelf)
            {
                ApplyTrailColor(lr, TrailColor(kv.Key));
                lr.gameObject.SetActive(true);
            }
        }
        foreach (var kv in tracks)
        {
            if (kv.Value == null || kv.Key.StartsWith("live_"))
                continue;
            if (kv.Value.positionCount < 1 || !planes.ContainsKey(kv.Key))
                continue;
            kv.Value.SetPosition(kv.Value.positionCount - 1, TrailTip(kv.Key));
        }
    }

    private void DrawVolumes(RadarDetectionSystem radar, List<FlightDataStreamReceiver.TrackPosition> positions)
    {
        var usedVolumes = new HashSet<string>();
        var usedHits = new HashSet<string>();
        outlineJobs.Clear();
        if (radar == null)
        {
            HideUnused(volumes, usedVolumes);
            HideUnused(volumeEdges, new HashSet<string>());
            HideUnused(hits, usedHits);
            return;
        }

        var sensors = radar.ActiveSensors;
        for (int i = 0; i < sensors.Count; i++)
        {
            var sensor = sensors[i];
            if (sensor.maxRangeMeters <= 0f)
                continue;
            string key = RadarDetectionSystem.MakeSensorKey(sensor.trackId, sensor.detectorType);
            if (!radar.IsSensorVisible(sensor.trackId, sensor.detectorType))
                continue;
            float alt = StableAltitude(sensor.trackId, sensor.alt);
            Vector3 apex = AircraftApex(sensor, alt);
            Vector3 originMeters = FromVisual(apex);
            Color color = radar.GetSensorColor(sensor.trackId, sensor.detectorType);
            bool anyHit = false;
            // 直接读二维那套检测结果，保证同一时刻二维填充、触发环和三维触发线是同一个集合。
            var detections = radar.CurrentDetections;
            for (int d = 0; d < detections.Count; d++)
            {
                var det = detections[d];
                if (det.sourceTrackId != sensor.trackId || det.detectorType != sensor.detectorType)
                    continue;
                anyHit = true;
                string hitKey = key + "->" + det.targetTrackId;
                float hitWidth = Mathf.Max(1e-6f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
                var beam = GetLine(hits, hitKey, hitWidth, Color.white);
                beam.widthMultiplier = hitWidth;
                beam.positionCount = 2;
                Vector3 from = planeCenters.TryGetValue(sensor.trackId, out var srcCenter)
                    ? srcCenter
                    : apex;
                Vector3 to = planeCenters.TryGetValue(det.targetTrackId, out var dstCenter)
                    ? dstCenter
                    : ToVisual(RadarDetectionSystem.GeoToLocalMeters(det.targetLng, det.targetLat, det.targetAlt, originLat, originLng));
                beam.SetPosition(0, from);
                beam.SetPosition(1, to);
                beam.startColor = beam.endColor = color;
                beam.gameObject.SetActive(true);
                usedHits.Add(hitKey);
            }
            var filter = GetVolume(key);
            filter.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            BuildVolume(filter.mesh, sensor, originMeters);
            volumeApexBuilt[key] = apex;
            volumeTrackId[key] = sensor.trackId;
            var renderer = filter.GetComponent<MeshRenderer>();
            ApplyRadarVolumeMaterial(renderer, color, anyHit);
            filter.gameObject.SetActive(true);
            outlineJobs.Add(new VolumeOutlineJob
            {
                key = key,
                sensor = sensor,
                originMeters = originMeters,
                color = color
            });
            usedVolumes.Add(key);
        }
        HideUnused(volumes, usedVolumes);
        HideUnused(hits, usedHits);
        UpdateVolumeOutlines();
    }

    private void UpdateVolumeOutlines()
    {
        var usedEdges = new HashSet<string>();
        if (flightCamera == null)
        {
            HideUnused(volumeEdges, usedEdges);
            return;
        }
        for (int i = 0; i < outlineJobs.Count; i++)
        {
            var job = outlineJobs[i];
            DrawCapOutline(job.key, job.sensor, job.originMeters, job.color, usedEdges);
        }
        HideUnused(volumeEdges, usedEdges);
    }

    private void BuildVolume(Mesh mesh, RadarDetectionSystem.SensorInfo sensor, Vector3 origin)
    {
        if (sensor.spherical)
        {
            BuildSphereVolume(mesh, origin, sensor.maxRangeMeters);
            return;
        }
        const int seg = 48;
        const int rings = 10;
        RadarDetectionSystem.ConeBasis(sensor, out Vector3 axis, out Vector3 side, out Vector3 up);
        float range = sensor.maxRangeMeters;
        var verts = new List<Vector3>(2 + rings * seg) { ToVisual(origin) };
        var tris = new List<int>();
        int tip = verts.Count;
        verts.Add(ToVisual(origin + axis * range));
        for (int r = 1; r <= rings; r++)
        {
            for (int i = 0; i < seg; i++)
            {
                float az = Mathf.PI * 2f * i / seg;
                float polarMax = RadarDetectionSystem.EllipticalHalfRad(sensor, az);
                float polar = polarMax * r / rings;
                verts.Add(ToVisual(origin + SphereOffset(axis, side, up, range, polar, az)));
            }
        }

        int Ring(int r) { return 2 + (r - 1) * seg; }
        int outer = Ring(rings);
        int coneApex = verts.Count;
        for (int i = 0; i < seg; i++)
        {
            float ang = Mathf.PI * 2f * (i + 0.5f) / seg;
            Vector3 outward = ConeOutward(axis, side, up, ang, RadarDetectionSystem.EllipticalHalfRad(sensor, ang));
            int rimA = verts.Count;
            verts.Add(verts[outer + i]);
            int rimB = verts.Count;
            verts.Add(verts[outer + (i + 1) % seg]);
            int apexI = verts.Count;
            verts.Add(verts[0]);
            Vector3 e1 = verts[rimA] - verts[apexI];
            Vector3 e2 = verts[rimB] - verts[apexI];
            if (Vector3.Dot(Vector3.Cross(e1, e2), outward) < 0f)
            {
                tris.Add(apexI); tris.Add(rimB); tris.Add(rimA);
            }
            else
            {
                tris.Add(apexI); tris.Add(rimA); tris.Add(rimB);
            }
        }

        int first = Ring(1);
        for (int i = 0; i < seg; i++)
        {
            int a = tip;
            int b = first + (i + 1) % seg;
            int c = first + i;
            AddOutward(tris, verts, a, b, c, verts[b] - verts[0]);
        }
        for (int r = 1; r < rings; r++)
        {
            int inner = Ring(r);
            int next = Ring(r + 1);
            for (int i = 0; i < seg; i++)
            {
                int i1 = (i + 1) % seg;
                Vector3 outward = verts[next + i] - verts[0];
                AddOutward(tris, verts, inner + i, next + i, next + i1, outward);
                AddOutward(tris, verts, inner + i, next + i1, inner + i1, outward);
            }
        }

        var colors = new List<Color>(verts.Count);
        var normals = new List<Vector3>(verts.Count);
        Vector3 apex = verts[0];
        for (int i = 0; i < verts.Count; i++)
        {
            Vector3 radial = verts[i] - apex;
            colors.Add(Color.white);
            normals.Add(radial.sqrMagnitude > 1e-6f ? radial.normalized : axis);
        }
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetColors(colors);
        for (int i = 0; i < seg; i++)
        {
            float ang = Mathf.PI * 2f * (i + 0.5f) / seg;
            Vector3 outward = ConeOutward(axis, side, up, ang, RadarDetectionSystem.EllipticalHalfRad(sensor, ang));
            int baseI = coneApex + i * 3;
            if (baseI + 2 < normals.Count)
            {
                normals[baseI] = outward;
                normals[baseI + 1] = outward;
                normals[baseI + 2] = outward;
            }
        }
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();
    }

    private void BuildSphereVolume(Mesh mesh, Vector3 origin, float range)
    {
        const int seg = 28;
        const int rings = 12;
        Vector3 axis = Vector3.up;
        Vector3 side = Vector3.right;
        Vector3 up = Vector3.forward;
        var verts = new List<Vector3>(2 + (rings - 1) * seg);
        var tris = new List<int>();
        verts.Add(ToVisual(origin + axis * range));
        int north = 0;
        for (int r = 1; r < rings; r++)
        {
            float polar = Mathf.PI * r / rings;
            for (int i = 0; i < seg; i++)
            {
                float az = Mathf.PI * 2f * i / seg;
                verts.Add(ToVisual(origin + SphereOffset(axis, side, up, range, polar, az)));
            }
        }
        int south = verts.Count;
        verts.Add(ToVisual(origin - axis * range));

        int Ring(int r) { return 1 + (r - 1) * seg; }
        for (int i = 0; i < seg; i++)
        {
            int b = Ring(1) + i;
            int c = Ring(1) + (i + 1) % seg;
            AddOutward(tris, verts, north, c, b, verts[b] - ToVisual(origin));
        }
        for (int r = 1; r < rings - 1; r++)
        {
            int inner = Ring(r);
            int next = Ring(r + 1);
            for (int i = 0; i < seg; i++)
            {
                int i1 = (i + 1) % seg;
                Vector3 outward = verts[next + i] - ToVisual(origin);
                AddOutward(tris, verts, inner + i, next + i, next + i1, outward);
                AddOutward(tris, verts, inner + i, next + i1, inner + i1, outward);
            }
        }
        int last = Ring(rings - 1);
        for (int i = 0; i < seg; i++)
        {
            int a = last + i;
            int b = last + (i + 1) % seg;
            AddOutward(tris, verts, a, b, south, verts[a] - ToVisual(origin));
        }

        Vector3 center = ToVisual(origin);
        var colors = new List<Color>(verts.Count);
        var normals = new List<Vector3>(verts.Count);
        for (int i = 0; i < verts.Count; i++)
        {
            Vector3 radial = verts[i] - center;
            colors.Add(Color.white);
            normals.Add(radial.sqrMagnitude > 1e-6f ? radial.normalized : axis);
        }
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetColors(colors);
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();
    }

    private static void AddOutward(List<int> tris, List<Vector3> verts, int a, int b, int c, Vector3 outward)
    {
        Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
        if (Vector3.Dot(n, outward) < 0f)
        {
            tris.Add(a); tris.Add(c); tris.Add(b);
        }
        else
        {
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
    }

    private static Vector3 ConeOutward(Vector3 axis, Vector3 side, Vector3 up, float az, float half)
    {
        Vector3 radial = side * Mathf.Cos(az) + up * Mathf.Sin(az);
        Vector3 surface = axis * Mathf.Cos(half) + radial * Mathf.Sin(half);
        Vector3 outward = surface * Mathf.Cos(half) - axis;
        return outward.sqrMagnitude > 1e-6f ? outward.normalized : radial;
    }

    private void DrawCapOutline(string key, RadarDetectionSystem.SensorInfo sensor, Vector3 originMeters, Color color, HashSet<string> used)
    {
        Color edge = new Color(
            Mathf.Min(1f, color.r + 0.35f),
            Mathf.Min(1f, color.g + 0.35f),
            Mathf.Min(1f, color.b + 0.35f),
            0.95f);
        var hull = ScreenSpaceHull(SampleVolumeSurface(sensor, originMeters));
        if (hull.Count >= 2)
            StrokeLoop(key + "|hull", hull.ToArray(), edge, used);
        if (!sensor.spherical)
            StrokeLoop(key + "|rim", BuildCapRim(sensor, originMeters), edge, used);
    }

    /// <summary>圆锥与球面交线：尾部上下沿，点落在扫描距离球面上。</summary>
    private Vector3[] BuildCapRim(RadarDetectionSystem.SensorInfo sensor, Vector3 originMeters)
    {
        RadarDetectionSystem.ConeBasis(sensor, out Vector3 axis, out Vector3 side, out Vector3 up);
        float range = sensor.maxRangeMeters;
        const int seg = 96;
        var rim = new Vector3[seg + 1];
        for (int i = 0; i <= seg; i++)
        {
            float az = Mathf.PI * 2f * i / seg;
            float polar = RadarDetectionSystem.EllipticalHalfRad(sensor, az);
            rim[i] = ToVisual(originMeters + SphereOffset(axis, side, up, range, polar, az));
        }
        return rim;
    }

    /// <summary>与填充网格同一套球面/椭锥采样，描边才能贴住可见轮廓。</summary>
    private List<Vector3> SampleVolumeSurface(RadarDetectionSystem.SensorInfo sensor, Vector3 origin)
    {
        var pts = new List<Vector3>();
        if (sensor.spherical)
        {
            const int seg = 48;
            const int rings = 16;
            Vector3 axis = Vector3.up;
            Vector3 side = Vector3.right;
            Vector3 up = Vector3.forward;
            float range = sensor.maxRangeMeters;
            pts.Add(ToVisual(origin + axis * range));
            pts.Add(ToVisual(origin - axis * range));
            for (int r = 1; r < rings; r++)
            {
                float polar = Mathf.PI * r / rings;
                for (int i = 0; i < seg; i++)
                {
                    float az = Mathf.PI * 2f * i / seg;
                    pts.Add(ToVisual(origin + SphereOffset(axis, side, up, range, polar, az)));
                }
            }
            return pts;
        }

        RadarDetectionSystem.ConeBasis(sensor, out Vector3 cAxis, out Vector3 cSide, out Vector3 cUp);
        float cRange = sensor.maxRangeMeters;
        const int cSeg = 96;
        const int cRings = 16;
        pts.Add(ToVisual(origin));
        pts.Add(ToVisual(origin + cAxis * cRange));
        for (int r = 1; r <= cRings; r++)
        {
            for (int i = 0; i < cSeg; i++)
            {
                float az = Mathf.PI * 2f * i / cSeg;
                float polarMax = RadarDetectionSystem.EllipticalHalfRad(sensor, az);
                float polar = polarMax * r / cRings;
                pts.Add(ToVisual(origin + SphereOffset(cAxis, cSide, cUp, cRange, polar, az)));
            }
        }
        return pts;
    }

    private List<Vector3> ScreenSpaceHull(List<Vector3> world)
    {
        var hull = new List<Vector3>();
        if (flightCamera == null || world == null || world.Count < 2)
            return hull;

        var pts = new List<HullPt>(world.Count);
        for (int i = 0; i < world.Count; i++)
        {
            Vector3 sp = flightCamera.WorldToScreenPoint(world[i]);
            if (sp.z <= 0.02f)
                continue;
            pts.Add(new HullPt { s = new Vector2(sp.x, sp.y), w = world[i] });
        }
        if (pts.Count < 3)
            return hull;

        pts.Sort((a, b) =>
        {
            int c = a.s.x.CompareTo(b.s.x);
            return c != 0 ? c : a.s.y.CompareTo(b.s.y);
        });

        var lower = new List<HullPt>();
        var upper = new List<HullPt>();
        for (int i = 0; i < pts.Count; i++)
        {
            while (lower.Count >= 2 && HullCross(lower[lower.Count - 2].s, lower[lower.Count - 1].s, pts[i].s) <= 0f)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(pts[i]);
        }
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            while (upper.Count >= 2 && HullCross(upper[upper.Count - 2].s, upper[upper.Count - 1].s, pts[i].s) <= 0f)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(pts[i]);
        }
        if (lower.Count > 0)
            lower.RemoveAt(lower.Count - 1);
        if (upper.Count > 0)
            upper.RemoveAt(upper.Count - 1);
        for (int i = 0; i < lower.Count; i++)
            hull.Add(lower[i].w);
        for (int i = 0; i < upper.Count; i++)
            hull.Add(upper[i].w);
        if (hull.Count > 0)
            hull.Add(hull[0]);
        return hull;
    }

    private struct HullPt
    {
        public Vector2 s;
        public Vector3 w;
    }

    private static float HullCross(Vector2 o, Vector2 a, Vector2 b)
    {
        return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
    }

    private void StrokeLoop(string lineKey, Vector3[] points, Color edge, HashSet<string> used)
    {
        if (points == null || points.Length < 2)
            return;
        var lr = GetLine(volumeEdges, lineKey, 1f, edge);
        lr.positionCount = points.Length;
        lr.SetPositions(points);
        ApplyScreenLineWidth(lr, points);
        lr.startColor = lr.endColor = edge;
        lr.numCapVertices = 2;
        lr.numCornerVertices = 3;
        lr.alignment = LineAlignment.View;
        lr.gameObject.SetActive(true);
        used.Add(lineKey);
    }

    private void StrokeArc(string lineKey, List<Vector3> points, Color edge, HashSet<string> used)
    {
        if (points == null || points.Count < 2)
            return;
        var lr = GetLine(volumeEdges, lineKey, 1f, edge);
        lr.positionCount = points.Count;
        lr.SetPositions(points.ToArray());
        ApplyScreenLineWidth(lr, points);
        lr.startColor = lr.endColor = edge;
        lr.numCapVertices = 2;
        lr.gameObject.SetActive(true);
        used.Add(lineKey);
    }

    private float PixelWidth(Vector3 worldPoint, float pixels)
    {
        if (flightCamera == null)
            return 0.02f;
        float dist = Vector3.Distance(flightCamera.transform.position, worldPoint);
        return PixelWidthAtDistance(dist, pixels);
    }

    private float PixelWidthAtDistance(float dist, float pixels)
    {
        if (flightCamera == null)
            return 0.02f;
        dist = Mathf.Max(0.02f, dist);
        float height = Mathf.Max(1f, flightCamera.pixelHeight);
        return Mathf.Max(1e-6f, pixels * 2f * dist * Mathf.Tan(flightCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / height);
    }

    /// <summary>描边按屏幕像素宽，随相机距离变，不用固定米制粗细。</summary>
    private void ApplyScreenLineWidth(LineRenderer lr, IList<Vector3> points)
    {
        if (lr == null)
            return;
        float dist = 8f;
        if (flightCamera != null && points != null && points.Count > 0)
        {
            dist = float.MaxValue;
            Vector3 cam = flightCamera.transform.position;
            for (int i = 0; i < points.Count; i++)
                dist = Mathf.Min(dist, Vector3.Distance(cam, points[i]));
            if (dist > 1e8f)
                dist = 8f;
        }
        lr.widthMultiplier = 1f;
        float w = PixelWidthAtDistance(dist, RadarEdgePixels);
        lr.startWidth = w;
        lr.endWidth = w;
        lr.alignment = LineAlignment.View;
    }

    private List<Vector3> SphereSilhouette(Vector3 originMeters, Vector3 axis, Vector3 side, Vector3 up, float range, float yawHalfRad, float pitchHalfRad)
    {
        var arc = new List<Vector3>();
        if (flightCamera == null)
            return arc;
        Vector3 apex = ToVisual(originMeters);
        Vector3 axisV = (ToVisual(originMeters + axis) - apex).normalized;
        Vector3 sideV = (ToVisual(originMeters + side) - apex).normalized;
        Vector3 upV = (ToVisual(originMeters + up) - apex).normalized;
        float rangeV = (ToVisual(originMeters + axis * range) - apex).magnitude;
        Vector3 view = flightCamera.transform.position - apex;
        float camDist = view.magnitude;
        if (camDist <= rangeV + 0.05f || rangeV < 0.01f)
            return arc;
        view /= camDist;
        float hDist = rangeV * rangeV / camDist;
        float hRad = rangeV * Mathf.Sqrt(Mathf.Max(0f, 1f - (rangeV / camDist) * (rangeV / camDist)));
        Vector3 center = apex + view * hDist;
        Vector3 limbSide = Vector3.Cross(view, axisV);
        if (limbSide.sqrMagnitude < 1e-6f)
            limbSide = Vector3.Cross(view, Vector3.up);
        limbSide.Normalize();
        Vector3 limbUp = Vector3.Cross(limbSide, view);
        const int seg = 96;
        var on = new bool[seg];
        var sample = new Vector3[seg];
        float yawLimit = Mathf.Max(0.5f, yawHalfRad * Mathf.Rad2Deg);
        float pitchLimit = Mathf.Max(0.5f, pitchHalfRad * Mathf.Rad2Deg);
        bool full = yawHalfRad >= Mathf.PI * 0.99f && pitchHalfRad >= Mathf.PI * 0.99f;
        for (int i = 0; i < seg; i++)
        {
            float ang = Mathf.PI * 2f * i / seg;
            sample[i] = center + (limbSide * Mathf.Cos(ang) + limbUp * Mathf.Sin(ang)) * hRad;
            if (full)
            {
                on[i] = true;
                continue;
            }
            Vector3 d = sample[i] - apex;
            float z = Vector3.Dot(d, axisV);
            float x = Vector3.Dot(d, sideV);
            float y = Vector3.Dot(d, upV);
            if (z <= 0f)
            {
                on[i] = false;
                continue;
            }
            float offAz = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
            float offEl = Mathf.Atan2(y, z) * Mathf.Rad2Deg;
            on[i] = (offAz / yawLimit) * (offAz / yawLimit) + (offEl / pitchLimit) * (offEl / pitchLimit) <= 1.02f;
        }
        int start = -1;
        for (int i = 0; i < seg; i++)
        {
            if (on[i] && !on[(i + seg - 1) % seg])
            {
                start = i;
                break;
            }
        }
        if (start < 0 && on[0])
            start = 0;
        if (start < 0)
            return arc;
        for (int n = 0; n < seg; n++)
        {
            int i = (start + n) % seg;
            if (!on[i])
                break;
            arc.Add(sample[i]);
        }
        if (arc.Count >= 3 && full)
            arc.Add(sample[start]);
        return arc;
    }

    private void ApplyRadarVolumeMaterial(MeshRenderer renderer, Color color, bool anyHit)
    {
        if (renderer == null)
            return;
        var mat = renderer.sharedMaterial;
        var shader = Shader.Find("HK132/RadarVolume");
        if (mat == null || (shader != null && mat.shader != shader))
        {
            mat = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            renderer.sharedMaterial = mat;
        }
        float alpha = Mathf.Clamp(RadarFillAlpha, 0.01f, 0.9f);
        if (anyHit)
            alpha = Mathf.Min(0.9f, alpha * 1.25f);
        Color fill = new Color(color.r, color.g, color.b, alpha);
        mat.SetColor("_Color", fill);
        mat.SetColor("_EdgeColor", fill);
        mat.SetFloat("_EdgeStart", 0f);
        mat.SetFloat("_EdgeEnd", 0f);
        mat.color = fill;
    }

    private static Vector3 SphereOffset(Vector3 axis, Vector3 side, Vector3 up, float range, float polar, float az)
    {
        float along = Mathf.Cos(polar) * range;
        float ring = Mathf.Sin(polar) * range;
        return axis * along + (side * Mathf.Cos(az) + up * Mathf.Sin(az)) * ring;
    }

    private MeshFilter GetVolume(string key)
    {
        if (volumes.TryGetValue(key, out var existing) && existing != null)
            return existing;
        var go = new GameObject("ScanVolume_" + key);
        go.transform.SetParent(root, false);
        var filter = go.AddComponent<MeshFilter>();
        filter.mesh = new Mesh { name = key };
        var renderer = go.AddComponent<MeshRenderer>();
        var shader = Shader.Find("HK132/RadarVolume");
        renderer.sharedMaterial = shader != null
            ? new Material(shader)
            : MakeTransparent(new Color(1f, 0.82f, 0.12f, 0.08f));
        renderer.sharedMaterial.SetColor("_Color", new Color(1f, 0.82f, 0.12f, 0.06f));
        renderer.sharedMaterial.SetColor("_EdgeColor", new Color(1f, 0.9f, 0.35f, 0.45f));
        SetLayer(go);
        volumes[key] = filter;
        return filter;
    }

    private static Material MakeTransparent(Color color)
    {
        var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");
        var mat = new Material(shader);
        mat.color = color;
        mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        return mat;
    }

    private LineRenderer GetLine(Dictionary<string, LineRenderer> map, string key, float width, Color color)
    {
        if (map.TryGetValue(key, out var existing) && existing != null)
            return existing;
        var go = new GameObject("FlightLine_" + key);
        go.transform.SetParent(root, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.widthMultiplier = width;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = lr.endColor = color;
        lr.numCapVertices = 2;
        SetLayer(go);
        map[key] = lr;
        return lr;
    }

    private void DrawVolumeEdges(string key, RadarDetectionSystem.SensorInfo sensor, Vector3 originMeters, Color color, HashSet<string> used)
    {
        if (flightCamera == null)
            return;
        RadarDetectionSystem.ConeBasis(sensor, out Vector3 axisM, out Vector3 sideM, out Vector3 upM);
        float half = Mathf.Max(0.5f, RadarDetectionSystem.ConeHalfAngleDeg(sensor)) * Mathf.Deg2Rad;
        float rangeM = sensor.maxRangeMeters;
        Vector3 apex = ToVisual(originMeters);
        Vector3 axis = (ToVisual(originMeters + axisM) - apex).normalized;
        Vector3 side = (ToVisual(originMeters + sideM) - apex).normalized;
        Vector3 up = (ToVisual(originMeters + upM) - apex).normalized;
        float range = (ToVisual(originMeters + axisM * rangeM) - apex).magnitude;
        if (range < 0.01f)
            return;

        Vector3 cam = flightCamera.transform.position;
        if (!TryConeSilhouette(apex, axis, side, up, range, half, cam, out Vector3 left, out Vector3 right))
            return;

        Color far = new Color(color.r, color.g, color.b, 0.28f);
        string leftKey = key + "|edgeL";
        string rightKey = key + "|edgeR";
        string capKey = key + "|cap";
        DrawGradientLine(leftKey, apex, left, color, far);
        DrawGradientLine(rightKey, apex, right, color, far);
        used.Add(leftKey);
        used.Add(rightKey);

        var cap = BuildCapSilhouette(apex, axis, range, half, cam, left, right);
        if (cap.Count >= 2)
        {
            var lr = GetLine(volumeEdges, capKey, 1f, far);
            lr.positionCount = cap.Count;
            lr.SetPositions(cap.ToArray());
            ApplyScreenLineWidth(lr, cap);
            lr.startColor = lr.endColor = far;
            lr.colorGradient = FlatGradient(far);
            lr.gameObject.SetActive(true);
            used.Add(capKey);
        }
    }

    private void DrawGradientLine(string key, Vector3 from, Vector3 to, Color near, Color far)
    {
        var lr = GetLine(volumeEdges, key, 1f, near);
        lr.positionCount = 2;
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
        ApplyScreenLineWidth(lr, new[] { from, to });
        lr.colorGradient = new Gradient
        {
            colorKeys = new[]
            {
                new GradientColorKey(near, 0f),
                new GradientColorKey(far, 1f)
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(near.a, 0f),
                new GradientAlphaKey(far.a, 1f)
            }
        };
        lr.gameObject.SetActive(true);
    }

    private static Gradient FlatGradient(Color color)
    {
        return new Gradient
        {
            colorKeys = new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            alphaKeys = new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) }
        };
    }

    private static bool TryConeSilhouette(Vector3 apex, Vector3 axis, Vector3 side, Vector3 up, float range, float half, Vector3 cam, out Vector3 left, out Vector3 right)
    {
        Vector3 center = apex + axis * (Mathf.Cos(half) * range);
        float radius = Mathf.Sin(half) * range;
        Vector3 toCam = cam - center;
        Vector3 inPlane = toCam - axis * Vector3.Dot(toCam, axis);
        if (inPlane.sqrMagnitude < 1e-6f)
            inPlane = side;
        float dist = inPlane.magnitude;
        Vector3 dir = inPlane / Mathf.Max(1e-4f, dist);
        Vector3 perp = Vector3.Cross(axis, dir).normalized;
        float cosB = Mathf.Clamp(radius / Mathf.Max(dist, radius + 0.01f), -1f, 1f);
        float sinB = Mathf.Sqrt(Mathf.Max(0f, 1f - cosB * cosB));
        left = center + (dir * cosB + perp * sinB) * radius;
        right = center + (dir * cosB - perp * sinB) * radius;
        return true;
    }

    private static List<Vector3> BuildCapSilhouette(Vector3 apex, Vector3 axis, float range, float half, Vector3 cam, Vector3 left, Vector3 right)
    {
        var arc = new List<Vector3>();
        Vector3 view = cam - apex;
        float camDist = view.magnitude;
        if (camDist > range + 0.05f)
        {
            view /= camDist;
            float hDist = range * range / camDist;
            float hRad = range * Mathf.Sqrt(Mathf.Max(0f, 1f - (range / camDist) * (range / camDist)));
            Vector3 hCenter = apex + view * hDist;
            Vector3 hSide = Vector3.Cross(view, axis);
            if (hSide.sqrMagnitude < 1e-6f)
                hSide = Vector3.Cross(view, Vector3.up);
            hSide.Normalize();
            Vector3 hUp = Vector3.Cross(hSide, view);
            const int seg = 64;
            var on = new bool[seg];
            var sample = new Vector3[seg];
            for (int i = 0; i < seg; i++)
            {
                float ang = Mathf.PI * 2f * i / seg;
                sample[i] = hCenter + (hSide * Mathf.Cos(ang) + hUp * Mathf.Sin(ang)) * hRad;
                on[i] = Vector3.Angle(sample[i] - apex, axis) <= half * Mathf.Rad2Deg + 0.5f;
            }
            int start = -1;
            for (int i = 0; i < seg; i++)
            {
                if (on[i] && !on[(i + seg - 1) % seg])
                {
                    start = i;
                    break;
                }
            }
            if (start < 0 && on[0])
                start = 0;
            if (start >= 0)
            {
                for (int n = 0; n < seg; n++)
                {
                    int i = (start + n) % seg;
                    if (!on[i])
                        break;
                    arc.Add(sample[i]);
                }
            }
        }

        if (arc.Count >= 3)
            return arc;

        const int rim = 28;
        var circle = new List<Vector3>(rim + 1);
        Vector3 side = (left - (apex + axis * Vector3.Dot(left - apex, axis)));
        if (side.sqrMagnitude < 1e-6f)
            side = Vector3.Cross(axis, Vector3.up);
        side.Normalize();
        Vector3 up = Vector3.Cross(side, axis).normalized;
        float ring = Mathf.Sin(half) * range;
        float along = Mathf.Cos(half) * range;
        for (int i = 0; i <= rim; i++)
        {
            float ang = Mathf.PI * 2f * i / rim;
            circle.Add(apex + axis * along + (side * Mathf.Cos(ang) + up * Mathf.Sin(ang)) * ring);
        }
        return circle;
    }

    private Vector3 lastFocus;
    private float lastHeading;
    private float lastSpan = 80f;

    private Camera GetChaseCamera(string trackId)
    {
        if (chaseCams.TryGetValue(trackId, out var existing) && existing != null)
            return existing;
        EnsureRoot();
        var go = new GameObject("ChaseCam_" + trackId);
        go.transform.SetParent(GetFollowRig(trackId), false);
        var cam = go.AddComponent<Camera>();
        cam.enabled = false;
        cam.cullingMask = 1 << FlightLayer;
        cam.clearFlags = CameraClearFlags.Skybox;
        cam.fieldOfView = flightCamera != null ? flightCamera.fieldOfView : 50f;
        ApplyClipPlanes(cam);
        if (skyMaterial != null)
        {
            var sky = go.AddComponent<Skybox>();
            sky.material = skyMaterial;
        }
        SetLayer(go);
        chaseCams[trackId] = cam;
        return cam;
    }

    private Transform GetFollowRig(string trackId)
    {
        if (followRigs.TryGetValue(trackId, out var existing) && existing != null)
            return existing;
        EnsureRoot();
        var go = new GameObject("FollowRig_" + trackId);
        go.transform.SetParent(root, false);
        SetLayer(go);
        followRigs[trackId] = go.transform;
        return go.transform;
    }

    private void PlaceChaseOnRig(string trackId)
    {
        var cam = GetChaseCamera(trackId);
        if (cam == null)
            return;
        if (!cam.gameObject.activeSelf)
            cam.gameObject.SetActive(true);
        var rig = GetFollowRig(trackId);
        if (cam.transform.parent != rig)
            cam.transform.SetParent(rig, false);
        var tune = ChaseCamTuner.Instance;
        float backM = tune != null ? tune.back : 80f;
        float heightM = tune != null ? tune.height : 22f;
        float sideM = tune != null ? tune.side : 0f;
        float lookPitch = tune != null ? tune.lookPitch : 8f;
        float lookYaw = tune != null ? tune.lookYaw : 0f;
        float scale = Mathf.Max(1e-8f, unitsPerMeter.x, unitsPerMeter.z);
        cam.transform.localPosition = new Vector3(sideM * scale, heightM * unitsPerMeter.y, -backM * scale);
        cam.transform.localRotation = Quaternion.Euler(-lookPitch, lookYaw, 0f);
        cam.fieldOfView = Mathf.Clamp(tune != null ? tune.fov : 55f, 20f, 90f);
        ApplyClipPlanes(cam);
    }

    private void HandleFollowHotkey()
    {
        if (IsTyping())
            return;
        if (!Input.GetKeyDown(KeyCode.B))
            return;
        if (followLocked)
            UnlockFollow();
        else
            LockFollow();
    }

    private void LockFollow()
    {
        if (string.IsNullOrWhiteSpace(followTrackId))
        {
            foreach (var kv in planes)
            {
                if (kv.Value != null && kv.Value.gameObject.activeInHierarchy)
                {
                    followTrackId = kv.Key;
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(followTrackId) || !planeCenters.ContainsKey(followTrackId))
            return;
        if (flightCamera != null)
        {
            unlockedPos = flightCamera.transform.position;
            unlockedYaw = yaw;
            unlockedPitch = pitch;
            unlockedOrbitDistance = orbitDistance;
            unlockedPivot = orbitPivot;
        }
        followLocked = true;
        followSnap = true;
        followSmoothVel = Vector3.zero;
        userHasControl = true;
        flying = false;
        panning = false;
        orbiting = false;
        zoomDragging = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        RefreshFollowHint();
    }

    private void UnlockFollow()
    {
        followLocked = false;
        if (flightCamera != null)
        {
            SyncAnglesFromCamera();
            orbitPivot = planeCenters.TryGetValue(followTrackId ?? "", out var pivot)
                ? pivot
                : flightCamera.transform.position + flightCamera.transform.forward * Mathf.Max(10f, orbitDistance);
            orbitDistance = Mathf.Max(5f, Vector3.Distance(flightCamera.transform.position, orbitPivot));
        }
        userHasControl = true;
        flying = false;
        panning = false;
        orbiting = false;
        zoomDragging = false;
        RefreshFollowHint();
    }

    private void ApplyFollowCamera()
    {
        if (!followLocked || flightCamera == null)
            return;
        if (string.IsNullOrWhiteSpace(followTrackId))
        {
            UnlockFollow();
            return;
        }
        if (!TryFollowTarget(followTrackId, out Vector3 center, out float heading, out _))
        {
            UnlockFollow();
            return;
        }
        var rig = GetFollowRig(followTrackId);
        rig.SetPositionAndRotation(center, Quaternion.AngleAxis(heading, Vector3.up));
        PlaceChaseOnRig(followTrackId);
        var chase = GetChaseCamera(followTrackId);
        if (chase == null)
        {
            UnlockFollow();
            return;
        }
        flightCamera.transform.SetPositionAndRotation(chase.transform.position, chase.transform.rotation);
        flightCamera.fieldOfView = chase.fieldOfView;
        ApplyClipPlanes(flightCamera);
        SyncAnglesFromCamera();
        orbitPivot = center;
        orbitDistance = Mathf.Max(5f, Vector3.Distance(flightCamera.transform.position, center));
    }

    private bool TryFollowTarget(string trackId, out Vector3 center, out float heading, out float bodyPitch)
    {
        center = Vector3.zero;
        heading = 0f;
        bodyPitch = 0f;
        var receiver = FlightDataStreamReceiver.Instance;
        var positions = receiver != null ? receiver.SnapshotPositions : null;
        if (positions != null)
        {
            for (int i = 0; i < positions.Count; i++)
            {
                var pos = positions[i];
                if (pos == null || pos.trackId != trackId)
                    continue;
                float alt = StableAltitude(trackId, pos.alt);
                center = ToVisual(pos.lng, pos.lat, alt);
                heading = pos.heading;
                bodyPitch = pos.pitch;
                planeCenters[trackId] = center;
                planeHeadings[trackId] = heading;
                return true;
            }
        }
        if (planeCenters.TryGetValue(trackId, out center))
        {
            planeHeadings.TryGetValue(trackId, out heading);
            return true;
        }
        return false;
    }

    private void EnsureFollowDropdown()
    {
        if (followDropdown != null || viewImage == null)
            return;
        var parent = viewImage.rectTransform;
        var go = new GameObject("PlaneFollowDropdown", typeof(RectTransform), typeof(Image), typeof(Dropdown));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.sizeDelta = new Vector2(176f, 28f);
        rt.anchoredPosition = new Vector2(-10f, -10f);
        var image = go.GetComponent<Image>();
        image.color = new Color(0.08f, 0.12f, 0.18f, 0.92f);
        var dropdown = go.GetComponent<Dropdown>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var label = CreateUiText(go.transform, "Label", font, 14, TextAnchor.MiddleLeft);
        label.rectTransform.offsetMin = new Vector2(8f, 0f);
        label.rectTransform.offsetMax = new Vector2(-22f, 0f);
        label.text = "选择飞机";

        var arrow = CreateUiText(go.transform, "Arrow", font, 14, TextAnchor.MiddleCenter);
        arrow.rectTransform.anchorMin = new Vector2(1f, 0f);
        arrow.rectTransform.anchorMax = new Vector2(1f, 1f);
        arrow.rectTransform.pivot = new Vector2(1f, 0.5f);
        arrow.rectTransform.anchoredPosition = Vector2.zero;
        arrow.rectTransform.sizeDelta = new Vector2(22f, 0f);
        arrow.rectTransform.offsetMin = new Vector2(-22f, 0f);
        arrow.rectTransform.offsetMax = Vector2.zero;
        arrow.text = "v";

        var template = CreateFollowDropdownTemplate(go.transform, font);
        dropdown.targetGraphic = image;
        dropdown.captionText = label;
        dropdown.template = template;
        dropdown.itemText = template.Find("Viewport/Content/Item/Item Label")?.GetComponent<Text>();
        dropdown.onValueChanged.AddListener(OnFollowDropdownChanged);
        followDropdown = dropdown;

        var hintGo = new GameObject("FollowHint", typeof(RectTransform), typeof(Text));
        hintGo.transform.SetParent(parent, false);
        var hintRt = hintGo.GetComponent<RectTransform>();
        hintRt.anchorMin = new Vector2(1f, 1f);
        hintRt.anchorMax = new Vector2(1f, 1f);
        hintRt.pivot = new Vector2(1f, 1f);
        hintRt.sizeDelta = new Vector2(176f, 18f);
        hintRt.anchoredPosition = new Vector2(-10f, -40f);
        followHint = hintGo.GetComponent<Text>();
        followHint.font = font;
        followHint.fontSize = 12;
        followHint.alignment = TextAnchor.MiddleRight;
        followHint.color = new Color(0.85f, 0.9f, 0.95f, 0.85f);
        followHint.raycastTarget = false;
        followHint.text = "B 锁定飞机视角";
    }

    private static Text CreateUiText(Transform parent, string name, Font font, int size, TextAnchor align)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var text = go.GetComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.alignment = align;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private static RectTransform CreateFollowDropdownTemplate(Transform dropdown, Font font)
    {
        var templateGo = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        templateGo.transform.SetParent(dropdown, false);
        var template = templateGo.GetComponent<RectTransform>();
        template.anchorMin = new Vector2(0f, 0f);
        template.anchorMax = new Vector2(1f, 0f);
        template.pivot = new Vector2(0.5f, 1f);
        template.sizeDelta = new Vector2(0f, 140f);
        template.anchoredPosition = Vector2.zero;
        templateGo.GetComponent<Image>().color = new Color(0.07f, 0.1f, 0.14f, 0.96f);
        templateGo.SetActive(false);
        var canvas = templateGo.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = 80;
        templateGo.AddComponent<GraphicRaycaster>();

        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewportGo.transform.SetParent(template, false);
        var viewport = viewportGo.GetComponent<RectTransform>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = Vector2.zero;
        viewportGo.GetComponent<Image>().color = Color.white;
        viewportGo.GetComponent<Mask>().showMaskGraphic = false;

        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewport, false);
        var content = contentGo.GetComponent<RectTransform>();
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, 28f);

        var itemGo = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
        itemGo.transform.SetParent(content, false);
        var item = itemGo.GetComponent<RectTransform>();
        item.anchorMin = new Vector2(0f, 0.5f);
        item.anchorMax = new Vector2(1f, 0.5f);
        item.sizeDelta = new Vector2(0f, 26f);
        var toggle = itemGo.GetComponent<Toggle>();

        var bgGo = new GameObject("Item Background", typeof(RectTransform), typeof(Image));
        bgGo.transform.SetParent(item, false);
        var bg = bgGo.GetComponent<RectTransform>();
        bg.anchorMin = Vector2.zero;
        bg.anchorMax = Vector2.one;
        bg.offsetMin = Vector2.zero;
        bg.offsetMax = Vector2.zero;
        bgGo.GetComponent<Image>().color = new Color(0.12f, 0.16f, 0.22f, 1f);

        var checkGo = new GameObject("Item Checkmark", typeof(RectTransform), typeof(Image));
        checkGo.transform.SetParent(item, false);
        var check = checkGo.GetComponent<RectTransform>();
        check.anchorMin = new Vector2(0f, 0.5f);
        check.anchorMax = new Vector2(0f, 0.5f);
        check.sizeDelta = new Vector2(8f, 8f);
        check.anchoredPosition = new Vector2(10f, 0f);
        checkGo.GetComponent<Image>().color = new Color(0.35f, 0.75f, 1f, 1f);

        var itemLabel = CreateUiText(item, "Item Label", font, 14, TextAnchor.MiddleLeft);
        itemLabel.rectTransform.offsetMin = new Vector2(20f, 0f);
        itemLabel.rectTransform.offsetMax = new Vector2(-6f, 0f);
        itemLabel.raycastTarget = true;

        toggle.targetGraphic = bgGo.GetComponent<Image>();
        toggle.graphic = checkGo.GetComponent<Image>();
        var scroll = templateGo.GetComponent<ScrollRect>();
        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.vertical = true;
        return template;
    }

    private void OnFollowDropdownChanged(int index)
    {
        if (ignoreFollowDropdown)
            return;
        if (index < 0 || index >= followDropdownIds.Count)
            return;
        followTrackId = followDropdownIds[index];
        if (followLocked)
        {
            followSnap = true;
            followSmoothVel = Vector3.zero;
            ApplyFollowCamera();
        }
        RefreshFollowHint();
    }

    private void RefreshFollowDropdown(HashSet<string> used)
    {
        EnsureFollowDropdown();
        if (followDropdown == null)
            return;
        var ids = new List<string>();
        foreach (string id in used)
        {
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        ids.Sort(System.StringComparer.Ordinal);
        bool changed = ids.Count != followDropdownIds.Count;
        if (!changed)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] != followDropdownIds[i])
                {
                    changed = true;
                    break;
                }
            }
        }
        if (changed)
        {
            followDropdownIds.Clear();
            followDropdownIds.AddRange(ids);
            ignoreFollowDropdown = true;
            followDropdown.ClearOptions();
            var options = new List<Dropdown.OptionData>();
            for (int i = 0; i < followDropdownIds.Count; i++)
                options.Add(new Dropdown.OptionData(followDropdownIds[i]));
            followDropdown.AddOptions(options);
            ignoreFollowDropdown = false;
        }
        if (followDropdownIds.Count == 0)
        {
            if (followLocked)
                UnlockFollow();
            followTrackId = null;
            RefreshFollowHint();
            return;
        }
        if (string.IsNullOrWhiteSpace(followTrackId) || !followDropdownIds.Contains(followTrackId))
            followTrackId = followDropdownIds[0];
        int selected = followDropdownIds.IndexOf(followTrackId);
        ignoreFollowDropdown = true;
        followDropdown.SetValueWithoutNotify(Mathf.Max(0, selected));
        followDropdown.RefreshShownValue();
        ignoreFollowDropdown = false;
        RefreshFollowHint();
    }

    private void RefreshFollowHint()
    {
        if (followHint == null)
            return;
        if (followLocked && !string.IsNullOrWhiteSpace(followTrackId))
            followHint.text = "已锁定 " + followTrackId + "  ·  B 解锁";
        else
            followHint.text = "B 锁定飞机视角";
    }

    private void Navigate()
    {
        if (flightCamera == null || viewImage == null || !viewImage.gameObject.activeInHierarchy)
            return;

        // 面板把 MouseUp 吃掉时，按住状态仍能松开漫游，避免点完 UI 三维再也拖不动。
        if (!Input.GetMouseButton(0))
            orbiting = false;
        if (!Input.GetMouseButton(1))
        {
            flying = false;
            zoomDragging = false;
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
        if (!Input.GetMouseButton(2))
            panning = false;

        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        bool overView = IsPointerOverView();
        bool overPanel = MouseOverConfigPanel();
        bool over = overView && !overPanel && !IsOverFollowDropdown();
        if (over && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)))
            ClearUiFocus();
        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");
        float scroll = Input.mouseScrollDelta.y;

        if (Input.GetMouseButtonDown(1) && over)
        {
            userHasControl = true;
            if (alt)
                zoomDragging = true;
            else
            {
                flying = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                SyncAnglesFromCamera();
            }
        }
        if (Input.GetMouseButtonDown(2) && over)
        {
            userHasControl = true;
            panning = true;
        }
        if (Input.GetMouseButtonDown(0) && over)
        {
            userHasControl = true;
            orbiting = true;
            orbitPivot = flightCamera.transform.position + flightCamera.transform.forward * orbitDistance;
        }

        if (over && !IsTyping() && Input.GetKeyDown(KeyCode.F))
        {
            FrameCamera(lastFocus, lastHeading, lastSpan, true);
            return;
        }

        if (flying)
        {
            yaw += mx * 2.2f;
            pitch = Mathf.Clamp(pitch - my * 2.2f, -89f, 89f);
            flightCamera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            if (Mathf.Abs(scroll) > 0.01f)
                flySpeed = Mathf.Clamp(flySpeed * (scroll > 0f ? 1.12f : 0.88f), 0.4f, 800f);
            Vector3 move = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            if (Input.GetKey(KeyCode.E))
                move.y += 1f;
            if (Input.GetKey(KeyCode.Q))
                move.y -= 1f;
            float boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 3f : 1f;
            flightCamera.transform.position += flightCamera.transform.rotation * move * flySpeed * boost * Time.unscaledDeltaTime;
            orbitDistance = Mathf.Max(10f, Vector3.Distance(flightCamera.transform.position, orbitPivot));
            return;
        }

        if (orbiting)
        {
            yaw += mx * 3f;
            pitch = Mathf.Clamp(pitch - my * 3f, -89f, 89f);
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            flightCamera.transform.rotation = rot;
            flightCamera.transform.position = orbitPivot - rot * Vector3.forward * orbitDistance;
            return;
        }

        if (panning)
        {
            float scale = flySpeed * 0.02f;
            Vector3 delta = (-flightCamera.transform.right * mx - flightCamera.transform.up * my) * scale;
            flightCamera.transform.position += delta;
            orbitPivot += delta;
            return;
        }

        if (zoomDragging)
        {
            Dolly(-my * flySpeed * 0.08f);
            return;
        }

        if (over)
        {
            Vector3 keys = IsTyping()
                ? Vector3.zero
                : new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
            if (Input.GetKey(KeyCode.E)) keys.y += 1f;
            if (Input.GetKey(KeyCode.Q)) keys.y -= 1f;
            if (keys.sqrMagnitude > 0.01f)
            {
                userHasControl = true;
                float boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 3f : 1f;
                flightCamera.transform.position += flightCamera.transform.rotation * keys * flySpeed * boost * Time.unscaledDeltaTime;
            }
            if (Mathf.Abs(scroll) > 0.01f)
                Dolly(scroll * flySpeed * 0.35f);
        }
    }

    private void Dolly(float distance)
    {
        flightCamera.transform.position += flightCamera.transform.forward * distance;
        orbitDistance = Mathf.Max(5f, orbitDistance - distance);
        userHasControl = true;
    }

    private void SyncAnglesFromCamera()
    {
        Vector3 euler = flightCamera.transform.eulerAngles;
        yaw = euler.y;
        pitch = euler.x > 180f ? euler.x - 360f : euler.x;
    }

    private bool IsPointerOverView()
    {
        if (viewImage == null || viewImage.rectTransform == null)
            return false;
        var rect = viewImage.rectTransform;
        var canvas = viewImage.canvas;
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        if (RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, uiCamera))
            return true;
        if (RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null))
            return true;

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector2 a = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[0]);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[2]);
        float minX = Mathf.Min(a.x, b.x);
        float maxX = Mathf.Max(a.x, b.x);
        float minY = Mathf.Min(a.y, b.y);
        float maxY = Mathf.Max(a.y, b.y);
        Vector2 m = Input.mousePosition;
        return m.x >= minX && m.x <= maxX && m.y >= minY && m.y <= maxY;
    }

    private static bool IsTyping()
    {
        if (EventSystem.current == null)
            return false;
        var selected = EventSystem.current.currentSelectedGameObject;
        return selected != null && selected.GetComponent<InputField>() != null;
    }

    private static void ClearUiFocus()
    {
        GUI.FocusControl(null);
        GUIUtility.hotControl = 0;
        GUIUtility.keyboardControl = 0;
        if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    private static bool MouseOverConfigPanel()
    {
        if (RadarStationTuner.Instance != null && RadarStationTuner.Instance.ContainsMouse())
            return true;
        if (DifferenceLabelTuner.Instance != null && DifferenceLabelTuner.Instance.ContainsMouse())
            return true;
        if (ChaseCamTuner.Instance != null && ChaseCamTuner.Instance.ContainsMouse())
            return true;
        var pin = FindAnyObjectByType<ReferencePlanePin>(FindObjectsInactive.Include);
        return pin != null && pin.ContainsMouse();
    }

    private bool IsOverFollowDropdown()
    {
        if (followDropdown == null)
            return false;
        var rt = followDropdown.transform as RectTransform;
        if (rt == null)
            return false;
        var canvas = viewImage != null ? viewImage.canvas : null;
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        if (RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, uiCamera))
            return true;
        var template = followDropdown.template;
        if (template != null && template.gameObject.activeInHierarchy
            && RectTransformUtility.RectangleContainsScreenPoint(template, Input.mousePosition, uiCamera))
            return true;
        return false;
    }

    private bool placedDefaultCamera;
    private float nextCameraSave;
    private static readonly Vector3 DefaultCameraPosition = new Vector3(-16.30917f, 18.95884f, -16.00072f);

    private bool RestoreCameraView()
    {
        if (flightCamera == null)
            return false;
        var saved = PanelMemory.Load();
        if (saved.flightView == null || !saved.flightView.saved)
            return false;
        var v = saved.flightView;
        flightCamera.transform.position = new Vector3(v.posX, v.posY, v.posZ);
        yaw = v.yaw;
        pitch = Mathf.Clamp(v.pitch, -89f, 89f);
        flightCamera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
        flySpeed = Mathf.Max(0.4f, v.flySpeed);
        orbitDistance = Mathf.Max(5f, v.orbitDistance);
        orbitPivot = new Vector3(v.pivotX, v.pivotY, v.pivotZ);
        userHasControl = true;
        placedDefaultCamera = true;
        return true;
    }

    private void PersistCameraIfNeeded()
    {
        if (flightCamera == null || !userHasControl)
            return;
        if (Time.unscaledTime < nextCameraSave)
            return;
        nextCameraSave = Time.unscaledTime + 0.8f;
        SaveCameraView();
    }

    private void SaveCameraView()
    {
        if (flightCamera == null)
            return;
        var store = PanelMemory.Load();
        if (store.flightView == null)
            store.flightView = new PanelMemory.FlightViewTune();
        var t = flightCamera.transform;
        store.flightView.saved = true;
        store.flightView.posX = t.position.x;
        store.flightView.posY = t.position.y;
        store.flightView.posZ = t.position.z;
        store.flightView.yaw = yaw;
        store.flightView.pitch = pitch;
        store.flightView.flySpeed = flySpeed;
        store.flightView.orbitDistance = orbitDistance;
        store.flightView.pivotX = orbitPivot.x;
        store.flightView.pivotY = orbitPivot.y;
        store.flightView.pivotZ = orbitPivot.z;
        PanelMemory.Save(store);
    }

    private void ApplyDefaultCameraIfNeeded()
    {
        if (placedDefaultCamera || flightCamera == null)
            return;
        placedDefaultCamera = true;
        userHasControl = true;
        flightCamera.transform.position = DefaultCameraPosition;
        flightCamera.transform.LookAt(Vector3.zero);
        flySpeed = 6f;
        orbitPivot = Vector3.zero;
        orbitDistance = Mathf.Max(5f, DefaultCameraPosition.magnitude);
        SyncAnglesFromCamera();
    }

    private void OnGUI()
    {
        if (flightCamera == null || viewImage == null || !viewImage.gameObject.activeInHierarchy)
            return;
        DrawWorldAxisGizmo();
    }

    /// <summary>左下角世界坐标轴：X=东、Y=天、Z=北，随相机朝向变化，限制在三维视图内。</summary>
    private void DrawWorldAxisGizmo()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint)
            return;

        var rect = viewImage.rectTransform;
        if (rect == null)
            return;

        var canvas = viewImage.canvas;
        Camera uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[2]);

        float guiLeft = Mathf.Min(bottomLeft.x, topRight.x);
        float guiRight = Mathf.Max(bottomLeft.x, topRight.x);
        float guiTop = Screen.height - Mathf.Max(bottomLeft.y, topRight.y);
        float guiBottom = Screen.height - Mathf.Min(bottomLeft.y, topRight.y);
        float viewW = guiRight - guiLeft;
        float viewH = guiBottom - guiTop;
        if (viewW < 48f || viewH < 48f)
            return;

        float lineW = Mathf.Clamp(AxisGizmoWidth, 0.5f, 12f);
        const float labelW = 44f;
        const float labelH = 18f;
        float halfW = lineW * 0.5f + 2f;
        Rect clip = new Rect(
            guiLeft + halfW,
            guiTop + halfW,
            Mathf.Max(8f, viewW - halfW * 2f),
            Mathf.Max(8f, viewH - halfW * 2f));

        float length = Mathf.Clamp(Mathf.Min(viewW, viewH) * 0.12f, 24f, 56f);
        float insetX = Mathf.Min(length + labelW, clip.width * 0.42f);
        float insetY = Mathf.Min(length + labelH, clip.height * 0.42f);
        length = Mathf.Max(12f, Mathf.Min(length, insetX - 8f, insetY - 8f));
        Vector2 origin = new Vector2(clip.xMin + insetX, clip.yMax - insetY);

        var cam = flightCamera.transform;
        Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
        string[] labels = { "东 X", "天 Y", "北 Z" };
        Color[] colors =
        {
            new Color(1f, 0.38f, 0.38f, 0.95f),
            new Color(0.45f, 1f, 0.5f, 0.95f),
            new Color(0.45f, 0.72f, 1f, 0.95f),
        };

        for (int i = 0; i < axes.Length; i++)
        {
            Vector3 axis = axes[i];
            Vector2 dir = new Vector2(Vector3.Dot(axis, cam.right), Vector3.Dot(axis, cam.up));
            bool towardsViewer = Vector3.Dot(axis, cam.forward) < 0f;
            float axisLen = length * (towardsViewer ? 1f : 0.82f);
            Vector2 tip = origin + new Vector2(dir.x, -dir.y) * axisLen;
            tip = ClipRayToRect(origin, tip, clip);
            Color color = towardsViewer ? colors[i] : new Color(colors[i].r, colors[i].g, colors[i].b, 0.6f);

            DrawGuiLine(origin, tip, color, towardsViewer ? lineW : lineW * 0.72f);

            Vector2 labelPos = new Vector2(tip.x + 4f, tip.y - 9f);
            labelPos.x = Mathf.Clamp(labelPos.x, clip.xMin, clip.xMax - labelW);
            labelPos.y = Mathf.Clamp(labelPos.y, clip.yMin, clip.yMax - labelH);
            var old = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(labelPos.x, labelPos.y, labelW, labelH), labels[i]);
            GUI.color = old;
        }

        float dot = Mathf.Clamp(lineW + 2f, 5f, 12f);
        var oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.DrawTexture(new Rect(origin.x - dot * 0.5f, origin.y - dot * 0.5f, dot, dot), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }

    private static Vector2 ClipRayToRect(Vector2 origin, Vector2 tip, Rect r)
    {
        Vector2 d = tip - origin;
        float t = 1f;
        if (Mathf.Abs(d.x) > 0.001f)
        {
            float tx = d.x > 0f ? (r.xMax - origin.x) / d.x : (r.xMin - origin.x) / d.x;
            if (tx >= 0f)
                t = Mathf.Min(t, tx);
        }
        if (Mathf.Abs(d.y) > 0.001f)
        {
            float ty = d.y > 0f ? (r.yMax - origin.y) / d.y : (r.yMin - origin.y) / d.y;
            if (ty >= 0f)
                t = Mathf.Min(t, ty);
        }
        return origin + d * Mathf.Clamp01(t);
    }

    private static void DrawGuiLine(Vector2 from, Vector2 to, Color color, float width)
    {
        float dx = to.x - from.x;
        float dy = to.y - from.y;
        float len = Mathf.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f)
            return;

        Matrix4x4 saved = GUI.matrix;
        Color savedColor = GUI.color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(dy, dx) * Mathf.Rad2Deg, from);
        GUI.color = color;
        GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, len, width), Texture2D.whiteTexture);
        GUI.color = savedColor;
        GUI.matrix = saved;
    }

    private void FrameCamera(Vector3 focus, float heading, float span, bool force = false)
    {
        lastFocus = focus;
        lastHeading = heading;
        lastSpan = span;
        if (followLocked)
            return;
        if (flightCamera == null || (userHasControl && !force))
            return;
        if (force)
            userHasControl = false;
        float dist = Mathf.Clamp(span * 1.6f, 80f, 120000f);
        flySpeed = Mathf.Max(2f, dist * 0.04f);
        orbitDistance = dist;
        orbitPivot = focus;
        float rad = heading * Mathf.Deg2Rad;
        Vector3 forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        ApplyClipPlanes(flightCamera);
        flightCamera.transform.position = focus - forward * dist + Vector3.up * dist * 0.38f;
        flightCamera.transform.LookAt(focus + Vector3.up * dist * 0.05f);
        SyncAnglesFromCamera();
    }

    private static void NormalizeLength(GameObject go, float targetLength)
    {
        var filters = go.GetComponentsInChildren<MeshFilter>(true);
        float length = 0.01f;
        for (int i = 0; i < filters.Length; i++)
        {
            if (filters[i].sharedMesh == null)
                continue;
            Vector3 size = Vector3.Scale(filters[i].sharedMesh.bounds.size, Abs(filters[i].transform.lossyScale));
            length = Mathf.Max(length, size.x, size.y, size.z);
        }
        float factor = targetLength / length;
        go.transform.localScale *= factor;
    }

    private static Vector3 Abs(Vector3 v)
    {
        return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }

    private float FormationSpan(List<FlightDataStreamReceiver.TrackPosition> positions)
    {
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos == null)
                continue;
            Vector3 world = ToVisual(pos.lng, pos.lat, pos.alt);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
        return Mathf.Max(40f, Vector3.Distance(min, max));
    }

    private static void HideUnused<T>(Dictionary<string, T> map, HashSet<string> used) where T : Component
    {
        foreach (var kv in map)
        {
            if (kv.Value != null && !used.Contains(kv.Key))
                kv.Value.gameObject.SetActive(false);
        }
    }

    private static void SetLayer(GameObject go)
    {
        var all = go.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            all[i].gameObject.layer = FlightLayer;
    }
}
