using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Z축 하강 시 노즐-베드 접촉을 찾고, 모델링 오류로 발생하는
/// gantry-base 충돌의 예외 범위를 측정하는 전용 탐색기.
/// </summary>
public class NegativeZBedCalibration : MonoBehaviour
{
    [Header("References")]
    public Transform zGantry;
    public Transform robotCoordinateFrame;
    public Collider nozzleCollider;
    public Transform baseCollisionRoot;
    public Transform gantryCollisionRoot;

    [Header("Search")]
    public bool runOnStart;
    public float stepDistance = 1f;
    public int maxSteps = 1000;
    public float contactLift = 0.2f;
    public float holdDuration = 0.2f;
    public bool returnToPostContact = true;

    [Header("Result")]
    public CalibrationState state = CalibrationState.Idle;
    public int safeStepsBeforeContact;
    public Vector3 nozzleBedContactWorldPosition;
    public Vector3 postContactWorldPosition;
    public float gantryBaseIgnoredDistance;

    public enum CalibrationState
    {
        Idle,
        Descending,
        ContactFound,
        MeasuringException,
        Returning,
        Ready,
        Blocked,
        Error
    }

    private readonly List<Collider> baseColliders = new List<Collider>();
    private readonly List<Collider> gantryColliders = new List<Collider>();
    private Coroutine routine;

    private void Awake()
    {
        if (zGantry == null)
            zGantry = GameObject.Find("z_gantry")?.transform;

        if (robotCoordinateFrame == null)
            robotCoordinateFrame = GameObject.Find("base_link/ReferenceFrame")?.transform;

        if (baseCollisionRoot == null)
            baseCollisionRoot = GameObject.Find("base_link/Collision")?.transform;

        if (gantryCollisionRoot == null)
            gantryCollisionRoot = GameObject.Find("z_gantry/Collision")?.transform;

        if (nozzleCollider == null)
        {
            GameObject nozzle = GameObject.Find("toolhead/Collision/Generated/nozzle_0");
            if (nozzle != null)
                nozzleCollider = nozzle.GetComponent<Collider>();
        }
    }

    private void Start()
    {
        if (runOnStart)
            BeginCalibration();
    }

    public void BeginCalibration()
    {
        if (routine != null)
            return;

        if (!ValidateConfiguration())
        {
            SetState(CalibrationState.Error);
            return;
        }

        baseColliders.Clear();
        gantryColliders.Clear();
        baseColliders.AddRange(baseCollisionRoot.GetComponentsInChildren<Collider>(true));
        gantryColliders.AddRange(gantryCollisionRoot.GetComponentsInChildren<Collider>(true));
        routine = StartCoroutine(RunCalibration());
    }

    public void StopCalibration()
    {
        if (routine == null)
            return;

        StopCoroutine(routine);
        routine = null;
        SetState(CalibrationState.Idle);
    }

    private IEnumerator RunCalibration()
    {
        // z_gantry의 이동이 x_beam과 toolhead constraint chain에 반영되도록 한 프레임 대기한다.
        yield return null;
        Physics.SyncTransforms();

        Vector3 startPosition = zGantry.position;
        Vector3 robotZ = (-robotCoordinateFrame.right).normalized;
        Vector3 negativeZ = -robotZ;
        safeStepsBeforeContact = 0;
        gantryBaseIgnoredDistance = 0f;

        for (int stepIndex = 1; stepIndex <= maxSteps; stepIndex++)
        {
            SetState(CalibrationState.Descending);
            Vector3 candidateGantryPosition = zGantry.position + negativeZ * stepDistance;

            if (PredictNozzleContact(negativeZ * stepDistance, out Collider bedCollider, out float contactDepth))
            {
                zGantry.position = candidateGantryPosition;
                Physics.SyncTransforms();
                yield return null;
                Physics.SyncTransforms();

                nozzleBedContactWorldPosition = nozzleCollider.transform.position;
                safeStepsBeforeContact = stepIndex - 1;
                SetState(CalibrationState.ContactFound);
                Debug.Log(
                    $"[NegativeZBedCalibration] 노즐-베드 접촉 | Step={stepIndex}, " +
                    $"Collider={bedCollider.name}, Depth={contactDepth:F4}, " +
                    $"NozzlePosition={nozzleBedContactWorldPosition}",
                    this);

                yield return MoveGantryStepped(robotZ, contactLift, "접촉 후 안전 상승");
                postContactWorldPosition = zGantry.position;

                yield return MeasureGantryBaseException(robotZ);

                if (returnToPostContact)
                {
                    SetState(CalibrationState.Returning);
                    yield return MoveGantryToPositionStepped(
                        postContactWorldPosition,
                        "접촉 후 기준 위치 복귀");
                }

                SetState(CalibrationState.Ready);
                routine = null;
                yield break;
            }

            zGantry.position = candidateGantryPosition;
            Physics.SyncTransforms();
            safeStepsBeforeContact = stepIndex;

            if (holdDuration > 0f)
                yield return new WaitForSeconds(holdDuration);
        }

        SetState(CalibrationState.Blocked);
        Debug.LogWarning(
            $"[NegativeZBedCalibration] 최대 스텝 안에서 노즐-베드 접촉을 찾지 못했습니다. " +
            $"SafeSteps={safeStepsBeforeContact}",
            this);
        zGantry.position = startPosition;
        Physics.SyncTransforms();
        routine = null;
    }

