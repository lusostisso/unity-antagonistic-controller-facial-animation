using System;
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
        Fixating,
        Neutral
    }

    public enum RequestState
    {
        Idle,
        Capturing,
        AwaitingResponse
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

    private sealed class PendingFixation
    {
        public long RequestId;
        public Vector2 Point;
        public VLMEmotion Emotion;
        public Ray HistoricalRay;
        public float FarClipPlane;
        public GridCell Cell;
        public bool HasTarget;
        public int CellX = -1;
        public int CellY = -1;
        public int VisualLayers;
        public string Diagnostic;
    }

    public const float FixationDurationSeconds = 5f;
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
    [Tooltip("Referência do corpo: +Z aponta para frente. Se vazio, usa Agent Root.")]
    public Transform bodyReference;

    [Header("Scene targets")]
    public LayerMask scanLayerMask = Physics.DefaultRaycastLayers & ~((1 << 3) | (1 << 5));
    public LayerMask groundLayers = 1 << 6;
    [Range(8, 128)]
    public int gridResolution = 64;
    [Range(0, 4)]
    public int neighborCells = 2;

    [Header("Forward gaze")]
    [Min(0.1f)]
    public float restDistance = 10f;

    public float fixationTime => FixationDurationSeconds;

    [Header("Debug")]
    public bool drawDebug;
    [SerializeField]
    private Vector2 fixationPoint = new Vector2(0.5f, 0.5f);
    [SerializeField]
    private VLMEmotion emotion = VLMEmotion.NEUTRAL;
    [SerializeField]
    private CycleState state = CycleState.Disabled;
    [SerializeField]
    private RequestState networkState = RequestState.Idle;
    [SerializeField]
    private FixationObject currentFocus;
    [SerializeField]
    private string lastDiagnostic;

    public Vector2 FixationPoint => fixationPoint;
    public VLMEmotion Emotion => emotion;
    public CycleState State => state;
    public RequestState NetworkState => networkState;
    public string LastDiagnostic => lastDiagnostic;
    public bool HasSceneTarget => state == CycleState.Fixating && trackedRoot != null && trackedRoot.activeInHierarchy;

    private readonly FixationObject noFocus = new FixationObject(null, Vector3.zero);
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
    private int trackedVisualLayers;
    private bool hasSnapshot;
    private long snapshotRequestId;
    private long requestId;
    private GameObject trackedRoot;
    private Renderer[] trackedRenderers;
    private Collider[] trackedColliders;
    private Collider trackedSurfaceCollider;
    private GameObject surfaceAnchor;
    private double fixationEndsAt;
    private PendingFixation pendingFixation;
    private bool hasDebugRay;
    private Ray debugRay;
    private float debugRayDistance;
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
            ReturnToForward("Aguardando o primeiro alvo.");
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
        Transform body = bodyReference != null ? bodyReference : agentRoot != null ? agentRoot : transform;
        Vector3 forward = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
        restAnchor.transform.SetParent(body, true);
        restAnchor.transform.position = source.transform.position + forward * SafeDuration(restDistance, 10f, 0.1f);
    }

    private bool IsAheadOfBody(Vector3 point)
    {
        Transform body = bodyReference != null ? bodyReference : agentRoot != null ? agentRoot : transform;
        Vector3 forward = Vector3.ProjectOnPlane(body.forward, Vector3.up);
        return IsFinite(point) && IsFinite(forward) && forward.sqrMagnitude > 0.000001f
            && Vector3.Dot(point - body.position, forward) > 0;
    }

    private void ReturnToForward(string diagnostic)
    {
        ReleaseTarget();
        Camera source = subscribedStreamer != null ? subscribedStreamer.visionCamera : null;
        if (source != null) UpdateRestFocus(source);
        currentFocus = restAnchor != null ? restFocus : noFocus;
        state = restAnchor != null ? CycleState.Recentering : CycleState.Neutral;
        lastDiagnostic = diagnostic;
    }

    private void Update()
    {
        RefreshTrackedTarget();
        if (subscribedStreamer == null || subscribedStreamer.visionCamera == null)
        {
            if (subscribedStreamer != null && requestId != 0) subscribedStreamer.CancelRequest(requestId);
            requestId = 0;
            networkState = RequestState.Idle;
            pendingFixation = null;
            InvalidateSnapshot();
            Fail("VisionStreamer ou câmera removidos.");
            return;
        }

        if (subscribedStreamer.TryGetResult(out VisionRequestResult result))
        {
            if (requestId != 0 && result.RequestId == requestId)
                ReceiveResult(result);
        }
        else if (requestId != 0 && (!subscribedStreamer.IsReady || subscribedStreamer.ActiveRequestId != requestId))
        {
            requestId = 0;
            networkState = RequestState.Idle;
            ClearGrid();
            Fail(subscribedStreamer.LastError ?? "Pedido descartado após desativação do VisionStreamer.");
        }

        if (state != CycleState.Fixating && pendingFixation != null)
        {
            PendingFixation next = pendingFixation;
            pendingFixation = null;
            BeginFixation(next);
        }
        if (state == CycleState.Recentering) UpdateRestFocus(subscribedStreamer.visionCamera);

        if (requestId == 0 && subscribedStreamer.IsReady && !subscribedStreamer.IsBusy)
        {
            if (subscribedStreamer.TryCaptureAndSend())
            {
                requestId = subscribedStreamer.ActiveRequestId;
                networkState = RequestState.Capturing;
            }
        }
    }

    private void ReceiveResult(VisionRequestResult result)
    {
        requestId = 0;
        networkState = RequestState.Idle;
        PendingFixation selection = null;
        string error = result.Error;
        bool valid = result.Status == VisionRequestStatus.Response && TryResolveResponse(result, out selection, out error);
        if (valid)
        {
            // O transporte continua; a fixação ativa completa seus cinco segundos.
            pendingFixation = selection;
            if (drawDebug)
                Debug.Log($"[VLMController] Resposta {result.RequestId}: ponto={selection.Point}, emoção={selection.Emotion}; próximo frame liberado.", this);
        }
        else
        {
            Fail(error ?? "Resposta inválida.");
        }
        ClearGrid();
    }

    private void PrepareSnapshot(long id, Camera source)
    {
        if (id != requestId || source != subscribedStreamer.visionCamera) return;
        ClearGrid();
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
        networkState = RequestState.AwaitingResponse;
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

    private bool TryResolveResponse(VisionRequestResult result, out PendingFixation selection, out string error)
    {
        selection = null;
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

            Vector2 point = new Vector2((float)payload.position.x, (float)payload.position.y);
            selection = new PendingFixation
            {
                RequestId = result.RequestId,
                Point = point,
                Emotion = receivedEmotion,
                HistoricalRay = snapshotCamera.ViewportPointToRay(new Vector3(point.x, point.y, 0)),
                FarClipPlane = snapshotCamera.farClipPlane,
                VisualLayers = capturedVisualLayers
            };

            if (receivedEmotion == VLMEmotion.NEUTRAL && payload.position.x == 0.5 && payload.position.y == 0.5)
            {
                selection.Diagnostic = "Sem alvo: centro + NEUTRAL.";
                return true;
            }

            selection.HasTarget = TryFindCell(point, out GridCell cell, out int cellX, out int cellY) && IsCellActive(cell);
            selection.Cell = cell;
            selection.CellX = cellX;
            selection.CellY = cellY;
            selection.Diagnostic = selection.HasTarget ? "Alvo associado à grade." : "Nenhum alvo válido na grade capturada.";
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void BeginFixation(PendingFixation selection)
    {
        ReleaseTarget();
        fixationPoint = selection.Point;
        debugRay = selection.HistoricalRay;
        debugRayDistance = selection.FarClipPlane;
        hasDebugRay = true;
        try
        {
            GridCell cell = selection.Cell;
            if (!selection.HasTarget || !IsCellActive(cell))
            {
                ReturnToForward(selection.HasTarget ? "O alvo pendente foi removido ou desativado." : selection.Diagnostic);
                LogResponse(selection.RequestId, selection.Emotion, selection.CellX, selection.CellY);
                return;
            }

            debugHitPoint = cell.WorldPoint;
            trackedRoot = cell.Root;
            trackedVisualLayers = selection.VisualLayers;
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
                    ReturnToForward("O alvo perdeu sua bounding box.");
                    LogResponse(selection.RequestId, selection.Emotion, selection.CellX, selection.CellY);
                    return;
                }
                Vector3 localCenter = trackedRoot.transform.InverseTransformPoint(center);
                if (!IsFinite(localCenter)) throw new InvalidOperationException("Centro local do alvo inválido.");
                currentFocus = new FixationObject(trackedRoot, localCenter);
            }
            emotion = selection.Emotion;
            state = CycleState.Fixating;
            fixationEndsAt = Time.timeAsDouble + FixationDurationSeconds;
            lastDiagnostic = "Alvo: " + trackedRoot.name;
            RefreshTrackedTarget();
            LogResponse(selection.RequestId, selection.Emotion, selection.CellX, selection.CellY);
        }
        catch (Exception exception)
        {
            ReturnToForward(exception.Message);
            Debug.LogWarning("[VLMController] " + exception.Message, this);
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
                    || (trackedVisualLayers & (1 << renderer.gameObject.layer)) == 0 || IsAgent(renderer.transform)) continue;
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
        if (Time.timeAsDouble >= fixationEndsAt)
        {
            ReturnToForward("Fixação de cinco segundos concluída.");
            return;
        }
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
        if (valid && IsAheadOfBody(currentFocus.GetFixationPoint())) return;
        ReturnToForward(valid
            ? "O alvo passou da linha do corpo; olhando para frente."
            : "O alvo foi destruído, desativado ou perdeu sua bounding box.");
        if (drawDebug) Debug.Log("[VLMController] " + lastDiagnostic, this);
    }

    public override FixationObject GetCurrentFocus()
    {
        if (!isActiveAndEnabled) return noFocus;
        RefreshTrackedTarget();
        if (state == CycleState.Recentering && subscribedStreamer != null && subscribedStreamer.visionCamera != null)
            UpdateRestFocus(subscribedStreamer.visionCamera);
        return currentFocus ?? noFocus;
    }

    public override float GetCurrentFixationTime()
    {
        if (!isActiveAndEnabled || currentFocus == null || currentFocus.gameObject == null) return 0;
        return state == CycleState.Fixating || state == CycleState.Recentering ? FixationDurationSeconds : 0;
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
        bool changed = lastDiagnostic != error;
        if (state != CycleState.Fixating)
        {
            ReturnToForward(error);
            fixationPoint = new Vector2(0.5f, 0.5f);
        }
        else lastDiagnostic = error;
        if (changed) Debug.LogWarning("[VLMController] " + error, this);
    }

    private void ReleaseTarget()
    {
        currentFocus = noFocus;
        emotion = VLMEmotion.NEUTRAL;
        fixationEndsAt = 0;
        trackedVisualLayers = 0;
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
        if (subscribedStreamer != null)
        {
            subscribedStreamer.CapturePrepared -= PrepareSnapshot;
            if (requestId != 0) subscribedStreamer.CancelRequest(requestId);
        }
        subscribedStreamer = null;
        requestId = 0;
        networkState = RequestState.Idle;
        pendingFixation = null;
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
            Gizmos.DrawRay(debugRay.origin, debugRay.direction * debugRayDistance);
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