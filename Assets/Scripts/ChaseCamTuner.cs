using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// F5：调整绑在飞机上的跟随相机位置和俯仰/偏航。
/// 距离单位是米，与雷达同一比例。
/// </summary>
public class ChaseCamTuner : MonoBehaviour
{
    public static ChaseCamTuner Instance { get; private set; }

    public float back = 80f;
    public float height = 22f;
    public float side;
    public float lookPitch = 8f;
    public float lookYaw;
    public float fov = 55f;

    private bool open;
    private Rect window = new Rect(400f, 80f, 340f, 320f);
    private Vector2 scroll;
    private readonly Dictionary<string, string> numberDrafts = new Dictionary<string, string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (Instance != null)
            return;
        var go = new GameObject("ChaseCamTuner");
        DontDestroyOnLoad(go);
        go.AddComponent<ChaseCamTuner>();
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

    private void OnGUI()
    {
        if (!open)
            return;
        window = GUI.Window(9145, window, DrawWindow, "跟随相机  F5隐藏");
    }

    private void DrawWindow(int id)
    {
        Event e = Event.current;
        if (e != null && e.type == EventType.ScrollWheel)
        {
            scroll.y += e.delta.y * 16f;
            e.Use();
        }
        scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.ExpandHeight(true));
        GUILayout.Label("相对飞机，单位米。锁定视角(B)后即时生效。");
        back = NumberRow("back", "后方距离", back, 5f, 400f);
        height = NumberRow("height", "高度", height, -40f, 200f);
        side = NumberRow("side", "右舷", side, -200f, 200f);
        lookPitch = NumberRow("pitch", "俯仰(度)", lookPitch, -45f, 45f);
        lookYaw = NumberRow("yaw", "偏航(度)", lookYaw, -90f, 90f);
        fov = NumberRow("fov", "视场角", fov, 20f, 90f);
        GUILayout.EndScrollView();
        if (GUILayout.Button("保存", GUILayout.Height(24f)))
            Save();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }

    private float NumberRow(string key, string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(72f));
        float slid = GUILayout.HorizontalSlider(Mathf.Clamp(value, min, max), min, max);
        string control = "f5num_" + key;
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

    private void Save()
    {
        var store = PanelMemory.Load();
        if (store.chaseCam == null)
            store.chaseCam = new PanelMemory.ChaseCamTune();
        store.chaseCam.saved = true;
        store.chaseCam.back = back;
        store.chaseCam.height = height;
        store.chaseCam.side = side;
        store.chaseCam.lookPitch = lookPitch;
        store.chaseCam.lookYaw = lookYaw;
        store.chaseCam.fov = fov;
        PanelMemory.Save(store);
    }

    private void Load()
    {
        var store = PanelMemory.Load();
        if (store.chaseCam == null || !store.chaseCam.saved)
            return;
        back = store.chaseCam.back;
        height = store.chaseCam.height;
        side = store.chaseCam.side;
        lookPitch = store.chaseCam.lookPitch;
        lookYaw = store.chaseCam.lookYaw;
        fov = store.chaseCam.fov > 1f ? store.chaseCam.fov : 55f;
    }
}
