using UnityEngine;

namespace Nimrita.InstaReload
{
    internal sealed class HotReloadEntryPointScanner : MonoBehaviour
    {
        private void Update()
        {
            HotReloadEntryPointManager.ScanIfNeeded();
        }
    }
}
