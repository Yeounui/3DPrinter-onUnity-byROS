using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Voron24;

namespace Voron24.Robot
{
    /// <summary>
    /// /printer/extrusion의 압출 점을 직선 구간으로 병합하고,
    /// 임시 파란 구와 확정 주황 원통/구로 실제 입체 필라멘트를 생성한다.
    /// extruding=false는 travel/stop 신호로 사용한다.
    /// </summary>
    public class ExtrusionStateSubscriber : MonoBehaviour
    {
        [SerializeField] private string topic = "/printer/extrusion";
        [SerializeField] private bool verbose = true;
        [Header("Coordinate frame")]
        [SerializeField] private Transform nozzel;
        [SerializeField] private Transform bedOrigin;
        [Header("3D filament output")]
        [SerializeField] private Transform filamentRoot;
        [SerializeField] private Material filamentMaterial;
        // 시각화 테스트용 출력 폭: 이전 0.01 m의 절반.
        [SerializeField] private float fallbackWidth = 0.005f;
        [SerializeField] private float layerHeightTolerance = 0.00001f;
        [SerializeField] private float angleToleranceDegrees = 5f;
        [SerializeField] private float minSegmentLength = 0.000001f;
        [SerializeField] private float previewSampleStep = 0.005f;
        [SerializeField] private bool debugLogs = true;
        [SerializeField] private int debugEveryMessages = 1;

        public bool IsExtruding { get; private set; }
        public Vector3 LastPosition { get; private set; }
        public float LastWidth { get; private set; }
        public float LastHeight { get; private set; }
        public uint LastLayer { get; private set; }

        private Material runtimeMaterial;
        private Material previewMaterial;
        private bool pathActive;
        private float activeLayerZ;
        private uint activeLayerNumber;
        private int messageCount;
        private int segmentCount;
        private Vector3 segmentStart;
        private Vector3 lastPoint;
        private Vector3 lastDirection;
        private bool hasPendingPoint;
        private bool hasDirection;
        private float pendingWidth;
        private Vector3 lastSamplePoint;
        private bool hasSamplePoint;
        private readonly List<GameObject> previewSpheres = new List<GameObject>();

        private void Awake()
        {
            if (nozzel == null) nozzel = FindDeep(transform, "nozzel");
            if (nozzel == null) nozzel = FindDeep(transform, "nozzle");
            if (bedOrigin == null) bedOrigin = FindDeep(transform, "bed_origin");

            if (filamentRoot == null)
            {
                var root = new GameObject("FilamentOutput");
                filamentRoot = root.transform;
            }
            if (bedOrigin != null && filamentRoot.parent == null)
            {
                filamentRoot.SetParent(bedOrigin, false);
                filamentRoot.localPosition = Vector3.zero;
                filamentRoot.localRotation = Quaternion.identity;
                filamentRoot.localScale = Vector3.one;
            }
            if (filamentMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                if (shader != null)
                {
                    runtimeMaterial = new Material(shader);
                    runtimeMaterial.color = new Color(0.95f, 0.35f, 0.06f, 1f);
                    filamentMaterial = runtimeMaterial;
                    previewMaterial = new Material(shader);
                    previewMaterial.color = new Color(0.1f, 0.35f, 1f, 1f);
                }
            }
            if (debugLogs)
                Debug.Log($"[ExtrusionDebug] Awake topic={topic} nozzel={(nozzel != null ? nozzel.name : "<null>")} " +
                          $"bedOrigin={(bedOrigin != null ? bedOrigin.name : "<null>")} " +
                          $"filamentRoot={(filamentRoot != null ? filamentRoot.name : "<null>")} width={fallbackWidth}");
        }

        private void Start()
        {
            ROSConnection.GetOrCreateInstance().Subscribe<ExtrusionPointMsg>(topic, OnExtrusionPoint);
            if (verbose || debugLogs) Debug.Log($"[ExtrusionDebug] subscribed to {topic}");
        }

