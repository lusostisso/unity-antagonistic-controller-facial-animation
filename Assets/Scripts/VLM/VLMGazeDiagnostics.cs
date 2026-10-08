using System;
using UnityEngine;

/// <summary>One received frame plus current tracking/rig state; never drives the gaze.</summary>
[Serializable]
public sealed class VLMGazeDiagnostics
{
    [Serializable]
    public sealed class Intersection
    {
        public GameObject candidate;
        public int instanceId;
        public string hierarchyPath, layer, rendererTypes;
        public float distance;
        public Bounds historicalBounds;
        public Vector3 historicalHitPoint;
    }

    public bool hasResponse, found, associated, currentFocusActive;
    public long requestId;
    public int generation, captureFrame, imageWidth, imageHeight;
    public int candidateCount, particleCandidateCount, eligibleLayerMask, intersectionCount;
    public string captureCamera, imageSha256, responseJson, status, matchedObjectPath;
    public float responseAgeSeconds;
    public Vector2 viewportPoint, imagePixelTopLeft, rayReprojectionErrorPixels;
    public Vector3 capturePosition;
    public Quaternion captureRotation;
    public Matrix4x4 captureView, captureProjection;
    public Vector3 rayOrigin, rayDirection;
    public float rayDistance;
    public GameObject matchedObject;
    public Bounds historicalBounds;
    public Vector3 historicalHitPoint, historicalFixationPoint, historicalFixationViewport;
    public Intersection[] closestIntersections = new Intersection[0];

    public Vector3 currentFixationPoint, currentLiveViewport;
    public bool behindAgent, inFrontOfLiveCamera;
    public bool gazeRejected;
    public string gazeRejectionReason;
    public Vector2 requiredYawElevation;
    public float distanceFromEyes, leftEyeErrorDegrees, rightEyeErrorDegrees;
    public Vector3 leftEyeLocalEuler, rightEyeLocalEuler, neckLocalEuler;
    public Vector3 neutralLeftEyeLocalEuler, neutralRightEyeLocalEuler, neutralNeckLocalEuler;
    public Vector2 leftEyePitchYaw, rightEyePitchYaw, neckPitchYaw;
}