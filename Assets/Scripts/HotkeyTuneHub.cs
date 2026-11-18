using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 统一收 F2–F5，避免挂在场景物体上的面板被切视图关掉后按键失效。
/// </summary>
public class HotkeyTuneHub : MonoBehaviour
{
    public static HotkeyTuneHub Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (Instance != null)
            return;
        var go = new GameObject("HotkeyTuneHub");
        DontDestroyOnLoad(go);
        go.AddComponent<HotkeyTuneHub>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (IsTyping())
            return;
        if (Input.GetKeyDown(KeyCode.F2))
        {
            EnsureRadarTuner()?.Toggle();
            ReleaseFly();
        }
        if (Input.GetKeyDown(KeyCode.F3))
        {
            EnsureDiffTuner()?.Toggle();
            ReleaseFly();
        }
        if (Input.GetKeyDown(KeyCode.F4))
        {
            var pin = FindAnyObjectByType<ReferencePlanePin>(FindObjectsInactive.Include);
            if (pin != null)
                pin.TogglePanel();
            ReleaseFly();
        }
        if (Input.GetKeyDown(KeyCode.F5))
        {
            EnsureChaseTuner()?.Toggle();
            ReleaseFly();
        }
    }

    private static RadarStationTuner EnsureRadarTuner()
    {
        if (RadarStationTuner.Instance != null)
            return RadarStationTuner.Instance;
        var go = new GameObject("RadarStationTuner");
        DontDestroyOnLoad(go);
        return go.AddComponent<RadarStationTuner>();
    }

    private static DifferenceLabelTuner EnsureDiffTuner()
    {
        if (DifferenceLabelTuner.Instance != null)
            return DifferenceLabelTuner.Instance;
        var go = new GameObject("DifferenceLabelTuner");
        DontDestroyOnLoad(go);
        return go.AddComponent<DifferenceLabelTuner>();
    }

    private static ChaseCamTuner EnsureChaseTuner()
    {
        if (ChaseCamTuner.Instance != null)
            return ChaseCamTuner.Instance;
        var go = new GameObject("ChaseCamTuner");
        DontDestroyOnLoad(go);
        return go.AddComponent<ChaseCamTuner>();
    }

    private static void ReleaseFly()
    {
        var view = FlightSceneView.Active;
        if (view != null)
            view.ReleasePointerCapture();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private static bool IsTyping()
    {
        if (EventSystem.current == null)
            return false;
        var selected = EventSystem.current.currentSelectedGameObject;
        return selected != null && selected.GetComponent<InputField>() != null;
    }
}
