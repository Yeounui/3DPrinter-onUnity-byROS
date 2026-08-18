using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Unity 초기화 흐름 최소 manager 골격.
/// 현재 버전: 한 부품·한 로봇축·한 방향 반복 탐색 시험.
/// </summary>
public class InitializationManager : MonoBehaviour
{
    public enum RobotAxis
    {
        X,
        Y,
        Z
    }

    public enum SearchDirection
    {
        Positive,
        Negative
    }

    public enum InitializationState
    {
        Idle,
        Preparing,
        EvaluatingCandidate,
        Moving,
        Holding,
        Returning,
        Ready,
        LimitReached,
        Blocked,
        Error
    }

    [Header("State")]
    public InitializationState state = InitializationState.Idle;

    [Header("Motion Target")]
    public GeometrySliderConstraint sliderConstraint;
    public Transform movingBody;

    [Tooltip("모든 부품이 공유하는 로봇 좌표 기준. 현재 base_link/ReferenceFrame 사용")]
    public Transform robotCoordinateFrame;

    public RobotAxis robotAxis = RobotAxis.Z;

    [Min(0.001f)]
    public float stepDistance = 1f;

    [Tooltip("충돌에서 먼 구간용 초기 스텝. 충돌 후보 발견 시 절반씩 축소")]
    [Min(0.001f)]
    public float coarseStepDistance = 4f;

    public bool useAdaptiveStep = true;

    public SearchDirection searchDirection = SearchDirection.Positive;

    [Min(1)]
    public int maxSteps = 20;

    [Min(0f)]
    public float holdDuration = 1f;

    public bool runOnStart = true;
    public bool returnToStart = true;

    [Header("Search Result")]
    [Min(0)]
    public int completedSafeSteps;

    public int blockedAtStep = -1;

    public bool reachedMaxSteps;
    public Vector3 lastSafeWorldPosition;
    public Vector3 blockedCandidateWorldPosition;
    public Vector3 lastSafeRobotPosition;
    public Vector3 blockedCandidateRobotPosition;

    [Header("Collision Roots")]
    [Tooltip("이동 링크 Collision. 하위 Collider 자동 수집")]
    public Transform movingCollisionRoot;

    [Tooltip("충돌 검사 대상 링크 Collision. 하위 Collider 자동 수집")]
    public Transform obstacleCollisionRoot;

    [Tooltip("Trigger Collider 충돌 검사 포함")]
    public bool includeTriggers;

    [Tooltip("활성화 시 현재 이동 루트 제외 씬 전체 Collision 루트를 장애물로 수집")]
    public bool collectAllSceneCollisionRoots = true;

    [Header("Initial Collision Baseline")]
    [Tooltip("초기 상태 기존 겹침 충돌쌍의 허용 증가 여유")]
    [Min(0f)]
    public float baselinePenetrationTolerance = 0.01f;

    private readonly List<Collider> movingColliders = new List<Collider>();
    private readonly List<Collider> obstacleColliders = new List<Collider>();
    private readonly Dictionary<string, float> baselinePenetrations =
        new Dictionary<string, float>();
    private Coroutine initializationRoutine;

    private void Awake()
    {
        if (movingCollisionRoot == null && movingBody != null)
            movingCollisionRoot = FindCollisionRoot(movingBody);

        if (robotCoordinateFrame == null)
        {
            GameObject referenceObject = GameObject.Find("base_link/ReferenceFrame");
            if (referenceObject != null)
                robotCoordinateFrame = referenceObject.transform;
        }
    }

    private void Start()
    {
        if (runOnStart)
            BeginInitialization();
    }

    public void BeginInitialization()
    {
        if (initializationRoutine != null)
        {
            Debug.LogWarning("[InitializationManager] 초기화 이미 실행 중", this);
            return;
        }

        if (!ValidateConfiguration())
        {
            SetState(InitializationState.Error, "설정 검증 실패");
            return;
        }

        CollectColliders();
        initializationRoutine = StartCoroutine(RunSingleStep());
    }

    public void StopInitialization()
    {
        if (initializationRoutine == null)
            return;

        StopCoroutine(initializationRoutine);
        initializationRoutine = null;
        SetState(InitializationState.Idle, "초기화 중지");
    }

