namespace Nimrita.InstaReload
{
    /// <summary>
    /// TEMPORARY target for the generic hot reload work. Delete before merge.
    ///
    /// The body does real work rather than returning a constant, so a cloned version must remap a
    /// typeof token, a generic-parameter reference and a string concat.
    ///
    /// CallSites() exists so the harvester has value-type instantiations to find - it can only see
    /// instantiations that appear at call sites inside the edited assembly:
    ///   int, float -> value types, each needs its own hook
    ///   string     -> reference type, already served by the shared hook
    ///   decimal    -> deliberately NOT called here, so it must stay stale
    /// </summary>
    public class CloneTarget
    {
        private int _counter;

        public string Describe<T>(T value)
        {
            _counter++;
            var name = typeof(T).Name;
            return "REAL-TYPEARGS:" + name + ":" + _counter;
        }

        public string CallSites()
        {
            return Describe(1) + " / " + Describe(1.5f) + " / " + Describe("x");
        }
    }
}
