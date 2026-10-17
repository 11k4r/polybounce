using UnityEngine;
using UnityEngine.UI;

public class ScrollingBackground : MonoBehaviour
{
    [Tooltip("The script will auto-assign this if left blank!")]
    public RawImage backgroundImage;
    
    public Vector2 scrollSpeed = new Vector2(0.02f, 0.05f);

    void Start()
    {
        // If we forgot to assign the image in the Inspector, find it automatically
        if (backgroundImage == null)
        {
            backgroundImage = GetComponent<RawImage>();
        }
    }

    void Update()
    {
        // Safety check: if it's STILL null, stop running to prevent errors
        if (backgroundImage == null) return; 

        // Get the current UV rectangle
        Rect uvRect = backgroundImage.uvRect;
        
        // Offset the position based on time and speed
        uvRect.position += scrollSpeed * Time.deltaTime;
        
        // Apply the new UV rectangle back to the image
        backgroundImage.uvRect = uvRect;
    }
}