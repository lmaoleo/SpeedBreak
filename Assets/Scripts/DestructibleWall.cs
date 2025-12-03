using UnityEngine;
using Random = UnityEngine.Random;

public class DestructibleWall : MonoBehaviour
{
	[Header("Destruction Settings")]
	[Tooltip("Only objects with this Tag will destroy the wall on collision/trigger.")] 
	public string destroyerTag = "Player"; // Default tag, change in Inspector.

	[Tooltip("If true, use trigger events (OnTriggerEnter). If false, use physics collisions (OnCollisionEnter).")]
	public bool useTrigger = false;

	[Tooltip("Optional: Spawn this effect prefab when destroyed.")]
	public GameObject destroyEffectPrefab;

	[Tooltip("Optional extra delay before actually destroying the wall (seconds). Set 0 for immediate.")]
	public float destroyDelay = 0f;

	[Header("Fracture Settings")]
	[Tooltip("Spawn physics debris instead of instantly destroying the wall.")]
	public bool spawnDebris = true;

	[Header("Auto Pieces")]
	[Tooltip("Desired approximate piece size in meters along each axis.")]
	public Vector3 targetPieceSize = new Vector3(0.25f, 0.25f, 0.25f);

	[Tooltip("Maximum pieces per axis when auto-computing.")]
	public int maxPiecesPerAxis = 20;

	[Tooltip("Cap on total spawned pieces regardless of axis counts.")]
	public int maxPiecesTotal = 256;

	[Tooltip("Small gap between debris cubes to reduce initial interpenetration.")]
	public float gap = 0.01f;

	[Tooltip("Minimum size for each piece on any axis (meters).")]
	public float minPieceSize = 0.03f;

	[Tooltip("Inherit the first material from this object for debris.")]
	public bool inheritMaterial = true;
	public Material debrisMaterialOverride;

	[Tooltip("Optional parent for spawned debris.")]
	public Transform debrisParent;

	[Header("Debris Physics")] 
	public float explosionForce = 6f;
	public float explosionRadius = 2.5f;
	public float upwardsModifier = 0.2f;
	public Vector3 extraRandomImpulseRange = new Vector3(0.2f, 0.4f, 0.2f);
	public float randomTorque = 1.5f;
	public float debrisLifetime = 6f;

	[Header("Debris Collision")]
	[Tooltip("If false, spawned debris will ignore collisions with any object with the specified Ignore Collision Tag. Enabled by default.")]
	public bool debrisPlayerCollision = true;
	[Tooltip("Tag of objects that debris should ignore collisions with when Debris Player Collision is disabled.")]
	public string ignoreCollisionTag = "Player";

	[Header("Debris Texturing")]
	[Tooltip("If true, debris meshes are generated with UVs scaled to world size so textures keep consistent texel density.")]
	public bool autoTileDebrisTextures = true;
	[Tooltip("How many texture tiles per 1 meter. 1 = one texture tile spans 1 meter.")]
	public float tilesPerMeter = 1f;

	// Runtime computed piece counts
	private int piecesX;
	private int piecesY;
	private int piecesZ;

	private void OnValidate() {
		if (string.IsNullOrEmpty(destroyerTag))
			destroyerTag = "Player";

		if (targetPieceSize.x <= 0f || targetPieceSize.y <= 0f || targetPieceSize.z <= 0f)
			targetPieceSize = new Vector3(0.25f, 0.25f, 0.25f);
		
		maxPiecesPerAxis = Mathf.Max(1, maxPiecesPerAxis);
		maxPiecesTotal = Mathf.Max(1, maxPiecesTotal);
		gap = Mathf.Max(0f, gap);
		tilesPerMeter = Mathf.Max(0.01f, tilesPerMeter);
	}

	private void OnCollisionEnter(Collision collision) {
		if (useTrigger) return;
		TryDestroy(collision.gameObject);
	}

	private void OnTriggerEnter(Collider other) {
		if (!useTrigger) return;
		TryDestroy(other.gameObject);
	}

	private void TryDestroy(GameObject other) {
		if (!other.CompareTag(destroyerTag)) return;

		if (destroyEffectPrefab != null)
			Instantiate(destroyEffectPrefab, transform.position, transform.rotation);

		if (spawnDebris)
			FractureIntoGrid();

		if (destroyDelay <= 0f)
			Destroy(gameObject);
		else
			Destroy(gameObject, destroyDelay);
	}

