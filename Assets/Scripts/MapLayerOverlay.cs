using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>StreamingAssets/MapLayers.json 里的一层贴图定义。</summary>
[Serializable]
public class MapLayerDef
{
    public string name;
    public string image;
    public double lonMin;
    public double latMin;
    public double lonMax;
    public double latMax;
    public int zoom;
    public int width;
    public int height;
}

[Serializable]
public class MapLayerFile
{
    public List<MapLayerDef> layers;
}

/// <summary>一层已加载的贴图：经纬范围 + 纹理，二维三维共用。</summary>
public class MapLayer
{
    public MapLayerDef def;
    public Texture2D texture;
    public Sprite sprite;

    public double Area
    {
        get
        {
            if (def == null)
                return 0.0;
            return Math.Abs(def.lonMax - def.lonMin) * Math.Abs(def.latMax - def.latMin);
        }
    }

    public string Label => def != null && !string.IsNullOrWhiteSpace(def.name) ? def.name : "层";
}

/// <summary>
/// 分层地图：底图之外再叠几层不同缩放的高清卫星图。
/// 大范围层在下面，小范围高清层在上面，二维和三维用同一份列表。
/// </summary>
public static class MapLayerCatalog
{
    private static bool loaded;
    private static readonly List<MapLayer> layers = new List<MapLayer>();

    public static IReadOnlyList<MapLayer> Layers
    {
        get
        {
            EnsureLoaded();
            return layers;
        }
    }

    public static void Reload()
    {
        loaded = false;
        layers.Clear();
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (loaded)
            return;
        loaded = true;
        layers.Clear();

        var defs = new List<MapLayerDef>();

        // 1) Resources/MapLayers 下由 Tools/build_map_tiles.py 生成的分块清单。
        var assets = Resources.LoadAll<TextAsset>("MapLayers");
        for (int i = 0; i < assets.Length; i++)
        {
            var asset = assets[i];
            if (asset == null || !asset.name.EndsWith("_manifest", StringComparison.Ordinal))
                continue;
            CollectDefs(asset.text, defs);
        }

        // 2) StreamingAssets/MapLayers.json：手工补充或微调用的入口。
        string path = Path.Combine(Application.streamingAssetsPath, "MapLayers.json");
        try
        {
            if (File.Exists(path))
                CollectDefs(File.ReadAllText(path), defs);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MapLayers] 读取 {path} 失败: {ex.Message}");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in defs)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.image))
                continue;
            string key = def.image.Trim();
            if (!seen.Add(key))
                continue;
            var tex = Resources.Load<Texture2D>(key);
            if (tex == null)
            {
                Debug.LogWarning($"[MapLayers] 找不到 Resources/{key}，跳过 {def.name}");
                continue;
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 16;
            tex.mipMapBias = 0f;
            tex.wrapMode = TextureWrapMode.Clamp;

            var layer = new MapLayer { def = def, texture = tex };
            if (tex.width > 1 && tex.height > 1)
            {
                layer.sprite = Sprite.Create(
                    tex, new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
            }
            layers.Add(layer);
        }

        // 范围大的排前面（画在下面），范围小的压在上面。
        layers.Sort((a, b) => b.Area.CompareTo(a.Area));
    }

    private static void CollectDefs(string json, List<MapLayerDef> into)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        MapLayerFile file = null;
        try
        {
            file = JsonUtility.FromJson<MapLayerFile>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MapLayers] 清单解析失败: {ex.Message}");
            return;
        }
        if (file?.layers == null)
            return;
        for (int i = 0; i < file.layers.Count; i++)
        {
            if (file.layers[i] != null)
                into.Add(file.layers[i]);
        }
    }
}

/// <summary>标记：这个 RectTransform 是一层地图贴图（供排序识别）。</summary>
public class MapLayerImage : MonoBehaviour
{
    public MapLayer layer;
}

