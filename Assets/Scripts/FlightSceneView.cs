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
    private Transform ground;
    private Material groundMaterial;
    private Texture groundTexture;
    private Vector2 cachedLonRange = new Vector2(97.2f, 110.5f);
    private Vector2 cachedLatRange = new Vector2(26.2f, 34.9f);
    private bool hasCachedGeoRange;
    private GameObject modelTemplate;
    private bool hasOrigin;
    private float originLat;
    private float originLng;
    private float nextRefresh;
    private readonly Dictionary<string, Transform> planes = new Dictionary<string, Transform>();
    private readonly Dictionary<string, LineRenderer> tracks = new Dictionary<string, LineRenderer>();
    private readonly Dictionary<string, MeshFilter> volumes = new Dictionary<string, MeshFilter>();
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
    public float TrailWidthMeters = 12f;
    public float AxisGizmoWidth = 3.5f;
    public float ModelLength { get; private set; } = 18f;
    private float appliedModelLength = 18f;

    public bool LabelsVisible => labelsVisible;

    public void SetModelLength(float length)
    {
        length = Mathf.Clamp(length, 0.2f, 80f);
        float ratio = length / Mathf.Max(0.01f, appliedModelLength);
        ModelLength = length;
        appliedModelLength = length;
        if (Mathf.Abs(ratio - 1f) < 0.001f)
            return;
        foreach (var kv in planes)
        {
            if (kv.Value != null)
                kv.Value.localScale *= ratio;
        }
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
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.radarLine3D > 0.05f)
            RadarEdgePixels = saved.planeIcon.radarLine3D;
        if (saved.planeIcon != null && saved.planeIcon.saved && saved.planeIcon.modelLength > 0.15f)
            SetModelLength(saved.planeIcon.modelLength);
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
        EnsureSky();
        RefreshDisplayScale();
        CacheMapSource(ResolveMap());
        EnsureGround();
        if (flightCamera != null)
        {
            flightCamera.cullingMask = 1 << FlightLayer;
            flightCamera.clearFlags = CameraClearFlags.Skybox;
            var sky = flightCamera.GetComponent<Skybox>();
            if (sky == null)
                sky = flightCamera.gameObject.AddComponent<Skybox>();
            sky.material = skyMaterial;
            Vector3 euler = flightCamera.transform.eulerAngles;
            yaw = euler.y;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }
    }

    private void Update()
    {
        if (flightCamera == null || !flightCamera.enabled)
            return;
        Navigate();
        FaceLabels();
        UpdateVolumeOutlines();
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
        if (skyMaterial != null)
            return;
        var shader = Shader.Find("Skybox/Procedural");
        if (shader == null)
            return;
        skyMaterial = new Material(shader);
        skyMaterial.SetFloat("_SunSize", 0.018f);
        skyMaterial.SetFloat("_AtmosphereThickness", 0.55f);
        skyMaterial.SetColor("_SkyTint", new Color(0.38f, 0.58f, 0.88f));
        skyMaterial.SetColor("_GroundColor", HorizonSkyColor);
        skyMaterial.SetFloat("_Exposure", 1.05f);
    }

    private PlaneDisplayController ResolveMap()
    {
        return FindAnyObjectByType<PlaneDisplayController>(FindObjectsInactive.Include);
    }

    private void CacheMapSource(PlaneDisplayController map)
    {
        if (map == null)
            return;
        if (map.TryGetGeoRange(out Vector2 lonRange, out Vector2 latRange))
        {
            cachedLonRange = lonRange;
            cachedLatRange = latRange;
            hasCachedGeoRange = true;
        }
        Texture tex = LoadMapTexture(map);
        if (tex != null)
            groundTexture = tex;
    }

    private void EnsureGround()
    {
        EnsureRoot();
        if (ground == null)
        {
            var existing = root.Find("MapGround");
            if (existing != null)
            {
                var leftoverCol = existing.GetComponent<Collider>();
                if (leftoverCol != null)
                    Destroy(leftoverCol);
                ground = existing;
            }
            else
            {
                var go = new GameObject("MapGround");
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>();
                ground = go.transform;
            }
        }

        var filter = ground.GetComponent<MeshFilter>();
        if (filter == null)
            filter = ground.gameObject.AddComponent<MeshFilter>();
        if (filter.sharedMesh == null || filter.sharedMesh.name != "MapGroundAtmosphere")
            filter.sharedMesh = BuildGroundMesh();

        if (groundMaterial == null)
        {
            var shader = Shader.Find("HK132/MapGround");
            if (shader == null)
                shader = Shader.Find("Unlit/Texture");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            groundMaterial = new Material(shader != null ? shader : Shader.Find("Standard"));
            groundMaterial.color = Color.white;
        }

        var renderer = ground.GetComponent<MeshRenderer>();
        if (renderer == null)
            renderer = ground.gameObject.AddComponent<MeshRenderer>();
        renderer.enabled = true;
        renderer.sharedMaterial = groundMaterial;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.allowOcclusionWhenDynamic = false;
        ground.localRotation = Quaternion.identity;
        SetLayer(ground.gameObject);
        RefreshGround();
    }

    private static readonly Color HorizonSkyColor = new Color(0.70f, 0.82f, 0.90f, 1f);

    private static Mesh BuildGroundMesh()
    {
        // 中间 ±0.5 铺真实地图；外面是同平面雾色延伸，用距离雾接天空，不按矩形淡出。
        const int n = 49;
        const float extent = 4.5f;
        const float mapHalf = 0.5f;
        var verts = new Vector3[n * n];
        var uvs = new Vector2[n * n];
        var norms = new Vector3[n * n];
        for (int iz = 0; iz < n; iz++)
        {
            float z = Mathf.Lerp(-extent, extent, iz / (n - 1f));
            for (int ix = 0; ix < n; ix++)
            {
                float x = Mathf.Lerp(-extent, extent, ix / (n - 1f));
                int i = iz * n + ix;
                verts[i] = new Vector3(x, 0f, z);
                uvs[i] = new Vector2(x + mapHalf, z + mapHalf);
                norms[i] = Vector3.up;
            }
        }
        var tris = new int[(n - 1) * (n - 1) * 6];
        int tri = 0;
        for (int iz = 0; iz < n - 1; iz++)
        {
            for (int ix = 0; ix < n - 1; ix++)
            {
                int a = iz * n + ix;
                int b = a + 1;
                int c = a + n;
                int d = c + 1;
                tris[tri++] = a; tris[tri++] = c; tris[tri++] = b;
                tris[tri++] = c; tris[tri++] = d; tris[tri++] = b;
            }
        }
        var mesh = new Mesh { name = "MapGroundAtmosphere" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.normals = norms;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    private void RefreshGround()
    {
        if (ground == null)
            EnsureGround();
        if (ground == null)
            return;

        RefreshDisplayScale();
        CacheMapSource(ResolveMap());
        Vector2 lonRange = hasCachedGeoRange ? cachedLonRange : new Vector2(97.2f, 110.5f);
        Vector2 latRange = hasCachedGeoRange ? cachedLatRange : new Vector2(26.2f, 34.9f);

        float lat0 = hasOrigin ? originLat : (latRange.x + latRange.y) * 0.5f;
        float lng0 = hasOrigin ? originLng : (lonRange.x + lonRange.y) * 0.5f;
        Vector3 sw = ToVisualRelative(lonRange.x, latRange.x, lat0, lng0);
        Vector3 se = ToVisualRelative(lonRange.y, latRange.x, lat0, lng0);
        Vector3 nw = ToVisualRelative(lonRange.x, latRange.y, lat0, lng0);
        Vector3 center = (sw + se + nw + ToVisualRelative(lonRange.y, latRange.y, lat0, lng0)) * 0.25f;
        float width = Mathf.Max(1f, Vector3.Distance(new Vector3(sw.x, 0f, sw.z), new Vector3(se.x, 0f, se.z)));
        float depth = Mathf.Max(1f, Vector3.Distance(new Vector3(sw.x, 0f, sw.z), new Vector3(nw.x, 0f, nw.z)));

        ground.position = new Vector3(center.x, 0f, center.z);
        ground.localRotation = Quaternion.identity;
        ground.localScale = new Vector3(width, 1f, depth);
        ground.gameObject.SetActive(true);
        SetLayer(ground.gameObject);

        if (groundTexture == null)
            groundTexture = LoadMapTexture(ResolveMap());
        if (groundMaterial != null)
        {
            if (groundTexture != null)
            {
                groundMaterial.mainTexture = groundTexture;
                groundMaterial.SetTexture("_MainTex", groundTexture);
            }
            groundMaterial.color = Color.white;
            groundMaterial.SetColor("_FogColor", HorizonSkyColor);
            groundMaterial.SetColor("_HorizonColor", HorizonSkyColor);
            float span = Mathf.Max(width, depth);
            groundMaterial.SetFloat("_FogDensity", 2.4f / Mathf.Max(1f, span));
            groundMaterial.SetFloat("_EdgeSoft", 0.018f);
            groundMaterial.SetVector("_Center", new Vector4(center.x, 0f, center.z, 0f));
            groundMaterial.SetFloat("_Curve", 0.035f / Mathf.Max(1f, span));
            var renderer = ground.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = true;
                renderer.sharedMaterial = groundMaterial;
            }
        }

        var filter = ground.GetComponent<MeshFilter>();
        if (filter != null && filter.sharedMesh != null)
            filter.sharedMesh.RecalculateBounds();

        if (flightCamera != null)
        {
            float span = Mathf.Max(width, depth);
            flightCamera.nearClipPlane = Mathf.Min(flightCamera.nearClipPlane, 0.3f);
            flightCamera.farClipPlane = Mathf.Max(flightCamera.farClipPlane, span * 8f, 250000f);
        }
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
                if (images[i] != null && images[i].sprite != null && images[i].sprite.texture != null)
                    return images[i].sprite.texture;
            }
        }
        return Resources.Load<Texture2D>("成都");
    }

    private Vector3 ToVisualRelative(double lng, double lat, float lat0, float lng0)
    {
        return ToVisual(RadarDetectionSystem.GeoToLocalMeters(lng, lat, 0f, lat0, lng0));
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
        RefreshGround();

        var usedPlanes = new HashSet<string>();
        Vector3 sum = Vector3.zero;
        int n = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            if (pos == null || string.IsNullOrWhiteSpace(pos.trackId))
                continue;
            float alt = StableAltitude(pos.trackId, pos.alt);
            Vector3 world = ToVisual(pos.lng, pos.lat, alt);
            var plane = GetPlane(pos.trackId);
            plane.rotation = FlightAttitude(pos.heading, pos.pitch, pos.roll);
            plane.position = world;
            Vector3 center = RendererCenter(plane.gameObject);
            plane.position += world - center;
            LiftModelAboveGround(plane);
            planeCenters[pos.trackId] = world;
            AppendLiveTrail(pos.trackId, world);
            usedPlanes.Add(pos.trackId);
            sum += world;
            n++;
        }
        HideUnused(planes, usedPlanes);
        DrawDifferenceLabels(positions);
        HideUnused(labels, usedPlanes);

        DrawTracks(receiver);
        DrawVolumes(radar, positions);
        if (n > 0)
            FrameToMappedWindow(sum / n, MeanHeading(positions), FormationSpan(positions));
    }

    private void RefreshDisplayScale()
    {
        var map = ResolveMap();
        if (map != null && map.TryGetDisplayScale(hasOrigin ? originLat : 30.55f, out _, out _, out float uniform))
        {
            uniform = Mathf.Max(1e-6f, uniform);
            // 东西、北、高度同一比例，雷达锥才不会被拉成长条。
            unitsPerMeter = new Vector3(uniform, uniform, uniform);
        }
    }

    /// <summary>只抬机模，不改雷达/轨迹高度，避免锥体跟着形变。</summary>
    private static void LiftModelAboveGround(Transform plane)
    {
        if (plane == null)
            return;
        const float clearance = 0.08f;
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
        // 网格机头在局部 +Y，但姿态四元数之后世界朝向是 -Y，沿 +Y 会把雷达推到机尾。
        Vector3 nose = -(FlightAttitude(sensor.heading, sensor.pitch, sensor.roll) * Vector3.up);
        if (nose.sqrMagnitude < 1e-4f)
            return body;
        float along = RadarStationTuner.Instance != null
            ? RadarStationTuner.Instance.Offset3D(sensor.mountSign)
            : sensor.mountSign * 8f;
        float up = RadarStationTuner.Instance != null
            ? RadarStationTuner.Instance.OffsetUp(sensor.mountSign)
            : 0f;
        return body + nose.normalized * along + Vector3.up * up;
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
        go.transform.SetParent(root, false);
        foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour != null)
                behaviour.enabled = false;
        }
        foreach (var collider in go.GetComponentsInChildren<Collider>(true))
        {
            if (collider != null)
                Destroy(collider);
        }
        NormalizeLength(go, ModelLength);
        appliedModelLength = ModelLength;
        SetLayer(go);
        planes[trackId] = go.transform;
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
        Vector3 camRight = flightCamera != null ? flightCamera.transform.right : Vector3.right;
        Vector3 camForward = flightCamera != null ? flightCamera.transform.forward : Vector3.forward;
        camRight.y = 0f;
        if (camRight.sqrMagnitude > 0.0001f)
            camRight.Normalize();
        camForward.y = 0f;
        if (camForward.sqrMagnitude > 0.0001f)
            camForward.Normalize();
        float right = tuner != null ? tuner.offsetRight : 0f;
        float up = tuner != null ? tuner.offsetUp : 12f;
        float forward = tuner != null ? tuner.offsetForward : 0f;

        for (int i = 0; i < live.Count; i++)
        {
            var pos = live[i];
            Vector3 world = planeCenters[pos.trackId];
            var mesh = GetLabel(pos.trackId);
            mesh.gameObject.SetActive(true);
            mesh.color = color;
            mesh.text = BuildCycledDifferenceLabel(pos, live, peerSlot);
            float scale = tuner != null ? Mathf.Max(0.05f, tuner.textScale) : 1f;
            mesh.transform.localScale = new Vector3(scale, scale, scale);
            mesh.transform.position = world + camRight * right + Vector3.up * up + camForward * forward;
            FaceLabel(mesh);
        }
    }

    private void FaceLabels()
    {
        if (!labelsVisible || flightCamera == null)
            return;
        foreach (var kv in labels)
            FaceLabel(kv.Value);
    }

    private void FaceLabel(TextMesh mesh)
    {
        if (mesh == null || flightCamera == null || !mesh.gameObject.activeInHierarchy)
            return;
        Vector3 toCam = mesh.transform.position - flightCamera.transform.position;
        if (toCam.sqrMagnitude > 0.0001f)
            mesh.transform.rotation = Quaternion.LookRotation(toCam, Vector3.up);
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
                float planWidth = Mathf.Max(0.002f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
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
        float liveWidth = Mathf.Max(0.002f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
        foreach (var kv in liveTrails)
        {
            if (kv.Value.Count < 2)
                continue;
            var lr = GetLine(tracks, "live_" + kv.Key, liveWidth, TrailColor(kv.Key));
            lr.widthMultiplier = liveWidth;
            // 全程航迹点数很多，一次 SetPositions 比逐点 SetPosition 快得多。
            lr.positionCount = kv.Value.Count;
            lr.SetPositions(kv.Value.ToArray());
            ApplyTrailColor(lr, TrailColor(kv.Key));
            lr.gameObject.SetActive(TrailVisible(kv.Key));
            used.Add("live_" + kv.Key);
        }
        HideUnused(tracks, used);
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
                float hitWidth = Mathf.Max(0.002f, TrailWidthMeters * Mathf.Max(unitsPerMeter.x, unitsPerMeter.z));
                var beam = GetLine(hits, hitKey, hitWidth, Color.white);
                beam.widthMultiplier = hitWidth;
                beam.positionCount = 2;
                beam.SetPosition(0, apex);
                Vector3 targetMeters = RadarDetectionSystem.GeoToLocalMeters(det.targetLng, det.targetLat, det.targetAlt, originLat, originLng);
                beam.SetPosition(1, ToVisual(targetMeters));
                beam.startColor = beam.endColor = color;
                beam.gameObject.SetActive(true);
                usedHits.Add(hitKey);
            }
            var filter = GetVolume(key);
            BuildVolume(filter.mesh, sensor, originMeters);
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
        Vector3 mid = points[points.Length / 2];
        lr.widthMultiplier = PixelWidth(mid, RadarEdgePixels);
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
        lr.widthMultiplier = PixelWidth(points[0], RadarEdgePixels);
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
        float height = Mathf.Max(1f, flightCamera.pixelHeight);
        return pixels * 2f * dist * Mathf.Tan(flightCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / height;
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

    private static void ApplyRadarVolumeMaterial(MeshRenderer renderer, Color color, bool anyHit)
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
        float alpha = anyHit ? 0.22f : 0.16f;
        Color fill = new Color(color.r, color.g, color.b, alpha);
        Color edge = new Color(color.r, color.g, color.b, alpha);
        mat.SetColor("_Color", fill);
        mat.SetColor("_EdgeColor", edge);
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
        DrawGradientLine(leftKey, apex, left, color, far, 2f);
        DrawGradientLine(rightKey, apex, right, color, far, 2f);
        used.Add(leftKey);
        used.Add(rightKey);

        var cap = BuildCapSilhouette(apex, axis, range, half, cam, left, right);
        if (cap.Count >= 2)
        {
            var lr = GetLine(volumeEdges, capKey, 1.8f, far);
            lr.positionCount = cap.Count;
            lr.SetPositions(cap.ToArray());
            lr.startColor = lr.endColor = far;
            lr.colorGradient = FlatGradient(far);
            lr.gameObject.SetActive(true);
            used.Add(capKey);
        }
    }

    private void DrawGradientLine(string key, Vector3 from, Vector3 to, Color near, Color far, float width)
    {
        var lr = GetLine(volumeEdges, key, width, near);
        lr.positionCount = 2;
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
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

    private void Navigate()
    {
        if (flightCamera == null || viewImage == null || !viewImage.gameObject.activeInHierarchy)
            return;
        if (IsTyping())
            return;

        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        bool over = IsPointerOverView();
        float mx = Input.GetAxis("Mouse X");
        float my = Input.GetAxis("Mouse Y");
        float scroll = Input.mouseScrollDelta.y;

        if (Input.GetMouseButtonDown(1))
        {
            if (!over)
                return;
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
        if (Input.GetMouseButtonDown(0) && alt && over)
        {
            userHasControl = true;
            orbiting = true;
            orbitPivot = flightCamera.transform.position + flightCamera.transform.forward * orbitDistance;
        }
        if (Input.GetMouseButtonUp(1))
        {
            flying = false;
            zoomDragging = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (Input.GetMouseButtonUp(2))
            panning = false;
        if (Input.GetMouseButtonUp(0))
            orbiting = false;

        if (over && Input.GetKeyDown(KeyCode.F))
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

        if (over && Mathf.Abs(scroll) > 0.01f)
            Dolly(scroll * flySpeed * 0.35f);
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
        if (viewImage == null)
            return false;
        var canvas = viewImage.canvas;
        Camera eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return RectTransformUtility.RectangleContainsScreenPoint(viewImage.rectTransform, Input.mousePosition, eventCamera);
    }

    private static bool IsTyping()
    {
        if (EventSystem.current == null)
            return false;
        var selected = EventSystem.current.currentSelectedGameObject;
        return selected != null && selected.GetComponent<InputField>() != null;
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
        flightCamera.farClipPlane = Mathf.Max(250000f, dist * 3f);
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
