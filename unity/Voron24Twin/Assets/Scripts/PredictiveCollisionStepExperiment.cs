using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 후보 위치를 실제로 확정하기 전에 Collider 쌍을 검사하는
/// 단일 축·단일 스텝 충돌 예측 실험 스크립트.
/// GeometrySliderConstraint는 유지하고 GeometrySliderTravelClamp는 사용하지 않는다.
/// </summary>
public class PredictiveCollisionStepExperiment : MonoBehaviour
{
    [Header("Motion")]
    public GeometrySliderConstraint sliderConstraint;
    public Transform movingBody;

    [Min(0.001f)]
    public float stepDistance = 1f;

    [Min(0f)]
    public float holdDuration = 1f;

    public bool runOnStart = true;
    public bool returnToStart = true;

    [Header("Collision Roots")]
    [Tooltip("이동 링크의 Collision GameObject. 하위 Collider를 자동 수집합니다.")]
    public Transform movingCollisionRoot;

    [Tooltip("충돌 검사 대상 링크의 Collision GameObject. 하위 Collider를 자동 수집합니다.")]
    public Transform obstacleCollisionRoot;

    [Tooltip("Trigger Collider를 충돌 검사에 포함합니다.")]
    public bool includeTriggers;

    private Coroutine experimentRoutine;
    private readonly List<Collider> movingColliders = new List<Collider>();
    private readonly List<Collider> obstacleColliders = new List<Collider>();

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

        if (movingCollisionRoot == null && movingBody != null)
            movingCollisionRoot = FindCollisionRoot(movingBody);
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
            Debug.LogWarning("[PredictiveCollisionStepExperiment] 이미 실험이 진행 중입니다.", this);
            return;
        }

        if (!ValidateReferences())
            return;

        CollectColliders();

        experimentRoutine = StartCoroutine(RunExperiment());
    }

    private IEnumerator RunExperiment()
    {
        Vector3 startPosition = movingBody.position;
        Vector3 axis = sliderConstraint.fixedReferenceFrame.right.normalized;
        Vector3 candidatePosition = startPosition + axis * stepDistance;
        Vector3 candidateDelta = candidatePosition - startPosition;

        Debug.Log(
            $"[PredictiveCollisionStepExperiment] 후보 검사 시작 | " +
            $"Body={movingBody.name}, Axis={axis}, " +
            $"Start={startPosition}, Candidate={candidatePosition}",
            this);

        if (!TryPredictCollision(candidateDelta, out string collisionLog))
        {
            movingBody.position = candidatePosition;
            Physics.SyncTransforms();

            Debug.Log(
                "[PredictiveCollisionStepExperiment] 충돌 없음: 후보 위치를 확정했습니다.",
                this);

            yield return new WaitForSeconds(holdDuration);

            if (returnToStart)
            {
                Vector3 returnDelta = startPosition - movingBody.position;

                if (!TryPredictCollision(returnDelta, out string returnCollisionLog))
                {
                    movingBody.position = startPosition;
                    Physics.SyncTransforms();
                    Debug.Log("[PredictiveCollisionStepExperiment] 원래 위치로 복귀했습니다.", this);
                }
                else
                {
                    Debug.LogWarning(
                        "[PredictiveCollisionStepExperiment] 복귀 후보가 충돌하여 현재 위치를 유지합니다. " +
                        returnCollisionLog,
                        this);
                }
            }
        }
        else
        {
            Debug.Log(
                "[PredictiveCollisionStepExperiment] 충돌 예상: 후보 위치로 이동하지 않았습니다. " +
                collisionLog,
                this);
        }

        experimentRoutine = null;
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

                // 같은 Collision 루트 안의 Collider끼리는 내부 부품 접촉으로 보고
                // 링크 간 충돌 후보로 검사하지 않습니다.
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
                    collisionLog =
                        $"Moving={GetColliderLabel(movingCollider)}, " +
                        $"Obstacle={GetColliderLabel(obstacleCollider)}, " +
                        $"Penetration={distance:F4}, Direction={direction}";
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsUsableCollider(Collider collider)
    {
        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
            return false;

        return includeTriggers || !collider.isTrigger;
    }

    private string GetColliderLabel(Collider collider)
    {
        if (collider == null)
            return "<null>";

        return collider.transform.name + "/" + collider.GetType().Name;
    }

    private bool ValidateReferences()
    {
        if (sliderConstraint == null)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] Slider Constraint 참조가 없습니다.", this);
            return false;
        }

        if (!sliderConstraint.enabled)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] GeometrySliderConstraint가 켜져 있어야 합니다.", this);
            return false;
        }

        if (sliderConstraint.fixedReferenceFrame == null)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] Fixed Reference Frame 참조가 없습니다.", this);
            return false;
        }

        if (movingBody == null)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] Moving Body 참조가 없습니다.", this);
            return false;
        }

        if (movingCollisionRoot == null)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] Moving Collision Root 참조가 없습니다.", this);
            return false;
        }

        if (obstacleCollisionRoot == null)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] Obstacle Collision Root 참조가 없습니다.", this);
            return false;
        }

        if (movingCollisionRoot == obstacleCollisionRoot)
        {
            Debug.LogError("[PredictiveCollisionStepExperiment] Moving과 Obstacle은 서로 다른 Collision Root여야 합니다.", this);
            return false;
        }

        return true;
    }

    private void CollectColliders()
    {
        movingColliders.Clear();
        obstacleColliders.Clear();

        movingColliders.AddRange(movingCollisionRoot.GetComponentsInChildren<Collider>(true));
        obstacleColliders.AddRange(obstacleCollisionRoot.GetComponentsInChildren<Collider>(true));

        Debug.Log(
            $"[PredictiveCollisionStepExperiment] Collider 자동 수집 | " +
            $"MovingRoot={movingCollisionRoot.name}: {movingColliders.Count}, " +
            $"ObstacleRoot={obstacleCollisionRoot.name}: {obstacleColliders.Count}",
            this);
    }

    private Transform FindCollisionRoot(Transform start)
    {
        foreach (Transform child in start.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Collision")
                return child;
        }

        Transform current = start;

        while (current != null)
        {
            if (current.name == "Collision")
                return current;

            current = current.parent;
        }

        return null;
    }

    private Transform GetCollisionRoot(Collider collider)
    {
        if (collider == null)
            return null;

        Transform current = collider.transform;

        while (current != null)
        {
            if (current.name == "Collision")
                return current;

            current = current.parent;
        }

        return null;
    }
}
