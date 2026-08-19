using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.UrdfImporter;
using RosJointState = RosMessageTypes.Sensor.JointStateMsg;

namespace Voron24.Robot
{
    /// <summary>
    /// ROS2 /joint_states 구독,
    /// Voron 2.4 ArticulationBody 구동.
    ///
    /// 단위 규약:
    /// - ROS prismatic joint: m
    /// - ROS revolute joint: rad
    /// - Unity prismatic joint: m
    /// - Unity revolute joint: degree
    ///
    /// 조인트를 이름으로 자동 탐색. URDF-Importer는 GameObject를 **링크명**으로
    /// 만들고 조인트명은 UrdfJoint.jointName에 보관. GameObject 이름 -> UrdfJoint
    /// 순서로 탐색. 메시 교체 재임포트 후에도 Inspector 재연결 불필요.
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

        [Tooltip("ROS /joint_states 조인트 이름")]
        [SerializeField]
        private string[] jointNames =
        {
            "joint_x",
            "joint_y",
            "joint_z"
        };

        [Tooltip("각 ROS 조인트에 대응하는 Unity 링크 이름")]
        [SerializeField]
        private string[] linkNames =
        {
            "toolhead",
            "x_beam",
            "z_gantry"
        };

        [System.Serializable]
        public struct DriveGain
        {
            public string joint;
            public float stiffness;
            public float damping;
            public float forceLimit;
        }

        [Header("Drive")]
        [Tooltip("xDrive 게인. URDF-Importer는 stiffness를 0으로 임포트하므로 " +
                 "여기서 넣지 않으면 target 설정해도 조인트 미반응. " +
                 "값 출처: docs/02_unity_workflow.md '드라이브 게인 튜닝'.")]
        [SerializeField]
        DriveGain[] driveGains =
        {
            new DriveGain { joint = "joint_x", stiffness = 100000f, damping =  3000f, forceLimit =  200f },
            new DriveGain { joint = "joint_y", stiffness = 150000f, damping =  5000f, forceLimit =  300f },
            new DriveGain { joint = "joint_z", stiffness = 300000f, damping = 20000f, forceLimit = 1000f },
        };

        [Header("Collision")]
        [Tooltip("로봇 내부 링크 간 충돌 off. URDF collision 박스가 서로 " +
                 "파고들어 있어 on 시 조인트가 limit에 걸려 움직이지 않음. " +
                 "이 트윈은 /joint_states 그대로 재생이 목적이라 자기충돌 불필요.")]
        [SerializeField] bool disableSelfCollision = true;

        [Header("Mode")]
        [Tooltip(
            "체크 시 ArticulationBody 물리 우회, " +
            "Transform 직접 이동")]
        [SerializeField]
        private bool kinematicMode = true;

