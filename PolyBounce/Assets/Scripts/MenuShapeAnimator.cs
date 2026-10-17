using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

// Drop this on a RectTransform (e.g. a child inside Center_Shape_Arena, or on
// Center_Shape_Arena itself). It draws an outline shape that spins
// continuously and smoothly morphs to a different random shape every few
// seconds, without ever interrupting the rotation. Fully self-contained —
// no dependency on any other script.
//
// Want several rotating shapes at once for a richer menu feel? Just add this
// same component to a few different child GameObjects (different sizes/
// positions/speeds) inside your panel — each instance runs independently.
[RequireComponent(typeof(RectTransform))]
public class MenuShapeAnimator : MonoBehaviour
{
    private enum ShapeKind { Circle, Triangle, Square, Diamond, Pentagon, Hexagon, Octagon, Star, Cross, Capsule, Gear, Shield }

    [Header("Visuals")]
    public float lineThickness = 4f;
    [Range(0.1f, 1f)] public float sizeMultiplier = 0.8f;
    [Tooltip("Every shape is resampled to this many points so any shape can morph smoothly into any other. Higher = smoother circle/star outlines, slightly more draw overhead.")]
    [Range(8, 96)] public int resolution = 48;

    [Header("Gradient")]
    [Tooltip("Sampled around the ring using a ping-pong wrap (0\u21921\u21920 as you go around) so the colors close seamlessly with no visible seam. Rotates together with the shape.")]
    public Gradient outlineGradient = CreateDefaultGradient();

    [Header("Glow")]
    [Tooltip("Draws a soft, wider, additive-feeling halo behind the crisp core line so the shape reads clearly against a busy/dark background — the neon look.")]
    public bool enableGlow = true;
    [Tooltip("How much wider the soft outer glow is than the core line, as a multiple of lineThickness.")]
    [Range(1.5f, 8f)] public float glowThicknessMultiplier = 4f;
    [Range(0f, 1f)] public float glowAlpha = 0.5f;
    [Tooltip("Pushes the glow color brighter/more saturated than the core gradient color, for a hot neon-tube look.")]
    [Range(1f, 2.2f)] public float glowBrightnessBoost = 1.4f;

    [Header("Spark Effect")]
    public bool enableSparkEffect = true;
    public Color sparkColor = Color.white;
    [Tooltip("Width of the traveling highlight as a fraction of the full perimeter.")]
    [Range(0.02f, 0.5f)] public float sparkWidth = 0.12f;
    [Tooltip("Time for the spark to travel once fully around the shape.")]
    public float sparkTravelDuration = 0.8f;
    public float minSecondsBetweenSparks = 4f;
    public float maxSecondsBetweenSparks = 9f;

    [Header("Rotation")]
    public float minRotationSpeed = 15f;  // degrees/sec
    public float maxRotationSpeed = 50f;
    public bool randomizeDirection = true;
    [Tooltip("If enabled, rotation speed smoothly drifts to a new random value every time the shape changes, instead of staying constant forever.")]
    public bool varyRotationSpeedOnChange = true;
    public float rotationSpeedSmoothTime = 1.5f;

    [Header("Shape Morphing")]
    [Tooltip("If off, the shape never automatically changes on its own — it only morphs when you call SetShape()/SetShapeImmediate() explicitly. Useful for a sandbox/editor context where the user picks the shape.")]
    public bool autoMorph = true;
    public float minSecondsBetweenChanges = 3f;
    public float maxSecondsBetweenChanges = 7f;
    public float morphDuration = 1.2f;
    public AnimationCurve morphEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    private RectTransform rectTransform;
    private readonly List<RectTransform> segmentLines = new List<RectTransform>();
    private readonly List<RectTransform> jointCaps = new List<RectTransform>();
    private readonly List<Image> segmentImages = new List<Image>();
    private readonly List<Image> jointImages = new List<Image>();