        private void OnExtrusionPoint(ExtrusionPointMsg msg)
        {
            if (msg == null || msg.position == null) return;

            messageCount++;
            LastPosition = new Vector3((float)msg.position.x, (float)msg.position.y, (float)msg.position.z);
            LastWidth = msg.width;
            LastHeight = msg.height;
            LastLayer = msg.layer;
            IsExtruding = msg.extruding;

            // 압출 점의 실제 기준은 ROS 좌표의 첫 점이 아니라 Unity의 nozzle 중심이다.
            // ROS 좌표는 로그/메타데이터로 보존하고, 렌더링 좌표는 현재 노즐 중심을 사용한다.
            var pointLocal = nozzel != null && bedOrigin != null
                ? bedOrigin.InverseTransformPoint(nozzel.position)
                : LastPosition;

            if (debugLogs && (debugEveryMessages <= 1 || messageCount % debugEveryMessages == 0))
                Debug.Log($"[ExtrusionDebug] RX #{messageCount} extruding={msg.extruding} posRos={LastPosition} " +
                          $"posLocal={pointLocal} width={msg.width} height={msg.height} layer={msg.layer} " +
                          $"source={(nozzel != null && bedOrigin != null ? "nozzle-center" : "ros-position")}");

            if (!msg.extruding)
            {
                FinishPath("travel/stop");
                return;
            }

            if (!pathActive || Mathf.Abs(pointLocal.z - activeLayerZ) > layerHeightTolerance ||
                msg.layer != activeLayerNumber)
            {
                FinishPath("height/layer change");
                BeginPath(pointLocal.z, msg.layer);
            }
            AddPoint(pointLocal, Mathf.Max(fallbackWidth, msg.width, 0.000001f));
        }

        private void BeginPath(float z, uint layer)
        {
            activeLayerZ = z;
            activeLayerNumber = layer;
            pathActive = true;
            hasPendingPoint = false;
            hasDirection = false;
            if (debugLogs) Debug.Log($"[ExtrusionDebug] begin 3D path layer={layer} z={z:F6}");
        }

        private void AddPoint(Vector3 point, float width)
        {
            if (!hasPendingPoint)
            {
                segmentStart = point;
                lastPoint = point;
                pendingWidth = width;
                hasPendingPoint = true;
                BeginPreviewSphere(point, width);
                return;
            }

            Vector3 delta = point - lastPoint;
            float distance = delta.magnitude;
            if (distance <= minSegmentLength) return;

            Vector3 direction = delta / distance;
            if (!hasDirection)
            {
                lastDirection = direction;
                hasDirection = true;
                lastPoint = point;
                pendingWidth = Mathf.Max(pendingWidth, width);
                AddPreviewSphereIfNeeded(point, pendingWidth);
                return;
            }

            float cosine = Mathf.Clamp(Vector3.Dot(lastDirection, direction), -1f, 1f);
            float angle = Mathf.Acos(cosine) * Mathf.Rad2Deg;
            if (angle <= angleToleranceDegrees)
            {
                // 거의 같은 방향이면 기존 구간을 연장한다.
                lastPoint = point;
                pendingWidth = Mathf.Max(pendingWidth, width);
                AddPreviewSphereIfNeeded(point, pendingWidth);
                return;
            }

            // 방향이 충분히 바뀐 지점에서 직선 구간을 확정한다.
            FinalizePreviewSegment(lastPoint);
            segmentStart = lastPoint;
            lastPoint = point;
            lastDirection = direction;
            pendingWidth = width;
            hasSamplePoint = false;
            BeginPreviewSphere(point, width);
        }

        private void FinishPath(string reason)
        {
            if (!pathActive) return;
            FinalizePreviewSegment(lastPoint);
            if (debugLogs)
                Debug.Log($"[ExtrusionDebug] finalized 3D path layer={activeLayerNumber} reason={reason} " +
                          $"totalSegments={segmentCount}");
            pathActive = false;
            hasPendingPoint = false;
            hasDirection = false;
            hasSamplePoint = false;
        }

