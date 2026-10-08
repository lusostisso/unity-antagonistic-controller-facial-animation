using System.Collections;
using System.Data.Common;
using UnityEngine;
using Voxus.Random;

[DefaultExecutionOrder(50)]
public class FaceController : MonoBehaviour
{
    [SerializeField]
    private AttentionController attentionController; //trocar pela tua classe VLM
    [SerializeField]
    private Animator faceAnimator;
    [SerializeField]
    private SkinnedMeshRenderer faceMeshRenderer;

    [Header("Eyes Settings")]
    [SerializeField]
    private Transform leftEyeTransform;
    [SerializeField]
    private Transform rightEyeTransform;
    [SerializeField]
    private float eyeXUpRotationLimit = 55f;
    [SerializeField]
    private float eyeXDownRotationLimit = 55f;
    [SerializeField]
    private float eyeYRotationLimit = 55f;
    // Retain serialized legacy roll values, but the gaze driver now adds pitch/yaw only.
    [SerializeField, HideInInspector]
    private float eyeZRotationLimit = 55f;
    [SerializeField]
    private float eyeXComfortableRotationLimit = 25f;
    [SerializeField]
    private float eyeYComfortableRotationLimit = 25f;
    [SerializeField, HideInInspector]
    private float eyeZComfortableRotationLimit = 25f;
    [SerializeField]
    private float eyeSaccadeSpeed = 13.9626f; //800 degrees in radians;
    [SerializeField]
    private float eyePursuitSpeed = 1.74533f; //100 degrees in radians;
    [SerializeField]
    private float moveHeadFixationTime = 0.1f; // Minimum fixation time required for agent to move head towards target

    #region Blendshapes
    private const int BrowOuterUpLeftBlendShapeIndex = 0;
    private const int BrowOuterUpRightBlendShapeIndex = 1;
    private const int EyeSquintLeftBlendShapeIndex = 2;
    private const int EyeSquintRightBlendShapeIndex = 3;
    private const int EyeLookInLeftBlendShapeIndex = 4;
    private const int EyeLookOutLeftBlendShapeIndex = 5;
    private const int EyeLookInRightBlendShapeIndex = 6;
    private const int EyeLookOutRightBlendShapeIndex = 7;
    private const int EyeLookUpLeftBlendShapeIndex = 8;
    private const int EyeLookUpRightBlendShapeIndex = 9;
    private const int EyeLookDownLeftBlendShapeIndex = 10;
    private const int EyeLookDownRightBlendShapeIndex = 11;
    private const int CheekPuffBlendShapeIndex = 12;
    private const int CheekSquintLeftBlendShapeIndex = 13;
    private const int CheekSquintRightBlendShapeIndex = 14;
    private const int NoseSneerLeftBlendShapeIndex = 15;
    private const int NoseSneerRightBlendShapeIndex = 16;
    private const int MouthLeftBlendShapeIndex = 17;
    private const int MouthRightBlendShapeIndex = 18;
    private const int MouthPuckerBlendShapeIndex = 19;
    private const int MouthFunnelBlendShapeIndex = 20;
    private const int MouthSmileLeftBlendShapeIndex = 21;
    private const int MouthSmileRightBlendShapeIndex = 22;
    private const int MouthFrownLeftBlendShapeIndex = 23;
    private const int MouthFrownRightBlendShapeIndex = 24;
    private const int MouthDimpleLeftBlendShapeIndex = 25;
    private const int MouthDimpleRightBlendShapeIndex = 26;
    private const int MouthPressLeftBlendShapeIndex = 27;
    private const int MouthPressRightBlendShapeIndex = 28;
    private const int MouthShrugLowerBlendShapeIndex = 29;
    private const int MouthShrugUpperBlendShapeIndex = 30;
    private const int MouthStretchLeftBlendShapeIndex = 31;
    private const int MouthStretchRightBlendShapeIndex = 32;
    private const int MouthUpperUpLeftBlendShapeIndex = 33;
    private const int MouthUpperUpRightBlendShapeIndex = 34;
    private const int MouthLowerDownLeftBlendShapeIndex = 35;
    private const int MouthLowerDownRightBlendShapeIndex = 36;
    private const int MouthRollUpperBlendShapeIndex = 37;
    private const int MouthRollLowerBlendShapeIndex = 38;
    private const int MouthClosedBlendShapeIndex = 39;
    private const int JawForwardBlendShapeIndex = 40;
    private const int JawOpenBlendShapeIndex = 41;
    private const int JawLeftBlendShapeIndex = 42;
    private const int JawRightBlendShapeIndex = 43;
    private const int BrowInnerUpBlendShapeIndex = 44;
    private const int EyeBlinkingRightBlendShapeIndex = 45;
    private const int EyeBlinkingLeftBlendShapeIndex = 46;
    private const int BrowDownLeftBlendShapeIndex = 47;
    private const int BrowDownRightBlendShapeIndex = 48;
    private const int EyeWideRightBlendShapeIndex = 49;
    private const int EyeWideLeftBlendShapeIndex = 50;
    private const int TongueJawOpenBlendShapeIndex = 51;
    private const int TongueJawForwardBlendShapeIndex = 52;
    private const int TongueJawLeftBlendShapeIndex = 53;
    private const int TongueJawRightBlendShapeIndex = 54;
    private const int TongueOutBlendShapeIndex = 55;

