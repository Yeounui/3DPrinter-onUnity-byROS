using UnityEngine;

public class GeometrySliderConstraint : MonoBehaviour
{
    public enum FrameTwist
    {
        Deg0 = 0,
        Deg90 = 90,
        Deg180 = 180,
        Deg270 = 270
    }

    [Header("Reference Frames")]
    public Transform fixedReferenceFrame;
    public Transform movingReferenceFrame;

    [Header("Orientation Correction")]
    public FrameTwist assemblyTwist = FrameTwist.Deg0;

    void Update()
    {
        if (fixedReferenceFrame == null ||
            movingReferenceFrame == null)
        {
            return;
        }

        float twistAngle =
            (float)assemblyTwist;

        Quaternion targetFrameRotation =
            fixedReferenceFrame.rotation *
            Quaternion.AngleAxis(
                twistAngle,
                Vector3.right
            );

        Quaternion rotationCorrection =
            targetFrameRotation *
            Quaternion.Inverse(
                movingReferenceFrame.rotation
            );

        transform.rotation =
            rotationCorrection *
            transform.rotation;

        Vector3 offset =
            movingReferenceFrame.position -
            fixedReferenceFrame.position;

        float constrainedUpDistance =
            Vector3.Dot(
                offset,
                fixedReferenceFrame.up
            );

        float constrainedForwardDistance =
            Vector3.Dot(
                offset,
                fixedReferenceFrame.forward
            );

        Vector3 positionCorrection =
            -fixedReferenceFrame.up *
                constrainedUpDistance
            -fixedReferenceFrame.forward *
                constrainedForwardDistance;

        transform.position +=
            positionCorrection;
    }
}
