using System.Collections.Generic;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// TEMPORARY target isolating the Mono "Method with open type while not compiling gshared"
    /// failure. Delete before merge.
    ///
    /// The earlier failure involved a method that BOTH constructed a closed instantiation AND
    /// called a generic method on it, so the trigger was ambiguous. Four shapes here, one per
    /// suspicion, each independently observable:
    ///
    ///   Plain           - no generics constructed at all. CONTROL, must patch.
    ///   WithOpenList    - new List&lt;T&gt;() using the TYPE's own parameter. The common real pattern;
    ///                     if this fails, "generic classes work" is a much weaker claim.
    ///   WithClosedList  - new List&lt;int&gt;(), a closed instantiation unrelated to T.
    ///   WithDictionary  - Dictionary&lt;string,T&gt;, open with two arguments.
    /// </summary>
    public class Repo<T>
    {
        private int _counter;

        public string Plain()
        {
            _counter++;
            return "GSHARED-TEST:plain:" + _counter;
        }

        public string WithOpenList()
        {
            var list = new List<T>();
            list.Add(default(T));
            return "GSHARED-TEST:openlist:" + list.Count + ":" + typeof(T).Name;
        }

        public string WithClosedList()
        {
            var list = new List<int> { 1, 2 };
            return "GSHARED-TEST:closedlist:" + list.Count;
        }

        public string WithDictionary()
        {
            var map = new Dictionary<string, T>();
            map["k"] = default(T);
            return "GSHARED-TEST:dict:" + map.Count;
        }
    }

    public static class RepoCallSites
    {
        public static string Exercise()
        {
            return new Repo<string>().WithOpenList()
                   + new Repo<int>().WithOpenList()
                   + new Repo<int>().Plain();
        }
    }
}