    #endregion

    [SerializeField]
    private float blinkIntervalMin = 0.1f,  blinkIntervalMax = 1f;

    [Header("Neck Settings")]
    [SerializeField]
    private Transform headTransform;
    [SerializeField]
    private Transform neckTransform;
    [SerializeField]
    private float neckXRotationLimit = 70f;
    [SerializeField]
    private float neckYRotationLimit = 70f;
    [SerializeField, HideInInspector]
    private float neckZRotationLimit = 30f;
    [SerializeField]
    private float neckMovementSpeed = 3.14159f; // 180 degrees in radians

    [Header("Safety Regions Settings")]
    [SerializeField]
    private FaceSafetyRegion faceSafetyRegionLeft;
    [SerializeField]
    private FaceSafetyRegion faceSafetyRegionRight;
    [SerializeField]
    private float minEyeDistance = 0.1f; // Minimum distance to eye needed to start animating squint blendshape
    private float maxEyeDistance = 0.05f; // Maximum eye distance for squint blendshape (we can change dynamically after)
    private Vector3 initialNeckForward;
    private Quaternion initialLeftEyeRotation = Quaternion.identity, initialRightEyeRotation = Quaternion.identity;
    private Quaternion initialNeckRotation = Quaternion.identity, initialHeadRotation = Quaternion.identity;
    private Quaternion neckToHeadRestRotation = Quaternion.identity;
    // Keep our smooth offsets separately: Animator may reset bone transforms before each LateUpdate.
    private Vector2 leftEyeAngles, rightEyeAngles, neckAngles, headAngles;

    // Manual override used by external drivers (e.g. VLM-based gaze) to bypass the attention controller
    private bool manualGazeActive = false;
    private Vector3 manualGazeDirection = Vector3.forward;

    public Vector3 InitialNeckForward => initialNeckForward;
    public AttentionController AttentionSource => attentionController;
    public Transform LeftEye => leftEyeTransform;
    public Transform RightEye => rightEyeTransform;
    public Transform Neck => neckTransform;
    public Transform Head => headTransform;
    public Vector3 NeutralLeftEyeLocalEuler => initialLeftEyeRotation.eulerAngles;
    public Vector3 NeutralRightEyeLocalEuler => initialRightEyeRotation.eulerAngles;
    public Vector3 NeutralNeckLocalEuler => initialNeckRotation.eulerAngles;
    public Vector3 GazeOrigin => leftEyeTransform != null && rightEyeTransform != null
        ? (leftEyeTransform.position + rightEyeTransform.position) / 2f : transform.position;
    public Vector3 NeutralNeckForward => NeutralWorldRotation(neckTransform, initialNeckRotation) * Vector3.forward;
    public Vector2 LeftEyeGazeAngles => leftEyeAngles;
    public Vector2 RightEyeGazeAngles => rightEyeAngles;
    public Vector2 NeckGazeAngles => neckAngles;
    public Vector3 LegacyRollLimits => new Vector3(eyeZRotationLimit, eyeZComfortableRotationLimit, neckZRotationLimit);

