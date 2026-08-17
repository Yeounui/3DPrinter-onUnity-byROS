using UnityEngine;

/// <summary>
/// GeometryTravelLimit에서 계산한 Rail/Block 투영 한계를 이용해
/// 슬라이더의 이동체를 자유축 방향으로만 보정한다.
/// GeometrySliderConstraint가 회전과 직교축 정렬을 담당한 뒤 LateUpdate에서 실행한다.
/// </summary>
public class GeometrySliderTravelClamp : MonoBehaviour
{
    [Header("References")]
    public GeometrySliderConstraint sliderConstraint;
    public GeometryTravelLimit travelLimit;

    [Tooltip("비워 두면 GeometrySliderConstraint가 붙은 GameObject를 이동시킨다.")]
    public Transform movingBody;

    [Header("Behavior")]
    [Tooltip("Play 중 매 프레임 이동체를 Rail 범위 안으로 보정한다.")]
    public bool clampInPlayMode = true;

    [Header("Diagnostics")]
    [Tooltip("Console에 투영값과 보정 상태를 주기적으로 출력한다.")]
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
            LogStatus("실패: Slider Constraint 참조가 비어 있습니다.");
            return false;
        }

        if (travelLimit == null)
        {
            LogStatus("실패: Travel Limit 참조가 비어 있습니다.");
            return false;
        }

        if (movingBody == null)
        {
            LogStatus("실패: Moving Body 참조가 비어 있습니다.");
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
            LogStatus("실패: 축으로 사용할 Fixed Reference Frame이 없습니다.");
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

        // Rail/Block 기준점은 movingBody와 함께 이동한다고 가정한다.
        // 자유축(axis) 성분만 보정하므로 슬라이더의 회전과 직교축 정렬은 유지된다.
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
