using UnityEngine;

public class SceneLoadButton : MonoBehaviour
{
    [Tooltip("Exact name of the scene to load (must be added to Build Settings).")]
    public string sceneName;

    public void Load()
    {
        if (SceneTransitionManager.Instance == null)
        {
            Debug.LogError("SceneLoadButton: SceneTransitionManager.Instance is null. " +
                "Make sure a TransitionCanvas exists in your very first/bootstrap scene.");
            return;
        }

        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("SceneLoadButton: sceneName is empty on " + gameObject.name + ".", this);
            return;
        }

        SceneTransitionManager.Instance.LoadScene(sceneName);
    }
}