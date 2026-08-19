using System.Collections;
using UnityEngine;

/// <summary>
/// GeometrySliderConstraint 부착 이동 부품을 자유축 방향으로
/// 이동 후 원위치 복귀하는 1차 검증용 실험 스크립트.
/// </summary>
public class AxisMotionExperiment : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("GeometrySliderConstraint 부착 이동 부품")]
    public GeometrySliderConstraint sliderConstraint;

    [Tooltip("비워 두면 Slider Constraint 부착 오브젝트 이동")]
    public Transform movingBody;

    [Header("Experiment")]
    [Min(0.001f)]
    public float distance = 10f;

    [Min(0.01f)]
    public float moveDuration = 1f;

    [Min(0f)]
    public float holdDuration = 0.5f;

    public bool runOnStart = true;

    [Header("Travel Clamp")]
    [Tooltip("실험 중 Travel Clamp off, 실험 종료 후 원래 상태 복구")]
    public bool disableTravelClampDuringExperiment = true;

    public GeometrySliderTravelClamp travelClamp;

    private Vector3 startPosition;
    private bool originalClampEnabled;
    private Coroutine experimentRoutine;

    private void Reset()
    {
        sliderConstraint = GetComponent<GeometrySliderConstraint>();
        movingBody = sliderConstraint != null ? sliderConstraint.transform : transform;
        travelClamp = GetComponent<GeometrySliderTravelClamp>();
    }

    private void Awake()
    {
        if (sliderConstraint == null)
            sliderConstraint = GetComponent<GeometrySliderConstraint>();

        if (movingBody == null && sliderConstraint != null)
            movingBody = sliderConstraint.transform;

        if (travelClamp == null)
            travelClamp = GetComponent<GeometrySliderTravelClamp>();
    }

    private void Start()
    {
        if (runOnStart)
            BeginExperiment();
    }

    public void BeginExperiment()
    {
        if (experimentRoutine != null)
        {
            Debug.LogWarning("[AxisMotionExperiment] 실험 이미 진행 중", this);
            return;
        }

        if (!ValidateReferences())
            return;

        experimentRoutine = StartCoroutine(RunExperiment());
    }

    private IEnumerator RunExperiment()
    {
        startPosition = movingBody.position;
        Vector3 axis = sliderConstraint.fixedReferenceFrame.right.normalized;

        if (disableTravelClampDuringExperiment && travelClamp != null)
        {
            originalClampEnabled = travelClamp.enabled;
            travelClamp.enabled = false;
        }

        Debug.Log(
            $"[AxisMotionExperiment] 시작 | Body={movingBody.name}, " +
            $"Axis={axis}, Start={startPosition}, Distance={distance}",
            this);

        yield return MoveAlongAxis(axis, distance, "+ 방향 이동");
        yield return new WaitForSeconds(holdDuration);
        yield return MoveAlongAxis(axis, -distance, "- 방향 복귀");
        yield return new WaitForSeconds(holdDuration);

        Vector3 finalPosition = movingBody.position;
        float returnError = Vector3.Distance(startPosition, finalPosition);

        if (travelClamp != null && disableTravelClampDuringExperiment)
            travelClamp.enabled = originalClampEnabled;

        Debug.Log(
            $"[AxisMotionExperiment] 완료 | Final={finalPosition}, " +
            $"ReturnError={returnError:F4}, " +
            $"ConstraintEnabled={sliderConstraint.enabled}, " +
            $"TravelClampRestored={travelClamp == null || travelClamp.enabled == originalClampEnabled}",
            this);

        experimentRoutine = null;
    }

    private IEnumerator MoveAlongAxis(Vector3 axis, float signedDistance, string phase)
    {
        Vector3 phaseStart = movingBody.position;
        float elapsed = 0f;
        float direction = Mathf.Sign(signedDistance);
        float amount = Mathf.Abs(signedDistance);

        Debug.Log(
            $"[AxisMotionExperiment] {phase} 시작 | Axis={axis}, Amount={signedDistance:F3}",
            this);

        while (elapsed < moveDuration)
        {
            float deltaTime = Mathf.Min(Time.deltaTime, moveDuration - elapsed);
            movingBody.position += axis * (direction * amount / moveDuration) * deltaTime;
            elapsed += deltaTime;
            yield return null;
        }

        Vector3 phaseEnd = movingBody.position;
        float projectedDistance = Vector3.Dot(phaseEnd - phaseStart, axis);

        Debug.Log(
            $"[AxisMotionExperiment] {phase} 완료 | " +
            $"ProjectedDistance={projectedDistance:F4}, Position={phaseEnd}",
            this);
    }

    private bool ValidateReferences()
    {
        if (sliderConstraint == null)
        {
            Debug.LogError("[AxisMotionExperiment] Slider Constraint 참조 없음", this);
            return false;
        }

        if (sliderConstraint.fixedReferenceFrame == null)
        {
            Debug.LogError("[AxisMotionExperiment] Fixed Reference Frame 참조 없음", this);
            return false;
        }

        if (movingBody == null)
        {
            Debug.LogError("[AxisMotionExperiment] Moving Body 참조 없음", this);
            return false;
        }

        if (!sliderConstraint.enabled)
        {
            Debug.LogError("[AxisMotionExperiment] 실험 중 GeometrySliderConstraint enabled 필수", this);
            return false;
        }

        return true;
    }
}