    // Soft, wider duplicates of the lines above — rendered first (so they sit
    // behind the crisp core) using a feathered sprite instead of a hard-edged
    // rectangle. This is what gives the shape a glowing "neon tube" look
    // instead of a thin flat line, without needing any post-process bloom.
    private readonly List<RectTransform> glowSegmentLines = new List<RectTransform>();
    private readonly List<RectTransform> glowJointCaps = new List<RectTransform>();
    private readonly List<Image> glowSegmentImages = new List<Image>();
    private readonly List<Image> glowJointImages = new List<Image>();

    private Vector2[] boundaryPointsLocal;

    private Vector2[] fromPoints;
    private Vector2[] toPoints;
    private Vector2[] currentPoints;

    private bool isMorphing = false;
    private float morphTimer = 0f;
    private float changeTimer = 0f;
    private float nextChangeDelay = 0f;

    private float currentRotationSpeed;
    private float targetRotationSpeed;
    private float rotationSpeedVelocity; // used by SmoothDamp
    private float currentAngle = 0f;

    private ShapeKind currentShapeKind;

    private bool isSparking = false;
    private float sparkProgress = 0f;
    private float sparkTimer = 0f;
    private float nextSparkDelay = 0f;

    void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        resolution = Mathf.Max(8, resolution);
        boundaryPointsLocal = new Vector2[resolution];
        BuildPool();
    }

    void Start()
    {
        currentShapeKind = ShapeKind.Circle;
        toPoints = GenerateResampledShape(currentShapeKind);
        fromPoints = (Vector2[])toPoints.Clone();
        currentPoints = (Vector2[])toPoints.Clone();

        currentRotationSpeed = RandomSpeed();
        targetRotationSpeed = currentRotationSpeed;

        ScheduleNextChange();
        ScheduleNextSpark();
        UpdatePositions(currentPoints);
        ApplyColors();
    }

    void Update()
    {
        float dt = Time.deltaTime;

        // --- Rotation: applied to the whole transform, so it's always
        // smooth and continuous regardless of what the shape is doing. ---
        if (varyRotationSpeedOnChange)
        {
            currentRotationSpeed = Mathf.SmoothDamp(currentRotationSpeed, targetRotationSpeed, ref rotationSpeedVelocity, rotationSpeedSmoothTime);
        }
        currentAngle += currentRotationSpeed * dt;
        rectTransform.localEulerAngles = new Vector3(0f, 0f, currentAngle);

        // --- Shape change scheduling ---
        changeTimer += dt;
        if (autoMorph && !isMorphing && changeTimer >= nextChangeDelay)
        {
            BeginMorphToNewShape();
        }

        // --- Morph interpolation ---
        if (isMorphing)
        {
            morphTimer += dt;
            float t = Mathf.Clamp01(morphTimer / morphDuration);
            float eased = morphEase.Evaluate(t);

            for (int i = 0; i < resolution; i++)
            {
                currentPoints[i] = Vector2.Lerp(fromPoints[i], toPoints[i], eased);
            }
            UpdatePositions(currentPoints);

            if (t >= 1f)
            {
                isMorphing = false;
                changeTimer = 0f;
                ScheduleNextChange();
            }
        }

        // --- Spark effect: a bright highlight that travels once around the
        // ring's perimeter, appearing every few random seconds. Only ever
        // recolors existing lines — never moves geometry — so it's cheap. ---
        if (enableSparkEffect)
        {
            if (!isSparking)
            {
                sparkTimer += dt;
                if (sparkTimer >= nextSparkDelay)
                {
                    isSparking = true;
                    sparkProgress = 0f;
                }
            }
            else
            {
                sparkProgress += dt / Mathf.Max(sparkTravelDuration, 0.05f);
                if (sparkProgress >= 1f)
                {
                    isSparking = false;
                    ScheduleNextSpark();
                }
                ApplyColors(); // force a final clean pass back to pure gradient too
            }
        }
    }

    private void BeginMorphToNewShape()
    {
        currentShapeKind = RandomShapeKind(excluding: currentShapeKind);

        fromPoints = (Vector2[])currentPoints.Clone();
        toPoints = GenerateResampledShape(currentShapeKind);

        if (varyRotationSpeedOnChange)
        {
            targetRotationSpeed = RandomSpeed();
        }

        morphTimer = 0f;
        isMorphing = true;
    }

    private void ScheduleNextChange()
    {
        nextChangeDelay = Random.Range(minSecondsBetweenChanges, maxSecondsBetweenChanges);
    }

    private void ScheduleNextSpark()
    {
        nextSparkDelay = Random.Range(minSecondsBetweenSparks, maxSecondsBetweenSparks);
        sparkTimer = 0f;
    }

    private float RandomSpeed()
    {
        float speed = Random.Range(minRotationSpeed, maxRotationSpeed);
        if (randomizeDirection && Random.value < 0.5f) speed = -speed;
        return speed;
    }

    private ShapeKind RandomShapeKind(ShapeKind? excluding = null)
    {
        int count = System.Enum.GetValues(typeof(ShapeKind)).Length;
        ShapeKind kind;
        do
        {
            kind = (ShapeKind)Random.Range(0, count);
        }
        while (excluding.HasValue && kind == excluding.Value);
        return kind;
    }

    // ---------------- Public shape control (for external UI, e.g. a sandbox) ----------------

    /// <summary>All available shape names, in stable index order — for populating a picker UI.</summary>
    public string[] GetShapeNames() => System.Enum.GetNames(typeof(ShapeKind));

    /// <summary>Index of the shape currently showing (or being morphed toward), matching GetShapeNames().</summary>
    public int GetCurrentShapeIndex() => (int)currentShapeKind;

    /// <summary>Smoothly morphs to the given shape (index into GetShapeNames()), same as an automatic change would.</summary>
    public void SetShape(int shapeIndex)
    {
        int count = System.Enum.GetValues(typeof(ShapeKind)).Length;
        shapeIndex = Mathf.Clamp(shapeIndex, 0, count - 1);
        ShapeKind target = (ShapeKind)shapeIndex;
        if (target == currentShapeKind && !isMorphing) return;

        currentShapeKind = target;
        fromPoints = currentPoints != null ? (Vector2[])currentPoints.Clone() : GenerateResampledShape(currentShapeKind);
        toPoints = GenerateResampledShape(currentShapeKind);
        morphTimer = 0f;
        isMorphing = true;
        changeTimer = 0f;
        ScheduleNextChange();
    }

    /// <summary>Snaps instantly to the given shape (index into GetShapeNames()) with no morph animation — e.g. for a Reset button.</summary>
    public void SetShapeImmediate(int shapeIndex)
    {
        int count = System.Enum.GetValues(typeof(ShapeKind)).Length;
        shapeIndex = Mathf.Clamp(shapeIndex, 0, count - 1);
        currentShapeKind = (ShapeKind)shapeIndex;

        toPoints = GenerateResampledShape(currentShapeKind);
        fromPoints = (Vector2[])toPoints.Clone();
        currentPoints = (Vector2[])toPoints.Clone();
        isMorphing = false;
        morphTimer = 0f;
        changeTimer = 0f;
        ScheduleNextChange();

        UpdatePositions(currentPoints);
        ApplyColors();
    }

    // ---------------- Shape generation ----------------

    private Vector2[] GenerateResampledShape(ShapeKind kind)
    {
        Vector2[] raw = GetRawVertices(kind);
        Vector2[] resampled = ResampleClosedPolygon(raw, resolution);
        NormalizeInPlace(resampled);
        return resampled;
    }

    private Vector2[] GetRawVertices(ShapeKind kind)
    {
        switch (kind)
        {
            case ShapeKind.Circle:   return RegularPolygonVerts(48, 0f);
            case ShapeKind.Triangle: return RegularPolygonVerts(3, 90f);
            case ShapeKind.Square:   return RegularPolygonVerts(4, 45f);
            case ShapeKind.Diamond:  return RegularPolygonVerts(4, 0f);
            case ShapeKind.Pentagon: return RegularPolygonVerts(5, 90f);
            case ShapeKind.Hexagon:  return RegularPolygonVerts(6, 90f);
            case ShapeKind.Octagon:  return RegularPolygonVerts(8, 22.5f);
            case ShapeKind.Star:     return StarVerts(5, 1f, 0.45f);
            case ShapeKind.Cross:    return CrossVerts();
            case ShapeKind.Capsule:  return CapsuleVerts();
            case ShapeKind.Gear:     return GearVerts();
            case ShapeKind.Shield:   return ShieldVerts();
            default: return RegularPolygonVerts(4, 0f);
        }
    }

    private Vector2[] RegularPolygonVerts(int sides, float offsetDegrees)
    {
        Vector2[] points = new Vector2[sides];
        float offsetRad = offsetDegrees * Mathf.Deg2Rad;
        for (int i = 0; i < sides; i++)
        {
            float angle = (Mathf.PI * 2f / sides) * i + offsetRad;
            points[i] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }
        return points;
    }

    private Vector2[] StarVerts(int points, float outerRadius, float innerRadius)
    {
        int n = points * 2;
        Vector2[] verts = new Vector2[n];
        float step = Mathf.PI / points;
        float angle = Mathf.PI / 2f;
        for (int i = 0; i < n; i++)
        {
            float r = (i % 2 == 0) ? outerRadius : innerRadius;
            verts[i] = new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r);
            angle += step;
        }
        return verts;
    }

    private Vector2[] CrossVerts()
    {
        const float t = 0.35f; // half-thickness of each arm
        const float L = 1f;    // outer extent of each arm
        return new Vector2[]
        {
            new Vector2(-t, L),  new Vector2(t, L),
            new Vector2(t, t),   new Vector2(L, t),
            new Vector2(L, -t),  new Vector2(t, -t),
            new Vector2(t, -L),  new Vector2(-t, -L),
            new Vector2(-t, -t), new Vector2(-L, -t),
            new Vector2(-L, t),  new Vector2(-t, t)
        };
    }

    private Vector2[] CapsuleVerts()
    {
        List<Vector2> pts = new List<Vector2>();
        const float radius = 0.5f;
        const float halfLength = 0.5f;
        const int arcRes = 12;
        for (int i = 0; i <= arcRes; i++)
        {
            float angle = -Mathf.PI / 2f + Mathf.PI * i / arcRes;
            pts.Add(new Vector2(halfLength + Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius));
        }
        for (int i = 0; i <= arcRes; i++)
        {
            float angle = Mathf.PI / 2f + Mathf.PI * i / arcRes;
            pts.Add(new Vector2(-halfLength + Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius));
        }
        return pts.ToArray();
    }

    private Vector2[] GearVerts(int teeth = 8, float outerR = 1f, float innerR = 0.75f)
    {
        List<Vector2> pts = new List<Vector2>();
        int res = teeth * 4;
        for (int i = 0; i < res; i++)
        {
            float angle = (Mathf.PI * 2f / res) * i;
            float r = (i % 4 < 2) ? outerR : innerR;
            pts.Add(new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r));
        }
        return pts.ToArray();
    }

    private Vector2[] HeartVerts()
    {
        int res = 32;
        Vector2[] pts = new Vector2[res];
        for (int i = 0; i < res; i++)
        {
            float t = (Mathf.PI * 2f / res) * i;
            float x = 16f * Mathf.Pow(Mathf.Sin(t), 3);
            float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t) - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
            pts[i] = new Vector2(x / 16f, y / 16f);
        }
        return pts;
    }

    private Vector2[] ShieldVerts()
    {
        List<Vector2> pts = new List<Vector2>();
        const int arcRes = 16;
        const float topRadius = 1f;
        for (int i = 0; i <= arcRes; i++)
        {
            float angle = Mathf.PI - Mathf.PI * i / arcRes;
            pts.Add(new Vector2(Mathf.Cos(angle) * topRadius, Mathf.Sin(angle) * topRadius * 0.6f + 0.4f));
        }
        pts.Add(new Vector2(0.65f, -0.2f));
        pts.Add(new Vector2(0f, -1f));
        pts.Add(new Vector2(-0.65f, -0.2f));
        return pts.ToArray();
    }

    // Resamples ANY closed polygon (regardless of its original vertex count)
    // into exactly `targetCount` points evenly spaced by arc length around
    // its perimeter. This is what makes morphing between shapes with
    // different natural vertex counts (e.g. Triangle vs Star) work cleanly —
    // every shape ends up with the same number of points in roughly
    // corresponding positions around the outline.
    private Vector2[] ResampleClosedPolygon(Vector2[] verts, int targetCount)
    {
        int n = verts.Length;
        float[] edgeLength = new float[n];
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            edgeLength[i] = Vector2.Distance(verts[i], verts[(i + 1) % n]);
            total += edgeLength[i];
        }

        Vector2[] result = new Vector2[targetCount];
        float step = total / targetCount;
        int edgeIndex = 0;
        float accumulated = 0f;

        for (int p = 0; p < targetCount; p++)
        {
            float targetDist = p * step;
            while (edgeIndex < n - 1 && accumulated + edgeLength[edgeIndex] < targetDist)
            {
                accumulated += edgeLength[edgeIndex];
                edgeIndex++;
            }
            float segLen = edgeLength[edgeIndex];
            float localT = segLen > 0.0001f ? (targetDist - accumulated) / segLen : 0f;
            result[p] = Vector2.Lerp(verts[edgeIndex], verts[(edgeIndex + 1) % n], localT);
        }
        return result;
    }

    // BUGFIX: previously centered each shape on its own bounding-box midpoint.
    // That's fine for symmetric shapes (circle, regular polygons, star) since
    // their bounding-box center IS their true center — but asymmetric shapes
    // like Heart have a bounding-box center that doesn't match where they
    // visually "sit", so the apparent center shifted depending on which
    // shape was currently showing. Centroid-based centering gives a
    // consistent, correct center for any shape, symmetric or not.
    private void NormalizeInPlace(Vector2[] points)
    {
        Vector2 centroid = ComputePolygonCentroid(points);
        for (int i = 0; i < points.Length; i++) points[i] -= centroid;

        float maxDist = 0f;
        foreach (var p in points) { float d = p.magnitude; if (d > maxDist) maxDist = d; }
        if (maxDist <= 0f) maxDist = 1f;

        for (int i = 0; i < points.Length; i++) points[i] /= maxDist;
    }

    // Standard shoelace-formula centroid for a simple closed polygon.
    private Vector2 ComputePolygonCentroid(Vector2[] pts)
    {
        float area = 0f, cx = 0f, cy = 0f;
        int n = pts.Length;
        for (int i = 0; i < n; i++)
        {
            Vector2 p0 = pts[i];
            Vector2 p1 = pts[(i + 1) % n];
            float cross = p0.x * p1.y - p1.x * p0.y;
            area += cross;
            cx += (p0.x + p1.x) * cross;
            cy += (p0.y + p1.y) * cross;
        }
        area *= 0.5f;

        if (Mathf.Abs(area) < 0.0001f)
        {
            // Degenerate/self-intersecting edge case — fall back to a simple
            // vertex average rather than dividing by a near-zero area.
            Vector2 avg = Vector2.zero;
            foreach (var p in pts) avg += p;
            return avg / n;
        }

        cx /= (6f * area);
        cy /= (6f * area);
        return new Vector2(cx, cy);
    }

    // ---------------- Rendering ----------------
    // Fixed-size pool built once (resolution segments + resolution joint
    // caps, doubled if the glow layer is on). Only their positions/sizes get
    // updated each frame — nothing is ever destroyed or recreated during
    // animation.

    private void BuildPool()
    {
        // Glow objects are created FIRST so they sit earlier in the sibling
        // order and therefore render BEHIND the crisp core lines created
        // afterwards — that's the entire trick to the layered neon look.
        if (enableGlow)
        {
            Sprite softBar = GetSoftBarSprite();
            Sprite softDot = GetSoftCircleSprite();
            for (int i = 0; i < resolution; i++)
            {
                glowSegmentLines.Add(CreateLineObject("GlowSegment", glowSegmentImages, softBar));
                glowJointCaps.Add(CreateLineObject("GlowJoint", glowJointImages, softDot));
            }
        }

        for (int i = 0; i < resolution; i++)
        {
            segmentLines.Add(CreateLineObject("Segment", segmentImages, null));
            jointCaps.Add(CreateLineObject("Joint", jointImages, null));
        }
    }

    private RectTransform CreateLineObject(string objName, List<Image> imageList, Sprite sprite)
    {
        GameObject obj = new GameObject(objName, typeof(Image));
        obj.transform.SetParent(transform, false);

        Image img = obj.GetComponent<Image>();
        img.sprite = sprite; // null = default hard-edged UI rectangle (core lines)
        img.color = Color.white; // placeholder — ApplyColors() overwrites this immediately
        img.raycastTarget = false;
        imageList.Add(img);

        RectTransform rt = obj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        return rt;
    }

    private static Gradient CreateDefaultGradient()
    {
        Gradient g = new Gradient();
        g.SetKeys(
            new GradientColorKey[] {
                new GradientColorKey(new Color(0.75f, 0.55f, 1f), 0f),   // light purple
                new GradientColorKey(new Color(0.55f, 0.75f, 1f), 0.5f), // light blue
                new GradientColorKey(new Color(0.95f, 0.6f, 0.9f), 1f)   // pink
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            }
        );
        return g;
    }

    // Ping-pong sample so the gradient closes seamlessly around the loop:
    // index 0 -> t=0, halfway around -> t=1, back to the end -> t=0 again.
    // Without this there'd be a hard color seam where the ring closes.
    private Color SampleGradient(int index)
    {
        float t = (float)index / resolution;
        float pingPong = 1f - Mathf.Abs(t * 2f - 1f);
        return outlineGradient.Evaluate(pingPong);
    }

    private void ApplyColors()
    {
        for (int i = 0; i < resolution; i++)
        {
            Color baseColor = SampleGradient(i);
            Color finalColor = baseColor;

            if (isSparking)
            {
                float t = (float)i / resolution;
                float d = Mathf.Abs(t - sparkProgress);
                d = Mathf.Min(d, 1f - d); // wraparound distance around the loop
                if (d < sparkWidth)
                {
                    float intensity = 1f - (d / sparkWidth);
                    float smooth = intensity * intensity * (3f - 2f * intensity); // smoothstep falloff
                    finalColor = Color.Lerp(baseColor, sparkColor, smooth);
                }
            }

            if (i < segmentImages.Count) segmentImages[i].color = finalColor;
            if (i < jointImages.Count) jointImages[i].color = finalColor;

            if (enableGlow && i < glowSegmentImages.Count)
            {
                Color glowColor = new Color(
                    Mathf.Clamp01(finalColor.r * glowBrightnessBoost),
                    Mathf.Clamp01(finalColor.g * glowBrightnessBoost),
                    Mathf.Clamp01(finalColor.b * glowBrightnessBoost),
                    glowAlpha);
                glowSegmentImages[i].color = glowColor;
                if (i < glowJointImages.Count) glowJointImages[i].color = glowColor;
            }
        }
    }

    private void UpdatePositions(Vector2[] points)
    {
        float radius = GetScaledRadius();
        float glowThickness = lineThickness * glowThicknessMultiplier;

        for (int i = 0; i < resolution; i++)
        {
            Vector2 aPx = points[i] * radius;
            Vector2 bPx = points[(i + 1) % resolution] * radius;

            boundaryPointsLocal[i] = aPx;

            Vector2 dir = (bPx - aPx).normalized;
            float dist = Vector2.Distance(aPx, bPx);
            float segAngle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Vector2 midPoint = aPx + dir * dist * 0.5f;

            RectTransform seg = segmentLines[i];
            seg.sizeDelta = new Vector2(dist + lineThickness, lineThickness);
            seg.anchoredPosition = midPoint;
            seg.localEulerAngles = new Vector3(0f, 0f, segAngle);

            RectTransform cap = jointCaps[i];
            cap.sizeDelta = new Vector2(lineThickness, lineThickness);
            cap.anchoredPosition = aPx;

            if (enableGlow && i < glowSegmentLines.Count)
            {
                RectTransform glowSeg = glowSegmentLines[i];
                glowSeg.sizeDelta = new Vector2(dist + glowThickness, glowThickness);
                glowSeg.anchoredPosition = midPoint;
                glowSeg.localEulerAngles = new Vector3(0f, 0f, segAngle);

                if (i < glowJointCaps.Count)
                {
                    RectTransform glowCap = glowJointCaps[i];
                    glowCap.sizeDelta = new Vector2(glowThickness, glowThickness);
                    glowCap.anchoredPosition = aPx;
                }
            }
        }
    }

    // The shape's current boundary polygon, in the same local (unrotated)
    // coordinate space as anchoredPosition — safe for another script (e.g. a
    // bouncing-balls decoration) to read every frame for collision checks.
    // Updated automatically whenever the shape's geometry actually changes
    // (initial draw + during morphs); stays correct at all other times since
    // rotation doesn't affect this local-space data at all. Treat as
    // read-only — this is the live internal array, not a copy.
    public Vector2[] GetBoundaryPointsLocal() => boundaryPointsLocal;

    private float GetScaledRadius()
    {
        float w = rectTransform.rect.width;
        float h = rectTransform.rect.height;
        if (w <= 0f || h <= 0f) { w = 200f; h = 200f; }
        return (Mathf.Min(w, h) / 2f) * sizeMultiplier;
    }

    // ---------------- Procedural soft sprites for the glow layer ----------------
    // Both are generated once and shared by every MenuShapeAnimator instance.
    // Unlike the core lines (plain hard-edged UI rectangles/squares), these
    // fade smoothly to transparent so the glow layer reads as a soft halo
    // instead of just "a second, bigger hard rectangle".

    private static Sprite cachedSoftBarSprite;
    private static Sprite cachedSoftCircleSprite;

    // A horizontally-uniform bar whose alpha falls off smoothly from a solid
    // center band to transparent top/bottom edges. Stretched by sizeDelta to
    // match each segment's length/thickness, so only the vertical falloff
    // needs to be baked into the texture.
    private static Sprite GetSoftBarSprite()
    {
        if (cachedSoftBarSprite != null) return cachedSoftBarSprite;

        const int w = 8;
        const int h = 64;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < h; y++)
        {
            float ny = (y + 0.5f) / h * 2f - 1f; // -1..1
            float alpha = Mathf.Clamp01(1f - ny * ny); // parabolic falloff, soft edges
            alpha = alpha * alpha; // extra falloff so it reads as a soft glow, not a disc
            Color c = new Color(1f, 1f, 1f, alpha);
            for (int x = 0; x < w; x++) tex.SetPixel(x, y, c);
        }
        tex.Apply();

        cachedSoftBarSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        return cachedSoftBarSprite;
    }

    // Same idea, radial instead of vertical — used for the glow joint caps
    // that sit at each vertex.
    private static Sprite GetSoftCircleSprite()
    {
        if (cachedSoftCircleSprite != null) return cachedSoftCircleSprite;

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
                alpha = alpha * alpha; // soft falloff instead of a hard antialiased edge
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();

        cachedSoftCircleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return cachedSoftCircleSprite;
    }
}