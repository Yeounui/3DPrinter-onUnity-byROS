using UnityEngine;

public class NozzleAxisHeightTest : MonoBehaviour
{
    [Header("References")]
    public Transform baseLink;
    public Transform selectionRoot;

    [Header("Validation")]
    [Range(0.1f, 0.999f)]
    public float axisAlignment = 0.85f;

    [Header("Selection A")]
    public MeshFilter meshFilterA;
    public int triangleIndexA = -1;

    [Header("Selection B")]
    public MeshFilter meshFilterB;
    public int triangleIndexB = -1;

    [Header("Side Selections (Experimental)")]
    public MeshFilter[] sideMeshFilters = new MeshFilter[6];
    public int[] sideTriangleIndices = { -1, -1, -1, -1, -1, -1 };

    public bool HasSelectionA => meshFilterA != null && triangleIndexA >= 0;
    public bool HasSelectionB => meshFilterB != null && triangleIndexB >= 0;

    private void OnValidate()
    {
        EnsureSideSelectionCapacity();
    }

    public void EnsureSideSelectionCapacity()
    {
        if (sideMeshFilters == null || sideMeshFilters.Length != 6)
        {
            MeshFilter[] resized = new MeshFilter[6];
            if (sideMeshFilters != null)
                System.Array.Copy(sideMeshFilters, resized, Mathf.Min(sideMeshFilters.Length, 6));
            sideMeshFilters = resized;
        }

        if (sideTriangleIndices == null || sideTriangleIndices.Length != 6)
        {
            int[] resized = { -1, -1, -1, -1, -1, -1 };
            if (sideTriangleIndices != null)
                System.Array.Copy(sideTriangleIndices, resized, Mathf.Min(sideTriangleIndices.Length, 6));
            sideTriangleIndices = resized;
        }
    }
}
