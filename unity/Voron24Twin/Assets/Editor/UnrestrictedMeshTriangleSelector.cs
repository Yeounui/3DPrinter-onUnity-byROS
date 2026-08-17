using UnityEditor;
using UnityEngine;

/// <summary>
/// Selects any readable MeshFilter triangle beneath a root transform.
/// This picker deliberately performs no normal/axis-angle validation.
/// </summary>
public static class UnrestrictedMeshTriangleSelector
{
    public static bool TryPick(
        Transform selectionRoot,
        Ray worldRay,
        out MeshFilter selectedMesh,
        out int selectedTriangle)
    {
        selectedMesh = null;
        selectedTriangle = -1;
        if (!selectionRoot)
            return false;

        float closestWorldDistance = float.PositiveInfinity;
        foreach (MeshFilter meshFilter in selectionRoot.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = meshFilter.sharedMesh;
            if (!mesh || !mesh.isReadable)
                continue;

            Renderer renderer = meshFilter.GetComponent<Renderer>();
            if (renderer && !renderer.bounds.IntersectRay(worldRay))
                continue;

            Matrix4x4 worldToLocal = meshFilter.transform.worldToLocalMatrix;
            Ray localRay = new Ray(
                worldToLocal.MultiplyPoint(worldRay.origin),
                worldToLocal.MultiplyVector(worldRay.direction).normalized
            );
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            for (int start = 0; start + 2 < triangles.Length; start += 3)
            {
                if (!RayTriangle(
                        localRay,
                        vertices[triangles[start]],
                        vertices[triangles[start + 1]],
                        vertices[triangles[start + 2]],
                        out float localDistance))
                {
                    continue;
                }

                Vector3 worldHit = meshFilter.transform.TransformPoint(
                    localRay.GetPoint(localDistance)
                );
                float worldDistance = Vector3.Distance(worldRay.origin, worldHit);
                if (worldDistance >= closestWorldDistance)
                    continue;

                closestWorldDistance = worldDistance;
                selectedMesh = meshFilter;
                selectedTriangle = start / 3;
            }
        }

        return selectedMesh != null;
    }

    public static void DrawTriangle(
        MeshFilter meshFilter,
        int triangleIndex,
        Color color)
    {
        if (!meshFilter || !meshFilter.sharedMesh)
            return;

        Mesh mesh = meshFilter.sharedMesh;
        int[] triangles = mesh.triangles;
        int start = triangleIndex * 3;
        if (start < 0 || start + 2 >= triangles.Length)
            return;

        Vector3 a = meshFilter.transform.TransformPoint(mesh.vertices[triangles[start]]);
        Vector3 b = meshFilter.transform.TransformPoint(mesh.vertices[triangles[start + 1]]);
        Vector3 c = meshFilter.transform.TransformPoint(mesh.vertices[triangles[start + 2]]);
        Color fill = color;
        fill.a = 0.3f;
        Handles.color = fill;
        Handles.DrawAAConvexPolygon(a, b, c);
        color.a = 0.95f;
        Handles.color = color;
        Handles.DrawLine(a, b);
        Handles.DrawLine(b, c);
        Handles.DrawLine(c, a);
    }

    private static bool RayTriangle(
        Ray ray,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        distance = 0f;
        Vector3 edge1 = b - a;
        Vector3 edge2 = c - a;
        Vector3 p = Vector3.Cross(ray.direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (Mathf.Abs(determinant) < 0.0000001f)
            return false;

        float inverse = 1f / determinant;
        Vector3 t = ray.origin - a;
        float u = Vector3.Dot(t, p) * inverse;
        if (u < 0f || u > 1f)
            return false;

        Vector3 q = Vector3.Cross(t, edge1);
        float v = Vector3.Dot(ray.direction, q) * inverse;
        if (v < 0f || u + v > 1f)
            return false;

        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f;
    }
}
