using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

// Sandbox-mode ball simulation. This is the same manual (no Physics2D)
// point-in-polygon / edge-reflection simulation as BouncingBallsDecoration,
// but built for the Sandbox loop instead of a fixed decorative ball count:
//
//   - Starts with exactly ONE ball.
//   - Every time ANY ball hits a wall, a fresh ball spawns from the arena's
//     center — until maxBalls is reached, after which existing balls just
//     keep bouncing.
//   - Still no ball-to-ball collision (Physics2D is never involved here
//     either, same as the menu version).
//   - Adds real physics knobs (gravity, restitution) since a sandbox needs
//     to be tunable, not just decorative.
//
// Setup: attach this to the SAME GameObject as your MenuShapeAnimator (so
// balls inherit its rotation for free), assign that MenuShapeAnimator in
// "Target Shape", and (optionally) assign a SandboxManager so Pause/Reset
// buttons can find this component. Pause itself needs no code here at all —
// SandboxManager pauses via Time.timeScale, and every position update below
// already reads Time.deltaTime, so it simply stops moving when time-scaled
// to zero.
[RequireComponent(typeof(RectTransform))]
public class SandboxBallSpawner : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("The shape whose current boundary the balls bounce inside. Attach this script to the SAME GameObject as that MenuShapeAnimator so balls inherit its rotation for free.")]
    public MenuShapeAnimator targetShape;

    [Header("Ball Cap")]
    [Tooltip("Simulation starts with exactly 1 ball. A new one spawns from the center on every wall hit until this many balls exist.")]
    [Range(1, 300)] public int maxBalls = 40;
    public bool spawnBallOnHit = true;

    [Header("Balls")]
    public float ballRadius = 6f;
    [Tooltip("Randomizes each spawned ball's radius by up to this fraction, so balls read as a varied cluster instead of identical dots.")]
    [Range(0f, 0.6f)] public float ballSizeVariation = 0.35f;
    [Tooltip("Each spawned ball's color is sampled from a random point on this gradient. Set this to a single solid color (e.g. white) for a plain look.")]
    public Gradient ballColorGradient = CreateDefaultBallGradient();

    [Header("3D Sphere Look")]
    [Range(0f, 1f)] public float coreAlpha = 0.95f;
    [Range(0.2f, 2f)] public float sphereShininess = 1f;

    [Header("Glow Halo")]
    public bool enableGlowHalo = true;
    [Range(0f, 1f)] public float glowAlpha = 0.35f;
    public float glowSizeMultiplier = 2.2f;

    [Header("Comet Trail")]
    public bool enableTrails = true;
    [Range(0f, 1f)] public float trailAlpha = 0.55f;
    [Range(2, 24)] public int trailPointCount = 10;
    public float trailSampleSpacing = 4f;
    public float trailWidthMultiplier = 0.9f;

    [Header("Motion")]
    public float minSpeed = 60f;
    public float maxSpeed = 140f;
    [Range(0f, 45f)] public float bounceAngleJitter = 12f;
    [Range(0f, 90f)] public float safetyNetAngleJitter = 40f;

    [Header("Physics")]
    [Tooltip("Constant acceleration applied to every ball every frame (screen-space pixels/sec^2). Leave at (0,0) for the classic zero-gravity bounce.")]
    public Vector2 gravity = Vector2.zero;
    [Tooltip("Velocity multiplier applied on every wall bounce. 1 = no energy loss/gain, <1 = balls gradually settle, >1 = balls speed up over time (chaotic).")]
    [Range(0.1f, 1.5f)] public float restitution = 1f;
    [Tooltip("Reserved for future features (e.g. gravity wells, ball-to-ball interactions). Currently has NO effect on the simulation, since balls don't collide with each other or anything mass-sensitive yet.")]
    public float mass = 1f;
    [Tooltip("Safety cap on ball speed so extreme gravity/restitution/jitter combinations set via the sandbox UI can't make a ball run away toward infinite speed.")]
    public float maxSimulationSpeed = 900f;

    [Header("Hit Sound")]
    public bool enableHitSound = true;
    public AudioClip hitSound;
    [Range(2, 32)] public int audioSourcePoolSize = 16;
    [Range(0f, 1f)] public float hitSoundVolume = 0.8f;

    [Header("Hit Sound — Pitch by Ball Size")]
    public float pitchForSmallestBall = 0.8f;
    public float pitchForLargestBall = 1.4f;

    [Header("Hit Sound — Stereo Pan by Hit Position")]
    public bool enableStereoPan = true;
    [Range(0f, 1f)] public float panStrength = 1f;

    [Header("Hit Sound — Volume by Impact Speed")]
    public bool scaleVolumeWithImpactSpeed = true;
    [Range(0f, 1f)] public float minImpactVolumeScale = 0.5f;

    private readonly List<RectTransform> ballTransforms = new List<RectTransform>();
    private readonly List<RectTransform> haloTransforms = new List<RectTransform>();
    private readonly List<List<RectTransform>> trailSegmentTransforms = new List<List<RectTransform>>();
    private readonly List<List<Vector2>> trailHistory = new List<List<Vector2>>();

    private readonly List<Vector2> ballPositions = new List<Vector2>();
    private readonly List<Vector2> ballVelocities = new List<Vector2>();
    private readonly List<float> ballRadii = new List<float>();
    private readonly List<bool> wasCollidingLastFrame = new List<bool>();

    private readonly List<AudioSource> audioSourcePool = new List<AudioSource>();
    private int nextPooledAudioSourceIndex = 0;
    private float minPossibleBallRadius;
    private float maxPossibleBallRadius;

    private readonly Vector3[] worldCornersCache = new Vector3[4];
    private RectTransform selfRect;

    /// <summary>How many balls currently exist in the simulation.</summary>
    public int CurrentBallCount => ballTransforms.Count;

    void Awake()
    {
        selfRect = GetComponent<RectTransform>();
    }

    void Start()
    {
        if (targetShape == null)
        {
            Debug.LogError("SandboxBallSpawner: Target Shape is not assigned.");
            enabled = false;
            return;
        }

        RecomputeRadiusBounds();

        if (enableHitSound) BuildAudioSourcePool();
        SpawnOneBall(Vector2.zero, RandomInitialVelocity());
    }

    // Public so a "Ball Settings" panel can call this after changing
    // ballRadius/ballSizeVariation at runtime, keeping pitch-mapping correct.
    public void RecomputeRadiusBounds()
    {
        minPossibleBallRadius = ballRadius * (1f - ballSizeVariation);
        maxPossibleBallRadius = ballRadius * (1f + ballSizeVariation);
    }

    void Update()
    {
        Vector2[] boundary = targetShape.GetBoundaryPointsLocal();
        if (boundary == null || boundary.Length < 3) return;

        float dt = Time.deltaTime;

        Vector2 shapeCentroidApprox = ComputeCentroidApprox(boundary);

        float arenaMinX = 0f, arenaMaxX = 0f;
        if (enableHitSound && enableStereoPan)
        {
            selfRect.GetWorldCorners(worldCornersCache);
            arenaMinX = Mathf.Min(Mathf.Min(worldCornersCache[0].x, worldCornersCache[1].x), Mathf.Min(worldCornersCache[2].x, worldCornersCache[3].x));
            arenaMaxX = Mathf.Max(Mathf.Max(worldCornersCache[0].x, worldCornersCache[1].x), Mathf.Max(worldCornersCache[2].x, worldCornersCache[3].x));
        }

        // Requests are collected and resolved AFTER the main loop rather
        // than spawning balls mid-iteration — keeps this frame's physics
        // pass over the existing balls simple and order-independent.
        int spawnRequests = 0;

        int ballCountThisFrame = ballTransforms.Count;
        for (int i = 0; i < ballCountThisFrame; i++)
        {
            float radius = ballRadii[i];

            Vector2 vel = ballVelocities[i] + gravity * dt;
            if (vel.magnitude > maxSimulationSpeed) vel = vel.normalized * maxSimulationSpeed;
            ballVelocities[i] = vel;

            Vector2 pos = ballPositions[i] + vel * dt;

            Vector2 closestPoint;
            Vector2 normal;
            FindClosestEdgePoint(pos, boundary, out closestPoint, out normal);

            bool inside = IsInsidePolygon(pos, boundary);
            float distToEdge = Vector2.Distance(pos, closestPoint);

            bool justStartedColliding = false;

            if (!inside || distToEdge < radius)
            {
                pos = closestPoint - normal * (radius * 1.02f);
                ballVelocities[i] = Vector2.Reflect(ballVelocities[i], normal) * restitution;
                if (bounceAngleJitter > 0f)
                {
                    ballVelocities[i] = RotateVector(ballVelocities[i], Random.Range(-bounceAngleJitter, bounceAngleJitter));
                }

                int safety = 0;
                while (!IsInsidePolygon(pos, boundary) && safety < 8)
                {
                    pos = Vector2.Lerp(pos, shapeCentroidApprox, 0.35f);
                    safety++;
                }
                if (safety > 0)
                {
                    Vector2 inward = shapeCentroidApprox - pos;
                    if (inward.sqrMagnitude > 0.0001f)
                    {
                        float speedMag = ballVelocities[i].magnitude;
                        float baseAngle = Mathf.Atan2(inward.y, inward.x) * Mathf.Rad2Deg;
                        float jitteredAngle = (baseAngle + Random.Range(-safetyNetAngleJitter, safetyNetAngleJitter)) * Mathf.Deg2Rad;
                        ballVelocities[i] = new Vector2(Mathf.Cos(jitteredAngle), Mathf.Sin(jitteredAngle)) * speedMag;
                    }
                }

                if (ballVelocities[i].magnitude > maxSimulationSpeed)
                {
                    ballVelocities[i] = ballVelocities[i].normalized * maxSimulationSpeed;
                }

                if (!wasCollidingLastFrame[i])
                {
                    justStartedColliding = true;
                    if (spawnBallOnHit) spawnRequests++;
                }
                wasCollidingLastFrame[i] = true;
            }
            else
            {
                wasCollidingLastFrame[i] = false;
            }

            ballPositions[i] = pos;
            ballTransforms[i].anchoredPosition = pos;

            if (enableGlowHalo && haloTransforms[i] != null)
            {
                haloTransforms[i].anchoredPosition = pos;
            }

            if (enableTrails)
            {
                UpdateBallTrail(i, pos);
            }

            if (justStartedColliding && enableHitSound)
            {
                float impactSpeed = ballVelocities[i].magnitude;
                float worldX = ballTransforms[i].position.x;
                PlayHitSound(radius, impactSpeed, worldX, arenaMinX, arenaMaxX);
            }
        }

        if (spawnRequests > 0)
        {
            int room = Mathf.Max(0, maxBalls - ballTransforms.Count);
            int actualSpawns = Mathf.Min(spawnRequests, room);
            for (int s = 0; s < actualSpawns; s++)
            {
                SpawnOneBall(Vector2.zero, RandomInitialVelocity());
            }
        }
    }

    /// <summary>Clears every ball and restarts the simulation with exactly one, fresh, from the center. Does not touch the arena/shape.</summary>
    public void ResetSimulation()
    {
        ClearAllBalls();
        RecomputeRadiusBounds();
        SpawnOneBall(Vector2.zero, RandomInitialVelocity());
    }

    private Vector2 RandomInitialVelocity()
    {
        float speed = Random.Range(minSpeed, maxSpeed);
        float angle = Random.Range(0f, Mathf.PI * 2f);
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
    }

    private void ClearAllBalls()
    {
        for (int i = 0; i < ballTransforms.Count; i++)
        {
            if (ballTransforms[i] != null) Destroy(ballTransforms[i].gameObject);
            if (haloTransforms[i] != null) Destroy(haloTransforms[i].gameObject);

            List<RectTransform> segs = trailSegmentTransforms[i];
            for (int k = 0; k < segs.Count; k++)
            {
                if (segs[k] != null) Destroy(segs[k].gameObject);
            }
        }

        ballTransforms.Clear();
        haloTransforms.Clear();
        trailSegmentTransforms.Clear();
        trailHistory.Clear();
        ballPositions.Clear();
        ballVelocities.Clear();
        ballRadii.Clear();
        wasCollidingLastFrame.Clear();
    }

    // Records a new history point once the ball has moved far enough since
    // the last one, then draws the segment chain from the ball's live
    // position through its recent history. Segments beyond the current
    // history length are hidden — that's what makes the trail visibly grow
    // from nothing right after a bounce/spawn instead of popping in at full
    // length.
    private void UpdateBallTrail(int ballIndex, Vector2 currentPos)
    {
        List<Vector2> history = trailHistory[ballIndex];
        List<RectTransform> segments = trailSegmentTransforms[ballIndex];

        if (history.Count == 0 || Vector2.Distance(currentPos, history[0]) >= trailSampleSpacing)
        {
            history.Insert(0, currentPos);
            if (history.Count > trailPointCount) history.RemoveAt(history.Count - 1);
        }

        Vector2 prevPoint = currentPos;
        for (int k = 0; k < segments.Count; k++)
        {
            RectTransform seg = segments[k];

            if (k >= history.Count)
            {
                if (seg.gameObject.activeSelf) seg.gameObject.SetActive(false);
                continue;
            }

            Vector2 nextPoint = history[k];
            Vector2 diff = nextPoint - prevPoint;
            float dist = diff.magnitude;

            if (!seg.gameObject.activeSelf) seg.gameObject.SetActive(true);

            Vector2 sizeDelta = seg.sizeDelta;
            sizeDelta.x = dist;
            seg.sizeDelta = sizeDelta;
            seg.anchoredPosition = prevPoint + diff * 0.5f;
            if (dist > 0.0001f)
            {
                float angle = Mathf.Atan2(diff.y, diff.x) * Mathf.Rad2Deg;
                seg.localEulerAngles = new Vector3(0f, 0f, angle);
            }

            prevPoint = nextPoint;
        }
    }

    // Spawns exactly one ball at the given position with the given starting
    // velocity, and registers it in every parallel list. Used both for the
    // initial ball and for every spawn-on-hit / Reset afterward.
    private void SpawnOneBall(Vector2 spawnPos, Vector2 initialVelocity)
    {
        Sprite sphereSprite = GetSphereSprite(sphereShininess);
        Sprite glowSprite = GetSoftGlowSprite();
        Sprite trailSegmentSprite = GetSoftBarSprite();

        Color ballColor = ballColorGradient.Evaluate(Random.value);
        float radius = ballRadius * (1f + Random.Range(-ballSizeVariation, ballSizeVariation));

        List<RectTransform> trailSegments = new List<RectTransform>(trailPointCount);
        if (enableTrails)
        {
            for (int k = 0; k < trailPointCount; k++)
            {
                float ageT = (float)k / trailPointCount;
                float segAlpha = trailAlpha * (1f - ageT);
                float segWidth = radius * 2f * trailWidthMultiplier * (1f - ageT * 0.85f);

                GameObject segObj = new GameObject("TrailSegment", typeof(Image));
                segObj.transform.SetParent(transform, false);
                segObj.SetActive(false);

                Image segImg = segObj.GetComponent<Image>();
                segImg.sprite = trailSegmentSprite;
                segImg.raycastTarget = false;
                segImg.color = new Color(ballColor.r, ballColor.g, ballColor.b, segAlpha);

                RectTransform segRt = segObj.GetComponent<RectTransform>();
                segRt.anchorMin = new Vector2(0.5f, 0.5f);
                segRt.anchorMax = new Vector2(0.5f, 0.5f);
                segRt.sizeDelta = new Vector2(0f, segWidth);
                segRt.anchoredPosition = spawnPos;

                trailSegments.Add(segRt);
            }
        }

        RectTransform haloRt = null;
        if (enableGlowHalo)
        {
            GameObject haloObj = new GameObject("BallGlow", typeof(Image));
            haloObj.transform.SetParent(transform, false);

            Image haloImg = haloObj.GetComponent<Image>();
            haloImg.sprite = glowSprite;
            haloImg.raycastTarget = false;
            haloImg.color = new Color(ballColor.r, ballColor.g, ballColor.b, glowAlpha);

            haloRt = haloObj.GetComponent<RectTransform>();
            haloRt.anchorMin = new Vector2(0.5f, 0.5f);
            haloRt.anchorMax = new Vector2(0.5f, 0.5f);
            haloRt.sizeDelta = new Vector2(radius * 2f * glowSizeMultiplier, radius * 2f * glowSizeMultiplier);
            haloRt.anchoredPosition = spawnPos;
        }

        GameObject ballObj = new GameObject("Ball", typeof(Image));
        ballObj.transform.SetParent(transform, false);

        Image ballImg = ballObj.GetComponent<Image>();
        ballImg.sprite = sphereSprite;
        ballImg.raycastTarget = false;
        ballImg.color = new Color(ballColor.r, ballColor.g, ballColor.b, coreAlpha);

        RectTransform ballRt = ballObj.GetComponent<RectTransform>();
        ballRt.anchorMin = new Vector2(0.5f, 0.5f);
        ballRt.anchorMax = new Vector2(0.5f, 0.5f);
        ballRt.sizeDelta = new Vector2(radius * 2f, radius * 2f);
        ballRt.anchoredPosition = spawnPos;

        ballTransforms.Add(ballRt);
        haloTransforms.Add(haloRt);
        trailSegmentTransforms.Add(trailSegments);
        trailHistory.Add(new List<Vector2>(trailPointCount));

        ballPositions.Add(spawnPos);
        ballVelocities.Add(initialVelocity);
        ballRadii.Add(radius);
        wasCollidingLastFrame.Add(false);
    }

    // ---------------- Hit sound ----------------

    private void BuildAudioSourcePool()
    {
        for (int i = 0; i < audioSourcePoolSize; i++)
        {
            GameObject go = new GameObject("HitSoundSource_" + i);
            go.transform.SetParent(transform, false);

            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            src.spatialBlend = 0f;
            audioSourcePool.Add(src);
        }
    }

    private AudioSource GetPooledAudioSource()
    {
        if (audioSourcePool.Count == 0) return null;

        for (int i = 0; i < audioSourcePool.Count; i++)
        {
            if (!audioSourcePool[i].isPlaying) return audioSourcePool[i];
        }

        AudioSource fallback = audioSourcePool[nextPooledAudioSourceIndex];
        nextPooledAudioSourceIndex = (nextPooledAudioSourceIndex + 1) % audioSourcePool.Count;
        return fallback;
    }

    private void PlayHitSound(float radius, float impactSpeed, float worldX, float arenaMinX, float arenaMaxX)
    {
        if (hitSound == null) return;

        AudioSource src = GetPooledAudioSource();
        if (src == null) return;

        float sizeT = Mathf.InverseLerp(minPossibleBallRadius, maxPossibleBallRadius, radius);
        src.pitch = Mathf.Lerp(pitchForSmallestBall, pitchForLargestBall, sizeT);

        float pan = 0f;
        if (enableStereoPan && arenaMaxX > arenaMinX)
        {
            float panT = Mathf.InverseLerp(arenaMinX, arenaMaxX, worldX);
            pan = Mathf.Clamp((panT * 2f - 1f) * panStrength, -1f, 1f);
        }
        src.panStereo = pan;

        float volumeScale = 1f;
        if (scaleVolumeWithImpactSpeed && maxSpeed > minSpeed)
        {
            float speedT = Mathf.InverseLerp(minSpeed, maxSpeed, impactSpeed);
            volumeScale = Mathf.Lerp(minImpactVolumeScale, 1f, speedT);
        }
        src.volume = hitSoundVolume * volumeScale;

        src.clip = hitSound;
        src.Play();
    }

    // ---------------- Geometry helpers ----------------

    private bool IsInsidePolygon(Vector2 point, Vector2[] polygon)
    {
        bool inside = false;
        int n = polygon.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 pi = polygon[i];
            Vector2 pj = polygon[j];
            if (((pi.y > point.y) != (pj.y > point.y)) &&
                (point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y) + pi.x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private void FindClosestEdgePoint(Vector2 point, Vector2[] polygon, out Vector2 closestPoint, out Vector2 normal)
    {
        int n = polygon.Length;
        float minDistSqr = float.MaxValue;
        closestPoint = polygon[0];
        normal = Vector2.up;
        int bestEdge = 0;
        float bestT = 0f;

        for (int i = 0; i < n; i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[(i + 1) % n];
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 0.0001f ? Mathf.Clamp01(Vector2.Dot(point - a, ab) / len2) : 0f;
            Vector2 proj = a + ab * t;
            float distSqr = (point - proj).sqrMagnitude;

            if (distSqr < minDistSqr)
            {
                minDistSqr = distSqr;
                closestPoint = proj;
                bestEdge = i;
                bestT = t;
                normal = new Vector2(ab.y, -ab.x).normalized;
            }
        }

        const float vertexEpsilon = 0.001f;
        if (bestT <= vertexEpsilon || bestT >= 1f - vertexEpsilon)
        {
            int vertexIndex = bestT <= vertexEpsilon ? bestEdge : (bestEdge + 1) % n;
            int prevEdge = (vertexIndex - 1 + n) % n;

            Vector2 prevA = polygon[prevEdge];
            Vector2 prevB = polygon[(prevEdge + 1) % n];
            Vector2 prevNormal = new Vector2((prevB - prevA).y, -(prevB - prevA).x).normalized;

            Vector2 nextA = polygon[vertexIndex];
            Vector2 nextB = polygon[(vertexIndex + 1) % n];
            Vector2 nextNormal = new Vector2((nextB - nextA).y, -(nextB - nextA).x).normalized;

            Vector2 miter = prevNormal + nextNormal;
            if (miter.sqrMagnitude > 0.0001f)
            {
                normal = miter.normalized;
            }
            closestPoint = polygon[vertexIndex];
        }
    }

    private Vector2 ComputeCentroidApprox(Vector2[] polygon)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < polygon.Length; i++) sum += polygon[i];
        return sum / polygon.Length;
    }

    private static Vector2 RotateVector(Vector2 v, float degrees)
    {
        float rad = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    // ---------------- Procedural sprites (no external assets needed) ----------------

    private static readonly Dictionary<int, Sprite> cachedSphereSprites = new Dictionary<int, Sprite>();
    private static Sprite cachedGlowSprite;
    private static Sprite cachedTrailSprite;

    private static Sprite GetSphereSprite(float shininess)
    {
        int key = Mathf.RoundToInt(Mathf.Clamp(shininess, 0.2f, 2f) * 10f);
        Sprite cached;
        if (cachedSphereSprites.TryGetValue(key, out cached) && cached != null) return cached;

        float specExponent = Mathf.Lerp(8f, 70f, Mathf.InverseLerp(0.2f, 2f, key / 10f));

        const int size = 96;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;

        Vector2 center = new Vector2(size / 2f, size / 2f);
        float radius = size / 2f - 1f;
        Vector3 lightDir = new Vector3(-0.5f, 0.55f, 0.85f).normalized;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float pixelDist = Vector2.Distance(new Vector2(px, py), center);
                float edgeAlpha = Mathf.Clamp01(radius - pixelDist + 0.5f);

                float dx = (px - center.x) / radius;
                float dy = (py - center.y) / radius;
                float r2 = dx * dx + dy * dy;

                if (r2 > 1f)
                {
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    continue;
                }

                float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - r2));
                Vector3 normal = new Vector3(dx, dy, nz);

                float diffuse = Mathf.Max(0f, Vector3.Dot(normal, lightDir));
                float specular = Mathf.Pow(diffuse, specExponent);
                float rim = Mathf.Pow(1f - nz, 2f) * 0.12f;

                float brightness = 0.30f + diffuse * 0.55f + specular * 0.95f + rim;
                brightness = Mathf.Clamp01(brightness);

                tex.SetPixel(x, y, new Color(brightness, brightness, brightness, edgeAlpha));
            }
        }
        tex.Apply();

        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        cachedSphereSprites[key] = sprite;
        return sprite;
    }

    private static Sprite GetSoftGlowSprite()
    {
        if (cachedGlowSprite != null) return cachedGlowSprite;

        const int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        Vector2 center = new Vector2(size / 2f, size / 2f);
        float radius = size / 2f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center) / radius;
                float alpha = Mathf.Clamp01(1f - d);
                alpha = alpha * alpha * alpha;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();

        cachedGlowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return cachedGlowSprite;
    }

    private static Sprite GetSoftBarSprite()
    {
        if (cachedTrailSprite != null) return cachedTrailSprite;

        const int w = 8;
        const int h = 48;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < h; y++)
        {
            float ny = (y + 0.5f) / h * 2f - 1f;
            float alpha = Mathf.Clamp01(1f - ny * ny);
            alpha *= alpha;
            Color c = new Color(1f, 1f, 1f, alpha);
            for (int x = 0; x < w; x++) tex.SetPixel(x, y, c);
        }
        tex.Apply();

        cachedTrailSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        return cachedTrailSprite;
    }

    private static Gradient CreateDefaultBallGradient()
    {
        Gradient g = new Gradient();
        g.SetKeys(
            new GradientColorKey[] {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.white, 1f)
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            }
        );
        return g;
    }
}