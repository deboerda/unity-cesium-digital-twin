using UnityEngine;

/// <summary>
/// 将飞行姿态数据（heading/pitch/roll）应用到3D飞机模型，
/// 只旋转不移动——飞机始终在原位置，仅显示姿态变化。
/// </summary>
public class PlaneAttitudeController : MonoBehaviour
{
    [Header("Data Source")]
    [SerializeField] private FlightDataStreamReceiver receiver;
    [SerializeField] private bool usePrimaryTrack = true;
    [SerializeField] private string trackId = "";
    [SerializeField] private bool useManualInput = false;
    [SerializeField] private float manualHeading = 0f;
    [SerializeField] private float manualPitch = 0f;
    [SerializeField] private float manualRoll = 0f;

    [Header("Rotation Mapping")]
    [Tooltip("航向 0 时在姿态窗口里机头垂直朝画面里。正值再顺时针偏。")]
    [SerializeField] private float headingOffset = 0f;
    [SerializeField] private string attitudeCameraName = "飞机姿态相机";
    private Camera attitudeCamera;
    private const int FlightSceneLayer = 3;
    [Tooltip("俯仰轴翻转（有些模型 pitch 正负相反）")]
    [SerializeField] private bool invertPitch = false;
    [Tooltip("横滚轴翻转")]
    [SerializeField] private bool invertRoll = false;

    [Header("Smoothing")]
    [SerializeField] private float rotationLerpSpeed = 5f;

    private Quaternion targetRotation;
    private Quaternion restRotation = Quaternion.identity;
    private bool hasRest;

    private void Awake()
    {
        restRotation = transform.rotation;
        hasRest = true;
        targetRotation = restRotation;
        ExcludeFlightGroundFromAttitudeCamera();
    }

    private void Update()
    {
        float heading, pitch, roll;

        if (useManualInput)
        {
            heading = manualHeading;
            pitch = manualPitch;
            roll = manualRoll;
        }
        else
        {
            if (receiver == null)
                receiver = FlightDataStreamReceiver.Instance;

            if (receiver == null)
                return;

            FlightDataStreamReceiver.TrackPosition pos = null;
            if (usePrimaryTrack || string.IsNullOrEmpty(trackId))
            {
                pos = receiver.PrimaryPosition;
            }
            else
            {
                var positions = receiver.SnapshotPositions;
                var sel = positions.Find(p => p != null && p.trackId == trackId);
                pos = sel;
            }

            if (pos == null)
                return;

            heading = pos.heading;
            pitch = pos.pitch;
            roll = pos.roll;
        }

        if (invertPitch) pitch = -pitch;
        if (invertRoll) roll = -roll;

        // J35 子物体的零位已在编辑器里调好。只在 feiji 上叠加姿态，不再重算机头轴向。
        // 航向 0、顺时针为正；横滚 0、右翼下为正；俯仰 0、机头上为正。
        targetRotation = AttitudeRotation(heading + headingOffset, pitch, roll);

        if (rotationLerpSpeed > 0f)
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * rotationLerpSpeed);
        else
            transform.rotation = targetRotation;
    }

    public void SetTrackId(string id)
    {
        trackId = id;
        usePrimaryTrack = string.IsNullOrEmpty(id);
    }

    private Quaternion AttitudeRotation(float heading, float pitch, float roll)
    {
        ExcludeFlightGroundFromAttitudeCamera();

        if (!hasRest)
        {
            restRotation = transform.rotation;
            hasRest = true;
        }

        Vector3 intoScreen = attitudeCamera != null ? attitudeCamera.transform.forward : Vector3.forward;
        Vector3 screenUp = attitudeCamera != null ? attitudeCamera.transform.up : Vector3.up;
        Vector3 screenRight = attitudeCamera != null ? attitudeCamera.transform.right : Vector3.right;

        Quaternion yaw = Quaternion.AngleAxis(heading, screenUp);
        Quaternion pitchRot = Quaternion.AngleAxis(-pitch, yaw * screenRight);
        Vector3 nose = pitchRot * yaw * intoScreen;
        Quaternion rollRot = Quaternion.AngleAxis(-roll, nose);
        return rollRot * pitchRot * yaw * restRotation;
    }

    /// <summary>姿态窗口只看机模，不画三维飞行视图的地面/锥体（FlightLayer）。</summary>
    private void ExcludeFlightGroundFromAttitudeCamera()
    {
        if (attitudeCamera == null)
        {
            var camGo = GameObject.Find(attitudeCameraName);
            if (camGo != null)
                attitudeCamera = camGo.GetComponent<Camera>();
        }
        if (attitudeCamera == null)
            return;
        attitudeCamera.cullingMask &= ~(1 << FlightSceneLayer);
    }
}
