namespace Nimrita.InstaReload
{
    /// <summary>
    /// TEMPORARY target for generic-CLASS hot reload. Delete before merge.
    ///
    /// This is the second axis: the type carries the parameter, not the method. Describe is an
    /// ordinary method - only its declaring type is generic - so a hook needs a CONSTRUCTED type
    /// (GenericContainer&lt;object&gt;) before there is any native code to attach to.
    ///
    /// CallSiteHolder gives the harvester GenericInstanceType operands to find:
    ///   int    -> value type WITH a call site, should be reachable
    ///   string -> reference type, served by the shared body
    ///   float / decimal -> deliberately absent, must stay stale
    /// </summary>
    public class GenericContainer<T>
    {
        private int _counter;

        public string Describe(T item)
        {
            _counter++;
            return "DECLKEY-FIXED:" + typeof(T).Name + ":" + _counter;
        }
    }

    public static class CallSiteHolder
    {
        public static string Exercise()
        {
            return new GenericContainer<int>().Describe(1)
                   + " / " + new GenericContainer<string>().Describe("x");
        }
    }
}