	private void FractureIntoGrid() {
		Bounds b;
		if (!TryGetSourceBounds(out b)) {
			SpawnDebrisCube(transform.position, transform.rotation, Vector3.one * 0.25f);
			return;
		}

		ComputeAutoPieces(b.size);

		Vector3 size = b.size;
		Vector3 pieceSize = new Vector3(
			Mathf.Max(minPieceSize, size.x / piecesX),
			Mathf.Max(minPieceSize, size.y / piecesY),
			Mathf.Max(minPieceSize, size.z / piecesZ)
		);

		Vector3 min = b.min;
		for (int ix = 0; ix < piecesX; ix++) {
			for (int iy = 0; iy < piecesY; iy++) {
				for (int iz = 0; iz < piecesZ; iz++) {
					Vector3 center = new Vector3(
						min.x + (ix + 0.5f) * pieceSize.x,
						min.y + (iy + 0.5f) * pieceSize.y,
						min.z + (iz + 0.5f) * pieceSize.z
					);

					Vector3 finalScale = new Vector3(
						Mathf.Max(minPieceSize, pieceSize.x - gap),
						Mathf.Max(minPieceSize, pieceSize.y - gap),
						Mathf.Max(minPieceSize, pieceSize.z - gap)
					);

					SpawnDebrisCube(center, transform.rotation, finalScale);
				}
			}
		}
	}

	private void ComputeAutoPieces(Vector3 size) {
		int CountForAxis(float axisSize, float target) {
			float denom = Mathf.Max(minPieceSize, Mathf.Max(0.01f, target));
			int c = Mathf.RoundToInt(axisSize / denom);
			c = Mathf.Max(1, c);
			return Mathf.Clamp(c, 1, maxPiecesPerAxis);
		}
		piecesX = CountForAxis(size.x, targetPieceSize.x);
		piecesY = CountForAxis(size.y, targetPieceSize.y);
		piecesZ = CountForAxis(size.z, targetPieceSize.z);

		ReduceToTotalCap(maxPiecesTotal);
	}

	private void ReduceToTotalCap(int cap) {
		cap = Mathf.Max(1, cap);
		for (int safety = 0; safety < 10000; safety++) {
			int total = piecesX * piecesY * piecesZ;
			if (total <= cap) break;

			if (piecesZ >= piecesY && piecesZ >= piecesX && piecesZ > 1)
				piecesZ--;
			else if (piecesY >= piecesX && piecesY > 1)
				piecesY--;
			else if (piecesX > 1)
				piecesX--;
			else
				break;
		}
	}

	private bool TryGetSourceBounds(out Bounds bounds)
	{
		var renderers = GetComponentsInChildren<Renderer>();
		if (renderers != null && renderers.Length > 0) {
			bounds = renderers[0].bounds;
			for (int i = 1; i < renderers.Length; i++)
				bounds.Encapsulate(renderers[i].bounds);
			return true;
		}

		var colliders = GetComponentsInChildren<Collider>();
		if (colliders != null && colliders.Length > 0) {
			bounds = colliders[0].bounds;
			for (int i = 1; i < colliders.Length; i++)
				bounds.Encapsulate(colliders[i].bounds);
			return true;
		}

		bounds = default;
		return false;
	}

	private void SpawnDebrisCube(Vector3 position, Quaternion rotation, Vector3 worldScale) {
		GameObject debris;
		MeshRenderer mr;
		if (autoTileDebrisTextures) {
			debris = new GameObject($"Debris_{name}");
			debris.transform.position = position;
			debris.transform.rotation = rotation;
			debris.transform.localScale = Vector3.one;

			var mf = debris.AddComponent<MeshFilter>();
			mr = debris.AddComponent<MeshRenderer>();
			mf.sharedMesh = BuildTiledCubeMesh(worldScale, tilesPerMeter);

			var box = debris.AddComponent<BoxCollider>();
			box.size = worldScale;
		}
		else {
			debris = GameObject.CreatePrimitive(PrimitiveType.Cube);
			debris.name = $"Debris_{name}";
			debris.transform.position = position;
			debris.transform.rotation = rotation;
			debris.transform.localScale = worldScale;
			mr = debris.GetComponent<MeshRenderer>();
		}

		if (debrisParent != null) debris.transform.SetParent(debrisParent);

		var rend = mr;
		if (inheritMaterial) {
			var srcRend = GetComponentInChildren<Renderer>();
			if (srcRend != null)
				rend.sharedMaterial = srcRend.sharedMaterial;
		}
		if (debrisMaterialOverride != null)
			rend.sharedMaterial = debrisMaterialOverride;

		var rb = debris.GetComponent<Rigidbody>();
		if (rb == null) rb = debris.AddComponent<Rigidbody>();
		float volume = Mathf.Max(0.01f, worldScale.x * worldScale.y * worldScale.z);
		rb.mass = Mathf.Clamp(volume * 50f, 0.1f, 50f);
		rb.interpolation = RigidbodyInterpolation.Interpolate;

		if (!debrisPlayerCollision) {
			var debrisCollider = debris.GetComponent<Collider>();
			if (debrisCollider != null && !string.IsNullOrEmpty(ignoreCollisionTag)) {
				var targets = GameObject.FindGameObjectsWithTag(ignoreCollisionTag);
				for (int i = 0; i < targets.Length; i++) {
					var pcs = targets[i].GetComponentsInChildren<Collider>();
					for (int c = 0; c < pcs.Length; c++)
						Physics.IgnoreCollision(debrisCollider, pcs[c], true);
				}
			}
		}

		rb.AddExplosionForce(explosionForce, transform.position, Mathf.Max(0.1f, explosionRadius), upwardsModifier, ForceMode.Impulse);
		Vector3 rndImpulse = new Vector3(
			Random.Range(-extraRandomImpulseRange.x, extraRandomImpulseRange.x),
			Random.Range(0f, extraRandomImpulseRange.y),
			Random.Range(-extraRandomImpulseRange.z, extraRandomImpulseRange.z)
		);
		rb.AddForce(rndImpulse, ForceMode.Impulse);
		rb.AddTorque(Random.insideUnitSphere * randomTorque, ForceMode.Impulse);

		if (debrisLifetime > 0f)
			Destroy(debris, debrisLifetime);
	}

