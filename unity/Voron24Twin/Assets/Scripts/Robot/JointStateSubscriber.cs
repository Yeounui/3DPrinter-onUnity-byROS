using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
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
    /// 조인트는 이름으로 자동 탐색한다. URDF-Importer 가 링크명으로 GameObject 를
    /// 만들기 때문에, 메시 교체 후 URDF 를 재임포트해도 Inspector 재연결이 필요 없다.
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
                var tf = FindDeep(robotRoot, n);

                if (tf == null)
                {
                    Debug.LogError($"[JointState] '{n}' 이름의 GameObject 를 찾을 수 없습니다. " +
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
