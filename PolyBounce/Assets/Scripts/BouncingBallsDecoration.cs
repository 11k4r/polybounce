using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

// Spawns a set of small colorful balls that bounce around inside the current
// boundary of a MenuShapeAnimator's shape — including through its morphs and
// rotation. No Unity physics involved (no Rigidbody2D/Collider2D at all):
// positions, velocities, and wall bounces are simulated manually every frame
// using point-in-polygon + nearest-edge-reflection math against the shape's
// live boundary. This sidesteps the whole "keep colliders in sync with a
// constantly morphing/resampled shape" problem entirely, and — since
// Physics2D is never involved — ball-to-ball collisions simply don't exist;
// only wall bounces happen.
//
// Each ball is actually THREE stacked UI elements (back to front): a soft
// glow halo, a speed-based comet trail, and a shaded "3D" sphere core. All
// three share the ball's simulated position, so the whole stack moves/
// bounces together with zero extra simulation cost.
//
// Setup: attach this to the SAME GameObject as your MenuShapeAnimator (so
// balls share its rotating transform automatically, the same way the shape's
// own line segments do), and assign that MenuShapeAnimator in the
// "Target Shape" field.
[RequireComponent(typeof(RectTransform))]
public class BouncingBallsDecoration : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("The shape whose current boundary the balls bounce inside. Attach this script to the SAME GameObject as that MenuShapeAnimator so balls inherit its rotation for free.")]
    public MenuShapeAnimator targetShape;

    [Header("Balls")]
    [Range(1, 60)] public int ballCount = 20;
    public float ballRadius = 6f;
    [Tooltip("Randomizes each ball's radius by up to this fraction, so balls read as a varied cluster instead of identical dots.")]
    [Range(0f, 0.6f)] public float ballSizeVariation = 0.35f;
    [Tooltip("Each ball's color is sampled from this gradient, spread evenly across all balls.")]
    public Gradient ballColorGradient = CreateDefaultBallGradient();

    [Header("3D Sphere Look")]
    [Tooltip("Opacity of each ball's shaded core.")]
    [Range(0f, 1f)] public float coreAlpha = 0.95f;
    [Tooltip("How tight/bright the specular highlight on each sphere is.")]
    [Range(0.2f, 2f)] public float sphereShininess = 1f;

    [Header("Glow Halo")]
    public bool enableGlowHalo = true;
    [Range(0f, 1f)] public float glowAlpha = 0.35f;
    [Tooltip("Size of the glow halo relative to the ball's own diameter.")]
    public float glowSizeMultiplier = 2.2f;

    [Header("Comet Trail")]
    public bool enableTrails = true;
    [Range(0f, 1f)] public float trailAlpha = 0.55f;
    [Tooltip("Number of past positions kept per ball. The trail starts at zero length and grows toward this many segments as the ball travels, so it 'builds up' instead of appearing instantly. Higher = longer/smoother trail, more draw calls.")]
    [Range(2, 24)] public int trailPointCount = 10;
    [Tooltip("Minimum distance (pixels) the ball must travel before a new trail point is recorded. Smaller = denser/smoother trail; larger = a longer-reaching trail for the same point count.")]
    public float trailSampleSpacing = 4f;
    [Tooltip("Trail width relative to the ball's own diameter, at the segment closest to the ball. Tapers down toward the tail automatically.")]
    public float trailWidthMultiplier = 0.9f;

    [Header("Motion")]
    public float minSpeed = 60f;
    public float maxSpeed = 140f;
    [Tooltip("Random angular variation (degrees) applied to every wall-bounce reflection. Keeps multiple balls that happen to hit a wall at the same moment from all reflecting in exactly the same direction.")]
    [Range(0f, 45f)] public float bounceAngleJitter = 12f;
    [Tooltip("Extra random angular variation (degrees) applied specifically to the concave-shape safety-net fallback (see FindClosestEdgePoint/safety net in Update). Without this, several balls caught by the same fast shape-shrink all get redirected toward the exact same centroid point and visibly end up flying in the same line — this is what fixes that.")]
    [Range(0f, 90f)] public float safetyNetAngleJitter = 40f;

    [Header("Hit Sound")]
    public bool enableHitSound = true;
    [Tooltip("Played once per wall bounce. A short, punchy 'tick'/'pop' works best since it gets pitch-shifted per ball.")]
    public AudioClip hitSound;
    [Tooltip("How many overlapping hit sounds can play at once. Each hit grabs one AudioSource from this pool so simultaneous hits with different pitch/pan don't bleed into each other.")]
    [Range(2, 32)] public int audioSourcePoolSize = 12;
    [Range(0f, 1f)] public float hitSoundVolume = 0.8f;

    [Header("Hit Sound — Pitch by Ball Size")]
    [Tooltip("Pitch used for the SMALLEST ball (per ballSizeVariation). Note this is the opposite of real-world acoustics (big=usually lower) — flip these two values if you'd rather have it realistic.")]
    public float pitchForSmallestBall = 0.8f;
    [Tooltip("Pitch used for the LARGEST ball.")]
    public float pitchForLargestBall = 1.4f;

    [Header("Hit Sound — Stereo Pan by Hit Position")]
    [Tooltip("A hit at the arena's left edge pans fully left, the right edge pans fully right, and dead center plays evenly in both ears. In-between positions (e.g. center-right) blend proportionally. Panning follows the shape's actual rotated on-screen bounds, not its un-rotated local space.")]
    public bool enableStereoPan = true;
    [Tooltip("Scales the pan effect. 1 = full left/right at the edges, lower values keep it more centered.")]
    [Range(0f, 1f)] public float panStrength = 1f;

    [Header("Hit Sound — Volume by Impact Speed")]
    public bool scaleVolumeWithImpactSpeed = true;
    [Range(0f, 1f)] public float minImpactVolumeScale = 0.5f;

    private readonly List<RectTransform> ballTransforms = new List<RectTransform>();
    private readonly List<RectTransform> haloTransforms = new List<RectTransform>();
    // Per ball: a fixed-size pool of segment RectTransforms (trailPointCount
    // of them) plus a growing history of recently-visited positions. Segment
    // k each frame is drawn connecting history[k-1] to history[k] (with the
    // live ball position standing in for history[-1]) — so the chain always
    // starts exactly at the ball and traces its actual recent path.
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

    // Reused every frame to avoid a GC allocation from GetWorldCorners().
    private readonly Vector3[] worldCornersCache = new Vector3[4];
    private RectTransform selfRect;

    void Awake()
    {
        selfRect = GetComponent<RectTransform>();
    }

    void Start()
    {
        if (targetShape == null)
        {
            Debug.LogError("BouncingBallsDecoration: Target Shape is not assigned.");
            enabled = false;
            return;
        }

        minPossibleBallRadius = ballRadius * (1f - ballSizeVariation);
        maxPossibleBallRadius = ballRadius * (1f + ballSizeVariation);

        if (enableHitSound) BuildAudioSourcePool();
        SpawnBalls();
    }

    void Update()
    {
        Vector2[] boundary = targetShape.GetBoundaryPointsLocal();
        if (boundary == null || boundary.Length < 3) return;

        float dt = Time.deltaTime;

        // Used only as a fallback "pull toward here if all else fails" point
        // for the safety net below — a cheap vertex average is fine for
        // this, it doesn't need to be the exact polygon centroid.
        Vector2 shapeCentroidApprox = ComputeCentroidApprox(boundary);

        // World-space horizontal extent of the arena AS CURRENTLY ROTATED —
        // used to turn a hit's world X position into a -1..1 stereo pan.
        // Reading actual rotated world corners (rather than un-rotated local
        // X) is what makes "left/right" correctly follow the shape's spin
        // instead of panning based on a rotating frame of reference.
        float arenaMinX = 0f, arenaMaxX = 0f;
        if (enableHitSound && enableStereoPan)
        {
            selfRect.GetWorldCorners(worldCornersCache);
            arenaMinX = Mathf.Min(Mathf.Min(worldCornersCache[0].x, worldCornersCache[1].x), Mathf.Min(worldCornersCache[2].x, worldCornersCache[3].x));
            arenaMaxX = Mathf.Max(Mathf.Max(worldCornersCache[0].x, worldCornersCache[1].x), Mathf.Max(worldCornersCache[2].x, worldCornersCache[3].x));
        }

        for (int i = 0; i < ballTransforms.Count; i++)
        {
            float radius = ballRadii[i];
            Vector2 pos = ballPositions[i] + ballVelocities[i] * dt;

            Vector2 closestPoint;
            Vector2 normal;
            FindClosestEdgePoint(pos, boundary, out closestPoint, out normal);

            // Check both "fully outside" AND "close enough to the boundary
            // that its radius would poke through" — the first case matters
            // because the shape can morph/shrink out from under a ball that
            // was previously safely interior, not just because it moved fast.
            bool inside = IsInsidePolygon(pos, boundary);
            float distToEdge = Vector2.Distance(pos, closestPoint);

            bool justStartedColliding = false;

            if (!inside || distToEdge < radius)
            {
                // Small extra margin (not just exactly `radius`) so floating
                // point / continuous-morph jitter doesn't immediately
                // re-trigger a correction right back at the boundary next
                // frame.
                pos = closestPoint - normal * (radius * 1.02f);
                ballVelocities[i] = Vector2.Reflect(ballVelocities[i], normal);
                if (bounceAngleJitter > 0f)
                {
                    ballVelocities[i] = RotateVector(ballVelocities[i], Random.Range(-bounceAngleJitter, bounceAngleJitter));
                }

                // Safety net for concave shapes (e.g. the Cross's inward
                // "notch" corners): FindClosestEdgePoint already blends
                // normals at vertices, but a shape that keeps morphing out
                // from under a ball several frames in a row — or a genuinely
                // ambiguous reflex corner — can still occasionally leave the
                // corrected position outside. Rather than let a ball get
                // permanently stranded outside the outline (visible as a dot
                // sitting just past a corner), keep nudging it toward the
                // shape's centroid and re-testing until it's verified inside.
                int safety = 0;
                while (!IsInsidePolygon(pos, boundary) && safety < 8)
                {
                    pos = Vector2.Lerp(pos, shapeCentroidApprox, 0.35f);
                    safety++;
                }
                if (safety > 0)
                {
                    // Redirect velocity inward too, so it doesn't immediately
                    // try to punch back out through the same corner. A
                    // sizeable random jitter on this angle is important: a
                    // fast shape shrink can catch several balls in the same
                    // frame, and without jitter every one of them would aim
                    // at exactly the same centroid point and visibly fly off
                    // in the same line together.
                    Vector2 inward = shapeCentroidApprox - pos;
                    if (inward.sqrMagnitude > 0.0001f)
                    {
                        float speedMag = ballVelocities[i].magnitude;
                        float baseAngle = Mathf.Atan2(inward.y, inward.x) * Mathf.Rad2Deg;
                        float jitteredAngle = (baseAngle + Random.Range(-safetyNetAngleJitter, safetyNetAngleJitter)) * Mathf.Deg2Rad;
                        ballVelocities[i] = new Vector2(Mathf.Cos(jitteredAngle), Mathf.Sin(jitteredAngle)) * speedMag;
                    }
                }

                // Only treat this as a fresh "hit" on the frame the ball
                // transitions from free-flight into contact — otherwise a
                // ball sliding along a wall/corner across several frames
                // would spam the sound every single frame.
                if (!wasCollidingLastFrame[i]) justStartedColliding = true;
                wasCollidingLastFrame[i] = true;
            }
            else
            {
                wasCollidingLastFrame[i] = false;
            }

            ballPositions[i] = pos;
            ballTransforms[i].anchoredPosition = pos;

            if (enableGlowHalo)
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
                // Read world X AFTER anchoredPosition was updated above, so
                // it reflects this ball's actual current on-screen position
                // (including the shape's rotation).
                float worldX = ballTransforms[i].position.x;
                PlayHitSound(radius, impactSpeed, worldX, arenaMinX, arenaMaxX);
            }
        }
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
                // Not enough history yet for this link in the chain.
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

    private void SpawnBalls()
    {
        Sprite sphereSprite = GetSphereSprite(sphereShininess);
        Sprite glowSprite = GetSoftGlowSprite();
        Sprite trailSegmentSprite = GetSoftBarSprite();

        for (int i = 0; i < ballCount; i++)
        {
            float t = ballCount > 1 ? (float)i / (ballCount - 1) : 0f;
            Color ballColor = ballColorGradient.Evaluate(t);
            float radius = ballRadius * (1f + Random.Range(-ballSizeVariation, ballSizeVariation));

            // Small cluster near the shape's center — safely inside virtually
            // any shape variant regardless of which one is currently showing.
            // Even in a pathological edge case where it isn't, Update()'s
            // "!inside" check corrects it on the very first frame anyway.
            Vector2 startPos = Random.insideUnitCircle * ballRadius * 2f;

            float speed = Random.Range(minSpeed, maxSpeed);
            float angle = Random.Range(0f, Mathf.PI * 2f);
            Vector2 vel = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;

            // --- Trail (created first -> renders behind everything else) ---
            // A fixed pool of `trailPointCount` segments, each pre-tinted
            // with alpha/width already tapered by its fixed position in the
            // chain (segment 0 = closest to the ball = brightest/widest).
            // Only position/length/rotation change per frame after this —
            // see UpdateBallTrail().
            List<RectTransform> trailSegments = new List<RectTransform>(trailPointCount);
            if (enableTrails)
            {
                for (int k = 0; k < trailPointCount; k++)
                {
                    float ageT = (float)k / trailPointCount; // 0 = nearest the ball, 1 = oldest
                    float segAlpha = trailAlpha * (1f - ageT);
                    float segWidth = radius * 2f * trailWidthMultiplier * (1f - ageT * 0.85f);

                    GameObject segObj = new GameObject("TrailSegment", typeof(Image));
                    segObj.transform.SetParent(transform, false);
                    segObj.SetActive(false); // hidden until history reaches this link

                    Image segImg = segObj.GetComponent<Image>();
                    segImg.sprite = trailSegmentSprite;
                    segImg.raycastTarget = false;
                    segImg.color = new Color(ballColor.r, ballColor.g, ballColor.b, segAlpha);

                    RectTransform segRt = segObj.GetComponent<RectTransform>();
                    segRt.anchorMin = new Vector2(0.5f, 0.5f);
                    segRt.anchorMax = new Vector2(0.5f, 0.5f);
                    segRt.sizeDelta = new Vector2(0f, segWidth);
                    segRt.anchoredPosition = startPos;

                    trailSegments.Add(segRt);
                }
            }

            // --- Glow halo (renders behind the core, in front of the trail) ---
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
                haloRt.anchoredPosition = startPos;
            }

            // --- Core sphere (front-most) ---
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
            ballRt.anchoredPosition = startPos;

            ballTransforms.Add(ballRt);
            haloTransforms.Add(haloRt);
            trailSegmentTransforms.Add(trailSegments);
            trailHistory.Add(new List<Vector2>(trailPointCount));

            ballPositions.Add(startPos);
            ballVelocities.Add(vel);
            ballRadii.Add(radius);
            wasCollidingLastFrame.Add(false);
        }
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
            // 2D sound: panning is done manually below via panStereo rather
            // than Unity's 3D spatialization, since these are UI elements
            // with no meaningful distance/orientation relationship to an
            // AudioListener — panStereo just needs a -1..1 number, which we
            // derive from the ball's on-screen X position ourselves.
            src.spatialBlend = 0f;
            audioSourcePool.Add(src);
        }
    }

    // Grabs a currently-idle pooled source if one exists; otherwise steals
    // the least-recently-used one (round robin) so a burst of simultaneous
    // hits never silently fails to play, at the cost of occasionally cutting
    // off the tail of an older overlapping hit.
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

        // Size -> pitch. minPossibleBallRadius/maxPossibleBallRadius are
        // derived from ballRadius +/- ballSizeVariation at Start(), so this
        // stays correctly normalized no matter how variation is configured.
        float sizeT = Mathf.InverseLerp(minPossibleBallRadius, maxPossibleBallRadius, radius);
        src.pitch = Mathf.Lerp(pitchForSmallestBall, pitchForLargestBall, sizeT);

        // Hit position -> stereo pan, based on the arena's actual current
        // (rotated) on-screen bounds so it stays correct as the shape spins.
        float pan = 0f;
        if (enableStereoPan && arenaMaxX > arenaMinX)
        {
            float panT = Mathf.InverseLerp(arenaMinX, arenaMaxX, worldX); // 0 = left edge, 1 = right edge
            pan = Mathf.Clamp((panT * 2f - 1f) * panStrength, -1f, 1f);
        }
        src.panStereo = pan;

        // Impact speed -> volume (harder hits sound louder).
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
                // Outward normal for a counter-clockwise-wound polygon —
                // matches how MenuShapeAnimator generates its shapes (walking
                // vertices via increasing angle).
                normal = new Vector2(ab.y, -ab.x).normalized;
            }
        }

        // If the closest feature is effectively a VERTEX rather than the
        // middle of an edge, a single edge's normal is a poor stand-in for
        // "outward" there. That matters most at the reflex (inward-pointing)
        // corners of concave shapes like the Cross: picking the wrong one of
        // the two adjacent edges' normals can push a ball further OUT
        // instead of back in, letting it leak past the corner permanently.
        // Blending both edges' normals (a standard polygon-offset "miter"
        // normal) gives a much better direction right at the vertex.
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

    // Cheap stand-in for the polygon's centroid, used only as a fallback
    // "pull toward here" target when the primary correction still leaves a
    // ball outside — a plain vertex average is good enough for that.
    private Vector2 ComputeCentroidApprox(Vector2[] polygon)
    {
        Vector2 sum = Vector2.zero;
        for (int i = 0; i < polygon.Length; i++) sum += polygon[i];
        return sum / polygon.Length;
    }

    // Rotates a vector by the given angle (degrees) without changing its
    // magnitude — used to jitter bounce/redirect directions.
    private static Vector2 RotateVector(Vector2 v, float degrees)
    {
        float rad = degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    // ---------------- Procedural sprites (no external assets needed) ----------------

    // Keyed by a rounded shininess value, since GetSphereSprite() bakes the
    // specular exponent into the texture itself — different shininess
    // settings need their own cached texture.
    private static readonly Dictionary<int, Sprite> cachedSphereSprites = new Dictionary<int, Sprite>();
    private static Sprite cachedGlowSprite;
    private static Sprite cachedTrailSprite;

    // Bakes a simple Lambert-lit sphere into a texture: a highlight offset
    // up-left, a tight specular hotspot, and natural rim darkening falling
    // straight out of the lighting math (the surface normal turns edge-on
    // near the silhouette, so diffuse light naturally drops off there). This
    // one grayscale-ish texture, multiplied by each ball's tint color via
    // Image.color, is what makes flat circles read as glossy 3D marbles.
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
                float edgeAlpha = Mathf.Clamp01(radius - pixelDist + 0.5f); // ~1px antialiased edge

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
                float rim = Mathf.Pow(1f - nz, 2f) * 0.12f; // faint glassy rim brightening at the silhouette

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

    // A uniform soft radial falloff (bright center to transparent edge) used
    // for the glow halo behind each ball.
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
                alpha = alpha * alpha * alpha; // soft, concentrated core with a long soft fade
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();

        cachedGlowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return cachedGlowSprite;
    }

    // A horizontally-uniform bar whose alpha falls off smoothly from a solid
    // center band to transparent top/bottom edges. Each trail segment is
    // stretched (via sizeDelta.x) to whatever length connects its two
    // history points, so only the vertical falloff needs to be baked in —
    // the per-segment alpha/width taper is applied separately via
    // Image.color and sizeDelta.y when each segment is created.
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
            float ny = (y + 0.5f) / h * 2f - 1f; // -1..1
            float alpha = Mathf.Clamp01(1f - ny * ny); // parabolic falloff, soft edges
            alpha *= alpha; // extra falloff for a softer glow instead of a hard-edged bar
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
                new GradientColorKey(new Color(1f, 0.4f, 0.4f), 0f),
                new GradientColorKey(new Color(1f, 0.9f, 0.3f), 0.25f),
                new GradientColorKey(new Color(0.4f, 1f, 0.6f), 0.5f),
                new GradientColorKey(new Color(0.4f, 0.7f, 1f), 0.75f),
                new GradientColorKey(new Color(0.85f, 0.4f, 1f), 1f)
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            }
        );
        return g;
    }
}