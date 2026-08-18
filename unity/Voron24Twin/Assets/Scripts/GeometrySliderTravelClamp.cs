using UnityEngine;

/// <summary>
/// GeometryTravelLimit의 Rail/Block 투영 한계로
/// 슬라이더 이동체를 자유축 방향으로만 보정.
/// GeometrySliderConstraint 회전/직교축 정렬 이후 LateUpdate에서 실행.
/// </summary>
public class GeometrySliderTravelClamp : MonoBehaviour
{
    [Header("References")]
    public GeometrySliderConstraint sliderConstraint;
    public GeometryTravelLimit travelLimit;

    [Tooltip("비워 두면 GeometrySliderConstraint 부착 GameObject 이동")]
    public Transform movingBody;

    [Header("Behavior")]
    [Tooltip("Play 중 매 프레임 이동체를 Rail 범위 안으로 보정")]
    public bool clampInPlayMode = true;

    [Header("Diagnostics")]
    [Tooltip("Console에 투영값/보정 상태 주기 출력")]
    public bool debugLogging = true;

    [Min(0.1f)]
    public float debugLogInterval = 0.5f;

    public GeometrySliderTravelLimitReport rangeReport;

    private float nextDebugLogTime;

    private void Reset()
    {
        sliderConstraint = GetComponent<GeometrySliderConstraint>();
        movingBody = sliderConstraint != null ? sliderConstraint.transform : transform;
    }

    private void Awake()
    {
        if (sliderConstraint == null)
            sliderConstraint = GetComponent<GeometrySliderConstraint>();

        if (movingBody == null && sliderConstraint != null)
            movingBody = sliderConstraint.transform;
    }

    private void LateUpdate()
    {
        if (!clampInPlayMode || !Application.isPlaying)
            return;

        ApplyClamp();
    }

    public bool ApplyClamp()
    {
        if (sliderConstraint == null)
        {
            LogStatus("Slider Constraint 참조 없음");
            return false;
        }

        if (travelLimit == null)
        {
            LogStatus("Travel Limit 참조 없음");
            return false;
        }

        if (movingBody == null)
        {
            LogStatus("Moving Body 참조 없음");
            return false;
        }

        if (!travelLimit.TryGetProjectedLimits(
                out float railMin,
                out float railMax,
                out float blockMin,
                out float blockMax))
        {
            LogStatus(
                "실패: " +
                travelLimit.GetValidationStatus()
            );
            return false;
        }

        Transform axisFrame =
            travelLimit.fixedReferenceFrame != null
                ? travelLimit.fixedReferenceFrame
                : sliderConstraint.fixedReferenceFrame;

        if (axisFrame == null)
        {
            LogStatus("Fixed Reference Frame 없음");
            return false;
        }

        Vector3 axis = axisFrame.right.normalized;

        if (rangeReport != null)
            rangeReport.RegisterPart(name, railMin, railMax, blockMin, blockMax, axis);

        float correction = 0.0f;

        if (blockMin < railMin)
            correction = railMin - blockMin;
        else if (blockMax > railMax)
            correction = railMax - blockMax;

        if (Mathf.Abs(correction) <= 0.000001f)
        {
            LogValues(railMin, railMax, blockMin, blockMax, 0.0f, "범위 안");
            return true;
        }

        // Rail/Block 기준점은 movingBody와 함께 이동 가정
        // 자유축(axis) 성분만 보정하므로 회전/직교축 정렬 유지
        movingBody.position += axis * correction;
        LogValues(railMin, railMax, blockMin, blockMax, correction, "보정 적용");
        return true;
    }

    private void LogStatus(string message)
    {
        if (!debugLogging || !Application.isPlaying || Time.time < nextDebugLogTime)
            return;

        nextDebugLogTime = Time.time + Mathf.Max(0.1f, debugLogInterval);
        Debug.Log($"[GeometrySliderTravelClamp] {name}: {message}", this);
    }

    private void LogValues(
        float railMin,
        float railMax,
        float blockMin,
        float blockMax,
        float correction,
        string state)
    {
        if (!debugLogging || !Application.isPlaying || Time.time < nextDebugLogTime)
            return;

        nextDebugLogTime = Time.time + Mathf.Max(0.1f, debugLogInterval);
        Debug.Log(
            $"[GeometrySliderTravelClamp] {name}: {state} | " +
            $"Rail={railMin:F3}~{railMax:F3}, " +
            $"Block={blockMin:F3}~{blockMax:F3}, " +
            $"Correction={correction:F3}",
            this);
    }

}
