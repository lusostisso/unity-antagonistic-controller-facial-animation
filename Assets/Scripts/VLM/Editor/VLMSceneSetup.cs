using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>Explicit, undoable migration of the selected scene instance; never changes a prefab asset.</summary>
public static class VLMSceneSetup
{
    [MenuItem("Tools/VLM/Configure selected VisionStreamer")]
    public static void ConfigureSelected()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[VLM] Configure the scene before entering Play mode.");
            return;
        }
        GameObject selected = Selection.activeGameObject;
        VisionStreamer streamer = selected != null ? selected.GetComponentInParent<VisionStreamer>() : null;
        if (streamer == null && selected != null) streamer = selected.GetComponentInChildren<VisionStreamer>(true);
        if (streamer == null || EditorUtility.IsPersistent(streamer) || !streamer.gameObject.scene.IsValid())
        {
            Debug.LogError("[VLM] Select VisionCharacter (or another scene instance with VisionStreamer).");
            return;
        }
        FaceController face = streamer.LegacyFaceController;
        VLMController existing = streamer.GetComponent<VLMController>();
        if (existing != null && existing.faceController != null) face = existing.faceController;
        if (face == null) face = streamer.GetComponentInChildren<FaceController>(true);
        if (face == null || streamer.visionCamera == null)
        {
            Debug.LogError("[VLM] Assign the existing FaceController and vision camera before migration.");
            return;
        }
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Configure VLM attention");
        try
        {
            VLMController bridge = existing != null ? existing : Undo.AddComponent<VLMController>(streamer.gameObject);
            Undo.RecordObject(bridge, "Assign VLM integration");
            Undo.RecordObject(streamer, "Configure vision camera");
            bridge.streamer = streamer;
            bridge.faceController = face;
            bridge.agentRoot = streamer.transform;
            if (existing == null)
            {
                bridge.returnToCenterWaitSeconds = streamer.LegacyRecenterWait;
                bridge.holdDurationSeconds = streamer.LegacyHoldDuration;
                bridge.sendIntervalSeconds = streamer.LegacySendInterval;
            }
            if (bridge.snapshotCamera == null)
            {
                var snapshot = new GameObject("VLM snapshot camera");
                Undo.RegisterCreatedObjectUndo(snapshot, "Create frozen VLM camera");
                SceneManager.MoveGameObjectToScene(snapshot, streamer.gameObject.scene);
                bridge.snapshotCamera = Undo.AddComponent<Camera>(snapshot);
                bridge.snapshotCamera.enabled = false;
            }
            Camera live = streamer.visionCamera;
            if (live.TryGetComponent(out UniversalAdditionalCameraData data) &&
                (data.renderType != CameraRenderType.Base || (data.cameraStack != null && data.cameraStack.Count != 0)))
            {
                var captureObject = new GameObject("VLM capture camera");
                Undo.RegisterCreatedObjectUndo(captureObject, "Create dedicated VLM capture camera");
                SceneManager.MoveGameObjectToScene(captureObject, live.gameObject.scene);
                captureObject.transform.SetParent(live.transform.parent, false);
                captureObject.transform.SetPositionAndRotation(live.transform.position, live.transform.rotation);
                Camera capture = Undo.AddComponent<Camera>(captureObject);
                capture.CopyFrom(live);
                capture.targetTexture = null;
                capture.rect = new Rect(0, 0, 1, 1);
                capture.stereoTargetEye = StereoTargetEyeMask.None;
                capture.enabled = true;
                var captureData = Undo.AddComponent<UniversalAdditionalCameraData>(captureObject);
                captureData.renderType = CameraRenderType.Base;
                captureData.allowXRRendering = false;
                captureData.renderPostProcessing = false;
                streamer.visionCamera = capture;
            }
            else
            {
                Undo.RecordObject(live, "Enable dedicated vision camera");
                live.enabled = true;
                live.rect = new Rect(0, 0, 1, 1);
                live.stereoTargetEye = StereoTargetEyeMask.None;
                if (data != null)
                {
                    Undo.RecordObject(data, "Disable XR and geometric post-processing");
                    data.allowXRRendering = false;
                    data.renderPostProcessing = false;
                    Record(data);
                }
                Record(live);
            }
            foreach (AttentionController controller in bridge.agentRoot.GetComponentsInChildren<AttentionController>(true))
                if (controller != bridge) Disable(controller);
            foreach (SaliencyController controller in bridge.agentRoot.GetComponentsInChildren<SaliencyController>(true)) Disable(controller);
            foreach (InferenceClient client in bridge.agentRoot.GetComponentsInChildren<InferenceClient>(true)) Disable(client);
            Undo.RecordObject(face, "Use VLM attention source");
            face.SetAttentionController(bridge);
            face.enabled = true;
            bridge.enabled = true;
            streamer.enabled = true;
            Record(face); Record(bridge); Record(streamer);
            EditorSceneManager.MarkSceneDirty(streamer.gameObject.scene);
            Selection.activeGameObject = bridge.gameObject;
            Debug.Log("[VLM] Configured this instance. Review selectable/surface layers and target roots, then save the scene. Emotion is stored only.", bridge);
        }
        catch (Exception exception)
        {
            Undo.RevertAllDownToGroup(undoGroup);
            Debug.LogException(exception);
        }
        finally { Undo.CollapseUndoOperations(undoGroup); }
    }

    private static void Disable(Behaviour component)
    {
        Undo.RecordObject(component, "Disable previous attention client");
        component.enabled = false;
        Record(component);
    }
    private static void Record(UnityEngine.Object value)
    {
        EditorUtility.SetDirty(value);
        PrefabUtility.RecordPrefabInstancePropertyModifications(value);
    }
}