using UnityEngine;

/// <summary>
/// 把一块更高精度的卫星图按经纬度叠在大底图上。
/// 范围必须与贴图实际覆盖的经纬度一致，不能把旧图拉伸。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(RectTransform))]
public class MapInsetOverlay : MonoBehaviour
{
    [Header("地理范围（须与贴图一致）")]
    [SerializeField] private double lonMin = 103.66956;
    [SerializeField] private double latMin = 30.46133;
    [SerializeField] private double lonMax = 104.50156;
    [SerializeField] private double latMax = 30.88204;

    private PlaneDisplayController display;
    private RectTransform rect;

    public double LonMin => lonMin;
    public double LatMin => latMin;
    public double LonMax => lonMax;
    public double LatMax => latMax;
    public Texture OverlayTexture
    {
        get
        {
            var img = GetComponent<UnityEngine.UI.Image>();
            return img != null && img.sprite != null ? img.sprite.texture : null;
        }
    }

    public void SetGeoRange(double west, double south, double east, double north)
    {
        lonMin = west;
        latMin = south;
        lonMax = east;
        latMax = north;
        Apply();
    }

    private void OnEnable()
    {
        Apply();
    }

    private void Start()
    {
        Apply();
    }

    private void LateUpdate()
    {
        Apply();
    }

    private void OnValidate()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.delayCall += () =>
        {
            if (this != null && isActiveAndEnabled)
                Apply();
        };
#endif
    }

    public void Apply()
    {
        if (display == null)
            display = FindAnyObjectByType<PlaneDisplayController>(FindObjectsInactive.Include);
        if (display == null || display.MapRoot == null)
            return;

        if (rect == null)
            rect = transform as RectTransform;
        if (rect == null)
            return;

        if (rect.parent != display.MapRoot)
            rect.SetParent(display.MapRoot, false);

        Vector2 sw = display.ConvertToAnchoredPosition((float)lonMin, (float)latMin);
        Vector2 ne = display.ConvertToAnchoredPosition((float)lonMax, (float)latMax);
        Vector2 min = new Vector2(Mathf.Min(sw.x, ne.x), Mathf.Min(sw.y, ne.y));
        Vector2 max = new Vector2(Mathf.Max(sw.x, ne.x), Mathf.Max(sw.y, ne.y));

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one;
        rect.anchoredPosition = (min + max) * 0.5f;
        rect.sizeDelta = max - min;
        var img = GetComponent<UnityEngine.UI.Image>();
        if (img != null)
            img.raycastTarget = false;
        // 所有贴图层按范围从大到小垫底，飞机、雷达在上面；统一由 MapLayerOrder 排。
        MapLayerOrder.Apply(display.MapRoot);
    }
}