/// <summary>
/// 地图子物体顺序：所有贴图层按范围从大到小垫底，飞机、雷达在上。
/// 二维插图、分层贴图、雷达都走这里，避免互相抢层级把东西盖住。
/// </summary>
public static class MapLayerOrder
{
    public static void Apply(RectTransform mapRoot)
    {
        if (mapRoot == null)
            return;

        var insets = new List<RectTransform>();
        var areas = new List<double>();
        for (int i = 0; i < mapRoot.childCount; i++)
        {
            var child = mapRoot.GetChild(i) as RectTransform;
            if (child == null)
                continue;
            double area = -1.0;
            var marker = child.GetComponent<MapLayerImage>();
            if (marker != null && marker.layer != null)
            {
                area = marker.layer.Area;
            }
            else
            {
                var inset = child.GetComponent<MapInsetOverlay>();
                if (inset != null)
                {
                    // 必须和分块层用同一量纲（平方度），否则像素面积会把它排到最底被盖住。
                    area = Math.Abs(inset.LonMax - inset.LonMin) * Math.Abs(inset.LatMax - inset.LatMin);
                }
            }
            if (area < 0.0)
                continue;
            insets.Add(child);
            areas.Add(area);
        }

        // 大范围在下，小范围高清在上。
        var pairs = new List<(RectTransform t, double a)>();
        for (int i = 0; i < insets.Count; i++)
            pairs.Add((insets[i], areas[i]));
        pairs.Sort((x, y) => y.a.CompareTo(x.a));

        int index = 0;
        for (int i = 0; i < pairs.Count; i++)
            pairs[i].t.SetSiblingIndex(index++);

        var planes = mapRoot.Find("PlaneIcons");
        if (planes != null)
            planes.SetSiblingIndex(index++);
        var overlay = mapRoot.Find("MapOverlay");
        if (overlay != null)
            overlay.SetSiblingIndex(index);
    }
}

/// <summary>把 MapLayers.json 里的每一层铺到二维地图上。</summary>
[DisallowMultipleComponent]
public class MapLayerOverlay2D : MonoBehaviour
{
    private PlaneDisplayController display;
    private RectTransform mapRoot;
    private readonly List<Image> images = new List<Image>();
    private int builtCount = -1;

    private void LateUpdate()
    {
        ResolveRefs();
        BuildIfNeeded();
        Layout();
    }

    private void ResolveRefs()
    {
        if (display == null)
            display = FindAnyObjectByType<PlaneDisplayController>(FindObjectsInactive.Include);
        if (display != null)
            mapRoot = display.MapRoot;
    }

    private void BuildIfNeeded()
    {
        var layers = MapLayerCatalog.Layers;
        if (mapRoot == null || layers.Count == builtCount)
            return;
        builtCount = layers.Count;

        for (int i = 0; i < images.Count; i++)
        {
            if (images[i] != null)
                Destroy(images[i].gameObject);
        }
        images.Clear();

        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.sprite == null)
                continue;
            var go = new GameObject("地图层_" + layer.Label);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(mapRoot, false);
            var image = go.AddComponent<Image>();
            image.sprite = layer.sprite;
            image.raycastTarget = false;
            image.preserveAspect = false;
            var marker = go.AddComponent<MapLayerImage>();
            marker.layer = layer;
            images.Add(image);
        }
    }

    private void Layout()
    {
        if (mapRoot == null || display == null)
            return;
        foreach (var image in images)
        {
            if (image == null)
                continue;
            var marker = image.GetComponent<MapLayerImage>();
            if (marker == null || marker.layer?.def == null)
                continue;
            var def = marker.layer.def;
            Vector2 sw = display.ConvertToAnchoredPosition((float)def.lonMin, (float)def.latMin);
            Vector2 ne = display.ConvertToAnchoredPosition((float)def.lonMax, (float)def.latMax);
            var min = new Vector2(Mathf.Min(sw.x, ne.x), Mathf.Min(sw.y, ne.y));
            var max = new Vector2(Mathf.Max(sw.x, ne.x), Mathf.Max(sw.y, ne.y));

            var rect = image.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.anchoredPosition = (min + max) * 0.5f;
            rect.sizeDelta = max - min;
        }
        MapLayerOrder.Apply(mapRoot);
    }
}
