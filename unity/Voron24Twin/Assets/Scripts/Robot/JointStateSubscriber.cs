using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosJointState = RosMessageTypes.Sensor.JointStateMsg;

namespace Voron24.Robot
{
    /// <summary>
    /// ROS2의 /joint_states를 구독하여
    /// Voron 2.4의 ArticulationBody를 구동한다.
    ///
    /// 단위 규약:
    /// - ROS prismatic joint: m
    /// - ROS revolute joint: rad
    /// - Unity prismatic joint: m
    /// - Unity revolute joint: degree
    ///
    /// ROS 조인트 이름과 Unity 링크 이름을 별도로 관리한다.
    /// </summary>
    public class JointStateSubscriber : MonoBehaviour
    {
        [Header("ROS")]
        [SerializeField]
        private string topic = "/joint_states";

        [Header("Robot")]
        [Tooltip("Hierarchy에 임포트된 voron24 최상위 객체")]
        [SerializeField]
        private Transform robotRoot;

        [Tooltip("ROS /joint_states에서 사용하는 조인트 이름")]
        [SerializeField]
        private string[] jointNames =
        {
            "joint_x",
            "joint_y",
            "joint_z"
        };

        [Tooltip("각 ROS 조인트와 연결되는 Unity 링크 이름")]
        [SerializeField]
        private string[] linkNames =
        {
            "toolhead",
            "x_beam",
            "z_gantry"
        };

        [Header("Mode")]
        [Tooltip(
            "체크하면 ArticulationBody 물리를 우회하고 " +
            "Transform을 직접 이동시킨다.")]
        [SerializeField]
        private bool kinematicMode = true;

        [Header("Smoothing")]
        [Tooltip("목표 위치 보간 시간. 0이면 보간하지 않는다.")]
        [SerializeField]
        private float smoothTime = 0.02f;

        [Header("Debug")]
        [SerializeField]
        private bool verbose = true;

        [SerializeField]
        private bool drawGizmos = true;

        private class Joint
        {
            public string name;
            public string linkName;
            public ArticulationBody body;
            public Transform tf;
            public bool isRevolute;
            public Vector3 localAxis;
            public Vector3 restPos;
            public Quaternion restRot;
            public float target;
            public float current;
            public float velocity;
            public bool bound;
        }

        private readonly List<Joint> joints =
            new List<Joint>();

        private readonly Dictionary<string, Joint> jointsByName =
            new Dictionary<string, Joint>();

        private bool gotFirstMessage;
        private int messageCount;
        private float lastMessageTime;

        private void Awake()
        {
            if (robotRoot == null)
            {
                robotRoot = transform;
            }

            Bind();
        }

        private void Start()
        {
            ROSConnection.GetOrCreateInstance()
                .Subscribe<RosJointState>(topic, OnJointState);

            if (verbose)
            {
                Debug.Log(
                    $"[JointState] subscribed to {topic}");
            }
        }

        /// <summary>
        /// ROS 조인트 이름과 Unity 링크 객체를 연결한다.
        /// </summary>
        public void Bind()
        {
            joints.Clear();
            jointsByName.Clear();

            if (robotRoot == null)
            {
                Debug.LogError(
                    "[JointState] Robot Root가 비어 있습니다. " +
                    "Hierarchy의 voron24 객체를 연결하십시오.");

                return;
            }

            if (jointNames == null || linkNames == null)
            {
                Debug.LogError(
                    "[JointState] Joint Names 또는 Link Names가 없습니다.");

                return;
            }

            if (jointNames.Length != linkNames.Length)
            {
                Debug.LogError(
                    "[JointState] Joint Names와 Link Names의 " +
                    "개수가 서로 다릅니다.");

                return;
            }

            for (int i = 0; i < jointNames.Length; i++)
            {
                string jointName = jointNames[i];
                string linkName = linkNames[i];

                Joint joint = new Joint
                {
                    name = jointName,
                    linkName = linkName,
                    bound = false,
                    target = 0f,
                    current = 0f,
                    velocity = 0f
                };

                Transform linkTransform =
                    FindDeep(robotRoot, linkName);

                if (linkTransform == null)
                {
                    Debug.LogError(
                        $"[JointState] Unity 객체 '{linkName}'을 " +
                        "Robot Root 아래에서 찾을 수 없습니다.");
                }
                else
                {
                    joint.tf = linkTransform;

                    joint.body =
                        linkTransform.GetComponent<ArticulationBody>();

                    if (joint.body == null)
                    {
                        Debug.LogError(
                            $"[JointState] '{linkName}'에 " +
                            "ArticulationBody가 없습니다.");
                    }
                    else
                    {
                        joint.isRevolute =
                            joint.body.jointType ==
                            ArticulationJointType.RevoluteJoint;

                        joint.restPos =
                            linkTransform.localPosition;

                        joint.restRot =
                            linkTransform.localRotation;

                        joint.localAxis =
                            AxisFromDrive(joint.body);

                        joint.bound = true;

                        if (Mathf.Approximately(
                            joint.body.mass, 0f))
                        {
                            Debug.LogError(
                                $"[JointState] '{linkName}'의 " +
                                "Mass가 0입니다.");
                        }
                    }
                }

                joints.Add(joint);

                // ROS 조인트 이름을 Dictionary 검색 키로 사용한다.
                jointsByName[jointName] = joint;
            }

            if (verbose)
            {
                int boundCount =
                    joints.FindAll(joint => joint.bound).Count;

                Debug.Log(
                    $"[JointState] bound {boundCount}/" +
                    $"{joints.Count} joints under " +
                    $"'{robotRoot.name}'");
            }
        }

