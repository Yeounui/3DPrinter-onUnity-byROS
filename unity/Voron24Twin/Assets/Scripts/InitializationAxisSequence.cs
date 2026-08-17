using System.Collections;
using UnityEngine;

/// <summary>
/// 공통 로봇 좌표계 기준으로 X/Y 양방향 탐색을 순서대로 실행한다.
/// 각 방향의 실제 충돌 예측과 이동은 InitializationManager가 담당한다.
/// </summary>
public class InitializationAxisSequence : MonoBehaviour
{
    [Header("References")]
    public InitializationManager manager;
    public NegativeZBedCalibration negativeZCalibration;
    public Transform obstacleCollisionRoot;

    [Header("Sequence")]
    public bool runOnStart;
    public bool includePositiveZ = true;
    [Tooltip("기존 양방향 Z 탐색 호환 옵션입니다. 일반 초기화에서는 사용하지 않습니다.")]
    public bool includeZ;
    public bool includeNegativeZ = true;
    public int maxSteps = 1000;
    public float holdDuration = 0.2f;

    private Coroutine sequenceRoutine;

    private void Awake()
    {
        if (manager == null)
            manager = FindObjectOfType<InitializationManager>();

        if (negativeZCalibration == null)
            negativeZCalibration = FindObjectOfType<NegativeZBedCalibration>();

        if (obstacleCollisionRoot == null)
        {
            GameObject baseCollision = GameObject.Find("base_link/Collision");
            if (baseCollision != null)
                obstacleCollisionRoot = baseCollision.transform;
        }
    }

    private void Start()
    {
        if (runOnStart)
            BeginSequence();
    }

    public void BeginSequence()
    {
        if (sequenceRoutine != null)
        {
            Debug.LogWarning("[InitializationAxisSequence] 순서 실행이 이미 진행 중입니다.", this);
            return;
        }

        if (manager == null || obstacleCollisionRoot == null)
        {
            Debug.LogError("[InitializationAxisSequence] manager 또는 장애물 Collision Root가 없습니다.", this);
            return;
        }

        sequenceRoutine = StartCoroutine(RunSequence());
    }

    public void StopSequence()
    {
        if (sequenceRoutine == null)
            return;

        manager.StopInitialization();
        StopCoroutine(sequenceRoutine);
        sequenceRoutine = null;
        Debug.Log("[InitializationAxisSequence] 순서 실행을 중지했습니다.", this);
    }

    private IEnumerator RunSequence()
    {
        manager.runOnStart = false;

        if (includePositiveZ)
        {
            // 상한까지 탐색한 뒤, -Z 베드 탐색이 초기 Z 위치에서 시작하도록
            // 원래 위치까지 한 스텝씩 복귀한다.
            yield return RunAxis(
                InitializationManager.RobotAxis.Z,
                InitializationManager.SearchDirection.Positive,
                true);
        }

        if (includeZ)
        {
            yield return RunAxis(
                InitializationManager.RobotAxis.Z,
                InitializationManager.SearchDirection.Negative,
                true);
        }

        yield return RunAxis(InitializationManager.RobotAxis.X, InitializationManager.SearchDirection.Positive, false);
        Vector3 xPositiveBoundary = manager.lastSafeRobotPosition;
        yield return RunAxis(InitializationManager.RobotAxis.X, InitializationManager.SearchDirection.Negative, false);
        Vector3 xNegativeBoundary = manager.lastSafeRobotPosition;
        // Robot X = ReferenceFrame.forward = InverseTransformPoint 결과의 z
        float xMidpoint = (xPositiveBoundary.z + xNegativeBoundary.z) * 0.5f;
        Debug.Log(
            $"[InitializationAxisSequence] X 안전범위 | Positive={xPositiveBoundary.z:F3}, " +
            $"Negative={xNegativeBoundary.z:F3}, Midpoint={xMidpoint:F3}", this);
        yield return ReturnToAxisMidpoint(
            InitializationManager.RobotAxis.X,
            xMidpoint,
            "Robot X 범위 중앙");

        yield return RunAxis(InitializationManager.RobotAxis.Y, InitializationManager.SearchDirection.Positive, false);
        Vector3 yPositiveBoundary = manager.lastSafeRobotPosition;
        yield return RunAxis(InitializationManager.RobotAxis.Y, InitializationManager.SearchDirection.Negative, false);
        Vector3 yNegativeBoundary = manager.lastSafeRobotPosition;
        float yMidpoint = (yPositiveBoundary.y + yNegativeBoundary.y) * 0.5f;
        Debug.Log(
            $"[InitializationAxisSequence] Y 안전범위 | Positive={yPositiveBoundary.y:F3}, " +
            $"Negative={yNegativeBoundary.y:F3}, Midpoint={yMidpoint:F3}", this);
        yield return ReturnToAxisMidpoint(
            InitializationManager.RobotAxis.Y,
            yMidpoint,
            "Robot Y 범위 중앙");

        if (includeNegativeZ)
            yield return RunNegativeZCalibration();

        Debug.Log("[InitializationAxisSequence] X/Y 양방향 및 -Z 베드 탐색 순서를 완료했습니다.", this);
        sequenceRoutine = null;
    }