	private static Mesh BuildTiledCubeMesh(Vector3 size, float tilesPerMeter) {
		float hx = Mathf.Max(0.0001f, size.x * 0.5f);
		float hy = Mathf.Max(0.0001f, size.y * 0.5f);
		float hz = Mathf.Max(0.0001f, size.z * 0.5f);

		var verts = new Vector3[24];
		var norms = new Vector3[24];
		var uvs = new Vector2[24];
		var tris = new int[36];

		int vi = 0;
		int ti = 0;

		void AddFace(Vector3 normal, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, float uLen, float vLen) {
			verts[vi + 0] = v0; verts[vi + 1] = v1; verts[vi + 2] = v2; verts[vi + 3] = v3;
			norms[vi + 0] = normal; norms[vi + 1] = normal; norms[vi + 2] = normal; norms[vi + 3] = normal;
			uvs[vi + 0] = new Vector2(0, 0);
			uvs[vi + 1] = new Vector2(uLen, 0);
			uvs[vi + 2] = new Vector2(uLen, vLen);
			uvs[vi + 3] = new Vector2(0, vLen);

			tris[ti + 0] = vi + 0; tris[ti + 1] = vi + 2; tris[ti + 2] = vi + 1;
			tris[ti + 3] = vi + 0; tris[ti + 4] = vi + 3; tris[ti + 5] = vi + 2;
			vi += 4; ti += 6;
		}

		AddFace(new Vector3(1,0,0), new Vector3(hx,-hy,-hz), new Vector3(hx,-hy,hz), new Vector3(hx,hy,hz), new Vector3(hx,hy,-hz), size.z * tilesPerMeter, size.y * tilesPerMeter);
		AddFace(new Vector3(-1,0,0), new Vector3(-hx,-hy,hz), new Vector3(-hx,-hy,-hz), new Vector3(-hx,hy,-hz), new Vector3(-hx,hy,hz), size.z * tilesPerMeter, size.y * tilesPerMeter);
		AddFace(new Vector3(0,1,0), new Vector3(-hx,hy,-hz), new Vector3(hx,hy,-hz), new Vector3(hx,hy,hz), new Vector3(-hx,hy,hz), size.x * tilesPerMeter, size.z * tilesPerMeter);
		AddFace(new Vector3(0,-1,0), new Vector3(-hx,-hy,hz), new Vector3(hx,-hy,hz), new Vector3(hx,-hy,-hz), new Vector3(-hx,-hy,-hz), size.x * tilesPerMeter, size.z * tilesPerMeter);
		AddFace(new Vector3(0,0,1), new Vector3(-hx,-hy,hz), new Vector3(-hx,hy,hz), new Vector3(hx,hy,hz), new Vector3(hx,-hy,hz), size.x * tilesPerMeter, size.y * tilesPerMeter);
		AddFace(new Vector3(0,0,-1), new Vector3(hx,-hy,-hz), new Vector3(hx,hy,-hz), new Vector3(-hx,hy,-hz), new Vector3(-hx,-hy,-hz), size.x * tilesPerMeter, size.y * tilesPerMeter);

		var mesh = new Mesh();
		mesh.name = "DebrisCubeTiled";
		mesh.SetVertices(verts);
		mesh.SetNormals(norms);
		mesh.SetUVs(0, uvs);
		mesh.SetTriangles(tris, 0);
		mesh.RecalculateBounds();
		return mesh;
	}
}