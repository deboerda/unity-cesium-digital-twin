using System.Globalization;
using UnityEngine;

/// <summary>
/// 三维三差标签配置。F3 显示/收起，默认隐藏。可调相对飞机的位置和文字颜色。
/// </summary>
public class DifferenceLabelTuner : MonoBehaviour
{
    public static DifferenceLabelTuner Instance { get; private set; }

    public float offsetRight;
    public float offsetUp = 12f;
    public float offsetForward;
    public float cycleSeconds = 4f;
    public Color textColor = new Color(0.92f, 0.96f, 1f, 1f);
    public float textScale = 1f;

    private bool open;
    private Rect window = new Rect(330f, 12f, 300f, 300f);
    private Vector2 scroll;
    private string cycleText;
    private string scaleText;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (FindObjectOfType<DifferenceLabelTuner>() != null)
            return;
        var go = new GameObject("DifferenceLabelTuner");
        DontDestroyOnLoad(go);
        go.AddComponent<DifferenceLabelTuner>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        Load();
    }

    public void Toggle()
    {
        open = !open;
    }

    public bool ContainsMouse()
    {
        return open && RadarStationTuner.GuiContainsMouse(window);
    }

    private void Update() { }

    private void OnGUI()
    {
        if (!open)
            return;
        window = GUI.Window(9143, window, DrawWindow, "三差标签  F3隐藏");
    }

    private void DrawWindow(int id)
    {
        WheelScroll();
        scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.ExpandHeight(true));
        GUILayout.Label("位置  相对飞机（屏幕像素）");
        offsetRight = Slider("左右", offsetRight, -120f, 120f);
        offsetUp = Slider("上下", offsetUp, -80f, 160f);
        offsetForward = Slider("前后", offsetForward, -120f, 120f);
        GUILayout.Space(6f);
        GUILayout.Label("文字大小（相对屏幕）");
        textScale = ScaleField(textScale);
        GUILayout.Space(6f);
        GUILayout.Label("轮播显示秒数");
        cycleSeconds = CycleField(cycleSeconds);
        GUILayout.Space(6f);
        GUILayout.Label("文字颜色");
        textColor.r = Slider("R", textColor.r, 0f, 1f);
        textColor.g = Slider("G", textColor.g, 0f, 1f);
        textColor.b = Slider("B", textColor.b, 0f, 1f);
        GUILayout.EndScrollView();
        if (GUILayout.Button("保存", GUILayout.Height(24f)))
            Save();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }

    private void WheelScroll()
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.ScrollWheel)
            return;
        scroll.y += e.delta.y * 16f;
        e.Use();
    }

    private float ScaleField(float value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("比例", GUILayout.Width(36f));
        float slid = GUILayout.HorizontalSlider(Mathf.Clamp(value, 0.02f, 6f), 0.02f, 6f);
        GUI.SetNextControlName("f3_label_scale");
        bool focused = GUI.GetNameOfFocusedControl() == "f3_label_scale";
        if (!focused)
        {
            if (Mathf.Abs(slid - value) > 0.00001f)
                value = slid;
            scaleText = value.ToString("0.###", CultureInfo.InvariantCulture);
        }
        string edited = GUILayout.TextField(scaleText ?? value.ToString("0.###", CultureInfo.InvariantCulture), GUILayout.Width(64f));
        scaleText = edited;
        if (focused && float.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            value = Mathf.Max(0.02f, parsed);
        GUILayout.EndHorizontal();
        return value;
    }

    private float CycleField(float value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("秒数", GUILayout.Width(36f));
        if (cycleText == null)
            cycleText = value.ToString("0.0");
        string next = GUILayout.TextField(cycleText, GUILayout.Width(48f));
        if (next != cycleText)
        {
            cycleText = next;
            if (float.TryParse(next, out float parsed))
                value = Mathf.Clamp(parsed, 1f, 30f);
        }
        value = GUILayout.HorizontalSlider(value, 1f, 30f);
        if (!Mathf.Approximately(value, cycleSeconds))
            cycleText = value.ToString("0.0");
        GUILayout.Label(value.ToString("0.0"), GUILayout.Width(36f));
        GUILayout.EndHorizontal();
        return value;
    }

    private static float Slider(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(36f));
        value = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.Label(value.ToString("0.0"), GUILayout.Width(36f));
        GUILayout.EndHorizontal();
        return value;
    }

    private void Save()
    {
        var store = PanelMemory.Load();
        if (store.diffLabel == null)
            store.diffLabel = new PanelMemory.DiffLabelTune();
        store.diffLabel.saved = true;
        store.diffLabel.offsetRight = offsetRight;
        store.diffLabel.offsetUp = offsetUp;
        store.diffLabel.offsetForward = offsetForward;
        store.diffLabel.cycleSeconds = cycleSeconds;
        store.diffLabel.r = textColor.r;
        store.diffLabel.g = textColor.g;
        store.diffLabel.b = textColor.b;
        store.diffLabel.textScale = textScale;
        PanelMemory.Save(store);
        PlayerPrefs.SetFloat("difflabel.right", offsetRight);
        PlayerPrefs.SetFloat("difflabel.up", offsetUp);
        PlayerPrefs.SetFloat("difflabel.forward", offsetForward);
        PlayerPrefs.SetFloat("difflabel.cycle", cycleSeconds);
        PlayerPrefs.SetFloat("difflabel.r", textColor.r);
        PlayerPrefs.SetFloat("difflabel.g", textColor.g);
        PlayerPrefs.SetFloat("difflabel.b", textColor.b);
        PlayerPrefs.Save();
    }

    private void Load()
    {
        var store = PanelMemory.Load();
        if (store.diffLabel != null && store.diffLabel.saved)
        {
            ApplyTune(store.diffLabel);
            return;
        }
        if (PlayerPrefs.HasKey("difflabel.right") || PlayerPrefs.HasKey("difflabel.up"))
        {
            offsetRight = PlayerPrefs.GetFloat("difflabel.right", offsetRight);
            offsetUp = PlayerPrefs.GetFloat("difflabel.up", offsetUp);
            offsetForward = PlayerPrefs.GetFloat("difflabel.forward", offsetForward);
            cycleSeconds = PlayerPrefs.GetFloat("difflabel.cycle", cycleSeconds);
            textColor.r = PlayerPrefs.GetFloat("difflabel.r", textColor.r);
            textColor.g = PlayerPrefs.GetFloat("difflabel.g", textColor.g);
            textColor.b = PlayerPrefs.GetFloat("difflabel.b", textColor.b);
            textColor.a = 1f;
            Save();
        }
        cycleText = cycleSeconds.ToString("0.0");
    }

    private void ApplyTune(PanelMemory.DiffLabelTune tune)
    {
        offsetRight = tune.offsetRight;
        offsetUp = tune.offsetUp;
        offsetForward = tune.offsetForward;
        cycleSeconds = Mathf.Max(1f, tune.cycleSeconds);
        textColor = new Color(tune.r, tune.g, tune.b, 1f);
        textScale = tune.textScale > 0.01f ? tune.textScale : 1f;
        cycleText = cycleSeconds.ToString("0.0");
    }
}
