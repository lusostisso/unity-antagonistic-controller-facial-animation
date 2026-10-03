using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Supplies object-relative VLM attention to the existing facial rig; never rotates bones itself.</summary>
[DisallowMultipleComponent, DefaultExecutionOrder(-100)]
public class VLMController : AttentionController
{
    public enum VLMEmotion { NEUTRAL, JOY, SADNESS, FEAR, ANGER, SURPRISE, DISGUST, CURIOSITY }
    public enum AttentionState { Disabled, Recentering, Capturing, AwaitingResponse, Holding, Interval }

    [Header("Integration")]
    public VisionStreamer streamer;
    public FaceController faceController;
    public Transform agentRoot;
    [Tooltip("Disabled camera, outside the moving agent hierarchy. Created automatically if unassigned.")]
    public Camera snapshotCamera;

    [Header("Attention cycle")]
    [Min(0)] public float returnToCenterWaitSeconds = 1f;
    [Min(0)] public float holdDurationSeconds = 5f;
    [Min(0.01f)] public float sendIntervalSeconds = 1f;
    [Header("Scene candidates")]
    public LayerMask selectableLayers = (1 << 0) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 14);
    public LayerMask surfaceLayers = 1 << 6;
    [Tooltip("Optional logical roots for compound objects; otherwise Rigidbody/Animator/renderer ownership is used.")]
    public List<Transform> targetRoots = new List<Transform>();

    [Header("Received state (not an emotion animation)")]
    [SerializeField] private Vector2 fixationPoint = new Vector2(0.5f, 0.5f);
    [SerializeField] private VLMEmotion emotion = VLMEmotion.NEUTRAL;
    [SerializeField] private bool found;
    [SerializeField] private bool hasSceneTarget;
    [SerializeField] private FixationObject currentFocus;
    [SerializeField] private AttentionState state;
    [SerializeField] private string lastDiagnostic;
    public Vector2 FixationPoint => fixationPoint;
    public VLMEmotion Emotion => emotion;
    public bool Found => found;
    public bool HasSceneTarget => hasSceneTarget && currentFocus != null && currentFocus.gameObject != null;
    public AttentionState State => state;
    public string LastDiagnostic => lastDiagnostic;

    private sealed class Candidate
    {
        public readonly GameObject Root;
        public readonly int InstanceId;
        public readonly Bounds Bounds;
        public readonly Matrix4x4 WorldToLocal, LocalToWorld;
        public readonly Renderer[] Renderers;
        public readonly Collider Surface;
        public Candidate(GameObject root, Bounds bounds, Renderer[] renderers, Collider surface = null)
        {
            Root = root; InstanceId = root.GetInstanceID(); Bounds = bounds;
            WorldToLocal = root.transform.worldToLocalMatrix;
            LocalToWorld = root.transform.localToWorldMatrix;
            Renderers = renderers; Surface = surface;
        }
    }

    private sealed class FrameSnapshot
    {
        public readonly long Id;
        public readonly int Generation, Frame;
        public readonly Matrix4x4 View, Projection;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly float NearClip, FarClip;
        public readonly Candidate[] Candidates;
        public FrameSnapshot(long id, int generation, Camera camera, Candidate[] candidates)
        {
            Id = id; Generation = generation; Frame = Time.frameCount;
            View = camera.worldToCameraMatrix; Projection = camera.projectionMatrix;
            Position = camera.transform.position; Rotation = camera.transform.rotation;
            NearClip = camera.nearClipPlane; FarClip = camera.farClipPlane;
            Candidates = candidates;
        }
    }

    private sealed class RendererGroup
    {
        public GameObject Root;
        public Bounds Bounds;
        public readonly List<Renderer> Renderers = new List<Renderer>();
    }

    private FrameSnapshot pendingSnapshot;
    private Candidate trackedTarget;
    private Coroutine cycle;
    private long pendingId;
    private int pendingGeneration;
    private bool requestCompleted, responseValid, started;
    private GameObject ownedSnapshotObject;
    private AttentionController previousAttention;

    private void Reset()
    {
        streamer = GetComponent<VisionStreamer>();
        if (streamer != null) faceController = streamer.LegacyFaceController;
        if (faceController == null) faceController = GetComponentInChildren<FaceController>(true);
        agentRoot = transform;
    }

    private void OnEnable()
    {
        if (streamer == null) streamer = GetComponent<VisionStreamer>();
        if (faceController == null && streamer != null) faceController = streamer.LegacyFaceController;
        if (agentRoot == null) agentRoot = transform;
        if (streamer == null || faceController == null)
        {
            Debug.LogError("[VLMController] Assign VisionStreamer and FaceController.", this);
            enabled = false;
            return;
        }
        try { EnsureSnapshotCamera(); }
        catch (Exception exception)
        {
            Debug.LogError("[VLMController] " + exception.Message, this);
            enabled = false;
            return;
        }
        previousAttention = faceController.AttentionSource;
        faceController.SetAttentionController(this);
        streamer.CapturePrepared += PrepareSnapshot;
        streamer.ResponseReceived += ReceiveResponse;
        streamer.RequestFailed += ReceiveFailure;
        ResetReceivedState();
        if (started) cycle = StartCoroutine(GazeCycle());
    }

    private void Start()
    {
        started = true;
        cycle = StartCoroutine(GazeCycle());
    }

    private void EnsureSnapshotCamera()
    {
        if (snapshotCamera == null)
        {
            ownedSnapshotObject = new GameObject("VLM snapshot camera");
            SceneManager.MoveGameObjectToScene(ownedSnapshotObject, gameObject.scene);
            snapshotCamera = ownedSnapshotObject.AddComponent<Camera>();
        }
        if (snapshotCamera == streamer.visionCamera || snapshotCamera.transform.parent != null)
            throw new InvalidOperationException("Snapshot camera must be separate and unparented.");
        if (snapshotCamera.GetComponents<MonoBehaviour>().Any(component => component != null && component.enabled))
            throw new InvalidOperationException("Snapshot camera must not have active follow/render scripts.");
        snapshotCamera.enabled = false;
        snapshotCamera.targetTexture = null;
    }

    private IEnumerator GazeCycle()
    {
        while (isActiveAndEnabled)
        {
            ClearFocus();
            state = AttentionState.Recentering;
            yield return new WaitForSecondsRealtime(SafeDuration(returnToCenterWaitSeconds, 1));
            if (!streamer.IsReady || streamer.IsBusy)
            {
                lastDiagnostic = "VisionStreamer not ready or busy.";
                yield return new WaitForSecondsRealtime(SafeDuration(sendIntervalSeconds, 1, 0.01f));
                continue;
            }
            requestCompleted = responseValid = false;
            pendingSnapshot = null;
            state = AttentionState.Capturing;
            if (!streamer.RequestCapture(out pendingId, out pendingGeneration))
            {
                ResetReceivedState();
                state = AttentionState.Interval;
                yield return new WaitForSecondsRealtime(SafeDuration(sendIntervalSeconds, 1, 0.01f));
                continue;
            }
            while (!requestCompleted)
            {
                if (streamer == null || !streamer.isActiveAndEnabled)
                {
                    ResetReceivedState();
                    responseValid = false;
                    lastDiagnostic = "VisionStreamer disabled while awaiting a reply.";
                    break;
                }
                yield return null;
            }
            pendingId = 0;
            pendingSnapshot = null;
            if (responseValid)
            {
                state = AttentionState.Holding;
                double holdEnd = Time.realtimeSinceStartupAsDouble + SafeDuration(holdDurationSeconds, 5);
                bool acquiredTarget = HasSceneTarget;
                while (Time.realtimeSinceStartupAsDouble < holdEnd && (!acquiredTarget || HasSceneTarget))
                    yield return null;
            }
            state = AttentionState.Interval;
            yield return new WaitForSecondsRealtime(SafeDuration(sendIntervalSeconds, 1, 0.01f));
        }
    }

    private void PrepareSnapshot(long id, int generation, Camera camera)
    {
        if (!Matches(id, generation)) throw new InvalidOperationException("Capture has no matching VLM request.");
        snapshotCamera.CopyFrom(camera);
        snapshotCamera.enabled = false;
        snapshotCamera.targetTexture = null;
        snapshotCamera.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
        snapshotCamera.aspect = camera.aspect;
        snapshotCamera.worldToCameraMatrix = camera.worldToCameraMatrix;
        snapshotCamera.projectionMatrix = camera.projectionMatrix;
        Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(camera);
        int mask = selectableLayers.value & camera.cullingMask;
        var groups = new Dictionary<int, RendererGroup>();
        foreach (Renderer renderer in FindObjectsOfType<Renderer>())
        {
            if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
            if (!Eligible(renderer.gameObject, mask) || !renderer.enabled || IsSurfaceLayer(renderer.gameObject.layer) || !ValidBounds(renderer.bounds)) continue;
            Transform root = ResolveRoot(renderer.transform);
            if (Excluded(root)) continue;
            int key = root.gameObject.GetInstanceID();
            if (!groups.TryGetValue(key, out RendererGroup group))
            {
                group = new RendererGroup { Root = root.gameObject, Bounds = renderer.bounds };
                groups.Add(key, group);
            }
            else group.Bounds.Encapsulate(renderer.bounds);
            group.Renderers.Add(renderer);
        }
        var candidates = new List<Candidate>();
        foreach (RendererGroup group in groups.Values)
            if (GeometryUtility.TestPlanesAABB(frustum, group.Bounds))
                candidates.Add(new Candidate(group.Root, group.Bounds, group.Renderers.ToArray()));
        foreach (Collider collider in FindObjectsOfType<Collider>())
        {
            if (!collider.enabled || collider.isTrigger || !Eligible(collider.gameObject, mask)) continue;
            if (!(collider is TerrainCollider) && !IsSurfaceLayer(collider.gameObject.layer)) continue;
            if (!ValidBounds(collider.bounds) || !GeometryUtility.TestPlanesAABB(frustum, collider.bounds)) continue;
            candidates.Add(new Candidate(collider.gameObject, collider.bounds, Array.Empty<Renderer>(), collider));
        }
        pendingSnapshot = new FrameSnapshot(id, generation, camera, candidates.ToArray());
        state = AttentionState.AwaitingResponse;
        lastDiagnostic = $"Frame {pendingSnapshot.Frame}: {candidates.Count} historical candidates.";
    }

    private Transform ResolveRoot(Transform rendererTransform)
    {
        // Explicit nearest ancestor first. Never use Transform.root of an arbitrary environment hierarchy.
        for (Transform ancestor = rendererTransform; ancestor != null; ancestor = ancestor.parent)
            if (targetRoots != null && targetRoots.Contains(ancestor)) return ancestor;
        Rigidbody body = rendererTransform.GetComponentInParent<Rigidbody>();
        if (body != null) return body.transform;
        Animator animator = rendererTransform.GetComponentInParent<Animator>();
        return animator != null ? animator.transform : rendererTransform;
    }

    private bool Eligible(GameObject candidate, int mask)
    {
        int layer = candidate.layer;
        return candidate.activeInHierarchy && (mask & (1 << layer)) != 0 &&
            layer != LayerMask.NameToLayer("Ignore Vision") && layer != LayerMask.NameToLayer("UI") &&
            layer != LayerMask.NameToLayer("Face Camera") && layer != LayerMask.NameToLayer("Third Person Camera") &&
            !Excluded(candidate.transform);
    }
    private bool Excluded(Transform candidate) => candidate == agentRoot || candidate.IsChildOf(agentRoot) ||
        candidate == snapshotCamera.transform || candidate == transform;
    private bool IsSurfaceLayer(int layer) => (surfaceLayers.value & (1 << layer)) != 0;
    private bool Matches(long id, int generation) => isActiveAndEnabled && id != 0 && id == pendingId && generation == pendingGeneration;

    private void ReceiveResponse(long id, int generation, string json)
    {
        if (!Matches(id, generation)) return;
        try
        {
            ParseResponse(json, out Vector2 point, out VLMEmotion receivedEmotion, out bool detected);
            if (pendingSnapshot == null || pendingSnapshot.Id != id || pendingSnapshot.Generation != generation)
                throw new InvalidOperationException("Reply has no historical frame snapshot.");
            fixationPoint = point; emotion = receivedEmotion; found = detected;
            ClearFocus();
            if (found) AssociateTarget(pendingSnapshot);
            else lastDiagnostic = "Server reported found=false; gaze neutral.";
            responseValid = true;
        }
        catch (Exception exception)
        {
            ResetReceivedState();
            responseValid = false;
            lastDiagnostic = "Invalid response/snapshot: " + exception.Message;
            Debug.LogWarning("[VLMController] " + lastDiagnostic, this);
        }
        finally { requestCompleted = true; }
    }

    private void ReceiveFailure(long id, int generation, string reason)
    {
        if (!Matches(id, generation)) return;
        ResetReceivedState();
        pendingSnapshot = null;
        lastDiagnostic = reason;
        responseValid = false;
        requestCompleted = true;
        Debug.LogWarning("[VLMController] " + reason, this);
    }

    private static void ParseResponse(string json, out Vector2 point, out VLMEmotion receivedEmotion, out bool detected)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Empty JSON.");
        JObject payload;
        using (var text = new StringReader(json))
        using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
        {
            payload = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new FormatException("Unexpected content after JSON.");
        }
        RequireKeys(payload, "position", "emotion", "found");
        if (!(payload["position"] is JObject position)) throw new FormatException("position must be an object.");
        RequireKeys(position, "x", "y");
        double x = Coordinate(position["x"]), y = Coordinate(position["y"]);
        if (payload["found"].Type != JTokenType.Boolean) throw new FormatException("found must be a JSON boolean.");
        detected = payload["found"].Value<bool>();
        if (payload["emotion"].Type != JTokenType.String) throw new FormatException("emotion must be a label.");
        string label = payload["emotion"].Value<string>();
        if (!Enum.GetNames(typeof(VLMEmotion)).Contains(label) || !Enum.TryParse(label, out receivedEmotion))
            throw new FormatException("Unknown emotion label.");
        if (!detected && (x != 0.5 || y != 0.5 || receivedEmotion != VLMEmotion.NEUTRAL))
            throw new FormatException("found=false requires the central NEUTRAL fallback.");
        point = new Vector2((float)x, (float)y);
    }

    private static void RequireKeys(JObject value, params string[] keys)
    {
        if (value.Count != keys.Length || keys.Any(key => value.Property(key) == null))
            throw new FormatException("Expected exactly: " + string.Join(", ", keys));
    }
    private static double Coordinate(JToken token)
    {
        if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer) throw new FormatException("Coordinates must be numbers, not strings/booleans.");
        double value = token.Value<double>();
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1) throw new FormatException("Coordinates must be finite in [0,1].");
        return value;
    }

    private void AssociateTarget(FrameSnapshot snapshot)
    {
        // Restore the frozen matrices, never use the current live camera or invert y a second time.
        snapshotCamera.transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
        snapshotCamera.nearClipPlane = snapshot.NearClip;
        snapshotCamera.farClipPlane = snapshot.FarClip;
        snapshotCamera.worldToCameraMatrix = snapshot.View;
        snapshotCamera.projectionMatrix = snapshot.Projection;
        Ray ray = snapshotCamera.ViewportPointToRay(new Vector3(fixationPoint.x, fixationPoint.y, 0));
        Vector3 far = snapshotCamera.ViewportToWorldPoint(new Vector3(fixationPoint.x, fixationPoint.y, snapshotCamera.farClipPlane));
        float maxDistance = Vector3.Dot(far - ray.origin, ray.direction);
        Candidate best = null;
        Vector3 bestPoint = Vector3.zero;
        float bestDistance = float.PositiveInfinity;
        foreach (Candidate candidate in snapshot.Candidates)
        {
            if (!candidate.Bounds.IntersectRay(ray, out float distance) || distance < 0 || distance > maxDistance) continue;
            Vector3 point = ray.GetPoint(distance);
            if (candidate.Surface != null)
            {
                // Only the explicitly static surface is queried; moved actors cannot change the result.
                if (!SurfaceUnchanged(candidate) || !candidate.Surface.Raycast(ray, out RaycastHit hit, maxDistance)) continue;
                distance = hit.distance;
                point = hit.point;
            }
            float depth = -snapshot.View.MultiplyPoint3x4(point).z;
            if (depth < snapshotCamera.nearClipPlane - 0.001f || depth > snapshotCamera.farClipPlane + 0.001f) continue;
            if (distance < bestDistance || (distance == bestDistance && (best == null || candidate.InstanceId < best.InstanceId)))
            {
                best = candidate; bestDistance = distance; bestPoint = point;
            }
        }
        if (best == null || best.Root == null || !best.Root.activeInHierarchy)
        {
            lastDiagnostic = "found=true, but no live scene identity matched the historical ray; gaze neutral.";
            return;
        }
        Vector3 localPoint = best.WorldToLocal.MultiplyPoint3x4(best.Surface != null ? bestPoint : best.Bounds.center);
        trackedTarget = best;
        currentFocus = new FixationObject(best.Root, localPoint);
        hasSceneTarget = true;
        if (!RefreshTrackedTarget()) return;
        lastDiagnostic = $"Frame {snapshot.Frame}: following {best.Root.name} (bounds association).";
    }

    private void LateUpdate() => RefreshTrackedTarget();

    private bool RefreshTrackedTarget()
    {
        if (trackedTarget == null) return false;
        if (trackedTarget.Root == null || !trackedTarget.Root.activeInHierarchy || currentFocus == null)
        {
            LoseTarget(); return false;
        }
        if (trackedTarget.Surface != null)
        {
            if (!SurfaceUnchanged(trackedTarget)) { LoseTarget(); return false; }
            return true;
        }
        bool any = false;
        Bounds currentBounds = default;
        foreach (Renderer renderer in trackedTarget.Renderers)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || !ValidBounds(renderer.bounds)) continue;
            if (!any) { currentBounds = renderer.bounds; any = true; }
            else currentBounds.Encapsulate(renderer.bounds);
        }
        if (!any) { LoseTarget(); return false; }
        currentFocus.localPoint = trackedTarget.Root.transform.InverseTransformPoint(currentBounds.center);
        return true;
    }

    private static bool SurfaceUnchanged(Candidate candidate)
    {
        if (candidate.Root == null || !candidate.Root.activeInHierarchy || candidate.Surface == null || !candidate.Surface.enabled) return false;
        Matrix4x4 now = candidate.Root.transform.localToWorldMatrix;
        for (int index = 0; index < 16; index++)
            if (Mathf.Abs(now[index] - candidate.LocalToWorld[index]) > 0.0001f) return false;
        return (candidate.Surface.bounds.center - candidate.Bounds.center).sqrMagnitude < 0.000001f &&
            (candidate.Surface.bounds.size - candidate.Bounds.size).sqrMagnitude < 0.000001f;
    }

    private static bool ValidBounds(Bounds bounds) => Finite(bounds.center) && Finite(bounds.size) && bounds.size.sqrMagnitude > 0.000001f;
    private static bool Finite(Vector3 vector) => !(float.IsNaN(vector.x) || float.IsInfinity(vector.x) ||
        float.IsNaN(vector.y) || float.IsInfinity(vector.y) || float.IsNaN(vector.z) || float.IsInfinity(vector.z));
    private static float SafeDuration(float value, float fallback, float minimum = 0) =>
        float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Max(minimum, value);
    private void ClearFocus() { currentFocus = null; trackedTarget = null; hasSceneTarget = false; }
    private void LoseTarget() { ClearFocus(); lastDiagnostic = "Tracked target disappeared or changed; gaze neutral."; }
    private void ResetReceivedState() { ClearFocus(); fixationPoint = new Vector2(0.5f, 0.5f); emotion = VLMEmotion.NEUTRAL; found = false; }

    public override FixationObject GetCurrentFocus()
    {
        // Called immediately before the rig uses the point, even if animation changed bounds this frame.
        return RefreshTrackedTarget() ? currentFocus : null;
    }
    public override float GetCurrentFixationTime() => HasSceneTarget ? SafeDuration(holdDurationSeconds, 5) : 0;

    private void OnDisable()
    {
        if (cycle != null) StopCoroutine(cycle);
        cycle = null;
        if (streamer != null)
        {
            streamer.CapturePrepared -= PrepareSnapshot;
            streamer.ResponseReceived -= ReceiveResponse;
            streamer.RequestFailed -= ReceiveFailure;
            streamer.CancelRequest(pendingId, pendingGeneration);
        }
        if (faceController != null && faceController.AttentionSource == this)
            faceController.SetAttentionController(previousAttention);
        pendingId = 0;
        pendingSnapshot = null;
        requestCompleted = true;
        ResetReceivedState();
        state = AttentionState.Disabled;
    }

    private void OnDestroy()
    {
        if (ownedSnapshotObject != null) Destroy(ownedSnapshotObject);
    }
}