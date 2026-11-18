using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// 运行时调整雷达前/中/后起点。二维是沿机头的像素，三维是沿机头的模型单位。正值朝机头。
/// </summary>
public class RadarStationTuner : MonoBehaviour
{
    public static RadarStationTuner Instance { get; private set; }

    public float front2D = 26f;
    public float mid2D;
    public float rear2D = -26f;
    public float front3D = 8f;
    public float mid3D;
    public float rear3D = -8f;
    public float frontUp;
    public float midUp;
    public float rearUp;

    private bool open;
    private Rect window = new Rect(12f, 12f, 360f, 360f);
    private Vector2 scroll;
    private readonly Dictionary<string, string> numberDrafts = new Dictionary<string, string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (FindObjectOfType<RadarStationTuner>() != null)
            return;
        var go = new GameObject("RadarStationTuner");
        DontDestroyOnLoad(go);
        go.AddComponent<RadarStationTuner>();
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

    public float Offset2D(int mountSign)
    {
        if (mountSign > 0) return front2D;
        if (mountSign < 0) return rear2D;
        return mid2D;
    }

    public float Offset3D(int mountSign)
    {
        if (mountSign > 0) return front3D;
        if (mountSign < 0) return rear3D;
        return mid3D;
    }

    public float OffsetUp(int mountSign)
    {
        if (mountSign > 0) return frontUp;
        if (mountSign < 0) return rearUp;
        return midUp;
    }

    public void Toggle()
    {
        open = !open;
    }

    public bool ContainsMouse()
    {
        return open && GuiContainsMouse(window);
    }

    public static bool GuiContainsMouse(Rect guiRect)
    {
        Vector2 m = Event.current != null
            ? Event.current.mousePosition
            : new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        return guiRect.Contains(m);
    }

    private void OnGUI()
    {
        if (!open)
            return;

        window = GUI.Window(9142, window, DrawWindow, "雷达位点  F2隐藏");
    }

    private void DrawWindow(int id)
    {
        WheelScroll();
        scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.ExpandHeight(true));
        GUILayout.Label("二维 沿机头像素");
        front2D = Slider("2d-front", "前", front2D, -200f, 200f);
        mid2D = Slider("2d-mid", "中", mid2D, -200f, 200f);
        rear2D = Slider("2d-rear", "后", rear2D, -200f, 200f);
        GUILayout.Space(6f);
        GUILayout.Label("三维 前后(米)  正值朝机头");
        front3D = Slider("3d-front", "前", front3D, -40f, 40f);
        mid3D = Slider("3d-mid", "中", mid3D, -40f, 40f);
        rear3D = Slider("3d-rear", "后", rear3D, -40f, 40f);
        GUILayout.Space(4f);
        GUILayout.Label("三维 上下(米)  正值向上");
        frontUp = Slider("up-front", "前", frontUp, -30f, 30f);
        midUp = Slider("up-mid", "中", midUp, -30f, 30f);
        rearUp = Slider("up-rear", "后", rearUp, -30f, 30f);
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

    private float Slider(string key, string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(22f));
        float slid = GUILayout.HorizontalSlider(Mathf.Clamp(value, min, max), min, max);
        string control = "f2num_" + key;
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
        if (store.radar == null)
            store.radar = new PanelMemory.RadarTune();
        store.radar.saved = true;
        store.radar.front2D = front2D;
        store.radar.mid2D = mid2D;
        store.radar.rear2D = rear2D;
        store.radar.front3D = front3D;
        store.radar.mid3D = mid3D;
        store.radar.rear3D = rear3D;
        store.radar.frontUp = frontUp;
        store.radar.midUp = midUp;
        store.radar.rearUp = rearUp;
        PanelMemory.Save(store);
        PlayerPrefs.SetFloat("radar.front2d", front2D);
        PlayerPrefs.SetFloat("radar.mid2d", mid2D);
        PlayerPrefs.SetFloat("radar.rear2d", rear2D);
        PlayerPrefs.SetFloat("radar.front3d", front3D);
        PlayerPrefs.SetFloat("radar.mid3d", mid3D);
        PlayerPrefs.SetFloat("radar.rear3d", rear3D);
        PlayerPrefs.SetFloat("radar.frontup", frontUp);
        PlayerPrefs.SetFloat("radar.midup", midUp);
        PlayerPrefs.SetFloat("radar.rearup", rearUp);
        PlayerPrefs.Save();
    }

    private void Load()
    {
        var store = PanelMemory.Load();
        if (store.radar != null && store.radar.saved)
        {
            ApplyTune(store.radar);
            return;
        }
        if (PlayerPrefs.HasKey("radar.front2d"))
        {
            front2D = PlayerPrefs.GetFloat("radar.front2d", front2D);
            mid2D = PlayerPrefs.GetFloat("radar.mid2d", mid2D);
            rear2D = PlayerPrefs.GetFloat("radar.rear2d", rear2D);
            front3D = PlayerPrefs.GetFloat("radar.front3d", front3D);
            mid3D = PlayerPrefs.GetFloat("radar.mid3d", mid3D);
            rear3D = PlayerPrefs.GetFloat("radar.rear3d", rear3D);
            frontUp = PlayerPrefs.GetFloat("radar.frontup", frontUp);
            midUp = PlayerPrefs.GetFloat("radar.midup", midUp);
            rearUp = PlayerPrefs.GetFloat("radar.rearup", rearUp);
            Save();
        }
    }

    private void ApplyTune(PanelMemory.RadarTune tune)
    {
        front2D = tune.front2D;
        mid2D = tune.mid2D;
        rear2D = tune.rear2D;
        front3D = tune.front3D;
        mid3D = tune.mid3D;
        rear3D = tune.rear3D;
        frontUp = tune.frontUp;
        midUp = tune.midUp;
        rearUp = tune.rearUp;
    }
}