    private IEnumerator MeasureGantryBaseException(Vector3 positiveZ)
    {
        SetState(CalibrationState.MeasuringException);

        while (HasGantryBasePenetration(positiveZ * stepDistance))
        {
            zGantry.position += positiveZ * stepDistance;
            gantryBaseIgnoredDistance += stepDistance;
            Physics.SyncTransforms();

            if (holdDuration > 0f)
                yield return new WaitForSeconds(holdDuration);
        }

        Debug.Log(
            $"[NegativeZBedCalibration] gantry-base 예외 범위 측정 완료 | " +
            $"IgnoredDistance={gantryBaseIgnoredDistance:F3}, " +
            $"PostContactPosition={postContactWorldPosition}",
            this);
    }

    private IEnumerator MoveGantryStepped(Vector3 direction, float distance, string label)
    {
        float remaining = Mathf.Abs(distance);
        Vector3 unitDirection = direction.normalized * Mathf.Sign(distance);

        while (remaining > 0.0001f)
        {
            float step = Mathf.Min(stepDistance, remaining);
            zGantry.position += unitDirection * step;
            remaining -= step;
            Physics.SyncTransforms();
            Debug.Log(
                $"[NegativeZBedCalibration] {label} | Step={step:F3}, Remaining={remaining:F3}",
                this);

            if (holdDuration > 0f)
                yield return new WaitForSeconds(holdDuration);
        }
    }

    private IEnumerator MoveGantryToPositionStepped(Vector3 targetPosition, string label)
    {
        Vector3 delta = targetPosition - zGantry.position;
        float remaining = delta.magnitude;
        Vector3 direction = remaining > 0.0001f ? delta.normalized : Vector3.zero;

        while (remaining > 0.0001f)
        {
            float step = Mathf.Min(stepDistance, remaining);
            zGantry.position += direction * step;
            remaining -= step;
            Physics.SyncTransforms();
            Debug.Log(
                $"[NegativeZBedCalibration] {label} | Step={step:F3}, Remaining={remaining:F3}",
                this);

            if (holdDuration > 0f)
                yield return new WaitForSeconds(holdDuration);
        }
    }

    private bool PredictNozzleContact(
        Vector3 translation,
        out Collider hitCollider,
        out float penetration)
    {
        hitCollider = null;
        penetration = 0f;
        Vector3 predictedPosition = nozzleCollider.transform.position + translation;

        foreach (Collider baseCollider in baseColliders)
        {
            if (!IsUsable(baseCollider))
                continue;

            if (Physics.ComputePenetration(
                nozzleCollider,
                predictedPosition,
                nozzleCollider.transform.rotation,
                baseCollider,
                baseCollider.transform.position,
                baseCollider.transform.rotation,
                out _,
                out float distance))
            {
                hitCollider = baseCollider;
                penetration = distance;
                return true;
            }
        }

        return false;
    }

    private bool HasGantryBasePenetration(Vector3 translation)
    {
        foreach (Collider gantryCollider in gantryColliders)
        {
            if (!IsUsable(gantryCollider))
                continue;

            foreach (Collider baseCollider in baseColliders)
            {
                if (!IsUsable(baseCollider))
                    continue;

                if (Physics.ComputePenetration(
                    gantryCollider,
                    gantryCollider.transform.position + translation,
                    gantryCollider.transform.rotation,
                    baseCollider,
                    baseCollider.transform.position,
                    baseCollider.transform.rotation,
                    out _,
                    out _))
                    return true;
            }
        }

        return false;
    }

    private bool ValidateConfiguration()
    {
        return zGantry != null && robotCoordinateFrame != null && nozzleCollider != null &&
               baseCollisionRoot != null && gantryCollisionRoot != null;
    }

    private bool IsUsable(Collider collider)
    {
        return collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
               !collider.isTrigger;
    }

    private void SetState(CalibrationState nextState)
    {
        state = nextState;
        Debug.Log($"[NegativeZBedCalibration] State={state}", this);
    }
}
