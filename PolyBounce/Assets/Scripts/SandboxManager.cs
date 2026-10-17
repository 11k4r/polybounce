using UnityEngine;
using UnityEngine.UI; // Required for manipulating UI Images

// Central controller for the Sandbox scene. Owns Pause/Resume/Reset and is
// the single thing UI buttons (and later, settings panels) talk to.
//
// Pause is implemented via Time.timeScale rather than a custom flag checked
// inside SandboxArena/SandboxBallSpawner. That's a deliberate simplification:
// every position/rotation update in both of those scripts already reads
// Time.deltaTime, so scaling time to 0 freezes the whole simulation for
// free, with zero extra "if paused, skip" code scattered through them.
//
// Caveat worth knowing for later: Time.timeScale is global — if you ever add
// UI animations elsewhere in the app that should keep playing while the
// sandbox is paused, drive those specific animations with
// Time.unscaledDeltaTime instead of Time.deltaTime.
public class SandboxManager : MonoBehaviour
{
    [Header("References")]
    public MenuShapeAnimator arena;
    public SandboxBallSpawner ballSpawner;

    [Header("UI References")]
    public Image pauseButton;   // Assign the Button's Image component here
    public Sprite pauseSprite;       // Assign "pause-removebg-preview.png" here
    public Sprite continueSprite;    // Assign your continue image here

    public bool IsPaused { get; private set; }

    void Awake()
    {
        // Defensive: guarantee we always start unpaused, even if a previous
        // play session (or scene) left timeScale at 0 for some reason.
        Time.timeScale = 1f;
        IsPaused = false;
        
        // Ensure the button starts with the correct icon
        UpdatePauseButtonUI(); 
    }

    void OnDestroy()
    {
        // Defensive: never leave the rest of the app frozen if this scene
        // is unloaded while paused.
        Time.timeScale = 1f;
    }

    public void Pause()
    {
        IsPaused = true;
        Time.timeScale = 0f;
        UpdatePauseButtonUI();
    }

    public void Resume()
    {
        IsPaused = false;
        Time.timeScale = 1f;
        UpdatePauseButtonUI();
    }

    public void TogglePause()
    {
        if (IsPaused) Resume();
        else Pause();
    }

    /// <summary>
    /// Swaps the sprite on the pause button based on the current IsPaused state.
    /// Includes null checks so the game doesn't crash if the UI isn't assigned yet.
    /// </summary>
    private void UpdatePauseButtonUI()
    {
        if (pauseButton != null)
        {
            if (IsPaused && continueSprite != null)
            {
                pauseButton.sprite = continueSprite;
            }
            else if (!IsPaused && pauseSprite != null)
            {
                pauseButton.sprite = pauseSprite;
            }
        }
    }

    /// <summary>
    /// Resets the ball simulation back to a single fresh ball at the
    /// center. Deliberately does NOT touch the arena shape or any settings
    /// the user has configured — this is a "restart the balls" reset, not a
    /// "restore defaults" reset. Wire a separate button later if you want
    /// the latter too.
    /// </summary>
    public void ResetSimulation()
    {
        if (ballSpawner != null) ballSpawner.ResetSimulation();
    }

    public void ReturnToMainMenu()
        {
            // Resets time scale in case the game is paused when hitting home
            Time.timeScale = 1f; 
            SceneTransitionManager.Instance.LoadScene("MainMenu"); // Use your exact scene name
        }
}