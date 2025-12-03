using UnityEngine;
using BNG;

[DisallowMultipleComponent]
[RequireComponent(typeof(SmoothLocomotion))]
public class PlayerSlopeSliding : MonoBehaviour {
    [Header("Ground Detection")]
    [SerializeField] Transform probeOrigin;
    [SerializeField, Min(0.01f)] float probeRadius = 0.18f;
    [SerializeField, Min(0f)] float probeStartOffset = 0.15f;
    [SerializeField, Min(0.05f)] float probeDistance = 1.5f;
    [SerializeField] LayerMask slopeLayers = ~0;
    [SerializeField, Range(0f, 90f)] float maxSupportedAngle = 88f;
    [SerializeField, Min(0f)] float normalLerpSpeed = 18f;
    [SerializeField, Min(0f)] float groundedGraceTime = 0.12f;

    [Header("Movement Alignment")]
    [SerializeField] bool alignWhileAirborne;
    [SerializeField, Min(0f)] float minPlanarSpeed = 0.01f;
    [SerializeField, Min(0f)] float jumpBypassVerticalSpeed = 0.05f;
    [SerializeField, Min(0f)] float stickToGroundVelocity = 0.25f;

    [Header("Slope Sliding")]
    [SerializeField] bool enableSlopeSliding = true;
    [SerializeField] string slopeTag = "Slope";
    [SerializeField, Range(0f, 90f)] float minSlopeSlideAngle = 5f;
    [SerializeField, Min(0f)] float slideAccelerationPerDegree = 0.08f;
    [SerializeField, Min(0f)] float maxSlideSpeed = 6f;
    [SerializeField, Min(0f)] float groundedSlideStopRate = 20f;

    [Header("Diagnostics")]
    [SerializeField] bool logMotionAdjustments;
    [SerializeField] bool drawDebugInfo;
    [SerializeField] Color debugColor = Color.cyan;

    SmoothLocomotion _locomotion;
    BNGPlayerController _playerController;
    CharacterController _characterController;
    Rigidbody _playerRigidbody;

    [SerializeField, Min(0f)] float distanceFromGroundTolerance = 0.025f;
    [SerializeField, Min(0f)] float fallbackRaycastPadding = 0.1f;
    [SerializeField, Min(0f)] float feetOffset = 0.005f;

    Vector3 _currentGroundNormal = Vector3.up;
    Vector3 _targetGroundNormal = Vector3.up;
    float _lastGroundedTime = -10f;
    bool _isGrounded;
    int _lastGroundSampleFrame = -1;
    RaycastHit _lastGroundHit;
    Vector3 _slopeSlideVelocity;

    enum GroundDetectionSource {
        None,
        PlayerControllerIsGrounded,
        DistanceFromGround,
        CharacterController,
        RigidbodyContacts,
        PhysicsSphereCast,
        PhysicsRaycast
    }

    enum GroundNormalSource {
        None,
        PlayerControllerHit,
        PhysicsSphereCast,
        PhysicsRaycast
    }

    GroundDetectionSource _lastGroundSource = GroundDetectionSource.None;
    GroundNormalSource _lastNormalSource = GroundNormalSource.None;

    public Vector3 CurrentGroundNormal => _currentGroundNormal;
    public float CurrentSlopeAngle => Vector3.Angle(ReferenceUp, _currentGroundNormal);
    public bool HasValidGround => _lastGroundHit.collider != null;
    bool IsOnSlideSurface => HasValidGround && _lastGroundHit.collider.CompareTag(slopeTag);

    void Reset() {
        _locomotion = GetComponent<SmoothLocomotion>();
        if (_locomotion == null) {
            _locomotion = GetComponentInParent<SmoothLocomotion>();
        }
        probeOrigin = _locomotion ? _locomotion.transform : transform;
    }