    /// <summary>
    /// 현재 위치에서 지정 로봇축 좌표까지 한 스텝씩 이동.
    /// 축 범위 반대편에서 중앙 복귀 시 사용. 순간이동 안 함.
    /// </summary>
    public IEnumerator MoveToRobotAxisCoordinateStepped(
        RobotAxis axis,
        float targetCoordinate,
        string label)
    {
        if (movingBody == null || robotCoordinateFrame == null)
        {
            SetState(InitializationState.Error, $"{label} 이동 설정 없음");
            yield break;
        }

        Vector3 currentRobotPosition = robotCoordinateFrame.InverseTransformPoint(movingBody.position);
        float remaining = targetCoordinate - GetRobotAxisCoordinate(axis, currentRobotPosition);
        Vector3 axisVector = GetRobotAxisVector(axis).normalized;
        int stepIndex = 0;

        while (Mathf.Abs(remaining) > 0.0001f)
        {
            float distance = Mathf.Min(Mathf.Abs(remaining), stepDistance);
            Vector3 candidateDelta = axisVector * Mathf.Sign(remaining) * distance;

            stepIndex++;
            SetState(
                InitializationState.EvaluatingCandidate,
                $"{label} 후보 위치 검사 | Step={distance:F3}, Index={stepIndex}");

            if (TryPredictCollision(candidateDelta, out string collisionLog))
            {
                SetState(
                    InitializationState.Blocked,
                    $"{label} 복귀 중 충돌로 중지 {collisionLog}");
                yield break;
            }

            movingBody.position += candidateDelta;
            Physics.SyncTransforms();
            remaining -= Mathf.Sign(remaining) * distance;

            SetState(
                InitializationState.Moving,
                $"{label} 한 스텝 이동 | Remaining={Mathf.Abs(remaining):F3}");

            if (holdDuration > 0f)
            {
                SetState(InitializationState.Holding, $"{label} 위치 유지 | {holdDuration:F2}초");
                yield return new WaitForSeconds(holdDuration);
            }
        }

        SetState(InitializationState.Ready, $"{label} 중앙 복귀 완료");
    }

    private float GetRobotAxisCoordinate(RobotAxis axis, Vector3 robotPosition)
    {
        switch (axis)
        {
            case RobotAxis.X:
                // Robot X = 공통 기준 forward = 로컬 z
                return robotPosition.z;
            case RobotAxis.Y:
                // Robot Y = 공통 기준 up = 로컬 y
                return robotPosition.y;
            case RobotAxis.Z:
                // Robot Z = 공통 기준 -right = 로컬 x 음수
                return -robotPosition.x;
            default:
                return 0f;
        }
    }

