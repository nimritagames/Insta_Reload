using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    ///   3. Change SuiteMarker.Value at the bottom of this file and SAVE while still playing.
    ///   4. Read the next SUITE line. It grades itself:  SUITE 22/22 PASS
    ///      Any FAIL names the case and what it saw. Any LEAK line names a case where the call
    ///      site and the method itself disagree - see VALIDITY below.
    /// ===================================================================
    ///
    /// WHY THIS WORKS: Marker is a const, so each method body carries its own copy of the marker
    /// value. After a hot patch, a PATCHED method returns the new marker and an UNPATCHED one still
    /// returns the old. So each case only has to declare whether it EXPECTS to be patched.
    ///
    /// ==================== VALIDITY: WHY WE GO THROUGH REFLECTION ====================
    /// An earlier version of this suite called each case DIRECTLY from Evaluate and graded the
    /// returned string. That could not tell these two situations apart:
    ///
    ///     (a) the case method's own body was patched            <- what we want to measure
    ///     (b) the CALL SITE inside patched Evaluate was resolved to some other copy of the
    ///         method, or the call was inlined, so the value never came from the runtime method
    ///
    /// Both look identical from the caller. Two concrete ways (b) happens here:
    ///   * INLINING. Mono may inline a small callee into its caller, so a value can be produced
    ///     without the callee's runtime method ever being entered.
    ///   * CALL-SITE FALL-THROUGH. When the patcher clones the new body it remaps each method
    ///     token to the runtime method, but on a lookup MISS it keeps the reference pointing at
    ///     the freshly compiled hot assembly (InstaReloadPatcher.CloneInstruction). The patched
    ///     caller then calls the HOT copy, which of course carries the new marker - so a method
    ///     the patcher explicitly REFUSED still reports as patched.
    ///
    /// So every graded case is invoked by name through reflection on the RUNTIME object's type.
    /// Reflection resolves the method at run time from the live type, never from a metadata token
    /// baked into a patched body, and reflection calls are not inlined. A PASS therefore means the
    /// runtime method itself behaved as expected.
    ///
    /// The direct call is still made, purely as a cross-check. When the direct result and the
    /// reflected result disagree the suite prints a LEAK line for that case. That line means the
    /// call site lied - which is a product bug, not a harness artefact.
    ///
    /// EVERY CASE IS ALSO STATE-DEPENDENT AND [MethodImpl(NoInlining)]: the body reads instance
    /// state before returning the marker, so a result can only come from that body executing
    /// against the real object.
    /// ===============================================================================
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
    /// line and hide the others - that mistake cost real time on 2026-08-05. Reflection failures
    /// come back as sentinel strings (NO-METHOD, THREW:xxx) instead of propagating.
    ///
    /// ADDING A CASE: add a method that reads instance state and returns Marker, mark it
    /// NoInlining, call it from Evaluate with BOTH a direct call and a Reflected(...) call plus the
    /// expectation, and if it needs a value-type generic instantiation add a call site in CallSites
    /// so the harvester can see it.
    ///
    /// GENERICS: fully supported as of 2026-08-06. Generic methods, generic classes, multiple type
    /// parameters, `where T : struct`, nested generic arguments, AND a generic method on a generic
    /// type (Boxed&lt;T&gt;.BothAxes&lt;U&gt;, both axes at once) all patch. Every generic case here
    /// is declared Patched. `async` is the only remaining Stale case in the suite.
    ///
    /// NO LEAK LINE SHOULD EVER PRINT NOW. The token fall-through behind them was fixed on
    /// 2026-08-06 (NeedsRuntimeRetarget): generic newobj and generic call sites are rebuilt against
    /// the runtime type instead of staying bound to the freshly compiled assembly. Before that fix
    /// this header documented one expected LEAK on "generic method on generic type", and a session's
    /// second reload printed ten. Both are gone, across three generations. ANY leak line is now a
    /// regression - chase it.
    ///
    /// ONE-CYCLE SETTLE, not a bug: "coroutine ongoing" can report the PREVIOUS marker on the first
    /// grade after a patch. OngoingCoroutine resumes every 0.25s, so if the first Evaluate after a
    /// reload runs before that resume, the field it writes has not been refreshed yet. It passes on
    /// the next line. Only chase it if it persists for more than one SUITE line.
    ///
    /// WHAT THIS DESIGN CANNOT COVER, and why - do not assume these are tested:
    ///   * method REMOVAL and SIGNATURE CHANGE - need structurally different source, not a marker
    ///     flip. Verified manually 2026-08-05.
    ///   * BASE CLASS / INTERFACE changes - same reason; they are also refused-with-warning, so
    ///     there is no observable behaviour change to grade.
    ///   * NEW types and NEW fields - a marker flip does not add members.
    ///   * REPLAY safety and the crash-loop guard - only observable across Editor sessions.
    /// A green suite therefore means "no regression in what a marker flip can reach", not
    /// "everything works".
    /// </summary>
    public sealed class InstaReloadSuite : MonoBehaviour
    {
        // The one edit point is SuiteMarker.Value at the bottom of this file.
        private const string Marker = SuiteMarker.Value;

        private const int PlainSeed = 4242;
        private const string FieldSeed = "seed";
        private const int ListSeed = 3;
        private const int MapSeed = 2;
        private const int MaxIterations = 50000;

        // Cases are private; reflection has to be told so explicitly.
        private const BindingFlags CaseBinding =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static int _iterations;

        private float _timer;

        private int _plainInt;
        private string _fieldSeed;
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
        /// Targets for the generic-class cases, constructed in Awake ON PURPOSE.
        /// Constructing them inside Evaluate produced HOT-ASSEMBLY instances - the newobj token for
        /// a generic type is not remapped to the runtime type, so the suite was grading the hot
        /// copy and could not see the runtime type at all. Awake runs once, before any patch
        /// exists, so these are unambiguously runtime-assembly objects.
        /// </summary>
        private Boxed<int> _boxedInt;
        private Boxed<string> _boxedString;
        private Combos _combos;

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
                _eventSeen = _plainInt == PlainSeed ? Marker : "EVENT-FIELD-LOST";
            }
            remove { _eventBacking -= value; }
        }

        private string Prop
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return _fieldSeed == FieldSeed ? Marker : "PROP-FIELD-LOST"; }
        }

        private void Awake()
        {
            _iterations = 0;
            _baselineMarker = Marker;
            _plainInt = PlainSeed;
            _fieldSeed = FieldSeed;
            _list = new List<int> { 1, 2, 3 };
            _map = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
            _lambda = () => Marker;
            _boxedInt = new Boxed<int>();
            _boxedString = new Boxed<string>();
            _combos = new Combos();
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

            // Triggers go through reflection for the same reason the graded cases do: a direct call
            // from patched Update could be resolved to a copy that is not the runtime method, and
            // these three cases are observed through the fields they write.
            var handler = (Action)NoOp;
            Trigger(this, "add_Evt", new object[] { handler });
            Trigger(this, "remove_Evt", new object[] { handler });
            Trigger(this, "RunAsync", null);
            if (Trigger(this, "FreshCoroutine", null) is IEnumerator fresh)
            {
                StartCoroutine(fresh);
            }

            // Direct, on purpose: this one exists so the generic instantiations appear as real IL
            // call sites for the harvester to find. Its return values are not graded.
            CallSites();

            Evaluate();
        }

        private void NoOp()
        {
        }

        // ---------------------------------------------------------------- the cases
        //
        // Every case: [MethodImpl(NoInlining)] so it cannot be folded into its caller, and reads
        // instance state so the marker it returns can only have come from this body running
        // against the real object.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string PlainBody()
        {
            return _plainInt == PlainSeed ? Marker : "INT-FIELD-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string PlainField()
        {
            return _fieldSeed == FieldSeed ? Marker : "STRING-FIELD-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string GenericField()
        {
            return _list == null ? "LIST-NULL" : (_list.Count == ListSeed ? Marker : "LIST-WRONG");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string GenericFieldTwoArgs()
        {
            return _map == null ? "MAP-NULL" : (_map.Count == MapSeed ? Marker : "MAP-WRONG");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string GenericParameter(List<int> values)
        {
            if (values == null)
            {
                return "PARAM-NULL";
            }

            return _plainInt == PlainSeed ? Marker : "PARAM-FIELD-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string GenericMethod<T>(T value)
        {
            return _plainInt == PlainSeed ? Marker : "GENERICMETHOD-FIELD-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string LambdaField()
        {
            return _lambda == null ? "LAMBDA-NULL" : _lambda();
        }

        private async Task RunAsync()
        {
            await Task.Yield();
            _asyncSeen = _plainInt == PlainSeed ? Marker : "ASYNC-FIELD-LOST";
        }

        private IEnumerator OngoingCoroutine()
        {
            while (++_iterations < MaxIterations)
            {
                _ongoingSeen = _plainInt == PlainSeed ? Marker : "ONGOING-FIELD-LOST";
                yield return new WaitForSeconds(0.25f);
            }

            _ongoingExited = true;
        }

        private IEnumerator FreshCoroutine()
        {
            _freshSeen = _plainInt == PlainSeed ? Marker : "FRESH-FIELD-LOST";
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

        // ---------------------------------------------------------------- invocation

        /// <summary>
        /// Invokes a case by NAME on the runtime type of <paramref name="target"/> and returns what
        /// it produced. This is the authoritative observation: the method is resolved from the live
        /// type at call time, so no metadata token baked into a patched body can redirect it, and
        /// reflection calls are never inlined.
        /// Failures come back as sentinel strings so one broken case cannot suppress the rest.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private string Reflected(object target, string methodName, Type[] typeArgs, object[] args)
        {
            try
            {
                if (target == null)
                {
                    return "TARGET-NULL";
                }

                // ORIGIN CHECK. Reflection proves WHICH METHOD ran; it says nothing about which
                // OBJECT it ran on. A case whose target is constructed inside patched Evaluate can
                // get a hot-assembly instance if that newobj token was not remapped - and then both
                // the direct call and the reflected call interrogate the SAME wrong object, agree
                // with each other, and report a method as patched that the patcher openly refused.
                // Caught exactly that way: the patcher logged "Boxed`1::BothAxes`1: NO instantiation
                // patched" while this suite scored it a pass.
                // `this` was created by Unity from the assembly Unity compiled, so its assembly is
                // authoritative even when read from inside a patched body.
                var targetType = target.GetType();
                if (targetType.Assembly != GetType().Assembly)
                {
                    return "HOT-OBJECT:" + targetType.Assembly.GetName().Name;
                }

                var method = targetType.GetMethod(methodName, CaseBinding);
                if (method == null)
                {
                    return "NO-METHOD";
                }

                if (typeArgs != null && typeArgs.Length > 0)
                {
                    method = method.MakeGenericMethod(typeArgs);
                }

                return method.Invoke(target, args) as string ?? "NULL-RESULT";
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException
                    : ex;
                return "THREW:" + inner.GetType().Name;
            }
        }

        /// <summary>
        /// Same resolution rules as <see cref="Reflected"/>, for the cases whose result is observed
        /// through a field they write rather than through a return value.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Trigger(object target, string methodName, object[] args)
        {
            try
            {
                if (target == null)
                {
                    return null;
                }

                var method = target.GetType().GetMethod(methodName, CaseBinding);
                return method == null ? null : method.Invoke(target, args);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- grading

        private void Evaluate()
        {
            var t = new Tally();
            var boxedInt = _boxedInt;
            var boxedString = _boxedString;
            var combos = _combos;

            // ---- proven working today ----
            Check(t, "plain body",
                PlainBody(),
                Reflected(this, "PlainBody", null, null), Expect.Patched);

            Check(t, "plain field",
                PlainField(),
                Reflected(this, "PlainField", null, null), Expect.Patched);

            Check(t, "generic field List<int>",
                GenericField(),
                Reflected(this, "GenericField", null, null), Expect.Patched);

            Check(t, "generic field Dictionary",
                GenericFieldTwoArgs(),
                Reflected(this, "GenericFieldTwoArgs", null, null), Expect.Patched);

            Check(t, "generic parameter",
                GenericParameter(_list),
                Reflected(this, "GenericParameter", null, new object[] { _list }), Expect.Patched);

            Check(t, "lambda field",
                LambdaField(),
                Reflected(this, "LambdaField", null, null), Expect.Patched);

            Check(t, "property getter",
                Prop,
                Reflected(this, "get_Prop", null, null), Expect.Patched);

            // Written by the event accessor / coroutines, which Update triggers reflectively.
            // There is one observation only, so it is passed as both channels.
            Check(t, "event add accessor", _eventSeen, _eventSeen, Expect.Patched);
            Check(t, "coroutine ongoing", _ongoingSeen, _ongoingSeen, Expect.Patched);
            Check(t, "coroutine fresh", _freshSeen, _freshSeen, Expect.Patched);

            Check(t, "generic method <string>",
                GenericMethod("x"),
                Reflected(this, "GenericMethod", new[] { typeof(string) }, new object[] { "x" }), Expect.Patched);

            Check(t, "generic method <int>",
                GenericMethod(1),
                Reflected(this, "GenericMethod", new[] { typeof(int) }, new object[] { 1 }), Expect.Patched);

            Check(t, "generic class <string>",
                boxedString.Read(),
                Reflected(boxedString, "Read", null, null), Expect.Patched);

            Check(t, "generic class <int>",
                boxedInt.Read(),
                Reflected(boxedInt, "Read", null, null), Expect.Patched);

            Check(t, "generic class new List<T>",
                boxedInt.WithOpenList(),
                Reflected(boxedInt, "WithOpenList", null, null), Expect.Patched);

            Check(t, "combo two type params",
                combos.TwoParams(1, "x"),
                Reflected(combos, "TwoParams", new[] { typeof(int), typeof(string) }, new object[] { 1, "x" }), Expect.Patched);

            Check(t, "combo where T : struct",
                combos.StructOnly(7),
                Reflected(combos, "StructOnly", new[] { typeof(int) }, new object[] { 7 }), Expect.Patched);

            Check(t, "combo nested generic arg",
                combos.Nested(new List<int>()),
                Reflected(combos, "Nested", new[] { typeof(List<int>) }, new object[] { new List<int>() }), Expect.Patched);

            Check(t, "constructs own generic type",
                combos.ConstructsOwnGeneric(),
                Reflected(combos, "ConstructsOwnGeneric", null, null), Expect.Patched);

            // ---- proven REFUSED today: these must stay stale ----
            // async is refused because our Release emit makes the state machine a struct while
            // Unity's build makes it a class (796b63e). A PASS here means the refusal still holds.
            Check(t, "async (refused)", _asyncSeen, _asyncSeen, Expect.Stale);

            Check(t, "generic method <double>",
                GenericMethod(1.0d),
                Reflected(this, "GenericMethod", new[] { typeof(double) }, new object[] { 1.0d }), Expect.Patched);

            Check(t, "generic method on generic type",
                boxedInt.BothAxes(1),
                Reflected(boxedInt, "BothAxes", new[] { typeof(int) }, new object[] { 1 }), Expect.Patched);

            var guard = _ongoingExited ? " GUARD-TRIPPED(iterator corrupted)" : string.Empty;
            var verdict = t.Passed == t.Total ? "PASS" : "FAIL";

            Debug.Log(
                $"[SUITE] {t.Passed}/{t.Total} {verdict} | marker={Marker}{guard} (frame {Time.frameCount})" +
                (t.Notes.Length > 0 ? "\n" + t.Notes : string.Empty));
        }

        private sealed class Tally
        {
            public int Passed;
            public int Total;
            public readonly StringBuilder Notes = new StringBuilder();
        }

        /// <summary>
        /// Grades one case. <paramref name="reflected"/> is the authoritative observation - it came
        /// from the runtime method itself. <paramref name="direct"/> is the ordinary call site, kept
        /// only so a disagreement between the two can be reported: that means the call site did not
        /// reach the method it names.
        /// </summary>
        private void Check(Tally tally, string name, string direct, string reflected, Expect expect)
        {
            tally.Total++;

            // Patched: must report the CURRENT marker.
            // Stale:   must still report the marker compiled in before the edit. Equal at baseline,
            //          so a clean run reports all-pass before anything is patched.
            var ok = expect == Expect.Patched
                ? reflected == Marker
                : reflected == _baselineMarker;

            if (direct != reflected)
            {
                tally.Notes.AppendLine(
                    $"   LEAK {name}: call site saw \"{direct}\" but the runtime method returned \"{reflected}\" " +
                    "- the call site did not reach the method it names");
            }

            if (ok)
            {
                tally.Passed++;
                return;
            }

            tally.Notes.AppendLine(
                expect == Expect.Patched
                    ? $"   FAIL {name}: expected patched to \"{Marker}\", saw \"{reflected}\""
                    : $"   FAIL {name}: expected STALE at \"{_baselineMarker}\" (documented limitation), saw \"{reflected}\"");
        }
    }

    /// <summary>Generic class coverage. Same file, because of single-file compilation.</summary>
    public sealed class Boxed<T>
    {
        private const string Marker = SuiteMarker.Value;

        private int _counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Read()
        {
            _counter++;
            return _counter > 0 ? Marker : "COUNTER-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string WithOpenList()
        {
            _counter++;
            var list = new List<T>();
            list.Add(default(T));
            return list.Count == 1 && _counter > 0 ? Marker : "OPENLIST-WRONG";
        }

        // Generic METHOD on a generic TYPE - both axes at once. Known to fail: constructing the
        // declaring type leaves the method open and ILHook refuses it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string BothAxes<U>(U value)
        {
            _counter++;
            return _counter > 0 ? Marker : "BOTHAXES-COUNTER-LOST";
        }
    }

    /// <summary>The generic combinations, kept here so the suite covers them too.</summary>
    public sealed class Combos
    {
        private const string Marker = SuiteMarker.Value;

        private int _counter;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string TwoParams<T, U>(T a, U b)
        {
            _counter++;
            return _counter > 0 ? Marker : "TWOPARAMS-COUNTER-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string StructOnly<T>(T value) where T : struct
        {
            _counter++;
            return _counter > 0 ? Marker : "STRUCTONLY-COUNTER-LOST";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Nested<T>(T value)
        {
            _counter++;
            return _counter > 0 ? Marker : "NESTED-COUNTER-LOST";
        }

        // A plain method that CONSTRUCTS our own generic type - regressed once via the collapsed
        // method key and Mono's "open type while not compiling gshared".
        // Constructs our own generic type but does NOT read through it, so this grades the
        // constructing method itself rather than the generic class's support level.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ConstructsOwnGeneric()
        {
            _counter++;
            var boxed = new Boxed<int>();
            return boxed != null && _counter > 0 ? Marker : "CONSTRUCT-NULL";
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
    /// Every case in this file carries its own copy of it, so patched methods report the new value
    /// and unpatched ones report the old.
    /// </summary>
    internal static class SuiteMarker
    {
        internal const string Value = "M0";
    }
}