    void Awake() {
        if (_locomotion == null) {
            _locomotion = GetComponent<SmoothLocomotion>() ?? GetComponentInParent<SmoothLocomotion>();
        }

        if (_locomotion != null) {
            _playerController = _locomotion.GetComponentInParent<BNGPlayerController>();
            _characterController = _locomotion.GetComponent<CharacterController>();
            _playerRigidbody = _locomotion.GetComponent<Rigidbody>();
        }
        else {
            _playerController = GetComponentInParent<BNGPlayerController>();
            _characterController = GetComponent<CharacterController>();
            _playerRigidbody = GetComponent<Rigidbody>();
        }

        if (probeOrigin == null) {
            probeOrigin = _locomotion ? _locomotion.transform : transform;
        }

        if (_playerController != null && slopeLayers == ~0) {
            slopeLayers = _playerController.GroundedLayers;
        }

        _currentGroundNormal = ReferenceUp;
        _targetGroundNormal = _currentGroundNormal;
    }

    void OnEnable() {
        if (_locomotion != null) {
            _locomotion.OnProcessMovement += ProcessMovement;
        }
    }

    void OnDisable() {
        if (_locomotion != null) {
            _locomotion.OnProcessMovement -= ProcessMovement;
        }
    }

    void Update() {
        RefreshGroundInfo(Time.deltaTime);
        DrawDebugInfo();
    }

    Vector3 ProcessMovement(Vector3 motion) {
        if (!enabled || _locomotion == null || motion == Vector3.zero) {
            return motion;
        }

        RefreshGroundInfo(Time.deltaTime);

        if (!ShouldAlignMotion()) {
            return motion;
        }

        Vector3 up = ReferenceUp;
        float verticalSpeed = Vector3.Dot(motion, up);
        bool bypassDueToJump = verticalSpeed > jumpBypassVerticalSpeed;

        Vector3 verticalComponent = up * verticalSpeed;
        Vector3 planarComponent = motion - verticalComponent;

        if (planarComponent.sqrMagnitude <= minPlanarSpeed * minPlanarSpeed) {
            LogMotion(motion, motion);
            return motion;
        }

        Vector3 projectedPlanar = Vector3.ProjectOnPlane(planarComponent, _currentGroundNormal);
        if (projectedPlanar.sqrMagnitude > 0.0001f) {
            projectedPlanar = projectedPlanar.normalized * planarComponent.magnitude;
        }

        Vector3 adjusted = projectedPlanar + verticalComponent;

        if (!bypassDueToJump && stickToGroundVelocity > 0f && ShouldStickToGround()) {
            adjusted += -up * stickToGroundVelocity;
        }

        adjusted += ApplySlopeSliding();

        LogMotion(motion, adjusted);
        return adjusted;
    }

    Vector3 ApplySlopeSliding() {
        if (!enableSlopeSliding) {
            return DecaySlideVelocity();
        }

        if (!HasValidGround) {
            return PreserveSlideVelocityInAir();
        }

        float slopeAngle = CurrentSlopeAngle;
        if (slopeAngle < minSlopeSlideAngle) {
            return DecaySlideVelocity();
        }

        if (!IsOnSlideSurface) {
            return DecaySlideVelocity();
        }

        Vector3 downhillDirection = Vector3.ProjectOnPlane(-ReferenceUp, _currentGroundNormal).normalized;
        if (downhillDirection.sqrMagnitude < 0.0001f) {
            return DecaySlideVelocity();
        }

        float acceleration = (slopeAngle - minSlopeSlideAngle) * slideAccelerationPerDegree;
        acceleration = Mathf.Max(acceleration, 0f);

        _slopeSlideVelocity += downhillDirection * acceleration * Time.deltaTime;
        _slopeSlideVelocity = Vector3.ClampMagnitude(_slopeSlideVelocity, maxSlideSpeed);

        return _slopeSlideVelocity;
    }

    Vector3 PreserveSlideVelocityInAir() {
        if (_slopeSlideVelocity.sqrMagnitude < 0.0001f) {
            return Vector3.zero;
        }

        return _slopeSlideVelocity;
    }

