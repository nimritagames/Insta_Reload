using UnityEngine;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// Test script for InstaReload invisible hot reload.
    ///
    /// HOW TO TEST:
    /// 1. Attach this to a GameObject
    /// 2. Enter Play Mode
    /// 3. While playing, edit Update() to add a new method call
    /// 4. Add the new method below
    /// 5. Save - changes apply in ~7ms with no domain reload!
    ///
    /// Example: Add CheckStatus() method and call it from Update()
    /// </summary>
    public sealed class TestInstaReload : MonoBehaviour
    {
        private float timer = 0f;

        private void Update()
        {
            timer += Time.deltaTime;
            if (timer >= 1.0f)
            {
                timer = 0f;
                Debug.Log($"[InstaReload] Update tick at {Time.time:F2}s");

                // DURING PLAY MODE: Uncomment this and add the method below
                // CheckStatus();
            }
        }

        // DURING PLAY MODE: Add new methods here
        // private void CheckStatus()
        // {
        //     Debug.Log($"[InstaReload] NEW METHOD! Frame: {Time.frameCount}");
        // }
    }
}
