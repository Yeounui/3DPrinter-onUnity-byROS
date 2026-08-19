using UnityEngine;

namespace Voron24.Viz
{
    /// <summary>
    /// 프린터 중심을 바라보며 수평으로 계속 공전하고, 측면과 탑뷰 사이를 정속 왕복한다.
    /// ROS/G-code 재생 배속이나 Time.timeScale의 영향을 받지 않도록 unscaled time을 쓴다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PrinterOrbitCamera : MonoBehaviour
    {
        [Header("Orbit target")]
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 targetOffset = new Vector3(0f, 0.23f, 0f);
        [SerializeField, Min(0.01f)] private float radius = 0.65f;

        [Header("Constant angular speed (unscaled time)")]
        [SerializeField] private float horizontalDegreesPerSecond = 12f;
        [SerializeField, Range(-89f, 89f)] private float minElevationDegrees = 10f;
        [SerializeField, Range(-89f, 89f)] private float maxElevationDegrees = 80f;
        [SerializeField, Min(0f)] private float verticalDegreesPerSecond = 6f;

        [Header("Starting pose")]
        [SerializeField] private float initialAzimuthDegrees = 180f;
        [SerializeField, Range(-89f, 89f)] private float initialElevationDegrees = 15f;
        [SerializeField] private bool startMovingUp = true;

        private float azimuthDegrees;
        private float verticalPhaseDegrees;

        private void Awake()
        {
            if (target == null)
            {
                GameObject robot = GameObject.FindWithTag("robot");
                if (robot != null)
                {
                    target = robot.transform;
                }
            }
        }

        private void OnEnable()
        {
            ResetOrbit();
        }

        private void LateUpdate()
        {
            float deltaTime = Time.unscaledDeltaTime;
            azimuthDegrees = Mathf.Repeat(
                azimuthDegrees + horizontalDegreesPerSecond * deltaTime,
                360f);

            float elevationRange = maxElevationDegrees - minElevationDegrees;
            float elevationDegrees = minElevationDegrees;

            if (elevationRange > Mathf.Epsilon)
            {
                float roundTripDegrees = elevationRange * 2f;
                verticalPhaseDegrees = Mathf.Repeat(
                    verticalPhaseDegrees + verticalDegreesPerSecond * deltaTime,
                    roundTripDegrees);
                elevationDegrees = minElevationDegrees + Mathf.PingPong(
                    verticalPhaseDegrees,
                    elevationRange);
            }

            ApplyPose(elevationDegrees);
        }

        [ContextMenu("Reset Orbit")]
        public void ResetOrbit()
        {
            azimuthDegrees = Mathf.Repeat(initialAzimuthDegrees, 360f);

            float elevationRange = Mathf.Max(
                maxElevationDegrees - minElevationDegrees,
                0f);
            float initialElevation = Mathf.Clamp(
                initialElevationDegrees,
                minElevationDegrees,
                maxElevationDegrees);
            float elevationFromMinimum = initialElevation - minElevationDegrees;

            verticalPhaseDegrees = startMovingUp
                ? elevationFromMinimum
                : elevationRange * 2f - elevationFromMinimum;

            ApplyPose(initialElevation);
        }

        private void ApplyPose(float elevationDegrees)
        {
            Vector3 targetPosition = target != null
                ? target.TransformPoint(targetOffset)
                : targetOffset;
            Quaternion orbitRotation = Quaternion.Euler(
                elevationDegrees,
                azimuthDegrees,
                0f);

            transform.position = targetPosition +
                orbitRotation * (Vector3.back * radius);

            Vector3 lookDirection = targetPosition - transform.position;
            if (lookDirection.sqrMagnitude > Mathf.Epsilon)
            {
                transform.rotation = Quaternion.LookRotation(
                    lookDirection,
                    Vector3.up);
            }
        }

        private void OnValidate()
        {
            radius = Mathf.Max(radius, 0.01f);
            minElevationDegrees = Mathf.Clamp(minElevationDegrees, -89f, 88.9f);
            maxElevationDegrees = Mathf.Clamp(
                maxElevationDegrees,
                minElevationDegrees + 0.1f,
                89f);
            initialElevationDegrees = Mathf.Clamp(
                initialElevationDegrees,
                minElevationDegrees,
                maxElevationDegrees);
            verticalDegreesPerSecond = Mathf.Max(verticalDegreesPerSecond, 0f);
        }
    }
}
