using UnityEngine;

namespace Voron24.Robot
{
    public class KeyboardJointController : MonoBehaviour
    {
        [Header("Required")]
        [SerializeField]
        private JointStateSubscriber subscriber;

        [Header("Movement")]
        [Tooltip("초당 이동 거리(m/s)")]
        [SerializeField]
        private float moveSpeed = 0.05f;

        [Tooltip("조인트 최소 위치(m)")]
        [SerializeField]
        private float lowerLimit = 0f;

        [Tooltip("조인트 최대 위치(m)")]
        [SerializeField]
        private float upperLimit = 0.25f;

        private float targetX;
        private float targetY;
        private float targetZ;

        private void Awake()
        {
            if (subscriber == null)
            {
                subscriber =
                    GetComponent<JointStateSubscriber>();
            }
        }

        private void Start()
        {
            if (subscriber == null)
            {
                Debug.LogError(
                    "[Keyboard] JointStateSubscriber가 연결되지 않았습니다.");

                enabled = false;
                return;
            }

            targetX = subscriber.GetTarget("joint_x");
            targetY = subscriber.GetTarget("joint_y");
            targetZ = subscriber.GetTarget("joint_z");

            ApplyTargets();
        }

        private void Update()
        {
            float amount = moveSpeed * Time.deltaTime;

            // X축: 좌우 방향키
            if (Input.GetKey(KeyCode.LeftArrow))
            {
                targetX -= amount;
            }

            if (Input.GetKey(KeyCode.RightArrow))
            {
                targetX += amount;
            }

            // Y축: 위아래 방향키
            if (Input.GetKey(KeyCode.DownArrow))
            {
                targetY -= amount;
            }

            if (Input.GetKey(KeyCode.UpArrow))
            {
                targetY += amount;
            }

            // Z축: Page Down / Page Up
            if (Input.GetKey(KeyCode.PageDown))
            {
                targetZ -= amount;
            }

            if (Input.GetKey(KeyCode.PageUp))
            {
                targetZ += amount;
            }

            // Home 키: X/Y/Z 원점 복귀
            if (Input.GetKeyDown(KeyCode.Home))
            {
                targetX = 0f;
                targetY = 0f;
                targetZ = 0f;
            }

            targetX = Mathf.Clamp(
                targetX, lowerLimit, upperLimit);

            targetY = Mathf.Clamp(
                targetY, lowerLimit, upperLimit);

            targetZ = Mathf.Clamp(
                targetZ, lowerLimit, upperLimit);

            ApplyTargets();
        }

        private void ApplyTargets()
        {
            subscriber.SetTargetExternal(
                "joint_x", targetX);

            subscriber.SetTargetExternal(
                "joint_y", targetY);

            subscriber.SetTargetExternal(
                "joint_z", targetZ);
        }
    }
}