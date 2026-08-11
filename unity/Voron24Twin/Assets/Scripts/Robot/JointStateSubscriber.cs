using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.UrdfImporter;
using RosJointState = RosMessageTypes.Sensor.JointStateMsg;

namespace Voron24.Robot
{
    /// <summary>
    /// /joint_states 를 구독해 ArticulationBody 를 구동한다.
    ///
    /// 단위 규약 (계약 00_interface_contract.md section 2):
    ///   ROS      : prismatic = m,      revolute = rad   (SI)
    ///   Unity AB : prismatic = m,      revolute = degree
    ///   -> revolute 만 rad2deg 변환한다. prismatic 은 그대로.
    ///
    /// 조인트는 이름으로 자동 탐색한다. URDF-Importer 는 GameObject 를 **링크명**으로
    /// 만들고 조인트명은 UrdfJoint.jointName 에 보관하므로, GameObject 이름 -> UrdfJoint
    /// 순서로 찾는다. 덕분에 메시 교체 후 재임포트해도 Inspector 재연결이 필요 없다.
    /// </summary>
    public class JointStateSubscriber : MonoBehaviour
    {
        [Header("ROS")]
        [SerializeField] string topic = "/joint_states";

        [Header("Robot")]
        [Tooltip("비워두면 이 컴포넌트가 붙은 GameObject 를 루트로 삼는다")]
        [SerializeField] Transform robotRoot;

        [Tooltip("계약 section 3 의 조인트 이름. URDF 와 정확히 일치해야 한다.")]
        [SerializeField]
        string[] jointNames = { "joint_x", "joint_y", "joint_z" };

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

        [Header("Mode")]
        [Tooltip("체크하면 물리를 우회하고 Transform 을 직접 갱신한다. " +
                 "데모에서 물리가 불안정할 때의 안전장치.")]
        [SerializeField] bool kinematicMode = false;

        [Header("Smoothing")]
        [Tooltip("퍼블리시 주기(50Hz)보다 물리 주기가 빠를 때 보간. 0 이면 끔.")]
        [SerializeField] float smoothTime = 0.02f;

        [Header("Debug")]
        [SerializeField] bool verbose = true;
        [SerializeField] bool drawGizmos = true;

        class Joint
        {
            public string name;
            public ArticulationBody body;
            public Transform tf;
            public bool isRevolute;
            public Vector3 localAxis;      // Unity 로컬 좌표계에서의 이동 축
            public Vector3 restPos;        // joint 값 0 일 때의 localPosition
            public float target;
            public float current;
            public float vel;
            public bool bound;
        }

        readonly List<Joint> _joints = new();
        readonly Dictionary<string, Joint> _byName = new();
        bool _gotFirstMessage;
        int _messageCount;
        float _lastMessageTime;

        // ------------------------------------------------------------------
        void Awake()
        {
            if (robotRoot == null) robotRoot = transform;
            Bind();
        }

        void Start()
        {
            ROSConnection.GetOrCreateInstance().Subscribe<RosJointState>(topic, OnJointState);
            if (verbose) Debug.Log($"[JointState] subscribed to {topic}");
        }

