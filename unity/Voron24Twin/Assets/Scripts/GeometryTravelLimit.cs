using UnityEngine;

public class GeometryTravelLimit : MonoBehaviour
{
    [Header("Reference Frame")]
    [Tooltip("Slider 자유축은 이 ReferenceFrame의 local X(right)를 사용함")]
    public Transform fixedReferenceFrame;

    [Header("Rail End A")]
    public MeshFilter railEndAMeshFilter;
    public Vector3 railEndALocalPoint;
    public Vector3 railEndALocalNormal;
    public bool hasRailEndA;

    [Header("Rail End B")]
    public MeshFilter railEndBMeshFilter;
    public Vector3 railEndBLocalPoint;
    public Vector3 railEndBLocalNormal;
    public bool hasRailEndB;

    [Header("Block End A")]
    public MeshFilter blockEndAMeshFilter;
    public Vector3 blockEndALocalPoint;
    public Vector3 blockEndALocalNormal;
    public bool hasBlockEndA;

    [Header("Block End B")]
    public MeshFilter blockEndBMeshFilter;
    public Vector3 blockEndBLocalPoint;
    public Vector3 blockEndBLocalNormal;
    public bool hasBlockEndB;

    public Vector3 RailEndAWorldPoint
    {
        get
        {
            if (!hasRailEndA || railEndAMeshFilter == null)
                return Vector3.zero;

            return railEndAMeshFilter.transform
                .TransformPoint(railEndALocalPoint);
        }
    }

    public Vector3 RailEndBWorldPoint
    {
        get
        {
            if (!hasRailEndB || railEndBMeshFilter == null)
                return Vector3.zero;

            return railEndBMeshFilter.transform
                .TransformPoint(railEndBLocalPoint);
        }
    }

    public Vector3 BlockEndAWorldPoint
    {
        get
        {
            if (!hasBlockEndA || blockEndAMeshFilter == null)
                return Vector3.zero;

            return blockEndAMeshFilter.transform
                .TransformPoint(blockEndALocalPoint);
        }
    }

    public Vector3 BlockEndBWorldPoint
    {
        get
        {
            if (!hasBlockEndB || blockEndBMeshFilter == null)
                return Vector3.zero;

            return blockEndBMeshFilter.transform
                .TransformPoint(blockEndBLocalPoint);
        }
    }

    public bool HasAllSurfaces
    {
        get
        {
            return
                fixedReferenceFrame != null &&
                hasRailEndA &&
                hasRailEndB &&
                hasBlockEndA &&
                hasBlockEndB &&
                railEndAMeshFilter != null &&
                railEndBMeshFilter != null &&
                blockEndAMeshFilter != null &&
                blockEndBMeshFilter != null;
        }
    }

    public string GetValidationStatus()
    {
        var missing = new System.Collections.Generic.List<string>();

        if (fixedReferenceFrame == null)
            missing.Add("FixedReferenceFrame");
        if (!hasRailEndA)
            missing.Add("RailEndA 선택 플래그");
        if (railEndAMeshFilter == null)
            missing.Add("RailEndA MeshFilter");
        if (!hasRailEndB)
            missing.Add("RailEndB 선택 플래그");
        if (railEndBMeshFilter == null)
            missing.Add("RailEndB MeshFilter");
        if (!hasBlockEndA)
            missing.Add("BlockEndA 선택 플래그");
        if (blockEndAMeshFilter == null)
            missing.Add("BlockEndA MeshFilter");
        if (!hasBlockEndB)
            missing.Add("BlockEndB 선택 플래그");
        if (blockEndBMeshFilter == null)
            missing.Add("BlockEndB MeshFilter");

        if (missing.Count == 0)
            return "검증 실패 원인을 확인할 수 없습니다.";

        return "누락 항목: " + string.Join(", ", missing);
    }

    public bool TryGetProjectedLimits(
        out float railMin,
        out float railMax,
        out float blockMin,
        out float blockMax
    )
    {
        railMin = 0.0f;
        railMax = 0.0f;
        blockMin = 0.0f;
        blockMax = 0.0f;

        if (!HasAllSurfaces)
            return false;

        Vector3 axis =
            fixedReferenceFrame.right.normalized;

        float railA =
            Vector3.Dot(
                RailEndAWorldPoint,
                axis
            );

        float railB =
            Vector3.Dot(
                RailEndBWorldPoint,
                axis
            );

        float blockA =
            Vector3.Dot(
                BlockEndAWorldPoint,
                axis
            );

        float blockB =
            Vector3.Dot(
                BlockEndBWorldPoint,
                axis
            );

        railMin =
            Mathf.Min(
                railA,
                railB
            );

        railMax =
            Mathf.Max(
                railA,
                railB
            );

        blockMin =
            Mathf.Min(
                blockA,
                blockB
            );

        blockMax =
            Mathf.Max(
                blockA,
                blockB
            );

        return true;
    }
}
