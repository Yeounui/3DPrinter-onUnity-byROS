using UnityEngine;

public class GeometryReference : MonoBehaviour
{
    [Header("Reference Frame")]
    [Tooltip("GeometryReference가 생성/관리하는 ReferenceFrame 자식 오브젝트 이름")]
    public string referenceFrameName = "ReferenceFrame";

    [Header("Surface A")]
    public MeshFilter selectedMeshFilterA;
    public int seedTriangleIndexA = -1;
    public int[] regionTriangleIndicesA;
    public Vector3 localPointA;
    public Vector3 localNormalA;
    public bool hasSelectionA;

    [Header("Surface B")]
    public MeshFilter selectedMeshFilterB;
    public int seedTriangleIndexB = -1;
    public int[] regionTriangleIndicesB;
    public Vector3 localPointB;
    public Vector3 localNormalB;
    public bool hasSelectionB;

    public Vector3 WorldPointA =>
        (!hasSelectionA || selectedMeshFilterA == null)
            ? Vector3.zero
            : selectedMeshFilterA.transform.TransformPoint(localPointA);

    public Vector3 WorldNormalA =>
        (!hasSelectionA || selectedMeshFilterA == null)
            ? Vector3.zero
            : selectedMeshFilterA.transform.TransformDirection(localNormalA).normalized;

    public Vector3 WorldPointB =>
        (!hasSelectionB || selectedMeshFilterB == null)
            ? Vector3.zero
            : selectedMeshFilterB.transform.TransformPoint(localPointB);

    public Vector3 WorldNormalB =>
        (!hasSelectionB || selectedMeshFilterB == null)
            ? Vector3.zero
            : selectedMeshFilterB.transform.TransformDirection(localNormalB).normalized;

    public Vector3 WorldAxis
    {
        get
        {
            if (!hasSelectionA || !hasSelectionB)
                return Vector3.zero;

            Vector3 axis = Vector3.Cross(WorldNormalA, WorldNormalB);

            return axis.sqrMagnitude < 0.000001f
                ? Vector3.zero
                : axis.normalized;
        }
    }
}
