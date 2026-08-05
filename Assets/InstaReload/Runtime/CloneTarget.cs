namespace Nimrita.InstaReload
{
    /// <summary>
    /// TEMPORARY target for the generic clone probe. Delete when done.
    ///
    /// The body deliberately does real work rather than returning a constant, so a cloned version
    /// has to remap a typeof token, a generic-parameter reference and a string concat - the kinds
    /// of token the async state machine clone got wrong.
    /// </summary>
    public class CloneTarget
    {
        private int _counter;

        public string Describe<T>(T value)
        {
            _counter++;
            var name = typeof(T).Name;
            return "SUBSTITUTION-FIX:" + name + ":" + _counter;
        }
    }
}
