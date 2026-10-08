using UnityEditor;
using UnityEngine;

/// <summary>Uses the paired JPEG and SceneView handles only; never annotates the capture camera.</summary>
[CustomEditor(typeof(VLMController))]
public sealed class VLMControllerEditor : Editor
{
    private Texture2D preview;
    private byte[] previewBytes;
    private static readonly Color CenterColor = Color.magenta;

    public override bool RequiresConstantRepaint() => Application.isPlaying;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var controller = (VLMController)target;
        if (Application.isPlaying) controller.RefreshDebugState();
        VLMGazeDiagnostics data = controller.Diagnostics;
        if (GUILayout.Button("Open gaze debug in Scene")) VLMGazeSceneDebug.Open();
        EditorGUILayout.HelpBox("Lines are drawn in the Scene tab, not the Game tab or the model camera. They no longer require selecting this object. Cyan/blue/gray axes appear even while waiting for Python.", MessageType.Info);
        if (!data.hasResponse)
        {
            EditorGUILayout.HelpBox("Enter Play and wait for a response to inspect the paired frame and association.", MessageType.Info);
            ReleasePreview();
            return;
        }
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Received frame / gaze diagnostics", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Request {data.requestId}, generation {data.generation}, frame {data.captureFrame}");
        EditorGUILayout.LabelField("Current attention state: " + controller.State);
        EditorGUILayout.LabelField($"Response age: {data.responseAgeSeconds:F2} s | candidates: {data.candidateCount} (particles: {data.particleCandidateCount})");
        EditorGUILayout.SelectableLabel("JPEG SHA256: " + data.imageSha256, EditorStyles.wordWrappedLabel, GUILayout.Height(36));
        EditorGUILayout.HelpBox(data.status ?? "", data.found ? MessageType.Info : MessageType.Warning);
        if (!data.found)
            EditorGUILayout.HelpBox("found=false: the central coordinate is a fallback, NOT a localized target. The rig should recenter.", MessageType.Warning);
        if (data.currentFocusActive && data.behindAgent)
            EditorGUILayout.HelpBox("The associated object is now behind the body. This is a stale/awkward gaze target, not necessarily a Y-coordinate error.", MessageType.Warning);
        if (data.gazeRejected)
            EditorGUILayout.HelpBox("Gaze target rejected: " + data.gazeRejectionReason, MessageType.Warning);

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Vector2Field("Received viewport (bottom-left)", data.viewportPoint);
            EditorGUILayout.Vector2Field("JPEG pixel (top-left)", data.imagePixelTopLeft);
            EditorGUILayout.ObjectField("Historically associated object", data.matchedObject, typeof(GameObject), true);
            EditorGUILayout.Toggle("Current focus active", data.currentFocusActive);
            if (data.found)
                EditorGUILayout.Vector2Field("Ray reprojection error (pixels)", data.rayReprojectionErrorPixels);
            if (data.associated)
            {
                EditorGUILayout.Vector3Field("Historical ray hit", data.historicalHitPoint);
                EditorGUILayout.Vector3Field("Historical bounds center / focus", data.historicalFixationPoint);
                EditorGUILayout.Vector3Field("Current world focus", data.currentFixationPoint);
                EditorGUILayout.Vector3Field("Current live viewport (z = depth)", data.currentLiveViewport);
                EditorGUILayout.Vector2Field("Required body yaw / elevation", data.requiredYawElevation);
                EditorGUILayout.FloatField("Distance from eyes", data.distanceFromEyes);
                EditorGUILayout.Toggle("Target now behind body", data.behindAgent);
                EditorGUILayout.Vector2Field("Eye-to-target errors (degrees)", new Vector2(data.leftEyeErrorDegrees, data.rightEyeErrorDegrees));
            }
            EditorGUILayout.Vector3Field("Left eye local Euler", data.leftEyeLocalEuler);
            EditorGUILayout.Vector3Field("Right eye local Euler", data.rightEyeLocalEuler);
            EditorGUILayout.Vector3Field("Neck local Euler", data.neckLocalEuler);
            EditorGUILayout.Vector3Field("Saved neutral left eye", data.neutralLeftEyeLocalEuler);
            EditorGUILayout.Vector3Field("Saved neutral right eye", data.neutralRightEyeLocalEuler);
            EditorGUILayout.Vector3Field("Saved neutral neck", data.neutralNeckLocalEuler);
            EditorGUILayout.Vector2Field("Left eye relative pitch / yaw", data.leftEyePitchYaw);
            EditorGUILayout.Vector2Field("Right eye relative pitch / yaw", data.rightEyePitchYaw);
            EditorGUILayout.Vector2Field("Neck relative pitch / yaw", data.neckPitchYaw);
            if (controller.faceController != null)
                EditorGUILayout.Vector3Field("Legacy roll limits (not applied)", controller.faceController.LegacyRollLimits);
        }
        if (data.associated) EditorGUILayout.LabelField(data.matchedObjectPath, EditorStyles.wordWrappedLabel);
        DrawPreview(controller, data);
        EditorGUILayout.LabelField($"Historical ray intersections: {data.intersectionCount} (nearest five)", EditorStyles.boldLabel);
        foreach (VLMGazeDiagnostics.Intersection hit in data.closestIntersections)
            EditorGUILayout.LabelField($"{hit.distance:F2} m | {hit.hierarchyPath} | {hit.layer} | {hit.rendererTypes}", EditorStyles.wordWrappedLabel);

        if (GUILayout.Button("Copy diagnostics JSON")) EditorGUIUtility.systemCopyBuffer = controller.ExportDiagnostics();
        if (data.matchedObject != null && GUILayout.Button("Ping associated object")) EditorGUIUtility.PingObject(data.matchedObject);
        if (data.currentFocusActive && GUILayout.Button("Frame current focus in Scene"))
            SceneView.lastActiveSceneView?.Frame(new Bounds(data.currentFixationPoint, Vector3.one * 3), false);
    }

    private void DrawPreview(VLMController controller, VLMGazeDiagnostics data)
    {
        byte[] bytes = controller.DebugFrameJpeg;
        if (bytes == null) { ReleasePreview(); return; }
        if (!ReferenceEquals(bytes, previewBytes))
        {
            ReleasePreview();
            preview = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!preview.LoadImage(bytes)) { ReleasePreview(); return; }
            previewBytes = bytes;
        }
        EditorGUILayout.LabelField("Frozen JPEG actually sent (not the live camera)", EditorStyles.boldLabel);
        Rect rect = GUILayoutUtility.GetAspectRect((float)data.imageWidth / data.imageHeight, GUILayout.MaxHeight(420));
        GUI.DrawTexture(rect, preview, ScaleMode.StretchToFill);
        DrawMarker(rect, data.viewportPoint, data.found ? Color.red : Color.gray);
        if (data.associated && data.historicalFixationViewport.z > 0)
            DrawMarker(rect, data.historicalFixationViewport, CenterColor);
        EditorGUILayout.HelpBox("Red = received point; magenta = chosen bounds center projected in the OLD frame; gray = fallback. A gap between red and magenta can explain aiming above/below the detected point. Ray reprojection checks math only, not object identity or image orientation.", MessageType.Info);
    }

    private static void DrawMarker(Rect rect, Vector2 viewport, Color color)
    {
        if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1) return;
        // IMGUI is top-left; this conversion is DISPLAY ONLY, never applied to the historical ray.
        Vector2 pixel = new Vector2(rect.x + viewport.x * rect.width, rect.y + (1 - viewport.y) * rect.height);
        EditorGUI.DrawRect(new Rect(pixel.x - 7, pixel.y - 2, 14, 4), Color.black);
        EditorGUI.DrawRect(new Rect(pixel.x - 2, pixel.y - 7, 4, 14), Color.black);
        EditorGUI.DrawRect(new Rect(pixel.x - 6, pixel.y - 1, 12, 2), color);
        EditorGUI.DrawRect(new Rect(pixel.x - 1, pixel.y - 6, 2, 12), color);
    }

    private void OnDisable() => ReleasePreview();
    private void ReleasePreview()
    {
        if (preview != null) DestroyImmediate(preview);
        preview = null; previewBytes = null;
    }
}