    Vector3 DecaySlideVelocity() {
        if (_slopeSlideVelocity.sqrMagnitude < 0.0001f) {
            _slopeSlideVelocity = Vector3.zero;
            return Vector3.zero;
        }

        float decay = groundedSlideStopRate * Time.deltaTime;
        _slopeSlideVelocity = Vector3.MoveTowards(_slopeSlideVelocity, Vector3.zero, decay);
        return _slopeSlideVelocity;
    }

    void RefreshGroundInfo(float deltaTime) {
        if (Time.frameCount == _lastGroundSampleFrame) {
            return;
        }

        _lastGroundSampleFrame = Time.frameCount;
        _isGrounded = EvaluateGroundedInternal();
        if (_isGrounded) {
            _lastGroundedTime = Time.time;
        }

        Vector3 referenceUp = ReferenceUp;
        bool canSampleSurface = _isGrounded || Time.time - _lastGroundedTime <= groundedGraceTime;

        if (canSampleSurface && TryGetGroundNormal(out Vector3 sampledNormal)) {
            _targetGroundNormal = sampledNormal;
        }
        else {
            _targetGroundNormal = referenceUp;
        }

        float lerpFactor = normalLerpSpeed > 0f ? 1f - Mathf.Exp(-normalLerpSpeed * Mathf.Max(deltaTime, 0f)) : 1f;
        _currentGroundNormal = Vector3.Slerp(_currentGroundNormal, _targetGroundNormal, lerpFactor);
    }

    bool ShouldAlignMotion() {
        if (alignWhileAirborne) {
            return true;
        }

        if (_isGrounded) {
            return true;
        }

        return Time.time - _lastGroundedTime <= groundedGraceTime;
    }

    bool ShouldStickToGround() {
        return _isGrounded || Time.time - _lastGroundedTime <= groundedGraceTime;
    }

    bool EvaluateGroundedInternal() {
        _lastGroundSource = GroundDetectionSource.None;

        if (_playerController != null) {
            if (_playerController.IsGrounded()) {
                _lastGroundSource = GroundDetectionSource.PlayerControllerIsGrounded;
                return true;
            }

            if (_playerController.DistanceFromGround != float.MaxValue && _playerController.DistanceFromGround <= distanceFromGroundTolerance) {
                _lastGroundSource = GroundDetectionSource.DistanceFromGround;
                return true;
            }
        }

        if (_characterController != null && _characterController.enabled && _characterController.isGrounded) {
            _lastGroundSource = GroundDetectionSource.CharacterController;
            return true;
        }

        if (_playerRigidbody != null && _locomotion != null && _locomotion.ControllerType == PlayerControllerType.Rigidbody && _locomotion.GroundContacts > 0) {
            _lastGroundSource = GroundDetectionSource.RigidbodyContacts;
            return true;
        }

        Vector3 origin = GetFeetProbeOrigin();
        Vector3 direction = -ReferenceUp;
        float maxDistance = probeDistance + probeStartOffset + fallbackRaycastPadding;

        if (Physics.SphereCast(origin, probeRadius, direction, out RaycastHit sphereHit, maxDistance, slopeLayers, QueryTriggerInteraction.Ignore)) {
            _lastGroundHit = sphereHit;
            _lastGroundSource = GroundDetectionSource.PhysicsSphereCast;
            return true;
        }

        if (Physics.Raycast(origin, direction, out RaycastHit rayHit, maxDistance, slopeLayers, QueryTriggerInteraction.Ignore)) {
            _lastGroundHit = rayHit;
            _lastGroundSource = GroundDetectionSource.PhysicsRaycast;
            return true;
        }

        return false;
    }

