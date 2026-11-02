using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 中间画面切换：总览视图 = 现有二维地图，飞行视图 = 同一块区域里的三维相机。
/// 航迹和雷达三维绘制后续再接到 FlightViewRoot，这一步只负责切换。
/// </summary>
public class ViewModeSwitcher : MonoBehaviour
{
    public enum ViewMode
    {
        Overview2D,
        Flight3D
    }

    public static ViewModeSwitcher Instance { get; private set; }

    [SerializeField] private Button overviewButton;
    [SerializeField] private Button flightButton;
    [SerializeField] private RectTransform mapViewport;
    [SerializeField] private Color activeButtonColor = new Color(0.15f, 0.45f, 0.75f, 0.95f);
    [SerializeField] private Color idleButtonColor = new Color(0.12f, 0.16f, 0.22f, 0.92f);

    private RawImage flightViewImage;
    private Camera flightCamera;
    private RenderTexture flightTarget;
    private ViewMode mode = ViewMode.Overview2D;

    public ViewMode Current => mode;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<ViewModeSwitcher>() != null)
            return;
        var go = new GameObject("ViewModeSwitcher");
        go.AddComponent<ViewModeSwitcher>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        if (flightTarget != null)
            flightTarget.Release();
    }

    private void Start()
    {
        ResolveRefs();
        BindButtons();
        EnsureFlightView();
        var saved = PanelMemory.Load();
        if (saved.mapView != null && saved.mapView.flight3D)
            ShowFlight();
        else
            ShowOverview();
    }

    public void ShowOverview()
    {
        mode = ViewMode.Overview2D;
        if (mapViewport != null)
            mapViewport.gameObject.SetActive(true);
        if (flightViewImage != null)
            flightViewImage.gameObject.SetActive(false);
        if (flightCamera != null)
            flightCamera.enabled = false;
        PaintButtons();
        DifferenceLabelToggleButton.SyncVisibility();
        RememberMode(false);
    }

    public void ShowFlight()
    {
        mode = ViewMode.Flight3D;
        EnsureFlightView();
        if (mapViewport != null)
            mapViewport.gameObject.SetActive(false);
        if (flightViewImage != null)
        {
            CopyMapRect(flightViewImage.rectTransform);
            flightViewImage.gameObject.SetActive(true);
        }
        if (flightCamera != null)
            flightCamera.enabled = true;
        var scene = GetComponent<FlightSceneView>();
        if (scene != null)
            scene.Bind(flightCamera, flightViewImage, false);
        PaintButtons();
        DifferenceLabelToggleButton.SyncVisibility();
        RememberMode(true);
    }

    private static void RememberMode(bool flight3D)
    {
        var store = PanelMemory.Load();
        if (store.mapView == null)
            store.mapView = new PanelMemory.MapViewTune();
        store.mapView.flight3D = flight3D;
        PanelMemory.Save(store);
    }

    private void ResolveRefs()
    {
        if (mapViewport == null)
        {
            var found = GameObject.Find("MapViewport");
            if (found != null)
                mapViewport = found.GetComponent<RectTransform>();
        }

        if (overviewButton == null)
            overviewButton = FindButton("总览视图");
        if (flightButton == null)
            flightButton = FindButton("飞行视图");
    }

    private void BindButtons()
    {
        if (overviewButton == null || flightButton == null)
            CreateFallbackButtons();

        if (overviewButton != null)
        {
            overviewButton.onClick.RemoveListener(ShowOverview);
            overviewButton.onClick.AddListener(ShowOverview);
        }
        if (flightButton != null)
        {
            flightButton.onClick.RemoveListener(ShowFlight);
            flightButton.onClick.AddListener(ShowFlight);
        }
    }

    private void CreateFallbackButtons()
    {
        if (mapViewport == null)
            return;
        var parent = mapViewport.parent as RectTransform;
        if (parent == null)
            return;

        Vector2 topLeft = mapViewport.anchoredPosition + new Vector2(-mapViewport.sizeDelta.x * 0.5f, mapViewport.sizeDelta.y * 0.5f + 8f);
        if (overviewButton == null)
            overviewButton = CreateButton(parent, "总览视图", topLeft);
        if (flightButton == null)
            flightButton = CreateButton(parent, "飞行视图", topLeft + new Vector2(132f, 0f));
    }

    private Button CreateButton(RectTransform parent, string label, Vector2 anchoredPosition)
    {
        var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = mapViewport.anchorMin;
        rt.anchorMax = mapViewport.anchorMax;
        rt.pivot = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(124f, 32f);
        rt.anchoredPosition = anchoredPosition;

        var image = go.GetComponent<Image>();
        image.color = idleButtonColor;
        var button = go.GetComponent<Button>();
        button.targetGraphic = image;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        textGo.transform.SetParent(go.transform, false);
        var textRt = textGo.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;
        var text = textGo.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 16;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.text = label;
        text.raycastTarget = false;
        return button;
    }

    private void EnsureFlightView()
    {
        if (mapViewport == null)
            return;

        if (flightTarget == null)
        {
            flightTarget = new RenderTexture(1280, 720, 16, RenderTextureFormat.ARGB32);
            flightTarget.name = "FlightViewRT";
            flightTarget.Create();
        }

        if (flightViewImage == null)
        {
            var go = new GameObject("FlightViewImage", typeof(RectTransform), typeof(RawImage));
            go.transform.SetParent(mapViewport.parent, false);
            go.transform.SetSiblingIndex(mapViewport.GetSiblingIndex());
            flightViewImage = go.GetComponent<RawImage>();
            flightViewImage.texture = flightTarget;
            flightViewImage.color = Color.white;
            flightViewImage.raycastTarget = true;
            CopyMapRect(flightViewImage.rectTransform);
            go.SetActive(false);
        }

        if (flightCamera == null)
        {
            var camGo = new GameObject("FlightViewCamera");
            flightCamera = camGo.AddComponent<Camera>();
            flightCamera.clearFlags = CameraClearFlags.SolidColor;
            flightCamera.backgroundColor = new Color(0.05f, 0.08f, 0.12f, 1f);
            flightCamera.fieldOfView = 50f;
            flightCamera.nearClipPlane = 0.3f;
            flightCamera.farClipPlane = 250000f;
            flightCamera.cullingMask = 1 << 3;
            flightCamera.targetTexture = flightTarget;
            flightCamera.depth = -2;
            flightCamera.enabled = false;
            var scene = GetComponent<FlightSceneView>();
            if (scene == null)
                scene = gameObject.AddComponent<FlightSceneView>();
            scene.Bind(flightCamera, flightViewImage, false);
        }
    }

    private void CopyMapRect(RectTransform target)
    {
        if (mapViewport == null || target == null)
            return;
        target.anchorMin = mapViewport.anchorMin;
        target.anchorMax = mapViewport.anchorMax;
        target.pivot = mapViewport.pivot;
        target.anchoredPosition = mapViewport.anchoredPosition;
        target.sizeDelta = mapViewport.sizeDelta;
        target.localRotation = mapViewport.localRotation;
        target.localScale = mapViewport.localScale;
    }

    private void PaintButtons()
    {
        Paint(overviewButton, mode == ViewMode.Overview2D);
        Paint(flightButton, mode == ViewMode.Flight3D);
    }

    private void Paint(Button button, bool active)
    {
        if (button == null)
            return;
        var image = button.targetGraphic as Image ?? button.GetComponent<Image>();
        if (image != null)
            image.color = active ? activeButtonColor : idleButtonColor;
    }

    private static Button FindButton(string label)
    {
        var texts = Resources.FindObjectsOfTypeAll<Text>();
        for (int i = 0; i < texts.Length; i++)
        {
            var text = texts[i];
            if (text == null || !text.gameObject.scene.IsValid())
                continue;
            string value = text.text == null ? "" : text.text.Trim();
            if (value != label && text.gameObject.name != label)
                continue;
            var button = text.GetComponentInParent<Button>();
            if (button != null)
                return button;
        }

        var buttons = Resources.FindObjectsOfTypeAll<Button>();
        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            if (button != null && button.gameObject.scene.IsValid() && button.gameObject.name == label)
                return button;
        }
        return null;
    }
}