    private IEnumerator RunSingleStep()
    {
        SetState(InitializationState.Preparing, "단일 축 초기화 준비");

        // GeometrySliderConstraint 첫 Update 완료 후 현재 위치를 기준점으로 사용
        yield return null;
        Physics.SyncTransforms();
        CaptureBaselineCollisions();

        Vector3 startPosition = movingBody.position;
        Vector3 robotAxisVector = GetRobotAxisVector(robotAxis).normalized;
        Vector3 constraintAxis = sliderConstraint.fixedReferenceFrame.right.normalized;

        float axisAlignment = Mathf.Abs(Vector3.Dot(robotAxisVector, constraintAxis));
        if (axisAlignment < 0.99f)
        {
            SetState(
                InitializationState.Error,
                $"로봇축과 constraint 자유축 비평행. " +
                $"RobotAxis={robotAxisVector}, ConstraintAxis={constraintAxis}, " +
                $"Alignment={axisAlignment:F4}");
            initializationRoutine = null;
            yield break;
        }

        Vector3 axis = robotAxisVector;
        if (searchDirection == SearchDirection.Negative)
            axis = -axis;

        completedSafeSteps = 0;
        blockedAtStep = -1;
        reachedMaxSteps = false;
        lastSafeWorldPosition = startPosition;
        lastSafeRobotPosition = robotCoordinateFrame.InverseTransformPoint(startPosition);
        blockedCandidateWorldPosition = startPosition;
        blockedCandidateRobotPosition = lastSafeRobotPosition;

        float activeStepDistance = useAdaptiveStep
            ? Mathf.Max(stepDistance, coarseStepDistance)
            : stepDistance;

        for (int stepIndex = 1; stepIndex <= maxSteps; stepIndex++)
        {
            Vector3 candidateDelta = axis * activeStepDistance;

            SetState(
                InitializationState.EvaluatingCandidate,
                $"후보 위치 검사 | RobotAxis={robotAxis}{searchDirection}, " +
                $"WorldAxis={axis}, " +
                $"Step={activeStepDistance:F3}, Index={stepIndex}/{maxSteps}");

            if (TryPredictCollision(candidateDelta, out string collisionLog))
            {
                if (useAdaptiveStep && activeStepDistance > stepDistance + 0.0001f)
                {
                    activeStepDistance = Mathf.Max(stepDistance, activeStepDistance * 0.5f);
                    SetState(
                        InitializationState.EvaluatingCandidate,
                        $"충돌 후보 근접 정밀화 | 다음 Step={activeStepDistance:F3} | {collisionLog}");
                    continue;
                }

                blockedAtStep = stepIndex;
                blockedCandidateWorldPosition = movingBody.position + candidateDelta;
                blockedCandidateRobotPosition =
                    robotCoordinateFrame.InverseTransformPoint(blockedCandidateWorldPosition);
                SetState(
                    InitializationState.Blocked,
                    $"{stepIndex}번째 후보에서 탐색 중지 " +
                    $"LastSafeRobot={lastSafeRobotPosition}, " +
                    $"BlockedCandidateRobot={blockedCandidateRobotPosition}, " +
                    collisionLog);
                break;
            }

            SetState(
                InitializationState.Moving,
                $"안전 후보 확정 SafeSteps={completedSafeSteps + 1}");
            movingBody.position += candidateDelta;
            Physics.SyncTransforms();
            completedSafeSteps++;
            lastSafeWorldPosition = movingBody.position;
            lastSafeRobotPosition = robotCoordinateFrame.InverseTransformPoint(movingBody.position);

            if (holdDuration > 0f)
            {
                SetState(InitializationState.Holding, $"후보 위치 유지 | {holdDuration:F2}초");
                yield return new WaitForSeconds(holdDuration);
            }
        }

        if (blockedAtStep < 0)
        {
            reachedMaxSteps = true;
            blockedCandidateWorldPosition = movingBody.position + axis * stepDistance;
            blockedCandidateRobotPosition =
                robotCoordinateFrame.InverseTransformPoint(blockedCandidateWorldPosition);
            SetState(
                InitializationState.LimitReached,
                $"최대 스텝 도달 LastSafeRobot={lastSafeRobotPosition}, " +
                $"NextCandidateRobot={blockedCandidateRobotPosition}, " +
                $"SafeSteps={completedSafeSteps}");
        }

        if (returnToStart && movingBody.position != startPosition)
        {
            Vector3 startRobotPosition = robotCoordinateFrame.InverseTransformPoint(startPosition);
            float startAxisCoordinate = GetRobotAxisCoordinate(robotAxis, startRobotPosition);
            yield return MoveToRobotAxisCoordinateStepped(
                robotAxis,
                startAxisCoordinate,
                $"Robot {robotAxis} 탐색 시작 위치");

            if (state != InitializationState.Blocked && state != InitializationState.Error)
            {
                SetState(
                    InitializationState.Ready,
                    $"Robot {robotAxis} 반복 탐색 + 스텝 복귀 완료 " +
                    $"SafeSteps={completedSafeSteps}");
            }
        }

        initializationRoutine = null;
    }

    private bool TryPredictCollision(Vector3 translationDelta, out string collisionLog)
    {
        collisionLog = string.Empty;

        foreach (Collider movingCollider in movingColliders)
        {
            if (!IsUsableCollider(movingCollider))
                continue;

            Vector3 predictedPosition = movingCollider.transform.position + translationDelta;
            Quaternion predictedRotation = movingCollider.transform.rotation;

            foreach (Collider obstacleCollider in obstacleColliders)
            {
                if (!IsUsableCollider(obstacleCollider))
                    continue;

                if (GetCollisionRoot(movingCollider) == GetCollisionRoot(obstacleCollider))
                    continue;

                if (Physics.ComputePenetration(
                    movingCollider,
                    predictedPosition,
                    predictedRotation,
                    obstacleCollider,
                    obstacleCollider.transform.position,
                    obstacleCollider.transform.rotation,
                    out Vector3 direction,
                    out float distance))
                {
                    string pairKey = GetPairKey(movingCollider, obstacleCollider);

                    if (baselinePenetrations.TryGetValue(pairKey, out float baselineDistance) &&
                        distance <= baselineDistance + baselinePenetrationTolerance)
                    {
                        continue;
                    }

                    collisionLog =
                        $"Moving={GetColliderLabel(movingCollider)}, " +
                        $"Obstacle={GetColliderLabel(obstacleCollider)}, " +
                        $"Penetration={distance:F4}, Baseline=" +
                        (baselinePenetrations.ContainsKey(pairKey)
                            ? baselinePenetrations[pairKey].ToString("F4")
                            : "none") +
                        $", Direction={direction}";
                    return true;
                }
            }
        }

        return false;
    }

