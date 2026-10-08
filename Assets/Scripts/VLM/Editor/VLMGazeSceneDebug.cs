using UnityEditor;
using UnityEngine;

/// <summary>Always-on Scene overlay, independent of selection and never rendered by game/capture cameras.</summary>
[InitializeOnLoad]
public static class VLMGazeSceneDebug
{
    private const string MenuPath = "Tools/VLM/Open gaze debug in Scene";
    private static VLMController[] controllers = new VLMController[0];
    private static double nextScan, nextRepaint;
    private static readonly Color HistoricalColor = new Color(1, 0.75f, 0);

    static VLMGazeSceneDebug()
    {
        SceneView.duringSceneGui += DrawScene;
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += ResetCache;
        AssemblyReloadEvents.beforeAssemblyReload += Unsubscribe;
    }

    private static void Update()
    {
        if (!EditorApplication.isPlaying) return;
        double now = EditorApplication.timeSinceStartup;
        if (now >= nextScan)
        {
            controllers = Object.FindObjectsOfType<VLMController>();
            nextScan = now + 0.5;
        }
        if (now < nextRepaint) return;
        nextRepaint = now + 0.1;
        SceneView.RepaintAll();
    }

    private static void ResetCache(PlayModeStateChange state)
    {
        controllers = new VLMController[0];
        nextScan = nextRepaint = 0;
    }

    [MenuItem(MenuPath)]
    public static void Open()
    {
        controllers = Object.FindObjectsOfType<VLMController>();
        SceneView view = EditorWindow.GetWindow<SceneView>();
        view.Show();
        view.Focus();
        foreach (VLMController controller in controllers)
        {
            if (!controller.isActiveAndEnabled) continue;
            if (!controller.drawDebugGizmos)
            {
                Undo.RecordObject(controller, "Enable VLM gaze debug");
                controller.drawDebugGizmos = true;
                EditorUtility.SetDirty(controller);
            }
            Vector3 origin = controller.faceController != null
                ? controller.faceController.GazeOrigin : controller.transform.position;
            Bounds bounds = new Bounds(origin + controller.transform.forward * 1.5f, Vector3.one * 6);
            if (controller.Diagnostics.currentFocusActive) bounds.Encapsulate(controller.Diagnostics.currentFixationPoint);
            view.Frame(bounds, false);
            break;
        }
        view.Repaint();
    }

    private static void DrawScene(SceneView view)
    {
        if (!EditorApplication.isPlaying) return;
        Color color = Handles.color;
        var depth = Handles.zTest;
        int drawn = 0;
        try
        {
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            foreach (VLMController controller in controllers)
            {
                if (controller == null || !controller.isActiveAndEnabled || !controller.drawDebugGizmos) continue;
                DrawController(controller);
                drawn++;
            }
        }
        finally { Handles.color = color; Handles.zTest = depth; }

        // Visible even with no selected object, no Python response or missing rig references.
        Handles.BeginGUI();
        try
        {
            GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(470, view.position.width - 24), 165), GUI.skin.box);
            GUILayout.Label("VLM gaze debug — Scene only", EditorStyles.boldLabel);
            if (drawn == 0)
                GUILayout.Label("No enabled VLMController with drawDebugGizmos. Select the agent and verify its component.", EditorStyles.wordWrappedLabel);
            foreach (VLMController controller in controllers)
            {
                if (controller == null || !controller.isActiveAndEnabled || !controller.drawDebugGizmos) continue;
                FaceController face = controller.faceController;
                GUILayout.Label($"{controller.name}: {controller.State} | found={controller.Found} | scene target={controller.HasSceneTarget}");
                if (face == null || face.LeftEye == null || face.RightEye == null || face.Neck == null)
                    GUILayout.Label("Missing FaceController / eye / neck reference — no axes to draw.", EditorStyles.wordWrappedLabel);
                else if (!face.isActiveAndEnabled)
                    GUILayout.Label("FaceController is disabled; the visible rig is not being driven by this controller.", EditorStyles.wordWrappedLabel);
                GUILayout.Label(controller.LastDiagnostic ?? "Awaiting a response.", EditorStyles.wordWrappedLabel);
                break;
            }
            GUILayout.Label("Yellow: old ray/hit | Magenta: old focus | Green: current target\nCyan: eyes | Blue: neck | Gray: neutral forward", EditorStyles.wordWrappedLabel);
            GUILayout.EndArea();
        }
        finally { Handles.EndGUI(); }
    }

    private static void DrawController(VLMController controller)
    {
        controller.RefreshDebugState();
        VLMGazeDiagnostics data = controller.Diagnostics;
        if (data.hasResponse && data.found)
        {
            Line(data.rayOrigin, data.rayOrigin + data.rayDirection * data.rayDistance, HistoricalColor);
            foreach (VLMGazeDiagnostics.Intersection hit in data.closestIntersections)
            {
                Handles.color = HistoricalColor;
                Handles.DrawWireCube(hit.historicalBounds.center, hit.historicalBounds.size);
            }
            if (data.associated)
            {
                Point(data.historicalHitPoint, HistoricalColor, "Old ray hit");
                Point(data.historicalFixationPoint, Color.magenta, "Old focus / bounds center");
            }
        }
        FaceController face = controller.faceController;
        if (data.currentFocusActive)
        {
            Point(data.currentFixationPoint, Color.green, "Current target: " + data.matchedObjectPath);
            if (face != null) Line(face.GazeOrigin, data.currentFixationPoint, Color.green);
        }
        else if (data.gazeRejected)
        {
            Point(data.currentFixationPoint, Color.red, "Rejected: " + data.gazeRejectionReason);
            if (face != null) Line(face.GazeOrigin, data.currentFixationPoint, Color.red);
        }
        if (face == null) return;
        float length = data.currentFocusActive ? Mathf.Clamp(data.distanceFromEyes, 2, 20) : 3;
        Axis(face.LeftEye, Color.cyan, length);
        Axis(face.RightEye, Color.cyan, length);
        Axis(face.Neck, Color.blue, 3);
        Axis(face.Head, new Color(0.3f, 0.5f, 1), 3);
        if (face.Neck != null)
        {
            // Dotted neutral axis must not cover the blue neck axis when both are aligned.
            Handles.color = Color.gray;
            Handles.DrawDottedLine(face.Neck.position, face.Neck.position + face.NeutralNeckForward * 3, 5);
            Handles.Label(face.Neck.position, controller.name + " | " + controller.State);
        }
    }

    private static void Line(Vector3 start, Vector3 end, Color color)
    {
        Handles.color = color;
        Handles.DrawAAPolyLine(3, start, end);
    }

    private static void Axis(Transform bone, Color color, float length)
    {
        if (bone != null) Line(bone.position, bone.position + bone.forward * length, color);
    }

    private static void Point(Vector3 position, Color color, string label)
    {
        Handles.color = color;
        Handles.SphereHandleCap(0, position, Quaternion.identity,
            HandleUtility.GetHandleSize(position) * 0.09f, EventType.Repaint);
        Handles.Label(position, label);
    }

    private static void Unsubscribe()
    {
        SceneView.duringSceneGui -= DrawScene;
        EditorApplication.update -= Update;
        EditorApplication.playModeStateChanged -= ResetCache;
        AssemblyReloadEvents.beforeAssemblyReload -= Unsubscribe;
        controllers = new VLMController[0];
    }
}