    public void SetAttentionController(AttentionController controller)
    {
        attentionController = controller;
        ClearManualGaze();
    }

    public void ClearManualGaze() => manualGazeActive = false;

    public void SetManualGazeDirection(Vector3 worldDirection)
    {
        manualGazeActive = true;
        manualGazeDirection = worldDirection;
    }


    private void Awake()
    {
        if (leftEyeTransform != null) initialLeftEyeRotation = leftEyeTransform.localRotation;
        if (rightEyeTransform != null) initialRightEyeRotation = rightEyeTransform.localRotation;
        if (neckTransform != null)
        {
            initialNeckForward = neckTransform.forward;
            initialNeckRotation = neckTransform.localRotation;
        }
        if (headTransform != null) initialHeadRotation = headTransform.localRotation;
        if (neckTransform != null && headTransform != null)
            neckToHeadRestRotation = GazeRotationMath.Inverse(neckTransform.rotation) * headTransform.rotation;
    }

    private void Start()
    {
        if (faceAnimator != null) StartCoroutine(Blink());
    }

    private void LateUpdate()
    {
        if (neckTransform == null || leftEyeTransform == null || rightEyeTransform == null) return;
        // Reapply the previous offsets on the body's CURRENT animated pose before calculating the next step.
        ApplyControlledPose();
        if (manualGazeActive)
        {
            if (ValidDirection(manualGazeDirection))
            {
                MoveBone(neckTransform, initialNeckRotation, ref neckAngles, manualGazeDirection,
                    neckMovementSpeed / 2f, neckXRotationLimit, neckXRotationLimit, neckYRotationLimit);
                FollowNeck();
                MoveBone(leftEyeTransform, initialLeftEyeRotation, ref leftEyeAngles, manualGazeDirection,
                    eyeSaccadeSpeed, eyeXUpRotationLimit, eyeXDownRotationLimit, eyeYRotationLimit);
                MoveBone(rightEyeTransform, initialRightEyeRotation, ref rightEyeAngles, manualGazeDirection,
                    eyeSaccadeSpeed, eyeXUpRotationLimit, eyeXDownRotationLimit, eyeYRotationLimit);
            }
            else RecenterGaze();
            AnimateGazeBlendShapes();
            return;
        }

        FixationObject currentObjectOfInterest = attentionController != null && attentionController.isActiveAndEnabled
            ? attentionController.GetCurrentFocus() : null;
        if (currentObjectOfInterest == null || currentObjectOfInterest.gameObject == null)
        {
            RecenterGaze();
            AnimateGazeBlendShapes();
            return;
        }

        Vector3 target = currentObjectOfInterest.GetFixationPoint();
        if (!ValidDirection(target - GazeOrigin))
        {
            RecenterGaze();
            AnimateGazeBlendShapes();
            return;
        }

        Vector2 leftRequired = RequiredAngles(leftEyeTransform, initialLeftEyeRotation, target - leftEyeTransform.position);
        Vector2 rightRequired = RequiredAngles(rightEyeTransform, initialRightEyeRotation, target - rightEyeTransform.position);
        if ((OutsideEyeComfort(leftRequired) || OutsideEyeComfort(rightRequired)) &&
            attentionController.GetCurrentFixationTime() > moveHeadFixationTime)
            MoveBone(neckTransform, initialNeckRotation, ref neckAngles, target - neckTransform.position,
                neckMovementSpeed / 2f, neckXRotationLimit, neckXRotationLimit, neckYRotationLimit);

        // Parents FIRST. Rotating the neck/head after the eyes would move the eyes off their target again.
        FollowNeck();
        float eyeMovementSpeed = GetEyeMovementSpeed(target);
        MoveBone(leftEyeTransform, initialLeftEyeRotation, ref leftEyeAngles, target - leftEyeTransform.position,
            eyeMovementSpeed, eyeXUpRotationLimit, eyeXDownRotationLimit, eyeYRotationLimit);
        MoveBone(rightEyeTransform, initialRightEyeRotation, ref rightEyeAngles, target - rightEyeTransform.position,
            eyeMovementSpeed, eyeXUpRotationLimit, eyeXDownRotationLimit, eyeYRotationLimit);
        AnimateGazeBlendShapes();
    }

