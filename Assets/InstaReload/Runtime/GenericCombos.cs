using System.Collections.Generic;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// TEMPORARY target for the four untested generic combinations. Delete before merge.
    ///
    ///   1. Multiple type parameters      TwoParams&lt;T,U&gt;
    ///   2. Value-type constraint         StructOnly&lt;T&gt; where T : struct
    ///                                    (object is NOT a valid argument, so there is no shared
    ///                                     reference body to hook - only value instantiations)
    ///   3. Nested generic argument       Nested&lt;T&gt; called with List&lt;int&gt;
    ///   4. Generic METHOD on generic TYPE  GenericHolder&lt;T&gt;.Both&lt;U&gt; - both axes at once.
    ///      PREDICTION: this one fails. Constructing the declaring type still leaves the METHOD
    ///      open, and ILHook refuses an open generic method definition.
    ///
    /// CallSites gives the harvester the value-type and nested instantiations to find.
    /// </summary>
    public class GenericCombos
    {
        public string TwoParams<T, U>(T a, U b)
        {
            return "COMBOS-PATCHED:" + typeof(T).Name + "+" + typeof(U).Name;
        }

        public string StructOnly<T>(T value) where T : struct
        {
            return "COMBOS-PATCHED:" + typeof(T).Name;
        }

        public string Nested<T>(T value)
        {
            return "COMBOS-PATCHED:" + typeof(T).Name;
        }

        public string CallSites()
        {
            return TwoParams(1, "x")
                   + TwoParams("a", 2.5f)
                   + StructOnly(7)
                   + Nested(new List<int>());
        }
    }

    public class GenericHolder<T>
    {
        public string Both<U>(T first, U second)
        {
            return "COMBOS-PATCHED:" + typeof(T).Name + "/" + typeof(U).Name;
        }

        public string HolderCallSites()
        {
            return new GenericHolder<int>().Both<float>(1, 2f);
        }
    }
}