        private void BeginPreviewSphere(Vector3 point, float width)
        {
            previewSpheres.Clear();
            CreatePreviewSphere(point, width);
            lastSamplePoint = point;
            hasSamplePoint = true;
        }

        private void AddPreviewSphereIfNeeded(Vector3 point, float width)
        {
            if (!hasSamplePoint || Vector3.Distance(lastSamplePoint, point) >= previewSampleStep)
            {
                CreatePreviewSphere(point, width);
                lastSamplePoint = point;
                hasSamplePoint = true;
            }
        }

        private void CreatePreviewSphere(Vector3 point, float width)
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = $"PreviewPoint_{previewSpheres.Count:D3}";
            sphere.transform.SetParent(filamentRoot, false);
            sphere.transform.localPosition = point;
            sphere.transform.localScale = Vector3.one * Mathf.Max(width, 0.000001f);
            ApplyMaterial(sphere, previewMaterial);
            RemoveCollider(sphere);
            previewSpheres.Add(sphere);
        }

        private void FinalizePreviewSegment(Vector3 endPoint)
        {
            if (!hasPendingPoint || previewSpheres.Count == 0) return;

            // 방향 전환/정지 직전의 실제 마지막 위치를 반드시 끝점으로 추가한다.
            if (Vector3.Distance(previewSpheres[previewSpheres.Count - 1].transform.localPosition, endPoint) >
                minSegmentLength)
            {
                CreatePreviewSphere(endPoint, pendingWidth);
            }

            if (previewSpheres.Count == 1)
            {
                ApplyMaterial(previewSpheres[0], filamentMaterial);
                previewSpheres.Clear();
                return;
            }

            var first = previewSpheres[0];
            var last = previewSpheres[previewSpheres.Count - 1];
            ApplyMaterial(first, filamentMaterial);
            ApplyMaterial(last, filamentMaterial);

            for (int i = previewSpheres.Count - 2; i > 0; i--)
            {
                Destroy(previewSpheres[i]);
            }

            Vector3 start = first.transform.localPosition;
            Vector3 end = last.transform.localPosition;
            CreateCylinder(start, end, pendingWidth, activeLayerNumber);
            previewSpheres.Clear();
        }

        private void CreateCylinder(Vector3 start, Vector3 end, float width, uint layer)
        {
            Vector3 delta = end - start;
            float length = delta.magnitude;
            if (length <= minSegmentLength) return;

            float diameter = Mathf.Max(width, 0.000001f);
            Vector3 direction = delta / length;
            var root = new GameObject($"FilamentSegment_{segmentCount:D4}_Layer_{layer}");
            root.transform.SetParent(filamentRoot, false);

            var cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cylinder.name = "Cylinder";
            cylinder.transform.SetParent(root.transform, false);
            cylinder.transform.localPosition = (start + end) * 0.5f;
            cylinder.transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
            // Unity 기본 Cylinder의 메시 반지름은 0.5이므로,
            // 원하는 실제 지름을 만들려면 X/Z 스케일에는 diameter를 사용한다.
            cylinder.transform.localScale = new Vector3(diameter, length * 0.5f, diameter);
            ApplyMaterial(cylinder, filamentMaterial);
            RemoveCollider(cylinder);
            segmentCount++;
        }

        private void ApplyMaterial(GameObject target, Material material)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.material = material;
        }

        private static void RemoveCollider(GameObject target)
        {
            var collider = target.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
        }

        private static Transform FindDeep(Transform root, string objectName)
        {
            if (root == null) return null;
            if (root.name == objectName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var result = FindDeep(root.GetChild(i), objectName);
                if (result != null) return result;
            }
            return null;
        }
    }
}