    private void RecenterGaze()
    {
        float eyeStep = eyeSaccadeSpeed * Mathf.Rad2Deg * Time.deltaTime;
        float neckStep = neckMovementSpeed * Mathf.Rad2Deg * Time.deltaTime;
        leftEyeAngles = GazeRotationMath.Step(leftEyeAngles, Vector2.zero, eyeStep);
        rightEyeAngles = GazeRotationMath.Step(rightEyeAngles, Vector2.zero, eyeStep);
        neckAngles = GazeRotationMath.Step(neckAngles, Vector2.zero, neckStep / 2f);
        headAngles = GazeRotationMath.Step(headAngles, Vector2.zero, neckStep / 1.5f);
        ApplyControlledPose();
    }

    private void ApplyControlledPose()
    {
        neckTransform.localRotation = GazeRotationMath.LocalRotation(initialNeckRotation, neckAngles);
        if (headTransform != null) headTransform.localRotation = GazeRotationMath.LocalRotation(initialHeadRotation, headAngles);
        leftEyeTransform.localRotation = GazeRotationMath.LocalRotation(initialLeftEyeRotation, leftEyeAngles);
        rightEyeTransform.localRotation = GazeRotationMath.LocalRotation(initialRightEyeRotation, rightEyeAngles);
    }

    private static Quaternion NeutralWorldRotation(Transform bone, Quaternion neutral)
    {
        return (bone != null && bone.parent != null ? bone.parent.rotation : Quaternion.identity) * neutral;
    }

    private static Vector2 RequiredAngles(Transform bone, Quaternion neutral, Vector3 worldDirection)
    {
        return GazeRotationMath.Angles(GazeRotationMath.Inverse(NeutralWorldRotation(bone, neutral)) * worldDirection);
    }

    private void MoveBone(Transform bone, Quaternion neutral, ref Vector2 angles, Vector3 direction,
        float radiansPerSecond, float upLimit, float downLimit, float yawLimit)
    {
        if (bone == null || !ValidDirection(direction)) return;
        Vector2 desired = GazeRotationMath.Clamp(RequiredAngles(bone, neutral, direction), upLimit, downLimit, yawLimit);
        angles = GazeRotationMath.Step(angles, desired, Mathf.Max(0, radiansPerSecond) * Mathf.Rad2Deg * Time.deltaTime);
        bone.localRotation = GazeRotationMath.LocalRotation(neutral, angles);
    }

    private void FollowNeck()
    {
        if (headTransform == null) return;
        if (headTransform.IsChildOf(neckTransform))
        {
            // The child's world pose ALREADY follows its neck. Keep its own bind offset, don't apply the neck twice.
            headAngles = GazeRotationMath.Step(headAngles, Vector2.zero, neckMovementSpeed * Mathf.Rad2Deg / 1.5f * Time.deltaTime);
            headTransform.localRotation = GazeRotationMath.LocalRotation(initialHeadRotation, headAngles);
        }
        else
            MoveBone(headTransform, initialHeadRotation, ref headAngles,
                neckTransform.rotation * neckToHeadRestRotation * Vector3.forward,
                neckMovementSpeed / 1.5f, neckXRotationLimit, neckXRotationLimit, neckYRotationLimit);
    }

    private bool OutsideEyeComfort(Vector2 angles) => Mathf.Abs(angles.x) > eyeXComfortableRotationLimit ||
        Mathf.Abs(angles.y) > eyeYComfortableRotationLimit;

    private static bool ValidDirection(Vector3 direction) => direction.sqrMagnitude > 0.000001f &&
        !(float.IsNaN(direction.x) || float.IsInfinity(direction.x) || float.IsNaN(direction.y) ||
          float.IsInfinity(direction.y) || float.IsNaN(direction.z) || float.IsInfinity(direction.z));

