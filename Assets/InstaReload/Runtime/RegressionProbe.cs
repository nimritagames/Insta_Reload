using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Nimrita.InstaReload
{
    /// <summary>
    /// TEMPORARY regression probe. Delete before merge.
    ///
    /// Re-checks every capability verified EARLIER today, because the six generic commits rewrote
    /// the core cloning and key logic and three of today's fixes each caused a regression.
    ///
    /// Self-contained on purpose - InstaReload compiles one file at a time, so a probe cannot
    /// reference types from other files (CS0246).
    ///
    /// Every read is guarded and separately tagged, so one failure cannot suppress the others.
    /// Async is included deliberately: it must still be REFUSED with a warning, not crash the
    /// Editor. Ongoing coroutine uses a bounded counter so a corrupted state machine reports
    /// instead of hanging.
    /// </summary>
    public sealed class RegressionProbe : MonoBehaviour
    {
        private const int MaxIterations = 50000;
        private static int _iterations;

        private float _timer;

        private int _plainInt;                        // non-generic field (control)
        private List<int> _list;                      // generic field - fixed in b47b42e
        private Dictionary<string, int> _map;         // generic field, two args
        private Func<string> _lambda;                 // lambda / Func<T> field
        private Action _eventBacking;
        private string _eventTag = "(not run)";
        private string _asyncTag = "(pending)";
        private string _ongoingTag = "(pending)";
        private bool _ongoingExited;

        private event Action Evt
        {
            add
            {
                _eventBacking += value;
                _eventTag = "event REGRESS-OK";
            }
            remove { _eventBacking -= value; }
        }

        private string Prop
        {
            get { return "prop REGRESS-OK"; }
        }

        private void Awake()
        {
            _iterations = 0;
            _plainInt = 4242;
            _list = new List<int> { 1, 2, 3 };
            _map = new Dictionary<string, int> { { "a", 1 }, { "b", 2 } };
            _lambda = () => "lambda REGRESS-OK";
            StartCoroutine(OngoingCoroutine());
        }

        private bool ShouldContinue()
        {
            return ++_iterations < MaxIterations;
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

            var intTag = _plainInt == 0 ? "PLAIN-ZERO" : "plainInt=" + _plainInt;
            var listTag = _list == null ? "LIST-NULL" : "list=" + _list.Count;
            var mapTag = _map == null ? "MAP-NULL" : "map=" + _map.Count;
            var lambdaTag = _lambda == null ? "LAMBDA-NULL" : _lambda();
            var guard = _ongoingExited ? " GUARD-TRIPPED" : string.Empty;

            Debug.Log(
                $"[Reg] {intTag} | {listTag} | {mapTag} | {lambdaTag} | {Prop} | {_eventTag} | " +
                $"{_asyncTag} | {_ongoingTag} | {GenericParam(_list)} | iter={_iterations}{guard} " +
                $"(frame {Time.frameCount})");
        }

        private void NoOp()
        {
        }

        // Method with a GENERIC PARAMETER - the other half of the b47b42e fix.
        private string GenericParam(List<int> values)
        {
            return values == null ? "param REGRESS-OK(null)" : "param REGRESS-OK(" + values.Count + ")";
        }

        // Must stay REFUSED with a warning. Never crash.
        private async Task RunAsync()
        {
            await Task.Yield();
            _asyncTag = "async REGRESS-OK";
        }

        private IEnumerator OngoingCoroutine()
        {
            while (ShouldContinue())
            {
                _ongoingTag = "ongoing REGRESS-OK";
                yield return new WaitForSeconds(0.25f);
            }

            _ongoingExited = true;
        }

        private IEnumerator FreshCoroutine()
        {
            Debug.Log($"[Reg] fresh REGRESS-OK (frame {Time.frameCount})");
            yield break;
        }
    }
}