        /// <summary>이름으로 ArticulationBody 를 찾아 연결한다. 재임포트 후에도 동작.</summary>
        public void Bind()
        {
            _joints.Clear();
            _byName.Clear();

            foreach (var n in jointNames)
            {
                var j = new Joint { name = n };
                // GameObject 이름을 먼저 본다. URDF-Importer 로 임포트한 로봇은 링크명이
                // 붙어 있어 여기서 실패하고 UrdfJoint.jointName 쪽에서 걸린다.
                // 손으로 만든 리그처럼 GameObject 를 조인트명으로 지은 경우를 위해 순서 유지.
                var tf = FindDeep(robotRoot, n) ?? FindByUrdfJointName(robotRoot, n);

                if (tf == null)
                {
                    Debug.LogError($"[JointState] '{n}' 조인트를 찾을 수 없습니다. " +
                                   $"URDF 의 조인트 이름과 계약 section 3 을 대조하세요.");
                }
                else
                {
                    j.tf = tf;
                    j.body = tf.GetComponent<ArticulationBody>();
                    if (j.body == null)
                    {
                        Debug.LogError($"[JointState] '{n}' 에 ArticulationBody 가 없습니다. " +
                                       $"URDF-Importer 로 임포트한 로봇인지 확인하세요.");
                    }
                    else
                    {
                        j.isRevolute = j.body.jointType == ArticulationJointType.RevoluteJoint;
                        j.restPos = tf.localPosition;
                        j.localAxis = AxisFromDrive(j.body);
                        j.bound = true;
                        ApplyDriveGain(j.body, n);

                        if (Mathf.Approximately(j.body.mass, 0f))
                        {
                            Debug.LogError($"[JointState] '{n}' 의 mass 가 0 입니다. " +
                                           $"URDF 의 <inertial> 누락 — CAD/ROS2 담당에게 알리세요.");
                        }
                    }
                }

                _joints.Add(j);
                _byName[n] = j;
            }

            if (verbose)
            {
                int ok = _joints.FindAll(x => x.bound).Count;
                Debug.Log($"[JointState] bound {ok}/{_joints.Count} joints under '{robotRoot.name}'");
            }
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
            // URDF-Importer 는 조인트 축을 로컬 X 로 정렬한 뒤 anchorRotation 으로 회전시킨다.
            return b.anchorRotation * Vector3.right;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // ------------------------------------------------------------------
        void OnJointState(RosJointState msg)
        {
            _messageCount++;
            _lastMessageTime = Time.time;

            if (verbose && !_gotFirstMessage)
            {
                _gotFirstMessage = true;
                Debug.Log($"[JointState] first message: names=[{string.Join(", ", msg.name)}] " +
                          $"positions=[{string.Join(", ", msg.position)}]");

                foreach (var n in jointNames)
                {
                    if (System.Array.IndexOf(msg.name, n) < 0)
                        Debug.LogWarning($"[JointState] '{n}' 이 메시지에 없습니다. " +
                                         $"퍼블리셔의 msg.name 을 확인하세요 (계약 section 5).");
                }
            }

            int count = Mathf.Min(msg.name.Length, msg.position.Length);
            for (int i = 0; i < count; i++)
            {
                if (!_byName.TryGetValue(msg.name[i], out var j)) continue;
                float v = (float)msg.position[i];
                j.target = j.isRevolute ? v * Mathf.Rad2Deg : v;   // rad -> deg
            }
        }

        // ------------------------------------------------------------------
        void FixedUpdate()
        {
            foreach (var j in _joints)
            {
                if (!j.bound) continue;

                j.current = smoothTime > 0f
                    ? Mathf.SmoothDamp(j.current, j.target, ref j.vel,
                                       smoothTime, Mathf.Infinity, Time.fixedDeltaTime)
                    : j.target;

                if (kinematicMode)
                {
                    // 물리 우회: 조인트 로컬 축을 따라 Transform 을 직접 이동
                    float d = j.isRevolute ? 0f : j.current;
                    j.tf.localPosition = j.restPos + j.localAxis * d;
                    if (j.isRevolute)
                        j.tf.localRotation = Quaternion.AngleAxis(j.current, j.localAxis);
                }
                else
                {
                    var drive = j.body.xDrive;      // prismatic/revolute 모두 xDrive
                    drive.target = j.current;
                    j.body.xDrive = drive;
                }
            }
        }

        // ------------------------------------------------------------------
        // 외부 조회 / 디버그
        // ------------------------------------------------------------------
        public float GetTarget(string jointName)
            => _byName.TryGetValue(jointName, out var j) ? j.target : 0f;

        /// <summary>
        /// ROS 없이 외부(LocalMockDriver 등)에서 목표값을 주입한다.
        /// 단위는 ROS 규약과 동일: prismatic = m, revolute = rad.
        /// </summary>
        public void SetTargetExternal(string jointName, float value)
        {
            if (!_byName.TryGetValue(jointName, out var j)) return;
            j.target = j.isRevolute ? value * Mathf.Rad2Deg : value;
        }

        public float GetActual(string jointName)
            => _byName.TryGetValue(jointName, out var j) && j.body != null
               ? j.body.jointPosition[0] : 0f;

        /// <summary>마지막 메시지 이후 경과 시간. 1초 넘으면 연결이 끊긴 것.</summary>
        public float TimeSinceLastMessage => Time.time - _lastMessageTime;

        public bool IsConnected => _gotFirstMessage && TimeSinceLastMessage < 1.0f;

        void OnDrawGizmos()
        {
            if (!drawGizmos || !Application.isPlaying) return;
            foreach (var j in _joints)
            {
                if (!j.bound) continue;
                Gizmos.color = Color.cyan;
                Gizmos.DrawRay(j.tf.position, j.tf.TransformDirection(j.localAxis) * 0.05f);
            }
        }

#if UNITY_EDITOR
        [ContextMenu("Rebind Joints")]
        void RebindFromMenu() => Bind();
#endif
    }
}
