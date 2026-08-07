using UnityEngine;

namespace Voron24.Robot
{
    /// <summary>
    /// bed_origin 기준 노즐 위치를 계산한다.
    /// 압출 궤적 렌더링과 UI 좌표 표시가 이 값을 쓴다.
    ///
    /// URDF 의 nozzle / bed_origin 은 형상이 없는 프레임 전용 링크다 (계약 section 3).
    /// URDF-Importer 는 이들도 GameObject 로 만들어 주므로 Transform 으로 접근 가능.
    /// </summary>
    public class NozzleTracker : MonoBehaviour
    {
        [SerializeField] Transform robotRoot;
        [SerializeField] Transform nozzle;
        [SerializeField] Transform bedOrigin;

        [Header("Debug")]
        [SerializeField] bool logPosition = false;

        void Awake()
        {
            if (robotRoot == null) robotRoot = transform;
            if (nozzle == null)    nozzle    = FindDeep(robotRoot, "nozzle");
            if (bedOrigin == null) bedOrigin = FindDeep(robotRoot, "bed_origin");

            if (nozzle == null)
                Debug.LogError("[NozzleTracker] 'nozzle' 링크를 찾을 수 없습니다.");
            if (bedOrigin == null)
                Debug.LogError("[NozzleTracker] 'bed_origin' 링크를 찾을 수 없습니다.");
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

        /// <summary>베드 원점 기준 노즐 위치 [m], Unity 좌표계.</summary>
        public Vector3 NozzleInBed()
        {
            if (nozzle == null || bedOrigin == null) return Vector3.zero;
            return bedOrigin.InverseTransformPoint(nozzle.position);
        }

        /// <summary>G-code 좌표계 [mm]. UI 표시용.</summary>
        public Vector3 NozzleGcodeMm()
        {
            var p = NozzleInBed();
            return p * 1000f;
        }

        void Update()
        {
            if (logPosition && Time.frameCount % 60 == 0)
            {
                var g = NozzleGcodeMm();
                Debug.Log($"[Nozzle] X{g.x:F1} Y{g.y:F1} Z{g.z:F1} mm");
            }
        }
    }
}