    private IEnumerator RunNegativeZCalibration()
    {
        if (negativeZCalibration == null)
        {
            Debug.LogError("[InitializationAxisSequence] NegativeZBedCalibration을 찾지 못했습니다.", this);
            yield break;
        }

        negativeZCalibration.runOnStart = false;
        negativeZCalibration.holdDuration = holdDuration;
        Debug.Log("[InitializationAxisSequence] 시작 | Robot ZNegative Bed Calibration", this);
        negativeZCalibration.BeginCalibration();

        while (negativeZCalibration.state != NegativeZBedCalibration.CalibrationState.Ready &&
               negativeZCalibration.state != NegativeZBedCalibration.CalibrationState.Blocked &&
               negativeZCalibration.state != NegativeZBedCalibration.CalibrationState.Error)
        {
            yield return null;
        }

        Debug.Log(
            $"[InitializationAxisSequence] 종료 | Robot ZNegative Bed Calibration | " +
            $"State={negativeZCalibration.state}, " +
            $"SafeSteps={negativeZCalibration.safeStepsBeforeContact}",
            this);
    }

    private IEnumerator RunAxis(
        InitializationManager.RobotAxis axis,
        InitializationManager.SearchDirection direction,
        bool returnToStart)
    {
        Transform movingBody = FindMovingBody(axis);
        if (movingBody == null)
        {
            Debug.LogError($"[InitializationAxisSequence] Robot {axis} 이동 부품을 찾지 못했습니다.", this);
            yield break;
        }

        GeometrySliderConstraint constraint = movingBody.GetComponent<GeometrySliderConstraint>();
        Transform movingCollisionRoot = FindCollisionRoot(movingBody);

        if (constraint == null || movingCollisionRoot == null)
        {
            Debug.LogError(
                $"[InitializationAxisSequence] Robot {axis} 설정 누락 | " +
                $"Constraint={(constraint != null)}, Collision={(movingCollisionRoot != null)}",
                this);
            yield break;
        }

        manager.sliderConstraint = constraint;
        manager.movingBody = movingBody;
        manager.movingCollisionRoot = movingCollisionRoot;
        manager.obstacleCollisionRoot = obstacleCollisionRoot;
        manager.robotAxis = axis;
        manager.searchDirection = direction;
        manager.maxSteps = maxSteps;
        manager.holdDuration = holdDuration;
        // +방향 끝에서 -방향 끝까지 실제로 한 스텝씩 이동하기 위해
        // 각 방향 탐색 후 시작점으로 순간 복귀하지 않는다.
        manager.returnToStart = returnToStart;

        Debug.Log(
            $"[InitializationAxisSequence] 시작 | Robot {axis}{direction} | Moving={movingBody.name}",
            this);

        manager.BeginInitialization();

        while (manager.state != InitializationManager.InitializationState.Ready &&
               manager.state != InitializationManager.InitializationState.Blocked &&
               manager.state != InitializationManager.InitializationState.Error)
        {
            yield return null;
        }

        Debug.Log(
            $"[InitializationAxisSequence] 종료 | Robot {axis}{direction} | " +
            $"State={manager.state}, SafeSteps={manager.completedSafeSteps}",
            this);

        // 같은 manager를 재사용하므로 다음 코루틴이 시작될 프레임을 분리한다.
        yield return null;
    }

    private IEnumerator ReturnToAxisMidpoint(
        InitializationManager.RobotAxis axis,
        float targetCoordinate,
        string label)
    {
        Debug.Log($"[InitializationAxisSequence] {label} 복귀 시작 | Target={targetCoordinate:F3}", this);
        yield return manager.MoveToRobotAxisCoordinateStepped(axis, targetCoordinate, label);
        Debug.Log($"[InitializationAxisSequence] {label} 복귀 종료 | State={manager.state}", this);
    }

    private Transform FindMovingBody(InitializationManager.RobotAxis axis)
    {
        string objectName;

        switch (axis)
        {
            case InitializationManager.RobotAxis.X:
                objectName = "toolhead";
                break;
            case InitializationManager.RobotAxis.Y:
                objectName = "x_beam";
                break;
            case InitializationManager.RobotAxis.Z:
                objectName = "z_gantry";
                break;
            default:
                return null;
        }

        GameObject movingObject = GameObject.Find(objectName);
        return movingObject != null ? movingObject.transform : null;
    }

    private Transform FindCollisionRoot(Transform movingBody)
    {
        foreach (Transform child in movingBody.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Collision")
                return child;
        }

        return null;
    }
}