        [Header("Smoothing")]
        [Tooltip("목표 위치 보간 시간. 0이면 보간 안 함")]
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
        /// ROS 조인트 이름 ↔ Unity 링크 객체 연결.
        /// </summary>
        public void Bind()
        {
            joints.Clear();
            jointsByName.Clear();

            if (robotRoot == null)
            {
                Debug.LogError(
                    "[JointState] Robot Root 비어 있음. " +
                    "Hierarchy voron24 객체 연결 필요");

                return;
            }

            if (jointNames == null || linkNames == null)
            {
                Debug.LogError(
                    "[JointState] Joint Names 또는 Link Names 없음");

                return;
            }

            if (jointNames.Length != linkNames.Length)
            {
                Debug.LogError(
                    "[JointState] Joint Names / Link Names 개수 불일치. " +
                    $"jointNames={jointNames.Length}, linkNames={linkNames.Length}");

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
                    Debug.LogError($"[JointState] '{linkName}' 링크 없음 " +
                                   $"(조인트 '{jointName}'). " +
                                   $"URDF 링크 이름과 계약 §3 대조 필요");
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
                            "ArticulationBody 없음");
                    }
                    else
                    {
                        joint.isRevolute =
                            joint.body.jointType ==
                            ArticulationJointType.RevoluteJoint;

                        ApplyDriveGain(joint.body, jointName);

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
                                "Mass 0");
                        }
                    }
                }

                joints.Add(joint);

                // ROS 조인트 이름을 Dictionary 검색 키로 사용
                jointsByName[jointName] = joint;
            }

            if (disableSelfCollision) DisableSelfCollision();

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
        /// 로봇 내부 링크 collider 간 충돌 전부 off.
        ///
        /// 이유: URDF collision geometry가 서로 겹침.
        /// base_link 박스가 기계 전체 부피를 감싸고 그 안에 z_gantry/x_beam/toolhead
        /// 가 들어앉는 구조. ArticulationBody는 **부모-자식 인접 링크만**
        /// 자동으로 충돌을 끄므로 base_link ↔ x_beam 처럼 한 다리 건넌 pair은 그대로
        /// 충돌. 완전 파묻힌 상태라 PhysX depenetration이 지속 발생,
        /// 결과적으로 조인트가 lower limit 0에 걸려 target 설정해도 위치 0 고정.
        /// 증상이 "bound 3/3인데 로봇 정지"로 나와서 바인딩 실패나
        /// 게인 0과 구분 어려움 — jointPosition 0 고정에 jointVelocity만
        /// 0 아니면 이쪽 의심.
        ///
        /// off가 맞는 이유: 이 트윈은 /joint_states 그대로 재생하는 시각화.
        /// 자기충돌 방어 대상 없고, 충돌 응답은 원본 궤적 왜곡.
        /// (외부 물체와의 충돌은 살아 있음 — 링크 pair만 끄기 때문.)
        /// </summary>
        void DisableSelfCollision()
        {
            var cols = robotRoot.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
                for (int k = i + 1; k < cols.Length; k++)
                    Physics.IgnoreCollision(cols[i], cols[k], true);

            if (verbose && cols.Length > 1)
                Debug.Log($"[JointState] 자기충돌 해제 — collider {cols.Length} 개 " +
                          $"{cols.Length * (cols.Length - 1) / 2} pair");
        }

        /// <summary>
        /// 계약 §3 조인트 이름으로 해당 조인트 구동 링크 탐색.
        ///
        /// URDF는 링크/조인트를 별개 entity로 두지만, Unity ArticulationBody는
        /// "강체 + 부모에 매다는 조인트"를 하나로 합침. 조인트가 독립 객체로
        /// 존재하지 않고 **자식 링크의 속성**. URDF-Importer는 GameObject를
        /// 링크명으로 만들고 조인트명은 UrdfJoint.jointName에 보관.
        ///
        /// 규칙: 조인트는 자기 **자식 링크** GameObject에 부착.
        ///
        ///   joint_z -> z_gantry   (parent: base_link)
        ///   joint_y -> x_beam     (parent: z_gantry)
        ///   joint_x -> toolhead   (parent: x_beam)
        ///
        /// 이름이 엇갈려 보이는 것은 CoreXY 구조 그대로. X beam이 Y축을 따라 이동,
        /// toolhead가 그 beam 위에서 X축을 따라 이동.
        ///
        /// 주의: jointNames를 링크명으로 바꿔 해결 시도 금지. 같은 문자열이
        /// _byName의 키로도 쓰이는데 그쪽은 /joint_states msg.name("joint_x")과
        /// 대조됨. 키는 계약상 조인트명 고정, 탐색 단계에서만 링크로 번역.
        ///
        /// 부수 효과: 링크명 변경돼도 조인트명만 계약과 일치하면 바인딩 유지.
        /// </summary>
        static Transform FindByUrdfJointName(Transform root, string jointName)
        {
            // UrdfJoint는 추상 클래스 — Prismatic/Revolute/Fixed 전부 포착
            foreach (var uj in root.GetComponentsInChildren<UrdfJoint>(true))
                if (uj.jointName == jointName) return uj.transform;
            return null;
        }

        /// <summary>
        /// xDrive 게인 주입. URDF에 stiffness/damping 개념 없어 임포트 직후 0,
        /// 그 상태로는 target 써도 힘 발생 안 함. 재임포트 시 코드가 재주입.
        /// </summary>
        void ApplyDriveGain(ArticulationBody body, string jointName)
        {
            var g = System.Array.Find(driveGains, x => x.joint == jointName);
            if (g.stiffness <= 0f)
            {
                Debug.LogWarning($"[JointState] '{jointName}' drive 게인 미설정. " +
                                 $"stiffness 0이면 조인트 목표 미반응");
                return;
            }

            var d = body.xDrive;
            d.stiffness  = g.stiffness;
            d.damping    = g.damping;
            d.forceLimit = g.forceLimit;
            body.xDrive = d;
        }

        static Vector3 AxisFromDrive(ArticulationBody b)
        {
            return b.anchorRotation * Vector3.right;
        }

        /// <summary>
        /// 지정 이름의 자식 객체 재귀 검색.
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
        /// ROS2 /joint_states 메시지 수신.
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
                            $"[JointState] '{jointName}' /joint_states 메시지에 없음");
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
        /// Transform 직접 변경으로 조인트 이동.
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
        /// ArticulationBody Drive Target 변경.
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
        /// 목표 위치 전달용.
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
                        $"[JointState] 외부 목표 적용 대상 " +
                        $"'{jointName}' 없음");
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