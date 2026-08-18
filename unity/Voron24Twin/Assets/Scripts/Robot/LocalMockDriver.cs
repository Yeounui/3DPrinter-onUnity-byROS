using UnityEngine;

namespace Voron24.Robot
{
    /// <summary>
    /// ROS 연결 없이 Unity 단독 조인트 구동.
    ///
    /// 용도:
    ///   - C가 아직 endpoint 안 올렸을 때 B 선행 작업
    ///   - ROS/Unity 문제 절분
    ///   - 데모 중 네트워크 단절 시 fallback
    ///
    /// ros2 mock_publisher와 동일 패턴/수식.
    /// (voron24_gcode/patterns.py와 동기화 필수)
    /// </summary>
    public class LocalMockDriver : MonoBehaviour
    {
        public enum Pattern { Home, Sweep, Square, Lissajous }

        [SerializeField] JointStateSubscriber subscriber;
        [SerializeField] Pattern pattern = Pattern.Sweep;
        [SerializeField] Vector3 stroke = new Vector3(0.250f, 0.250f, 0.250f);
        [SerializeField] float margin = 0.010f;
        [SerializeField] float period = 12f;

        [Tooltip("ROS 연결 시 자동 비활성화")]
        [SerializeField] bool yieldToRos = true;

        float _t;

        void Update()
        {
            if (yieldToRos && subscriber != null && subscriber.IsConnected) return;
            _t += Time.deltaTime;
            var p = Compute(_t);
            Apply(p);
        }

        Vector3 Compute(float t)
        {
            float m = margin;
            float ax = stroke.x - 2 * m, ay = stroke.y - 2 * m;

            switch (pattern)
            {
                case Pattern.Home:
                    return Vector3.zero;

                case Pattern.Sweep:
                {
                    float seg = period / 3f;
                    float phase = (t % period) / seg;
                    int k = Mathf.FloorToInt(phase);
                    float u = phase - k;
                    float tri = 1f - Mathf.Abs(2f * u - 1f);
                    if (k == 0) return new Vector3(m + ax * tri, m, 0f);
                    if (k == 1) return new Vector3(m, m + ay * tri, 0f);
                    return new Vector3(m, m, stroke.z * tri);
                }

                case Pattern.Square:
                {
                    float u = (t % period) / period * 4f;
                    int k = Mathf.FloorToInt(u);
                    float v = u - k;
                    float x0 = m, y0 = m, x1 = stroke.x - m, y1 = stroke.y - m;
                    float x, y;
                    if (k == 0)      { x = Mathf.Lerp(x0, x1, v); y = y0; }
                    else if (k == 1) { x = x1; y = Mathf.Lerp(y0, y1, v); }
                    else if (k == 2) { x = Mathf.Lerp(x1, x0, v); y = y1; }
                    else             { x = x0; y = Mathf.Lerp(y1, y0, v); }
                    float layer = Mathf.Floor(t / period);
                    return new Vector3(x, y, Mathf.Min(0.0002f + layer * 0.0002f, stroke.z));
                }

                default:
                {
                    float w = 2f * Mathf.PI / period;
                    return new Vector3(
                        stroke.x / 2f + (ax / 2f) * Mathf.Sin(w * t),
                        stroke.y / 2f + (ay / 2f) * Mathf.Sin(w * t * 0.618f),
                        0.010f + 0.010f * (1f + Mathf.Sin(w * t * 0.13f)));
                }
            }
        }

        void Apply(Vector3 p)
        {
            p.x = Mathf.Clamp(p.x, 0f, stroke.x);
            p.y = Mathf.Clamp(p.y, 0f, stroke.y);
            p.z = Mathf.Clamp(p.z, 0f, stroke.z);
            LocalTarget = p;

            if (subscriber == null) return;
            subscriber.SetTargetExternal("joint_x", p.x);
            subscriber.SetTargetExternal("joint_y", p.y);
            subscriber.SetTargetExternal("joint_z", p.z);
        }

        /// <summary>현재 목표 위치 [m]. UI/디버그용.</summary>
        public Vector3 LocalTarget { get; private set; }

        void Reset()
        {
            subscriber = GetComponent<JointStateSubscriber>();
        }
    }
}