    bool TryGetGroundNormal(out Vector3 normal) {
        _lastNormalSource = GroundNormalSource.None;

        if (_playerController != null && _playerController.groundHit.collider != null) {
            float controllerAngle = Vector3.Angle(ReferenceUp, _playerController.groundHit.normal);
            if (controllerAngle <= maxSupportedAngle) {
                _lastGroundHit = _playerController.groundHit;
                _lastNormalSource = GroundNormalSource.PlayerControllerHit;
                normal = _playerController.groundHit.normal.normalized;
                return true;
            }
        }

        Vector3 origin = GetFeetProbeOrigin();
        Vector3 direction = -ReferenceUp;
        float maxDistance = probeDistance + probeStartOffset + fallbackRaycastPadding;

        if (Physics.SphereCast(origin, probeRadius, direction, out RaycastHit sphereHit, maxDistance, slopeLayers, QueryTriggerInteraction.Ignore)) {
            float sphereAngle = Vector3.Angle(ReferenceUp, sphereHit.normal);
            if (sphereAngle <= maxSupportedAngle) {
                _lastGroundHit = sphereHit;
                _lastNormalSource = GroundNormalSource.PhysicsSphereCast;
                normal = sphereHit.normal.normalized;
                return true;
            }
        }

        if (Physics.Raycast(origin, direction, out RaycastHit rayHit, maxDistance, slopeLayers, QueryTriggerInteraction.Ignore)) {
            float rayAngle = Vector3.Angle(ReferenceUp, rayHit.normal);
            if (rayAngle <= maxSupportedAngle) {
                _lastGroundHit = rayHit;
                _lastNormalSource = GroundNormalSource.PhysicsRaycast;
                normal = rayHit.normal.normalized;
                return true;
            }
        }

        normal = ReferenceUp;
        return false;
    }

    Vector3 ReferenceUp {
        get {
            if (_playerController != null) {
                return _playerController.transform.up;
            }

            return transform.up;
        }
    }

    Vector3 GetFeetProbeOrigin() {
        if (_characterController != null && _characterController.enabled) {
            Vector3 ccPosition = _characterController.transform.position;
            float feetY = ccPosition.y + _characterController.center.y - (_characterController.height * 0.5f) + _characterController.skinWidth + feetOffset;
            return new Vector3(ccPosition.x, feetY, ccPosition.z);
        }

        if (_playerController != null) {
            Vector3 pcPosition = _playerController.transform.position;
            return pcPosition + (ReferenceUp * -(_playerController.CharacterControllerYOffset)) + (ReferenceUp * feetOffset);
        }

        Vector3 origin = probeOrigin ? probeOrigin.position : transform.position;
        origin += ReferenceUp * (probeStartOffset - Mathf.Abs(feetOffset));
        return origin;
    }

    void LogMotion(Vector3 original, Vector3 adjusted) {
        if (!logMotionAdjustments) {
            return;
        }

        Debug.Log($"[PlayerSlopeSliding] Grounded: {_isGrounded} Source: {_lastGroundSource} NormalSource: {_lastNormalSource} Angle: {CurrentSlopeAngle:F2} Original: {original} Adjusted: {adjusted}");
    }

    void DrawDebugInfo() {
        if (!drawDebugInfo) {
            return;
        }

        Vector3 origin = GetFeetProbeOrigin();
        Vector3 down = -ReferenceUp * (probeDistance + probeStartOffset);
        Debug.DrawLine(origin, origin + down, debugColor);
        Debug.DrawRay(origin, _currentGroundNormal, Color.green);

        if (HasValidGround) {
            Debug.DrawRay(_lastGroundHit.point, _lastGroundHit.normal, Color.yellow);
        }
    }

    void OnDrawGizmosSelected() {
        if (!drawDebugInfo) {
            return;
        }

        Gizmos.color = debugColor;
        Vector3 origin = probeOrigin ? probeOrigin.position : transform.position;
        Gizmos.DrawWireSphere(origin, probeRadius);
        Gizmos.color = Color.green;
        Gizmos.DrawLine(origin, origin + _currentGroundNormal * 0.4f);
    }
}