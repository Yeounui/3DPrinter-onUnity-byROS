using UnityEngine;

public class NozzleFrustumCollisionData : MonoBehaviour
{
    public string generatedName;
    public FrustumAxis symmetryAxis;
    public float axialMinimum;
    public float axialMaximum;
    public Vector2 minimumCenter;
    public Vector2 maximumCenter;
    public float minimumCircleRadius;
    public float maximumCircleRadius;
    public float minimumOctagonVertexRadius;
    public float maximumOctagonVertexRadius;
    public int topGroup;
    public int bottomGroup;
    public bool minimumCircleUsedFallback;
    public bool maximumCircleUsedFallback;
    public Mesh generatedMesh;
}
