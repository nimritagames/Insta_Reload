using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Nimrita.InstaReload.Tests
{
    /// <summary>
    /// PERMANENT hot reload regression suite. Do NOT delete - this is the thing that catches a
    /// regression without anyone having to notice one.
    ///
    /// ============================ HOW TO RUN ============================
    ///   1. Attach this component to a GameObject in the scene (once).
    ///   2. Enter Play Mode. It logs a SUITE line once per second.
    ///   3. Change Marker below to any new value and SAVE while still playing.
    ///   4. Read the next SUITE line. It grades itself:  SUITE 16/16 PASS
    ///      Any FAIL names the case and what it saw.
    /// ===================================================================
    ///
    /// WHY THIS WORKS: Marker is a const, so the compiler inlines it into every method body. After
    /// a hot patch, a PATCHED method returns the new marker and an UNPATCHED one still returns the
    /// old. So each case only has to declare whether it EXPECTS to be patched.
    ///
    /// Cases that expect to stay STALE are not bugs - they are documented limitations, and the
    /// suite fails if they silently start working (which would mean the documentation is wrong) or
    /// if they were supposed to work and did not.
    ///
    /// SELF-CONTAINED ON PURPOSE: InstaReload compiles one file at a time against the last
    /// assembly Unity built, so a fixture that references another file fails with CS0246. Every
    /// type this suite needs lives in this file.
    ///
    /// EVERY READ IS GUARDED: an unhandled exception in one case would suppress the whole SUITE
    /// line and hide the others - that mistake cost real time on 2026-08-05.
    ///
    /// ADDING A CASE: add a method returning Marker, call it from Evaluate with the expectation,
    /// and if it needs a value-type generic instantiation add a call site in CallSites so the
    /// harvester can see it.
    /// </summary>
    public sealed class InstaReloadSuite : MonoBehaviour
    {
        // The one edit point is SuiteMarker.Value at the bottom of this file.
        private const string Marker = SuiteMarker.Value;

        private const int MaxIterations = 50000;
        private static int _iterations;

        private float _timer;

        private int _plainInt;
        private List<int> _list;
        private Dictionary<string, int> _map;
        private Func<string> _lambda;
        private Action _eventBacking;
        private string _eventSeen = "(not run)";
        private string _asyncSeen = "(pending)";
        private string _ongoingSeen = "(pending)";
        private string _freshSeen = "(pending)";
        private bool _ongoingExited;

        /// <summary>
        /// The marker as compiled into THIS assembly, captured before any hot patch. Awake does not
        /// re-run on a hot reload, so this stays at the pre-edit value while patched methods start
        /// reporting the new one - which is what makes "expected to stay stale" gradable.
        /// Without it, every Stale case failed at baseline simply because nothing had been patched.
        /// </summary>
        private string _baselineMarker = "(unset)";

        private enum Expect
        {
            /// <summary>Must pick up the edit.</summary>
            Patched,

            /// <summary>Documented limitation - must keep the old body. A PASS here means the
            /// limitation still holds; a FAIL means behaviour changed and the docs are stale.</summary>
            Stale
        }

        private event Action Evt
        {
            add
            {
                _eventBacking += value;
                _eventSeen = Marker;
            }
            remove { _eventBacking -= value; }
        }

        private string Prop
        {
            get { return Marker; }
        }

        private void Awake()
        {
            _iterations = 0;
            _baselineMarker = Marker;
            _plainInt = 4242;
            _list = new List<int> { 1, 2, 3 };
            _map = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
            _lambda = () => Marker;
            StartCoroutine(OngoingCoroutine());
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 1.0f)
            {
                return;
            }

            _timer = 0f;

            Evt += NoOp;
            Evt -= NoOp;
            _ = RunAsync();
            StartCoroutine(FreshCoroutine());
            CallSites();

            Evaluate();
        }

        private void NoOp()
        {
        }

        // ---------------------------------------------------------------- the cases

        private string PlainBody()
        {
            return Marker;
        }

        private string PlainField()
        {
            return _plainInt == 4242 ? Marker : "FIELD-LOST";
        }

        private string GenericField()
        {
            return _list == null ? "LIST-NULL" : (_list.Count == 3 ? Marker : "LIST-WRONG");
        }

        private string GenericFieldTwoArgs()
        {
            return _map == null ? "MAP-NULL" : (_map.Count == 2 ? Marker : "MAP-WRONG");
        }

        private string GenericParameter(List<int> values)
        {
            return values == null ? "PARAM-NULL" : Marker;
        }

        private string GenericMethod<T>(T value)
        {
            return Marker;
        }

        private string LambdaField()
        {
            return _lambda == null ? "LAMBDA-NULL" : _lambda();
        }

        private async Task RunAsync()
        {
            await Task.Yield();
            _asyncSeen = Marker;
        }

        private IEnumerator OngoingCoroutine()
        {
            while (++_iterations < MaxIterations)
            {
                _ongoingSeen = Marker;
                yield return new WaitForSeconds(0.25f);
            }

            _ongoingExited = true;
        }

        private IEnumerator FreshCoroutine()
        {
            _freshSeen = Marker;
            yield break;
        }

        // Gives the harvester value-type instantiations to find. Without a call site, a value-type
        // instantiation cannot be reached and would legitimately stay stale.
        private void CallSites()
        {
            GenericMethod(1);
            GenericMethod(1.5f);
            GenericMethod("x");
            var box = new Boxed<int>();
            box.Read();
            box.WithOpenList();
            var refBox = new Boxed<string>();
            refBox.Read();
            new Combos().CallSites();
            new Boxed<int>().BothAxes(1);
        }

        // ---------------------------------------------------------------- grading

        private void Evaluate()
        {
            var failures = new StringBuilder();
            var passed = 0;
            var total = 0;

            Check("plain body", PlainBody(), Expect.Patched, ref passed, ref total, failures);
            Check("plain field", PlainField(), Expect.Patched, ref passed, ref total, failures);
            Check("generic field List<int>", GenericField(), Expect.Patched, ref passed, ref total, failures);
            Check("generic field Dictionary", GenericFieldTwoArgs(), Expect.Patched, ref passed, ref total, failures);
            Check("generic parameter", GenericParameter(_list), Expect.Patched, ref passed, ref total, failures);
            Check("lambda field", LambdaField(), Expect.Patched, ref passed, ref total, failures);
            Check("property getter", Prop, Expect.Patched, ref passed, ref total, failures);
            Check("event add accessor", _eventSeen, Expect.Patched, ref passed, ref total, failures);
            Check("coroutine ongoing", _ongoingSeen, Expect.Patched, ref passed, ref total, failures);
            Check("coroutine fresh", _freshSeen, Expect.Patched, ref passed, ref total, failures);

            // async is REFUSED by design (0e910e9) - it cloned to invalid IL and crashed the Editor.
            Check("async (refused)", _asyncSeen, Expect.Stale, ref passed, ref total, failures);

            Check("generic method <string>", GenericMethod("x"), Expect.Patched, ref passed, ref total, failures);
            Check("generic method <int>", GenericMethod(1), Expect.Patched, ref passed, ref total, failures);
            Check("generic method <double> no call site", GenericMethod(1.0d), Expect.Stale, ref passed, ref total, failures);

            Check("generic class <string>", new Boxed<string>().Read(), Expect.Patched, ref passed, ref total, failures);
            Check("generic class <int>", new Boxed<int>().Read(), Expect.Patched, ref passed, ref total, failures);
            Check("generic class new List<T>", new Boxed<int>().WithOpenList(), Expect.Patched, ref passed, ref total, failures);

            var combos = new Combos();
            Check("combo two type params", combos.TwoParams(1, "x"), Expect.Patched, ref passed, ref total, failures);
            Check("combo where T : struct", combos.StructOnly(7), Expect.Patched, ref passed, ref total, failures);
            Check("combo nested generic arg", combos.Nested(new List<int>()), Expect.Patched, ref passed, ref total, failures);
            Check("constructs own generic type", combos.ConstructsOwnGeneric(), Expect.Patched, ref passed, ref total, failures);

            // Both axes at once is a KNOWN failure - ILHook refuses a method left open after the
            // declaring type is constructed.
            Check("generic method on generic type", new Boxed<int>().BothAxes(1), Expect.Stale, ref passed, ref total, failures);

            var guard = _ongoingExited ? " GUARD-TRIPPED(iterator corrupted)" : string.Empty;
            var verdict = passed == total ? "PASS" : "FAIL";

            Debug.Log(
                $"[SUITE] {passed}/{total} {verdict} | marker={Marker}{guard} (frame {Time.frameCount})" +
                (failures.Length > 0 ? "\n" + failures : string.Empty));
        }

        private void Check(
            string name,
            string observed,
            Expect expect,
            ref int passed,
            ref int total,
            StringBuilder failures)
        {
            total++;

            // Patched: must report the CURRENT marker.
            // Stale:   must still report the marker compiled in before the edit. Equal at baseline,
            //          so a clean run reports all-pass before anything is patched.
            var ok = expect == Expect.Patched
                ? observed == Marker
                : observed == _baselineMarker;

            if (ok)
            {
                passed++;
                return;
            }

            failures.AppendLine(
                expect == Expect.Patched
                    ? $"   FAIL {name}: expected patched to \"{Marker}\", saw \"{observed}\""
                    : $"   FAIL {name}: expected STALE at \"{_baselineMarker}\" (documented limitation), saw \"{observed}\"");
        }
    }

    /// <summary>Generic class coverage. Same file, because of single-file compilation.</summary>
    public sealed class Boxed<T>
    {
        private const string Marker = SuiteMarker.Value;

        private int _counter;

        public string Read()
        {
            _counter++;
            return Marker;
        }

        public string WithOpenList()
        {
            var list = new List<T>();
            list.Add(default(T));
            return list.Count == 1 ? Marker : "OPENLIST-WRONG";
        }

        // Generic METHOD on a generic TYPE - both axes at once. Known to fail: constructing the
        // declaring type leaves the method open and ILHook refuses it.
        public string BothAxes<U>(U value)
        {
            return Marker;
        }
    }

    /// <summary>The generic combinations, kept here so the suite covers them too.</summary>
    public sealed class Combos
    {
        private const string Marker = SuiteMarker.Value;

        public string TwoParams<T, U>(T a, U b)
        {
            return Marker;
        }

        public string StructOnly<T>(T value) where T : struct
        {
            return Marker;
        }

        public string Nested<T>(T value)
        {
            return Marker;
        }

        // A plain method that CONSTRUCTS our own generic type - regressed once via the collapsed
        // method key and Mono's "open type while not compiling gshared".
        public string ConstructsOwnGeneric()
        {
            var boxed = new Boxed<int>();
            return boxed.Read() == Marker ? Marker : "CONSTRUCT-STALE";
        }

        public void CallSites()
        {
            TwoParams(1, "x");
            StructOnly(7);
            Nested(new List<int>());
        }
    }

    /// <summary>
    /// THE ONE EDIT POINT. Change Value while Play Mode is running to run the whole suite.
    /// Every case in this file inlines it, so patched methods report the new value and unpatched
    /// ones report the old.
    /// </summary>
    internal static class SuiteMarker
    {
        internal const string Value = "M0";
    }
}
