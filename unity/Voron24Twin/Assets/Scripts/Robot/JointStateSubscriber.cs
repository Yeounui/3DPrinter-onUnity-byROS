using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.UrdfImporter;
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
    /// 조인트는 이름으로 자동 탐색한다. URDF-Importer 는 GameObject 를 **링크명**으로
    /// 만들고 조인트명은 UrdfJoint.jointName 에 보관하므로, GameObject 이름 -> UrdfJoint
    /// 순서로 찾는다. 덕분에 메시 교체 후 재임포트해도 Inspector 재연결이 필요 없다.
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

        [System.Serializable]
        public struct DriveGain
        {
            public string joint;
            public float stiffness;
            public float damping;
            public float forceLimit;
        }

        [Header("Drive")]
        [Tooltip("xDrive 게인. URDF-Importer 는 stiffness 를 0 으로 임포트하므로 " +
                 "여기서 넣지 않으면 target 을 줘도 조인트가 따라가지 않는다. " +
                 "값의 출처는 docs/02_unity_workflow.md '드라이브 게인 튜닝'.")]
        [SerializeField]
        DriveGain[] driveGains =
        {
            new DriveGain { joint = "joint_x", stiffness = 100000f, damping =  3000f, forceLimit =  200f },
            new DriveGain { joint = "joint_y", stiffness = 150000f, damping =  5000f, forceLimit =  300f },
            new DriveGain { joint = "joint_z", stiffness = 300000f, damping = 20000f, forceLimit = 1000f },
        };

        [Header("Collision")]
        [Tooltip("로봇 내부 링크끼리의 충돌을 끈다. URDF 의 collision 박스는 서로 " +
                 "파고들어 있어서 켜 두면 조인트가 리밋에 물려 아예 움직이지 않는다. " +
                 "이 트윈은 /joint_states 를 그대로 재생하는 것이 목적이라 자기충돌은 필요 없다.")]
        [SerializeField] bool disableSelfCollision = true;

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
                var j = new Joint { name = n };
                // GameObject 이름을 먼저 본다. URDF-Importer 로 임포트한 로봇은 링크명이
                // 붙어 있어 여기서 실패하고 UrdfJoint.jointName 쪽에서 걸린다.
                // 손으로 만든 리그처럼 GameObject 를 조인트명으로 지은 경우를 위해 순서 유지.
                var tf = FindDeep(robotRoot, n) ?? FindByUrdfJointName(robotRoot, n);

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
                    Debug.LogError($"[JointState] '{n}' 조인트를 찾을 수 없습니다. " +
                                   $"URDF 의 조인트 이름과 계약 section 3 을 대조하세요.");
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
                        j.isRevolute = j.body.jointType == ArticulationJointType.RevoluteJoint;
                        j.restPos = tf.localPosition;
                        j.localAxis = AxisFromDrive(j.body);
                        j.bound = true;
                        ApplyDriveGain(j.body, n);

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
        /// 로봇 내부 링크 콜라이더끼리의 충돌을 전부 끈다.
        ///
        /// 왜 필요한가: URDF 의 collision 지오메트리는 서로 겹치도록 그려져 있다.
        /// base_link 의 박스가 기계 전체 부피를 감싸고 그 안에 z_gantry/x_beam/toolhead
        /// 가 들어앉는 식이다. ArticulationBody 는 **부모-자식으로 인접한 링크끼리만**
        /// 자동으로 충돌을 끄므로 base_link ↔ x_beam 처럼 한 다리 건넌 쌍은 그대로
        /// 충돌한다. 완전히 파묻힌 상태라 PhysX 가 밀어내기(depenetration)를 계속 걸고,
        /// 그 결과 조인트가 lower 리밋 0 에 물려 target 을 줘도 위치가 0 에서 안 움직인다.
        /// 증상이 "bound 3/3 인데 로봇이 가만히 있다" 로 나오기 때문에 바인딩 실패나
        /// 게인 0 과 구분이 잘 안 된다 — jointPosition 이 0 고정인데 jointVelocity 만
        /// 0 이 아니면 이쪽을 의심할 것.
        ///
        /// 끄는 게 맞는 이유: 이 트윈은 /joint_states 를 그대로 재생하는 시각화다.
        /// 자기충돌로 막아야 할 대상이 없고, 충돌 응답은 오히려 원본 궤적을 왜곡한다.
        /// (외부 물체와의 충돌은 살아 있다 — 링크 쌍만 끄기 때문.)
        /// </summary>
        void DisableSelfCollision()
        {
            var cols = robotRoot.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
                for (int k = i + 1; k < cols.Length; k++)
                    Physics.IgnoreCollision(cols[i], cols[k], true);

            if (verbose && cols.Length > 1)
                Debug.Log($"[JointState] 자기충돌 해제: 콜라이더 {cols.Length} 개, " +
                          $"{cols.Length * (cols.Length - 1) / 2} 쌍");
        }

        /// <summary>
        /// 계약 section 3 의 조인트 이름으로 해당 조인트가 구동하는 링크를 찾는다.
        ///
        /// URDF 는 링크와 조인트를 별개 엔티티로 두지만, Unity 의 ArticulationBody 는
        /// "강체 + 그것을 부모에 매다는 조인트" 를 하나로 합친다. 조인트가 독립 객체로
        /// 존재하지 않고 **자식 링크의 속성**이다. 그래서 URDF-Importer 는 GameObject 를
        /// 링크명으로 만들고, 갈 곳이 없어진 조인트명은 UrdfJoint.jointName 에 보관한다.
        ///
        /// 규칙: 조인트는 자기 **자식 링크**의 GameObject 에 얹힌다.
        ///
        ///   joint_z -> z_gantry   (base_link 를 부모로)
        ///   joint_y -> x_beam     (z_gantry  를 부모로)
        ///   joint_x -> toolhead   (x_beam    를 부모로)
        ///
        /// 이름이 엇갈려 보이는 것은 CoreXY 구조 그대로다. X 빔이 Y 축을 따라 움직이고,
        /// 툴헤드가 그 빔 위에서 X 축을 따라 움직인다.
        ///
        /// 주의: jointNames 를 링크명으로 바꿔 해결하려 하지 말 것. 같은 문자열이
        /// _byName 의 키로도 쓰이는데 그쪽은 /joint_states 의 msg.name("joint_x") 과
        /// 대조된다. 키는 계약상 조인트명으로 고정하고 탐색 단계에서만 링크로 번역한다.
        ///
        /// 부수 효과: 링크명이 바뀌어도 조인트명만 계약과 맞으면 계속 바인딩된다.
        /// </summary>
        static Transform FindByUrdfJointName(Transform root, string jointName)
        {
            // UrdfJoint 는 추상 클래스 — Prismatic/Revolute/Fixed 를 모두 잡는다.
            foreach (var uj in root.GetComponentsInChildren<UrdfJoint>(true))
                if (uj.jointName == jointName) return uj.transform;
            return null;
        }

        /// <summary>
        /// xDrive 게인 주입. URDF 에는 stiffness/damping 개념이 없어 임포트 직후 0 이고,
        /// 그 상태로는 target 을 써도 힘이 나오지 않는다. 재임포트해도 코드가 다시 채운다.
        /// </summary>
        void ApplyDriveGain(ArticulationBody body, string jointName)
        {
            var g = System.Array.Find(driveGains, x => x.joint == jointName);
            if (g.stiffness <= 0f)
            {
                Debug.LogWarning($"[JointState] '{jointName}' 의 드라이브 게인이 없습니다. " +
                                 $"stiffness 0 이면 조인트가 목표를 따라가지 않습니다.");
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