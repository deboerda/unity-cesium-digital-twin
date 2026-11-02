using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Image-viewer style zoom/pan for the map window.
/// The viewport stays fixed; content (the map, planes, radar, trails) is scaled and moved.
/// Zoom cannot go below 1, and pan is clamped so the view never shows outside the map.
/// Geo mapping stays in map-local space and is not affected.
/// </summary>
[DisallowMultipleComponent]
public class MapZoomPanController : MonoBehaviour, IScrollHandler
{
    [SerializeField] private RectTransform viewport;
    [SerializeField] private RectTransform content;
    [SerializeField] private float minZoom = 1f;
    [SerializeField] [Min(1f)] [Tooltip("地图最大放大倍数，越大越能看清机场跑道。")]
    private float maxZoom = 80f;
    [SerializeField] private float zoomStep = 0.18f;
    [SerializeField] private bool zoomTowardPointer = true;

    private Canvas rootCanvas;
    private Camera eventCamera;
    private float zoom = 1f;
    private bool dragging;
    private bool pressStartedOverMap;
    private Vector2 lastPointerLocal;
    private int lastZoomFrame = -1;

    public float Zoom => zoom;
    public float MinZoom => minZoom;
    public float MaxZoom
    {
        get => maxZoom;
        set
        {
            maxZoom = Mathf.Max(minZoom, value);
            ApplyZoomAndClamp(zoom, null);
        }
    }

    private void Awake()
    {
        ResolveRefs();
        EnsureViewportMask();
        if (!RestoreView())
            ApplyZoomAndClamp(zoom, null);
    }

    private void OnDisable()
    {
        if (Application.isPlaying)
            SaveView();
    }

    private void OnApplicationQuit()
    {
        if (Application.isPlaying)
            SaveView();
    }

    private void OnEnable()
    {
        ResolveRefs();
        EnsureViewportMask();
    }