    public bool CanReachGazeTarget(Vector3 point, float minimumDistance, out string reason)
    {
        Vector3 direction = point - GazeOrigin;
        if (!ValidDirection(direction) || direction.magnitude < minimumDistance)
        {
            reason = "Target too close to the eyes or invalid.";
            return false;
        }
        Vector3 local = GazeRotationMath.Inverse(NeutralWorldRotation(neckTransform, initialNeckRotation)) * direction;
        Vector2 required = GazeRotationMath.Angles(local);
        if (local.z <= 0)
        {
            reason = "Target is now behind the neutral gaze plane.";
            return false;
        }
        if (Mathf.Abs(required.y) > neckYRotationLimit + eyeYRotationLimit ||
            required.x < -(neckXRotationLimit + eyeXUpRotationLimit) ||
            required.x > neckXRotationLimit + eyeXDownRotationLimit)
        {
            reason = $"Target outside rig reach: pitch={required.x:F1}, yaw={required.y:F1} degrees.";
            return false;
        }
        reason = null;
        return true;
    }

    private float GetEyeMovementSpeed(Vector3 fixationTarget)
    {
        Ray r = new Ray(leftEyeTransform.position, leftEyeTransform.forward);
        var closestPointToTarget = UnityExtensions.RayExt.ClosestPointAlongRay(r, fixationTarget);
        float distanceToTarget = Vector3.Distance(closestPointToTarget, fixationTarget);
        return distanceToTarget < 0.1f ? eyePursuitSpeed : eyeSaccadeSpeed; 
    }

    private void AnimateGazeBlendShapes()
    {
        if (faceMeshRenderer == null) return;
        // Clear both directions before setting the active one; never leave the opposite gaze weight behind.
        for (int index = EyeLookInLeftBlendShapeIndex; index <= EyeLookDownRightBlendShapeIndex; index++)
            faceMeshRenderer.SetBlendShapeWeight(index, 0f);
        float xLeftEyeRotation = leftEyeAngles.x;
        float yLeftEyeRotation = leftEyeAngles.y;
        int xLeftEyeBlendShapeIndex = Mathf.Sign(xLeftEyeRotation) < 0 ? EyeLookUpLeftBlendShapeIndex : EyeLookDownLeftBlendShapeIndex;
        int yLeftEyeBlendShapeIndex = Mathf.Sign(yLeftEyeRotation) < 0 ? EyeLookOutLeftBlendShapeIndex : EyeLookInLeftBlendShapeIndex;

        float xRightEyeRotation = rightEyeAngles.x;
        float yRightEyeRotation = rightEyeAngles.y;
        int xRightEyeBlendShapeIndex = Mathf.Sign(xRightEyeRotation) < 0 ? EyeLookUpRightBlendShapeIndex : EyeLookDownRightBlendShapeIndex;
        int yRightEyeBlendShapeIndex = Mathf.Sign(yRightEyeRotation) < 0 ? EyeLookOutRightBlendShapeIndex : EyeLookInRightBlendShapeIndex;

        faceMeshRenderer.SetBlendShapeWeight(xLeftEyeBlendShapeIndex, GazeBlendshapeWeight(xLeftEyeRotation,
            xLeftEyeRotation < 0 ? eyeXUpRotationLimit : eyeXDownRotationLimit));
        faceMeshRenderer.SetBlendShapeWeight(yLeftEyeBlendShapeIndex, GazeBlendshapeWeight(yLeftEyeRotation, eyeYRotationLimit));
        faceMeshRenderer.SetBlendShapeWeight(xRightEyeBlendShapeIndex, GazeBlendshapeWeight(xRightEyeRotation,
            xRightEyeRotation < 0 ? eyeXUpRotationLimit : eyeXDownRotationLimit));
        faceMeshRenderer.SetBlendShapeWeight(yRightEyeBlendShapeIndex, GazeBlendshapeWeight(yRightEyeRotation, eyeYRotationLimit));
    }

    private static float GazeBlendshapeWeight(float angle, float limit) =>
        limit > 0 ? Mathf.Clamp01(Mathf.Abs(angle) / limit) * 100 : 0;