    private void CaptureBaselineCollisions()
    {
        baselinePenetrations.Clear();

        int overlapCount = 0;

        foreach (Collider movingCollider in movingColliders)
        {
            if (!IsUsableCollider(movingCollider))
                continue;

            foreach (Collider obstacleCollider in obstacleColliders)
            {
                if (!IsUsableCollider(obstacleCollider))
                    continue;

                if (GetCollisionRoot(movingCollider) == GetCollisionRoot(obstacleCollider))
                    continue;

                if (Physics.ComputePenetration(
                    movingCollider,
                    movingCollider.transform.position,
                    movingCollider.transform.rotation,
                    obstacleCollider,
                    obstacleCollider.transform.position,
                    obstacleCollider.transform.rotation,
                    out _,
                    out float distance))
                {
                    baselinePenetrations[GetPairKey(movingCollider, obstacleCollider)] = distance;
                    overlapCount++;
                }
            }
        }

        Debug.Log(
            $"[InitializationManager] 초기 충돌 baseline 저장 | OverlapPairs={overlapCount}",
            this);
    }

    private void CollectColliders()
    {
        movingColliders.Clear();
        obstacleColliders.Clear();

        movingColliders.AddRange(movingCollisionRoot.GetComponentsInChildren<Collider>(true));

        if (collectAllSceneCollisionRoots)
        {
            Transform[] sceneTransforms = FindObjectsOfType<Transform>(true);
            foreach (Transform sceneTransform in sceneTransforms)
            {
                if (sceneTransform.name != "Collision" || sceneTransform == movingCollisionRoot)
                    continue;

                obstacleColliders.AddRange(sceneTransform.GetComponentsInChildren<Collider>(true));
            }
        }
        else
        {
            obstacleColliders.AddRange(obstacleCollisionRoot.GetComponentsInChildren<Collider>(true));
        }

        Debug.Log(
            $"[InitializationManager] Collider 수집 | " +
            $"Moving={movingCollisionRoot.name}:{movingColliders.Count}, " +
            $"ObstacleColliders={obstacleColliders.Count}, " +
            $"Scope={(collectAllSceneCollisionRoots ? "AllSceneCollisionRoots" : obstacleCollisionRoot.name)}",
            this);
    }

    private bool ValidateConfiguration()
    {
        if (sliderConstraint == null || !sliderConstraint.enabled)
            return FailConfiguration("GeometrySliderConstraint 없음 또는 비활성 상태");

        if (sliderConstraint.fixedReferenceFrame == null)
            return FailConfiguration("Fixed Reference Frame 없음");

        if (robotCoordinateFrame == null)
            return FailConfiguration("공통 로봇 좌표 기준 없음");

        if (movingBody == null)
            return FailConfiguration("Moving Body 없음");

        if (movingCollisionRoot == null)
            return FailConfiguration("Moving Collision Root 없음");

        if (obstacleCollisionRoot == null)
            return FailConfiguration("Obstacle Collision Root 없음");

        if (movingCollisionRoot == obstacleCollisionRoot)
            return FailConfiguration("Moving과 Obstacle Collision Root 동일 불가");

        return true;
    }

    private Vector3 GetRobotAxisVector(RobotAxis axis)
    {
        // 현재 모델 base_link/ReferenceFrame 기준 매핑:
        // Robot X = frame.forward, Robot Y = frame.up, Robot Z = -frame.right.
        switch (axis)
        {
            case RobotAxis.X:
                return robotCoordinateFrame.forward;
            case RobotAxis.Y:
                return robotCoordinateFrame.up;
            case RobotAxis.Z:
                return -robotCoordinateFrame.right;
            default:
                return robotCoordinateFrame.forward;
        }
    }

    private bool FailConfiguration(string message)
    {
        Debug.LogError("[InitializationManager] " + message, this);
        return false;
    }

    private bool IsUsableCollider(Collider collider)
    {
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
            return false;

        return includeTriggers || !collider.isTrigger;
    }

    private Transform FindCollisionRoot(Transform start)
    {
        foreach (Transform child in start.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Collision")
                return child;
        }

        return null;
    }

    private Transform GetCollisionRoot(Collider collider)
    {
        Transform current = collider != null ? collider.transform : null;

        while (current != null)
        {
            if (current.name == "Collision")
                return current;

            current = current.parent;
        }

        return null;
    }

    private string GetColliderLabel(Collider collider)
    {
        return collider == null
            ? "<null>"
            : collider.transform.name + "/" + collider.GetType().Name;
    }

    private string GetPairKey(Collider movingCollider, Collider obstacleCollider)
    {
        return movingCollider.GetInstanceID() + ":" + obstacleCollider.GetInstanceID();
    }

    private void SetState(InitializationState nextState, string message)
    {
        state = nextState;
        Debug.Log($"[InitializationManager] State={state} | {message}", this);
    }
}