        /// <summary>
        /// URDF Importer가 생성한 조인트의 로컬 이동축을 구한다.
        /// </summary>
        private static Vector3 AxisFromDrive(
            ArticulationBody body)
        {
            return body.anchorRotation * Vector3.right;
        }

        /// <summary>
        /// 지정된 이름의 자식 객체를 재귀적으로 검색한다.
        /// </summary>
        private static Transform FindDeep(
            Transform root,
            string objectName)
        {
            if (root == null)
            {
                return null;
            }

            if (root.name == objectName)
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform result =
                    FindDeep(root.GetChild(i), objectName);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /// <summary>
        /// ROS2 /joint_states 메시지를 수신한다.
        /// </summary>
        private void OnJointState(RosJointState message)
        {
            if (message == null)
            {
                return;
            }

            messageCount++;
            lastMessageTime = Time.time;

            if (verbose && !gotFirstMessage)
            {
                gotFirstMessage = true;

                Debug.Log(
                    "[JointState] first message: " +
                    $"names=[{string.Join(", ", message.name)}] " +
                    $"positions=[{string.Join(", ", message.position)}]");

                foreach (string jointName in jointNames)
                {
                    if (System.Array.IndexOf(
                        message.name, jointName) < 0)
                    {
                        Debug.LogWarning(
                            $"[JointState] '{jointName}'이 " +
                            "/joint_states 메시지에 없습니다.");
                    }
                }
            }

            int nameCount =
                message.name != null ? message.name.Length : 0;

            int positionCount =
                message.position != null
                    ? message.position.Length
                    : 0;

            int count =
                Mathf.Min(nameCount, positionCount);

            for (int i = 0; i < count; i++)
            {
                string jointName = message.name[i];

                if (!jointsByName.TryGetValue(
                    jointName, out Joint joint))
                {
                    continue;
                }

                float value =
                    (float)message.position[i];

                joint.target =
                    joint.isRevolute
                        ? value * Mathf.Rad2Deg
                        : value;
            }
        }

        private void FixedUpdate()
        {
            foreach (Joint joint in joints)
            {
                if (!joint.bound)
                {
                    continue;
                }

                if (smoothTime > 0f)
                {
                    joint.current = Mathf.SmoothDamp(
                        joint.current,
                        joint.target,
                        ref joint.velocity,
                        smoothTime,
                        Mathf.Infinity,
                        Time.fixedDeltaTime);
                }
                else
                {
                    joint.current = joint.target;
                }

                if (kinematicMode)
                {
                    ApplyKinematic(joint);
                }
                else
                {
                    ApplyArticulationDrive(joint);
                }
            }
        }

        /// <summary>
        /// Transform을 직접 변경하여 조인트를 이동시킨다.
        /// </summary>
        private static void ApplyKinematic(Joint joint)
        {
            if (joint.isRevolute)
            {
                joint.tf.localRotation =
                    joint.restRot *
                    Quaternion.AngleAxis(
                        joint.current,
                        joint.localAxis);
            }
            else
            {
                joint.tf.localPosition =
                    joint.restPos +
                    joint.localAxis * joint.current;
            }
        }

        /// <summary>
        /// ArticulationBody의 Drive Target을 변경한다.
        /// </summary>
        private static void ApplyArticulationDrive(
            Joint joint)
        {
            ArticulationDrive drive =
                joint.body.xDrive;

            drive.target = joint.current;

            joint.body.xDrive = drive;
        }

        /// <summary>
        /// LocalMockDriver 등 외부 스크립트에서
        /// 목표 위치를 전달할 때 사용한다.
        /// </summary>
        public void SetTargetExternal(
            string jointName,
            float value)
        {
            if (!jointsByName.TryGetValue(
                jointName, out Joint joint))
            {
                if (verbose)
                {
                    Debug.LogWarning(
                        $"[JointState] 외부 목표를 적용할 " +
                        $"조인트 '{jointName}'을 찾지 못했습니다.");
                }

                return;
            }

            joint.target =
                joint.isRevolute
                    ? value * Mathf.Rad2Deg
                    : value;
        }

        public float GetTarget(string jointName)
        {
            if (jointsByName.TryGetValue(
                jointName, out Joint joint))
            {
                return joint.target;
            }

            return 0f;
        }

        public float GetActual(string jointName)
        {
            if (!jointsByName.TryGetValue(
                jointName, out Joint joint))
            {
                return 0f;
            }

            if (kinematicMode)
            {
                return joint.current;
            }

            if (joint.body == null)
            {
                return 0f;
            }

            if (joint.body.jointPosition.dofCount == 0)
            {
                return 0f;
            }

            return joint.body.jointPosition[0];
        }

        public float TimeSinceLastMessage
        {
            get
            {
                if (!gotFirstMessage)
                {
                    return Mathf.Infinity;
                }

                return Time.time - lastMessageTime;
            }
        }

        public bool IsConnected
        {
            get
            {
                return gotFirstMessage &&
                       TimeSinceLastMessage < 1.0f;
            }
        }

        private void OnDrawGizmos()
        {
            if (!drawGizmos || !Application.isPlaying)
            {
                return;
            }

            foreach (Joint joint in joints)
            {
                if (!joint.bound || joint.tf == null)
                {
                    continue;
                }

                Gizmos.color = Color.cyan;

                Vector3 worldAxis =
                    joint.tf.parent != null
                        ? joint.tf.parent.TransformDirection(
                            joint.localAxis)
                        : joint.localAxis;

                Gizmos.DrawRay(
                    joint.tf.position,
                    worldAxis.normalized * 0.05f);
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Rebind Joints")]
        private void RebindFromMenu()
        {
            Bind();
        }
#endif
    }
}