    private void AnimateSquintBlendShapes()
    {
        if (faceSafetyRegionLeft.closestObstacle == null)
        {
            faceMeshRenderer.SetBlendShapeWeight(EyeSquintLeftBlendShapeIndex, 0f);
            faceMeshRenderer.SetBlendShapeWeight(EyeBlinkingLeftBlendShapeIndex, 0f);
            faceMeshRenderer.SetBlendShapeWeight(BrowDownLeftBlendShapeIndex, 0f);
            //faceMeshRenderer.SetBlendShapeWeight(MouthUpperUpLeftBlendShapeIndex, 0f);
            faceMeshRenderer.SetBlendShapeWeight(MouthSmileLeftBlendShapeIndex, 0f);
            faceAnimator.enabled = true;
        }
        if (faceSafetyRegionRight.closestObstacle == null)
        {
            faceMeshRenderer.SetBlendShapeWeight(EyeSquintRightBlendShapeIndex, 0f);
            faceMeshRenderer.SetBlendShapeWeight(EyeBlinkingRightBlendShapeIndex, 0f);
            faceMeshRenderer.SetBlendShapeWeight(BrowDownRightBlendShapeIndex, 0f);
            //faceMeshRenderer.SetBlendShapeWeight(MouthUpperUpRightBlendShapeIndex, 0f);
            faceMeshRenderer.SetBlendShapeWeight(MouthSmileRightBlendShapeIndex, 0f);
            faceAnimator.enabled = true;
        }
        if (faceSafetyRegionLeft.closestDistanceToEye < minEyeDistance)
        {
            faceAnimator.enabled = false;
            faceMeshRenderer.SetBlendShapeWeight(EyeSquintLeftBlendShapeIndex, 100f - NormalizeBlendshapeValue(faceSafetyRegionLeft.closestDistanceToEye, minEyeDistance, maxEyeDistance));
            faceMeshRenderer.SetBlendShapeWeight(EyeBlinkingLeftBlendShapeIndex, (100f - NormalizeBlendshapeValue(faceSafetyRegionLeft.closestDistanceToEye, minEyeDistance, maxEyeDistance))/2);
            faceMeshRenderer.SetBlendShapeWeight(BrowDownLeftBlendShapeIndex, 100f - NormalizeBlendshapeValue(faceSafetyRegionLeft.closestDistanceToEye, minEyeDistance, maxEyeDistance));
            //faceMeshRenderer.SetBlendShapeWeight(MouthUpperUpLeftBlendShapeIndex, (100f - NormalizeBlendshapeValue(faceSafetyRegionLeft.closestDistanceToEye, minEyeDistance, maxEyeDistance))/2);
            faceMeshRenderer.SetBlendShapeWeight(MouthSmileLeftBlendShapeIndex, (100f - NormalizeBlendshapeValue(faceSafetyRegionLeft.closestDistanceToEye, minEyeDistance, maxEyeDistance))/2);
        }
        if (faceSafetyRegionRight.closestDistanceToEye < minEyeDistance)
        {
            faceAnimator.enabled = false;
            faceMeshRenderer.SetBlendShapeWeight(EyeSquintRightBlendShapeIndex, 100f - NormalizeBlendshapeValue(faceSafetyRegionRight.closestDistanceToEye, minEyeDistance, maxEyeDistance));
            faceMeshRenderer.SetBlendShapeWeight(EyeBlinkingRightBlendShapeIndex, (100f - NormalizeBlendshapeValue(faceSafetyRegionRight.closestDistanceToEye, minEyeDistance, maxEyeDistance))/2);
            faceMeshRenderer.SetBlendShapeWeight(BrowDownRightBlendShapeIndex, 100f - NormalizeBlendshapeValue(faceSafetyRegionRight.closestDistanceToEye, minEyeDistance, maxEyeDistance));
            //faceMeshRenderer.SetBlendShapeWeight(MouthUpperUpRightBlendShapeIndex, (100f - NormalizeBlendshapeValue(faceSafetyRegionRight.closestDistanceToEye, minEyeDistance, maxEyeDistance))/2);
            faceMeshRenderer.SetBlendShapeWeight(MouthSmileRightBlendShapeIndex, (100f - NormalizeBlendshapeValue(faceSafetyRegionRight.closestDistanceToEye, minEyeDistance, maxEyeDistance))/2);
        }
    }

    private float NormalizeBlendshapeValue(float value, float max, float min=0)
    {
        return 100 * Mathf.Abs(value - min)/(max - min);
    }

    private IEnumerator Blink()
    {
        float blinkInterval = UnityEngine.Random.Range(blinkIntervalMin, blinkIntervalMax);
        //Debug.Log("Blinking: " + blinkInterval);
        yield return new WaitForSeconds(blinkInterval);
        faceAnimator.Play("Blinking");
        yield return Blink();
        yield return null;
    }
}
