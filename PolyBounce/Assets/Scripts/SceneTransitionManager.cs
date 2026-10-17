using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneTransitionManager : MonoBehaviour
{
    public static SceneTransitionManager Instance;

    [Header("UI References")]
    public CanvasGroup fadeCanvasGroup;
    public float fadeDuration = 0.5f;

    [Header("Hitch Protection")]
    [Tooltip("The maximum amount of (unscaled) time a single frame is allowed to contribute to a fade's timer. Without this cap, a scene-activation hitch (heavy Awake/Start work in the newly loaded scene spiking one or two frames' real duration) can push the timer from 0 straight past fadeDuration in a single iteration — the loop 'completes' correctly by the numbers, but it renders as an instant snap instead of a smooth fade. Capping it guarantees a minimum number of rendered frames for the animation no matter how slow any single frame is.")]
    public float maxDeltaTimePerFrame = 1f / 30f;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        // Automatically fade in when the game first launches
        fadeCanvasGroup.alpha = 1f;
        StartCoroutine(FadeCanvasGroup(1f, 0f));
    }

    public void LoadScene(string sceneName)
    {
        StartCoroutine(FadeAndLoad(sceneName));
    }

    private IEnumerator FadeAndLoad(string sceneName)
    {
        // 1. Fade out the current scene to black
        fadeCanvasGroup.blocksRaycasts = true;
        yield return FadeCanvasGroup(fadeCanvasGroup.alpha, 1f);
        fadeCanvasGroup.alpha = 1f;

        // 2. Load the new scene in the background
        yield return SceneManager.LoadSceneAsync(sceneName);

        // Eat at least one frame to clear the immediate post-activation
        // spike. This alone isn't a complete fix (see maxDeltaTimePerFrame
        // above for why), but it's a cheap first line of defense.
        yield return null;

        // 3. Fade in the new scene (reveal)
        yield return FadeCanvasGroup(1f, 0f);
        fadeCanvasGroup.alpha = 0f;
        fadeCanvasGroup.blocksRaycasts = false;
    }

    // Shared fade helper used by the initial launch fade, and both halves
    // of a scene transition. Clamps each frame's contribution to the timer
    // (see maxDeltaTimePerFrame) so a scene-load hitch can't compress the
    // whole animation down into a single invisible frame.
    private IEnumerator FadeCanvasGroup(float from, float to)
    {
        float timer = 0f;
        while (timer < fadeDuration)
        {
            float t = fadeDuration > 0f ? timer / fadeDuration : 1f;
            fadeCanvasGroup.alpha = Mathf.Lerp(from, to, t);
            timer += Mathf.Min(Time.unscaledDeltaTime, maxDeltaTimePerFrame);
            yield return null;
        }
        fadeCanvasGroup.alpha = to;
    }
}