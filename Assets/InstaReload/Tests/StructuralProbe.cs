using System;
using UnityEngine;

namespace Nimrita.InstaReload.Tests
{
    /// <summary>
    /// PROBE for the four changes InstaReloadSuite structurally CANNOT reach: method removal,
    /// signature change, a new type, and a new field. The suite grades a marker flip, which only
    /// alters a constant inside existing bodies - it can never remove a member or add one. Those
    /// four were the last silent gap: hand-checked once each, months apart, and never since.
    ///
    /// They are gradeable now because the patcher emits a structured record for each. Before
    /// 2026-08-06 these were console prose - and the new-type line was Verbose-only, so invisible at
    /// default log level - which is why nothing could ever assert on them.
    ///
    /// HOW TO RUN. Attach, enter Play Mode, apply ONE variant by editing this file while playing,
    /// then read the [STRUCT] line and Library/InstaReload/events.jsonl.
    ///
    ///   A. METHOD REMOVAL     delete Removable() and its use in Probe()
    ///   B. SIGNATURE CHANGE   Signature(int) -> Signature(int, int), fix the call
    ///   C. NEW TYPE           uncomment AddedType and construct it in Probe()
    ///   D. NEW FIELD          add `private int _added = 7;` and read it in Probe()
    ///
    /// MEASURED 2026-08-06, Play Mode, each edit applied while running:
    ///   A -> events.jsonl: method.removed  StructuralProbe::Removable  reason=removed_from_source
    ///        [STRUCT] removable=(deleted)             patch landed; old body retained by design
    ///   B -> [STRUCT] sig=23 (was 20), dispatched=2   the 2-arg method runs via the dispatcher,
    ///        and events.jsonl records method.removed for the OLD 1-arg signature, correctly
    ///   C -> events.jsonl: type.added  Nimrita.InstaReload.Tests.AddedType
    ///        [STRUCT] type=added                      new type constructible AND callable
    ///   D -> events.jsonl: field.added  reason=routed_to_field_store
    ///        [STRUCT] field=0  <-- NOT 7. See below.
    ///
    /// WORTH KNOWING, from variant D: a field added during Play Mode reads its DEFAULT, not its
    /// initializer, on any instance that already exists. Field initializers run in the constructor,
    /// and an existing object's constructor does not re-run - the field is routed to
    /// HotReloadFieldStore and starts at default(T). Objects created AFTER the edit are unaffected.
    /// A real semantic rather than a defect, and invisible unless someone looks, which is exactly
    /// why it is written down here instead of being rediscovered later.
    ///
    /// WHY A PROBE AND NOT A SELF-GRADING SUITE CASE: a marker flip is reversible and idempotent, so
    /// the suite can run it every second forever. These four change the SHAPE of the assembly and
    /// cannot be un-applied without another reload, so they are driven deliberately and graded from
    /// the event log rather than from a pass counter.
    ///
    /// SELF-CONTAINED, same rule as the suite: InstaReload compiles one file at a time, so naming a
    /// type from another file makes this probe unable to hot reload itself.
    /// </summary>
    public sealed class StructuralProbe : MonoBehaviour
    {
        private float _timer;
        private int _calls;

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 1.0f)
            {
                return;
            }

            _timer = 0f;
            _calls++;

            string seen;
            try
            {
                seen = Probe();
            }
            catch (Exception ex)
            {
                seen = "THREW:" + ex.GetType().Name;
            }

            // Frame and call count included so a stalled probe is visible rather than looking idle.
            Debug.Log($"[STRUCT] {seen} calls={_calls} (frame {Time.frameCount})");
        }

        // EDIT ME. Each variant in the header changes this method or the members it uses.
        private string Probe()
        {
            var removable = Removable();
            var sig = Signature(2);
            return $"removable={removable} sig={sig}";
        }

        /// <summary>Variant A deletes this. The running build must keep the old body.</summary>
        private string Removable()
        {
            return "present";
        }

        /// <summary>Variant B changes this to Signature(int, int).</summary>
        private int Signature(int a)
        {
            return a * 10;
        }
    }

    // Variant C: uncomment this type AND a line in Probe() that constructs it.
    //
    // public sealed class AddedType
    // {
    //     public string Describe()
    //     {
    //         return "added";
    //     }
    // }
}
