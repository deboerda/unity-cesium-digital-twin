using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>数据配置 / 传感器 / 航迹的跨次记忆（persistentDataPath JSON）。</summary>
public static class PanelMemory
{
    private const string FileName = "HK132PanelMemory.json";

    [Serializable]
    public class Color4
    {
        public float r, g, b, a = 1f;
        public static Color4 From(Color c) => new Color4 { r = c.r, g = c.g, b = c.b, a = c.a };
        public Color ToColor() => new Color(r, g, b, a);
    }

    [Serializable]
    public class RuleItem
    {
        public string fieldKey;
        public string rule;
        public bool hideMarker;
        public Color4 color;
    }

    [Serializable]
    public class TrackRules
    {
        public string trackId;
        public List<RuleItem> entries = new List<RuleItem>();
    }

    [Serializable]
    public class SensorItem
    {
        public string key;
        public float range = -1f;
        public Color4 color;
        public bool visible = true;
        public bool hasColor;
    }

    [Serializable]
    public class SensorCatalogItem
    {
        public string trackId;
        public string detectorType;
        public string rangeType;
        public float detectRange;
        public float yawAngle;
        public float pitchAngle;
        public string centerPoint = "中";
        public string centerPoint1 = "中";
        public float yawScanAngle;
        public float pitchScanAngle;
        public string radarStatus = "有效";
    }

    [Serializable]
    public class TrailItem
    {
        public string trackId;
        public Color4 color;
        public bool visible = true;
        public bool hasColor;
    }

    [Serializable]
    public class RadarTune
    {
        public bool saved;
        public float front2D = 26f;
        public float mid2D;
        public float rear2D = -26f;
        public float front3D = 10f;
        public float mid3D = 2f;
        public float rear3D = -6f;
        public float frontUp;
        public float midUp;
        public float rearUp;
    }

    [Serializable]
    public class PlaneIconTune
    {
        public bool saved;
        public float scale = 4f;
        public float trailWidth = 3f;
        public float trailWidth3D = 12f;
        public float trailSampleMeters = 50f;
        public float trailMaxPoints = 12000f;
        public float ringRadius = 16f;
        public bool hasRingOffset;
        public float ringOffsetX;
        public float ringOffsetY;
        public float ringThickness;
        public float radarLine2D = 1.5f;
        public float radarLine3D = 1.15f;
        public float modelLength = 18f;
        public float axisGizmoWidth = 3.5f;
    }

    [Serializable]
    public class DiffLabelTune
    {
        public bool saved;
        public float offsetRight;
        public float offsetUp = 12f;
        public float offsetForward;
        public float cycleSeconds = 4f;
        public float r = 0.92f;
        public float g = 0.96f;
        public float b = 1f;
        public float textScale = 1f;
    }

    [Serializable]
    public class MapViewTune
    {
        public bool saved;
        public float zoom = 1f;
        public float panX;
        public float panY;
        public float maxZoom = 80f;
        public bool flight3D;
    }

    [Serializable]
    public class FlightViewTune
    {
        public bool saved;
        public float posX;
        public float posY;
        public float posZ;
        public float yaw;
        public float pitch;
        public float flySpeed = 8f;
        public float orbitDistance = 80f;
        public float pivotX;
        public float pivotY;
        public float pivotZ;
    }

    [Serializable]
    public class Store
    {
        public List<TrackRules> tracks = new List<TrackRules>();
        public float defaultDetectRange = -1f;
        public List<SensorItem> sensors = new List<SensorItem>();
        public List<TrailItem> trails = new List<TrailItem>();
        public List<SensorCatalogItem> sensorCatalog = new List<SensorCatalogItem>();
        public List<string> removedSensors = new List<string>();
        public RadarTune radar = new RadarTune();
        public DiffLabelTune diffLabel = new DiffLabelTune();
        public PlaneIconTune planeIcon = new PlaneIconTune();
        public MapViewTune mapView = new MapViewTune();
        public FlightViewTune flightView = new FlightViewTune();
    }

    public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);
    public static string PackagedPath => Path.Combine(Application.streamingAssetsPath, FileName);

    public static Store Load()
    {
        var store = ReadFile(FilePath);
        if (store != null)
            return store;
        store = ReadFile(PackagedPath);
        return store ?? new Store();
    }

    public static void Save(Store store)
    {
        if (store == null)
            return;
        WriteFile(FilePath, store);
        if (Application.isEditor)
            WriteFile(PackagedPath, store);
    }

    private static Store ReadFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonUtility.FromJson<Store>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[PanelMemory] Load failed: " + e.Message);
            return null;
        }
    }

    private static void WriteFile(string path, Store store)
    {
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonUtility.ToJson(store, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[PanelMemory] Save failed: " + e.Message);
        }
    }
}
