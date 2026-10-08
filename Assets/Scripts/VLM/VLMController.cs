using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;

public enum VLMEmotion
{
    NEUTRAL,
    JOY,
    SADNESS,
    FEAR,
    ANGER,
    SURPRISE,
    DISGUST,
    CURIOSITY
}

[DisallowMultipleComponent]
public class VLMController : AttentionController
{
    public enum CycleState
    {
        Disabled,
        Recentering,
        Capturing,
        AwaitingResponse,
        Fixating,
        Neutral,
        Interval
    }

    [Serializable]
    private sealed class ResponsePosition
    {
        public double x;
        public double y;
    }

    [Serializable]
    private sealed class ResponsePayload
    {
        public ResponsePosition position;
        public string emotion;
    }

    private struct GridCell
    {
        public bool HasHit;
        public GameObject Root;
        public Collider Collider;
        public bool IsSurface;
        public Vector3 WorldPoint;
        public Vector3 LocalPoint;
        public float Distance;
    }

    private const string NumberJson = @"-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?";
    private const string XField = @"""x""\s*:\s*" + NumberJson;
    private const string YField = @"""y""\s*:\s*" + NumberJson;
    private const string PositionJson = @"\{\s*(?:" + XField + @"\s*,\s*" + YField + "|" + YField + @"\s*,\s*" + XField + @")\s*\}";
    private const string PositionField = @"""position""\s*:\s*" + PositionJson;
    private const string EmotionField = @"""emotion""\s*:\s*""(?:JOY|SADNESS|FEAR|ANGER|SURPRISE|DISGUST|CURIOSITY|NEUTRAL)""";
    private static readonly Regex ResponseShape = new Regex(
        @"\A\s*\{\s*(?:" + PositionField + @"\s*,\s*" + EmotionField + "|" + EmotionField + @"\s*,\s*" + PositionField + @")\s*\}\s*\z",
        RegexOptions.CultureInvariant);

    [Header("Vision")]
    public VisionStreamer visionStreamer;
    [SerializeField]
    private Camera snapshotCamera;
    public Transform agentRoot;

    [Header("Scene targets")]
    public LayerMask scanLayerMask = Physics.DefaultRaycastLayers & ~((1 << 3) | (1 << 5));
    public LayerMask groundLayers = 1 << 6;
    [Range(8, 128)]
    public int gridResolution = 64;
    [Range(0, 4)]
    public int neighborCells = 2;

    [Header("Timing")]
    [Min(0)]
    public float fixationTime = 5f;
    [Min(0.2f)]
    public float returnToCenterWaitSeconds = 1f;
    [Min(0)]
    public float sendIntervalSeconds = 1f;
    [Min(0.1f)]
    public float restDistance = 10f;

    [Header("Debug")]
    public bool drawDebug;
    [SerializeField]
    private Vector2 fixationPoint = new Vector2(0.5f, 0.5f);
    [SerializeField]
    private VLMEmotion emotion = VLMEmotion.NEUTRAL;
    [SerializeField]
    private CycleState state = CycleState.Disabled;
    [SerializeField]
    private FixationObject currentFocus;
    [SerializeField]
    private string lastDiagnostic;

    public Vector2 FixationPoint => fixationPoint;
    public VLMEmotion Emotion => emotion;
    public CycleState State => state;
    public string LastDiagnostic => lastDiagnostic;
    public bool HasSceneTarget => state == CycleState.Fixating && trackedRoot != null && trackedRoot.activeInHierarchy;

    private readonly FixationObject noFocus = new FixationObject(null, Vector3.zero);
    private Coroutine cycle;
    private VisionStreamer subscribedStreamer;
    private GameObject restAnchor;
    private FixationObject restFocus;
    private bool ownsSnapshotCamera;
    private GridCell[] grid;
    private RaycastHit[] rayHits = new RaycastHit[32];
    private int capturedResolution;
    private int capturedNeighbors;
    private int capturedLayers;
    private int capturedVisualLayers;
    private bool hasSnapshot;
    private long snapshotRequestId;
    private long requestId;
    private GameObject trackedRoot;
    private Renderer[] trackedRenderers;
    private Collider[] trackedColliders;
    private Collider trackedSurfaceCollider;
    private GameObject surfaceAnchor;
    private float activeFixationDuration;
    private bool lostTarget;
    private bool hasDebugRay;
    private Ray debugRay;
    private Vector3 debugHitPoint;

    private void OnEnable()
    {
        try
        {
            if (visionStreamer == null) visionStreamer = GetComponent<VisionStreamer>();
            if (visionStreamer == null || visionStreamer.visionCamera == null)
                throw new InvalidOperationException("Atribua o VisionStreamer e a câmera de captura.");
            if (agentRoot == null) agentRoot = transform;
            Camera source = visionStreamer.visionCamera;
            if (snapshotCamera == source || (snapshotCamera != null && snapshotCamera.transform == agentRoot))
                throw new InvalidOperationException("A câmera de snapshot deve ser separada da câmera viva e do corpo.");

            if (snapshotCamera == null)
            {
                var cameraObject = new GameObject("VLM Snapshot Camera");
                cameraObject.hideFlags = HideFlags.DontSave;
                snapshotCamera = cameraObject.AddComponent<Camera>();
                ownsSnapshotCamera = true;
            }
            snapshotCamera.enabled = false;
            snapshotCamera.transform.SetParent(null, true);

            UpdateRestFocus(source);
            currentFocus = noFocus;
            subscribedStreamer = visionStreamer;
            subscribedStreamer.CapturePrepared += PrepareSnapshot;
            cycle = StartCoroutine(GazeCycle());
        }
        catch (Exception exception)
        {
            lastDiagnostic = exception.Message;
            Debug.LogError("[VLMController] " + lastDiagnostic, this);
            enabled = false;
        }
    }

    private void UpdateRestFocus(Camera source)
    {
        if (restAnchor == null)
        {
            restAnchor = new GameObject("VLM Rest Focus");
            restAnchor.hideFlags = HideFlags.DontSave;
            restFocus = new FixationObject(restAnchor, Vector3.zero);
        }
        restAnchor.transform.SetParent(source.transform, false);
        restAnchor.transform.localPosition = Vector3.forward * SafeDuration(restDistance, 10f, 0.1f);
    }

    private IEnumerator GazeCycle()
    {
        while (isActiveAndEnabled)
        {
            ReleaseTarget();
            InvalidateSnapshot();
            while (subscribedStreamer != null && subscribedStreamer.TryGetResult(out _)) { }

            if (subscribedStreamer == null || subscribedStreamer.visionCamera == null || !subscribedStreamer.IsReady)
            {
                lastDiagnostic = subscribedStreamer == null ? "VisionStreamer ausente." : subscribedStreamer.LastError ?? "Aguardando câmera/transporte.";
                state = CycleState.Interval;
                yield return new WaitForSeconds(SafeDuration(sendIntervalSeconds, 1f, 0.1f));
                continue;
            }

            UpdateRestFocus(subscribedStreamer.visionCamera);
            currentFocus = restFocus;
            state = CycleState.Recentering;
            yield return new WaitForSeconds(SafeDuration(returnToCenterWaitSeconds, 1f, 0.2f));

            currentFocus = noFocus;
            state = CycleState.Capturing;
            if (subscribedStreamer == null || subscribedStreamer.visionCamera == null || restAnchor == null || !subscribedStreamer.TryCaptureAndSend())
            {
                Fail(subscribedStreamer != null ? subscribedStreamer.LastError ?? "Não foi possível iniciar a captura." : "VisionStreamer removido.");
                state = CycleState.Interval;
                yield return new WaitForSeconds(SafeDuration(sendIntervalSeconds, 1f, 0.1f));
                continue;
            }
            requestId = subscribedStreamer.ActiveRequestId;
            VisionRequestResult result = null;
            while (result == null)
            {
                if (subscribedStreamer == null)
                {
                    result = new VisionRequestResult(requestId, VisionRequestStatus.Cancelled, error: "VisionStreamer removido.");
                }
                else if (subscribedStreamer.TryGetResult(out VisionRequestResult received))
                {
                    if (received.RequestId == requestId) result = received;
                }
                if (result == null && subscribedStreamer != null
                    && (!subscribedStreamer.IsReady || subscribedStreamer.ActiveRequestId != requestId))
                {
                    result = new VisionRequestResult(requestId, VisionRequestStatus.Cancelled,
                        error: subscribedStreamer.LastError ?? "Pedido descartado após desativação do VisionStreamer.");
                }
                if (result == null) yield return null;
            }
            requestId = 0;

            bool valid = result.Status == VisionRequestStatus.Response;
            string error = result.Error;
            if (valid) valid = ApplyResponse(result, out error);
            if (!valid)
            {
                Fail(error ?? "Resposta inválida.");
                InvalidateSnapshot();
                state = CycleState.Interval;
                yield return new WaitForSeconds(SafeDuration(sendIntervalSeconds, 1f, 0.1f));
                continue;
            }

            ClearGrid();
            float elapsed = 0;
            float holdDuration = SafeDuration(fixationTime, 5f);
            activeFixationDuration = holdDuration;
            while (elapsed < holdDuration)
            {
                if (state == CycleState.Fixating) RefreshTrackedTarget();
                if (lostTarget) break;
                elapsed += Time.deltaTime;
                yield return null;
            }

            ReleaseTarget();
            state = CycleState.Interval;
            yield return new WaitForSeconds(SafeDuration(sendIntervalSeconds, 1f));
        }
    }

    private void PrepareSnapshot(long id, Camera source)
    {
        if (id != requestId || source != subscribedStreamer.visionCamera) return;
        InvalidateSnapshot();
        snapshotCamera.CopyFrom(source);
        snapshotCamera.enabled = false;
        snapshotCamera.targetTexture = null;
        snapshotCamera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        snapshotCamera.aspect = (float)source.targetTexture.width / source.targetTexture.height;
        snapshotCamera.worldToCameraMatrix = source.worldToCameraMatrix;
        snapshotCamera.projectionMatrix = source.projectionMatrix;
        capturedResolution = Mathf.Clamp(gridResolution, 8, 128);
        capturedNeighbors = Mathf.Clamp(neighborCells, 0, 4);
        capturedLayers = scanLayerMask.value & source.cullingMask;
        capturedVisualLayers = source.cullingMask;
        if (grid == null || grid.Length != capturedResolution * capturedResolution)
            grid = new GridCell[capturedResolution * capturedResolution];

        Physics.SyncTransforms();
        for (int y = 0; y < capturedResolution; y++)
        {
            for (int x = 0; x < capturedResolution; x++)
            {
                Ray ray = snapshotCamera.ViewportPointToRay(new Vector3((x + 0.5f) / capturedResolution, (y + 0.5f) / capturedResolution, 0));
                float directionDepth = Vector3.Dot(ray.direction, snapshotCamera.transform.forward);
                float originDepth = Vector3.Dot(ray.origin - snapshotCamera.transform.position, snapshotCamera.transform.forward);
                float distance = directionDepth > 0 ? (snapshotCamera.farClipPlane - originDepth) / directionDepth : 0;
                if (!IsFinite(distance) || distance <= 0 || !TryClosestHit(ray, distance, out RaycastHit hit)) continue;

                bool surface = hit.collider is TerrainCollider || (groundLayers.value & (1 << hit.collider.gameObject.layer)) != 0;
                GameObject root = surface ? hit.collider.gameObject : GetTargetRoot(hit.collider);
                if (root == null || IsAgent(root.transform)) continue;
                grid[y * capturedResolution + x] = new GridCell
                {
                    HasHit = true,
                    Root = root,
                    Collider = hit.collider,
                    IsSurface = surface,
                    WorldPoint = hit.point,
                    LocalPoint = root.transform.InverseTransformPoint(hit.point),
                    Distance = hit.distance
                };
            }
        }
        snapshotRequestId = id;
        hasSnapshot = true;
        state = CycleState.AwaitingResponse;
    }

    private bool TryClosestHit(Ray ray, float distance, out RaycastHit closest)
    {
        int count;
        while (true)
        {
            count = Physics.RaycastNonAlloc(ray, rayHits, distance, capturedLayers, QueryTriggerInteraction.Ignore);
            if (count < rayHits.Length) break;
            if (rayHits.Length >= 256)
            {
                RaycastHit[] all = Physics.RaycastAll(ray, distance, capturedLayers, QueryTriggerInteraction.Ignore);
                return SelectClosest(all, all.Length, out closest);
            }
            rayHits = new RaycastHit[rayHits.Length * 2];
        }
        return SelectClosest(rayHits, count, out closest);
    }

    private bool SelectClosest(RaycastHit[] hits, int count, out RaycastHit closest)
    {
        closest = default;
        float minimum = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.collider == null || IsAgent(hit.collider.transform) || !IsFinite(hit.point) || !IsFinite(hit.distance)) continue;
            if (hit.distance >= minimum) continue;
            closest = hit;
            minimum = hit.distance;
        }
        return minimum < float.PositiveInfinity;
    }

    private bool IsAgent(Transform candidate) => agentRoot != null && (candidate == agentRoot || candidate.IsChildOf(agentRoot));

    private static GameObject GetTargetRoot(Collider collider)
    {
        if (collider.attachedRigidbody != null) return collider.attachedRigidbody.gameObject;
        Animator animator = collider.GetComponentInParent<Animator>();
        return animator != null ? animator.gameObject : collider.gameObject;
    }

    private bool ApplyResponse(VisionRequestResult result, out string error)
    {
        error = null;
        try
        {
            // JsonUtility aceita campos ausentes; a forma do contrato é conferida antes.
            if (string.IsNullOrWhiteSpace(result.Json) || !ResponseShape.IsMatch(result.Json))
                throw new FormatException("Esperados position {x,y} numéricos e uma emotion válida.");
            ResponsePayload payload = JsonUtility.FromJson<ResponsePayload>(result.Json);
            if (payload == null || payload.position == null || !IsFinite(payload.position.x) || !IsFinite(payload.position.y)
                || payload.position.x < 0 || payload.position.x > 1 || payload.position.y < 0 || payload.position.y > 1
                || !Enum.TryParse(payload.emotion, out VLMEmotion receivedEmotion) || !Enum.IsDefined(typeof(VLMEmotion), receivedEmotion))
                throw new FormatException("Coordenadas ou emoção inválidas.");
            if (!hasSnapshot || snapshotRequestId != result.RequestId)
                throw new InvalidOperationException("A resposta não corresponde ao snapshot disponível.");

            fixationPoint = new Vector2((float)payload.position.x, (float)payload.position.y);
            lostTarget = false;
            debugRay = snapshotCamera.ViewportPointToRay(new Vector3(fixationPoint.x, fixationPoint.y, 0));
            hasDebugRay = true;

            if (receivedEmotion == VLMEmotion.NEUTRAL && payload.position.x == 0.5 && payload.position.y == 0.5)
            {
                state = CycleState.Neutral;
                lastDiagnostic = "Sem alvo: centro + NEUTRAL.";
                LogResponse(result.RequestId, receivedEmotion, -1, -1);
                return true;
            }

            if (!TryFindCell(fixationPoint, out GridCell cell, out int cellX, out int cellY)
                || !IsCellActive(cell))
            {
                state = CycleState.Neutral;
                lastDiagnostic = "Nenhum alvo válido na grade capturada.";
                LogResponse(result.RequestId, receivedEmotion, cellX, cellY);
                return true;
            }

            debugHitPoint = cell.WorldPoint;
            trackedRoot = cell.Root;
            if (cell.IsSurface)
            {
                trackedSurfaceCollider = cell.Collider;
                surfaceAnchor = new GameObject("VLM Surface Focus");
                surfaceAnchor.hideFlags = HideFlags.DontSave;
                surfaceAnchor.transform.SetParent(trackedRoot.transform, false);
                surfaceAnchor.transform.localPosition = cell.LocalPoint;
                currentFocus = new FixationObject(surfaceAnchor, Vector3.zero);
            }
            else
            {
                trackedRenderers = trackedRoot.GetComponentsInChildren<Renderer>();
                trackedColliders = trackedRoot.GetComponentsInChildren<Collider>();
                if (!TryTargetCenter(out Vector3 center))
                {
                    ReleaseTarget();
                    state = CycleState.Neutral;
                    lastDiagnostic = "O alvo perdeu sua bounding box.";
                    LogResponse(result.RequestId, receivedEmotion, cellX, cellY);
                    return true;
                }
                Vector3 localCenter = trackedRoot.transform.InverseTransformPoint(center);
                if (!IsFinite(localCenter)) throw new InvalidOperationException("Centro local do alvo inválido.");
                currentFocus = new FixationObject(trackedRoot, localCenter);
            }
            emotion = receivedEmotion;
            state = CycleState.Fixating;
            lastDiagnostic = "Alvo: " + trackedRoot.name;
            LogResponse(result.RequestId, receivedEmotion, cellX, cellY);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private bool TryFindCell(Vector2 point, out GridCell cell, out int selectedX, out int selectedY)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt(point.x * capturedResolution), 0, capturedResolution - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(point.y * capturedResolution), 0, capturedResolution - 1);
        selectedX = x;
        selectedY = y;
        cell = grid[y * capturedResolution + x];
        // Um alvo histórico removido não deve ser substituído por um alvo vizinho.
        if (cell.HasHit) return true;

        for (int ring = 1; ring <= capturedNeighbors; ring++)
        {
            bool found = false;
            float bestDistance = float.PositiveInfinity;
            for (int row = Mathf.Max(0, y - ring); row <= Mathf.Min(capturedResolution - 1, y + ring); row++)
            {
                for (int col = Mathf.Max(0, x - ring); col <= Mathf.Min(capturedResolution - 1, x + ring); col++)
                {
                    if (Mathf.Max(Mathf.Abs(col - x), Mathf.Abs(row - y)) != ring) continue;
                    GridCell candidate = grid[row * capturedResolution + col];
                    if (!IsCellActive(candidate)) continue;
                    Vector2 center = new Vector2((col + 0.5f) / capturedResolution, (row + 0.5f) / capturedResolution);
                    float distance = (center - point).sqrMagnitude;
                    if (distance > bestDistance || (distance == bestDistance && found && candidate.Distance >= cell.Distance)) continue;
                    found = true;
                    bestDistance = distance;
                    selectedX = col;
                    selectedY = row;
                    cell = candidate;
                }
            }
            if (found) return true;
        }
        return false;
    }

    private static bool IsCellActive(GridCell cell)
    {
        return cell.HasHit && cell.Root != null && cell.Root.activeInHierarchy && cell.Collider != null
            && cell.Collider.enabled && cell.Collider.gameObject.activeInHierarchy;
    }

    private bool TryTargetCenter(out Vector3 center)
    {
        Bounds bounds = default;
        bool found = false;
        if (trackedRenderers != null && trackedRenderers.Length > 0)
        {
            foreach (Renderer renderer in trackedRenderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy
                    || (capturedVisualLayers & (1 << renderer.gameObject.layer)) == 0 || IsAgent(renderer.transform)) continue;
                AccumulateBounds(renderer.bounds, ref bounds, ref found);
            }
        }
        else if (trackedColliders != null)
        {
            foreach (Collider collider in trackedColliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                AccumulateBounds(collider.bounds, ref bounds, ref found);
            }
        }
        center = bounds.center;
        return found && IsFinite(center);
    }

    private static void AccumulateBounds(Bounds candidate, ref Bounds total, ref bool found)
    {
        if (!IsFinite(candidate.center) || !IsFinite(candidate.extents) || candidate.size.sqrMagnitude <= 0) return;
        if (found) total.Encapsulate(candidate);
        else total = candidate;
        found = true;
    }

    private void RefreshTrackedTarget()
    {
        if (state != CycleState.Fixating) return;
        bool valid = trackedRoot != null && trackedRoot.activeInHierarchy;
        if (valid && surfaceAnchor != null)
            valid = trackedSurfaceCollider != null && trackedSurfaceCollider.enabled
                && trackedSurfaceCollider.gameObject.activeInHierarchy && IsFinite(surfaceAnchor.transform.position);
        else if (valid)
        {
            valid = TryTargetCenter(out Vector3 center);
            if (valid)
            {
                Vector3 localPoint = trackedRoot.transform.InverseTransformPoint(center);
                valid = IsFinite(localPoint);
                if (valid) currentFocus.localPoint = localPoint;
            }
        }
        if (valid) return;
        ReleaseTarget();
        lostTarget = true;
        state = CycleState.Neutral;
        lastDiagnostic = "O alvo foi destruído, desativado ou perdeu sua bounding box.";
        if (drawDebug) Debug.Log("[VLMController] " + lastDiagnostic, this);
    }

    public override FixationObject GetCurrentFocus()
    {
        if (!isActiveAndEnabled) return noFocus;
        RefreshTrackedTarget();
        return currentFocus ?? noFocus;
    }

    public override float GetCurrentFixationTime()
    {
        if (!isActiveAndEnabled || currentFocus == null || currentFocus.gameObject == null) return 0;
        if (state == CycleState.Recentering) return SafeDuration(returnToCenterWaitSeconds, 1f, 0.2f);
        return state == CycleState.Fixating ? activeFixationDuration : 0;
    }

    private void LogResponse(long id, VLMEmotion receivedEmotion, int x, int y)
    {
        if (!drawDebug) return;
        string target = trackedRoot != null ? trackedRoot.name : "none";
        Debug.Log($"[VLMController] Pedido {id}: ponto={fixationPoint}, célula=({x},{y}), alvo={target}, " +
            $"emoção recebida={receivedEmotion}, ativa={emotion}. {lastDiagnostic}", this);
    }

    private void Fail(string error)
    {
        ReleaseTarget();
        fixationPoint = new Vector2(0.5f, 0.5f);
        lastDiagnostic = error;
        Debug.LogWarning("[VLMController] " + error, this);
    }

    private void ReleaseTarget()
    {
        currentFocus = noFocus;
        emotion = VLMEmotion.NEUTRAL;
        activeFixationDuration = 0;
        trackedRoot = null;
        trackedRenderers = null;
        trackedColliders = null;
        trackedSurfaceCollider = null;
        if (surfaceAnchor != null) Destroy(surfaceAnchor);
        surfaceAnchor = null;
    }

    private void ClearGrid()
    {
        if (grid != null) Array.Clear(grid, 0, grid.Length);
        hasSnapshot = false;
        snapshotRequestId = 0;
    }

    private void InvalidateSnapshot()
    {
        ClearGrid();
        hasDebugRay = false;
    }

    private void OnDisable()
    {
        if (cycle != null) StopCoroutine(cycle);
        cycle = null;
        if (subscribedStreamer != null)
        {
            subscribedStreamer.CapturePrepared -= PrepareSnapshot;
            if (requestId != 0) subscribedStreamer.CancelRequest(requestId);
        }
        subscribedStreamer = null;
        requestId = 0;
        ReleaseTarget();
        InvalidateSnapshot();
        fixationPoint = new Vector2(0.5f, 0.5f);
        state = CycleState.Disabled;
        if (restAnchor != null) Destroy(restAnchor);
        restAnchor = null;
        restFocus = null;
        if (ownsSnapshotCamera && snapshotCamera != null) Destroy(snapshotCamera.gameObject);
        if (ownsSnapshotCamera) snapshotCamera = null;
        ownsSnapshotCamera = false;
    }

    private void OnDrawGizmos()
    {
        if (!drawDebug || !Application.isPlaying || Camera.current == null || Camera.current.cameraType != CameraType.SceneView) return;
        if (hasDebugRay)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(debugRay.origin, debugRay.direction * (snapshotCamera != null ? snapshotCamera.farClipPlane : 100f));
            if (HasSceneTarget) Gizmos.DrawSphere(debugHitPoint, 0.05f);
        }
        if (currentFocus != null && currentFocus.gameObject != null)
        {
            Vector3 point = currentFocus.GetFixationPoint();
            Gizmos.color = state == CycleState.Recentering ? Color.cyan : Color.green;
            Gizmos.DrawWireSphere(point, 0.12f);
        }
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool IsFinite(Vector3 point) => IsFinite(point.x) && IsFinite(point.y) && IsFinite(point.z);

    private static float SafeDuration(float value, float fallback, float minimum = 0)
    {
        return IsFinite(value) && value >= 0 ? Mathf.Clamp(value, minimum, 86400f) : Mathf.Max(minimum, fallback);
    }
}