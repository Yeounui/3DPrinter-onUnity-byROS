using UnityEngine;

public enum FrustumAxis
{
    X,
    Y,
    Z
}

/// <summary>
/// Final output settings for the oblique circumscribed-octagon nozzle collider.
/// Face selections and the tested classification algorithm come from
/// NozzleAxisHeightTest.
/// </summary>
public class NozzleFrustumColliderGenerator : MonoBehaviour
{
    [Header("Tested Selection Source")]
    public NozzleAxisHeightTest selectionSource;

    [Header("References")]
    public Transform baseLink;
    public Transform collisionParent;

    [Header("Conservative Expansion")]
    [Min(0f)] public float radialOffset;
    [Min(0f)] public float axialOffset;

    [Header("Output")]
    public string namePrefix = "nozzle";
    [Min(0)] public int nextIndex;
    public bool showGeneratedMesh;

    private void Reset()
    {
        selectionSource = GetComponent<NozzleAxisHeightTest>();
    }

    private void OnValidate()
    {
        if (!selectionSource)
            selectionSource = GetComponent<NozzleAxisHeightTest>();
        radialOffset = Mathf.Max(0f, radialOffset);
        axialOffset = Mathf.Max(0f, axialOffset);
        nextIndex = Mathf.Max(0, nextIndex);
    }
}
