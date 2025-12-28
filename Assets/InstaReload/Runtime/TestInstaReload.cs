using UnityEngine;

namespace Nimrita.InstaReload
{
    public sealed class TestInstaReload : MonoBehaviour
    {
        private void Start()
        {
            InvokeRepeating(nameof(LogTick), 0.25f, 1.0f);
            InvokeRepeating(nameof(TickTick), 0.25f, 1.0f);
        }

        private void LogTick()
        {
            Debug.Log("InstaReload Test Hello");
        }

        private void TickTick()
        {
            Debug.Log("InstaReload Tick");
        }
    }
}
