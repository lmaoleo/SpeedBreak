using Unity.VisualScripting;
using UnityEngine;

namespace BNG {
    
    public class TelekinesisController : MonoBehaviour {
        [Header("Detection Settings")]
        public RemoteGrabType detectionType = RemoteGrabType.Raycast;
        public float raycastLength = 20f;
        public float sphereCastLength = 20f;
        public float sphereCastRadius = 0.05f;
        public LayerMask telekinesisLayers = ~0;
        [Tooltip("Ignore sphere hits that are too close (to avoid grabbing own hand)")]
        public bool skipCloseSphereHits = true;
        [Tooltip("Minimum distance from controller to accept a sphere hit")]
        public float sphereIgnoreDistance = 0.3f;

        [Header("Telekinesis Settings")]
        [Tooltip("Distance from controller where object will be held")]
        public float holdDistance = 2f;
        [Tooltip("Minimum distance object can be moved to")]
        public float minDistance = 0.5f;
        [Tooltip("Maximum distance object can be moved to")]
        public float maxDistance = 10f;
        [Tooltip("How fast the object moves to target position")]
        public float moveSpeed = 10f;
        [Tooltip("How fast the object rotates with controller")]
        public float rotationSpeed = 5f;
        [Tooltip("If true object follows controller rotation")]
        public bool enableRotationControl = true;

        [Header("Input Settings")]
        [Tooltip("Which hand controller this is attached to")]
        public ControllerHand handSide = ControllerHand.Right;
        [Tooltip("Button to activate telekinesis")]
        public GrabButton telekinesisButton = GrabButton.Grip;
        [Tooltip("Speed of distance adjustment using thumbstick")]
        public float distanceAdjustSpeed = 2f;
        [Tooltip("Only grab objects with this tag (empty = any)")]
        public string requiredTag = "";

        [Header("Visual Feedback")]
        public bool showGizmos = true;
        public Color rayColor = Color.cyan;
        public Color holdingColor = Color.yellow;
        [Tooltip("Show line renderer ray")]
        public bool showGameVisuals = true;

        [Header("Throw Settings")]
        [Tooltip("Apply captured velocities on release")]
        public bool inheritMomentumOnRelease = true;
        [Range(0f,1f), Tooltip("Lerp factor for velocity smoothing")]
        public float velocitySmoothing = 0.35f;
        [Tooltip("Multiplier for linear velocity on release")]
        public float throwVelocityMultiplier = 1f;
        [Tooltip("Multiplier for angular velocity on release")]
        public float throwAngularVelocityMultiplier = 1f;
        [Tooltip("Max linear speed applied")]
        public float maxThrowSpeed = 20f;
        [Tooltip("Max angular speed (rad/s)")]
        public float maxAngularSpeed = 20f;
        [Tooltip("Enable gravity permanently when object is first grabbed")]
        public bool enableGravityOnFirstGrab = true;

        // Runtime state
        private GameObject _heldObject;
        private Rigidbody _heldRigidbody;
        private bool _wasKinematic;
        private bool _wasUsingGravity;
        private float _currentDistance;
        private Quaternion _rotationOffset;
        private InputBridge _input;
        private LineRenderer _lineRenderer;
        private Vector3 _smoothedVelocity;
        private Vector3 _smoothedAngularVelocity;
        private readonly RaycastHit[] _sphereHitBuffer = new RaycastHit[32];

        void Start() {
            _currentDistance = holdDistance;
            _input = InputBridge.Instance;
            SetupLineRenderer();
        }

        void Update() {
            HandleInput();
            if (_heldObject) {
                MoveHeldObject();
                AdjustDistance();
            }
            UpdateLine();
        }

        void HandleInput() {
            if (TelekinesisPressedDown() && _heldObject.IsUnityNull()) {
                TryStartTelekinesis();
            } else if (!TelekinesisHeld() && !_heldObject.IsUnityNull()) {
                ReleaseTelekinesis();
            }
        }