    private void Update()
    {
        if (content == null || viewport == null)
            return;
        if (eventCamera == null)
            ResolveRefs();

        Vector2 screen = Input.mousePosition;
        bool over = IsPointerInsideViewport(screen);

        float scroll = Input.mouseScrollDelta.y;
        if (over && Mathf.Abs(scroll) > 0.01f)
            ZoomAtScreenPoint(screen, 1f + scroll * zoomStep);

        if (Input.GetMouseButtonDown(0))
        {
            pressStartedOverMap = over;
            dragging = over && TryScreenToViewport(screen, out lastPointerLocal);
        }

        if (dragging && pressStartedOverMap && Input.GetMouseButton(0))
        {
            if (TryScreenToViewport(screen, out var current))
            {
                content.anchoredPosition += current - lastPointerLocal;
                lastPointerLocal = current;
                ClampToMap();
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            if (dragging)
                SaveView();
            dragging = false;
            pressStartedOverMap = false;
        }
    }

    private void ResolveRefs()
    {
        if (content == null)
            content = transform as RectTransform;
        if (viewport == null && content != null)
            viewport = content.parent as RectTransform;

        rootCanvas = (viewport != null ? viewport : content).GetComponentInParent<Canvas>();
        if (rootCanvas != null)
            rootCanvas = rootCanvas.rootCanvas;
        eventCamera = rootCanvas != null ? rootCanvas.worldCamera : Camera.main;
    }

    private void EnsureViewportMask()
    {
        if (viewport == null)
            return;

        if (viewport.GetComponent<RectMask2D>() == null)
            viewport.gameObject.AddComponent<RectMask2D>();

        // Do not use stencil Mask + UISprite: that sprite's transparent corners
        // stretch into large notches and punch holes in the map.
        var stencil = viewport.GetComponent<Mask>();
        if (stencil != null)
            stencil.enabled = false;
        var image = viewport.GetComponent<Image>();
        if (image != null)
            image.enabled = false;
    }

    private bool RestoreView()
    {
        var store = PanelMemory.Load();
        if (store.mapView == null || !store.mapView.saved || content == null)
            return false;
        if (store.mapView.maxZoom > 1f)
            maxZoom = Mathf.Max(minZoom, store.mapView.maxZoom);
        zoom = Mathf.Clamp(store.mapView.zoom, minZoom, maxZoom);
        content.localScale = new Vector3(zoom, zoom, 1f);
        content.anchoredPosition = new Vector2(store.mapView.panX, store.mapView.panY);
        ClampToMap();
        return true;
    }

    private void SaveView()
    {
        if (content == null)
            return;
        var store = PanelMemory.Load();
        if (store.mapView == null)
            store.mapView = new PanelMemory.MapViewTune();
        store.mapView.saved = true;
        store.mapView.zoom = zoom;
        store.mapView.panX = content.anchoredPosition.x;
        store.mapView.panY = content.anchoredPosition.y;
        store.mapView.maxZoom = maxZoom;
        PanelMemory.Save(store);
    }

    public void ResetView()
    {
        zoom = minZoom;
        lastZoomFrame = Time.frameCount;
        if (content != null)
        {
            content.localScale = Vector3.one;
            content.anchoredPosition = Vector2.zero;
        }
    }

    public void OnScroll(PointerEventData eventData)
    {
        if (!IsPointerInsideViewport(eventData.position))
            return;
        float delta = eventData.scrollDelta.y;
        if (Mathf.Abs(delta) < 0.01f)
            return;
        ZoomAtScreenPoint(eventData.position, 1f + delta * zoomStep);
    }

    private void ZoomAtScreenPoint(Vector2 screenPosition, float factor)
    {
        Vector2? pivot = null;
        if (zoomTowardPointer && TryScreenToViewport(screenPosition, out var local))
            pivot = local;
        ApplyZoomAndClamp(zoom * factor, pivot);
    }

    private void ApplyZoomAndClamp(float targetZoom, Vector2? viewportPivot)
    {
        if (content == null || viewport == null)
            return;
        if (lastZoomFrame == Time.frameCount && Application.isPlaying)
            return;

        float oldZoom = Mathf.Max(0.0001f, zoom);
        zoom = Mathf.Clamp(targetZoom, minZoom, maxZoom);
        lastZoomFrame = Time.frameCount;
        if (Mathf.Abs(zoom - oldZoom) < 0.0001f)
        {
            ClampToMap();
            return;
        }

        Vector2 pivot = viewportPivot ?? Vector2.zero;
        Vector2 oldPos = content.anchoredPosition;
        content.localScale = new Vector3(zoom, zoom, 1f);
        content.anchoredPosition = pivot - (pivot - oldPos) * (zoom / oldZoom);
        ClampToMap();
        if (Application.isPlaying)
            SaveView();
    }

    private void ClampToMap()
    {
        if (content == null || viewport == null)
            return;

        // At zoom 1 the map exactly fills the viewport, so pan range is 0.
        // At zoom > 1 the extra scaled size is the only allowed pan range.
        float maxX = Mathf.Max(0f, viewport.rect.width * 0.5f * (zoom - 1f));
        float maxY = Mathf.Max(0f, viewport.rect.height * 0.5f * (zoom - 1f));
        Vector2 pos = content.anchoredPosition;
        pos.x = Mathf.Clamp(pos.x, -maxX, maxX);
        pos.y = Mathf.Clamp(pos.y, -maxY, maxY);
        content.anchoredPosition = pos;
        content.localScale = new Vector3(zoom, zoom, 1f);
    }

    private bool IsPointerInsideViewport(Vector2 screenPosition)
    {
        if (viewport == null)
            return false;
        return RectTransformUtility.RectangleContainsScreenPoint(viewport, screenPosition, eventCamera);
    }

    private bool TryScreenToViewport(Vector2 screenPosition, out Vector2 localPoint)
    {
        localPoint = Vector2.zero;
        if (viewport == null)
            return false;
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            viewport, screenPosition, eventCamera, out localPoint);
    }
}
