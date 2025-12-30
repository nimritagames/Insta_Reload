namespace Nimrita.InstaReload
{
    internal static class HotReloadBridge
    {
        public static object Invoke(object instance, int methodId, object[] args)
        {
            return HotReloadDispatcher.Invoke(instance, methodId, args);
        }
    }
}