        bool TelekinesisPressedDown() {
            if (_input.IsUnityNull()) return false;
            bool left = handSide == ControllerHand.Left;
            bool grip = telekinesisButton == GrabButton.Grip;
            return left ? (grip ? _input.LeftGripDown : _input.LeftTriggerDown) : (grip ? _input.RightGripDown : _input.RightTriggerDown);
        }

        bool TelekinesisHeld() {
            if (_input.IsUnityNull()) return false;
            bool left = handSide == ControllerHand.Left;
            bool grip = telekinesisButton == GrabButton.Grip;
            float thresh = 0.5f;
            return left ? (grip ? _input.LeftGrip > thresh : _input.LeftTrigger > thresh) : (grip ? _input.RightGrip > thresh : _input.RightTrigger > thresh);
        }

        void TryStartTelekinesis() {
            if (DetectTarget(out RaycastHit hit) && ValidTarget(hit))
                BeginHold(hit.collider.gameObject, hit.collider.attachedRigidbody);
        }

        bool DetectTarget(out RaycastHit hit) {
            if (detectionType == RemoteGrabType.Raycast)
                return Physics.Raycast(transform.position, transform.forward, out hit, maxDistance, telekinesisLayers);
            
            var origin = transform.position;
            var direction = transform.forward;
            int hitCount = Physics.SphereCastNonAlloc(origin, sphereCastRadius, direction, _sphereHitBuffer, sphereCastLength, telekinesisLayers);
            if (hitCount > 0) {
                System.Array.Sort(_sphereHitBuffer, 0, hitCount, System.Collections.Generic.Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance)));
                for (int i = 0; i < hitCount; i++) {
                    var h = _sphereHitBuffer[i];
                    if (skipCloseSphereHits && h.distance < sphereIgnoreDistance)
                        continue;
                    if (ValidTarget(h)) {
                        hit = h;
                        return true;
                    }
                }
            }
            hit = default;
            return false;
        }

        bool ValidTarget(RaycastHit hit) {
            if (!hit.collider || !hit.collider.attachedRigidbody || hit.collider.gameObject.isStatic) return false;
            if (!string.IsNullOrEmpty(requiredTag)) return hit.collider.CompareTag(requiredTag);
            return true;
        }

        void BeginHold(GameObject obj, Rigidbody rb) {
            _heldObject = obj;
            _heldRigidbody = rb;
            if (_heldRigidbody) {
                _wasKinematic = _heldRigidbody.isKinematic;
                _wasUsingGravity = _heldRigidbody.useGravity;
                _heldRigidbody.isKinematic = true;
                _heldRigidbody.useGravity = false;
                
                if (enableGravityOnFirstGrab && !_wasUsingGravity)
                    _wasUsingGravity = false;
                
                SpinningCube spinningCube = _heldObject.GetComponent<SpinningCube>();
                if (!spinningCube.IsUnityNull() && spinningCube.shouldSpin)
                    spinningCube.shouldSpin = false;
            }
            _currentDistance = Vector3.Distance(transform.position, _heldObject.transform.position);
            if (enableRotationControl)
                _rotationOffset = Quaternion.Inverse(transform.rotation) * _heldObject.transform.rotation;
            _smoothedVelocity = Vector3.zero;
            _smoothedAngularVelocity = Vector3.zero;
        }

        void ReleaseTelekinesis() {
            if (_heldRigidbody) {
                _heldRigidbody.isKinematic = _wasKinematic;
                if (enableGravityOnFirstGrab)
                    _heldRigidbody.useGravity = true;
                else
                    _heldRigidbody.useGravity = _wasUsingGravity;
                
                if (inheritMomentumOnRelease && !_wasKinematic) {
                    if (_smoothedVelocity.magnitude > maxThrowSpeed)
                        _smoothedVelocity = _smoothedVelocity.normalized * maxThrowSpeed;
                    if (_smoothedAngularVelocity.magnitude > maxAngularSpeed)
                        _smoothedAngularVelocity = _smoothedAngularVelocity.normalized * maxAngularSpeed;
                    
                    _heldRigidbody.linearVelocity = _smoothedVelocity * throwVelocityMultiplier;
                    _heldRigidbody.angularVelocity = _smoothedAngularVelocity * throwAngularVelocityMultiplier;
                }
            }
            _heldObject = null;
            _heldRigidbody = null;
        }

        void MoveHeldObject() {
            Vector3 targetPos = transform.position + transform.forward * _currentDistance;
            Vector3 currentPos = _heldObject.transform.position;
            Quaternion currentRot = _heldObject.transform.rotation;
            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            Vector3 nextPos = Vector3.Lerp(currentPos, targetPos, Time.deltaTime * moveSpeed);
            Vector3 instVel = (nextPos - currentPos) / dt;
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, instVel, velocitySmoothing);
            if (enableRotationControl) {
                Quaternion targetRot = transform.rotation * _rotationOffset;
                Quaternion nextRot = Quaternion.Slerp(currentRot, targetRot, Time.deltaTime * rotationSpeed);
                Quaternion delta = nextRot * Quaternion.Inverse(currentRot);
                delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
                if (angleDeg > 180f) angleDeg -= 360f;
                Vector3 instAngVel = Vector3.zero;
                if (axis.sqrMagnitude > 1e-6f) {
                    axis.Normalize();
                    instAngVel = axis * (angleDeg * Mathf.Deg2Rad) / dt;
                }
                _smoothedAngularVelocity = Vector3.Lerp(_smoothedAngularVelocity, instAngVel, velocitySmoothing);
                _heldObject.transform.rotation = nextRot;
            }
            _heldObject.transform.position = nextPos;
        }

        void AdjustDistance() {
            if (_input.IsUnityNull()) return;
            float y = handSide == ControllerHand.Left ? _input.LeftThumbstickAxis.y : _input.RightThumbstickAxis.y;
            if (Mathf.Abs(y) > 0.1f)
                _currentDistance = Mathf.Clamp(_currentDistance + y * distanceAdjustSpeed * Time.deltaTime, minDistance, maxDistance);
        }

        void SetupLineRenderer() {
            if (!showGameVisuals) return;
            _lineRenderer = GetComponent<LineRenderer>() ?? gameObject.AddComponent<LineRenderer>();
            _lineRenderer.startWidth = 0.01f; _lineRenderer.endWidth = 0.01f;
            if (_lineRenderer.material.IsUnityNull()) {
                var mat = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default"));
                _lineRenderer.material = mat;
            }
            _lineRenderer.startColor = rayColor; _lineRenderer.endColor = rayColor;
            _lineRenderer.enabled = true;
        }

        void UpdateLine() {
            if (!showGameVisuals || !_lineRenderer) {
                if (_lineRenderer) _lineRenderer.enabled = false;
                return;
            }
            _lineRenderer.enabled = true;
            if (_heldObject) {
                _lineRenderer.startColor = holdingColor; _lineRenderer.endColor = holdingColor;
                _lineRenderer.SetPosition(0, transform.position);
                _lineRenderer.SetPosition(1, _heldObject.transform.position);
            } else {
                _lineRenderer.startColor = rayColor; _lineRenderer.endColor = rayColor;
                _lineRenderer.SetPosition(0, transform.position);
                float len = detectionType == RemoteGrabType.Raycast ? raycastLength : sphereCastLength;
                _lineRenderer.SetPosition(1, transform.position + transform.forward * len);
            }
        }

#if UNITY_EDITOR
        void OnDrawGizmos() {
            if (!isActiveAndEnabled || !showGizmos) return;
            Color c = _heldObject ? holdingColor : rayColor;
            if (detectionType == RemoteGrabType.Raycast)
                Debug.DrawRay(transform.position, transform.forward * raycastLength, c);
            else if (detectionType == RemoteGrabType.Spherecast) {
                Debug.DrawRay(transform.position, transform.forward * sphereCastLength, c);
                Gizmos.color = c;
                Gizmos.DrawWireSphere(transform.position + transform.forward * sphereCastLength, sphereCastRadius);
            }
            if (_heldObject || Application.isPlaying) {
                Gizmos.color = holdingColor;
                Gizmos.DrawWireSphere(transform.position + transform.forward * _currentDistance, 0.1f);
            }
        }
#endif
    }
}
