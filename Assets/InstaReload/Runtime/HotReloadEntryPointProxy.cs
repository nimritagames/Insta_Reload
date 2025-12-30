using UnityEngine;
using EntryPointKind = Nimrita.InstaReload.HotReloadEntryPointManager.EntryPointKind;

namespace Nimrita.InstaReload
{
    internal sealed class HotReloadEntryPointProxy : MonoBehaviour
    {
        internal Component Target { get; private set; }

        private HotReloadEntryPointManager.EntryPointRegistration _registration;
        private bool _hasUpdate;
        private int _updateMethodId;
        private bool _hasFixedUpdate;
        private int _fixedUpdateMethodId;
        private bool _hasLateUpdate;
        private int _lateUpdateMethodId;
        private object[] _singleArgBuffer;
        private object[] _doubleArgBuffer;

        internal void Configure(Component target, HotReloadEntryPointManager.EntryPointRegistration registration)
        {
            Target = target;
            _registration = registration;
            _hasUpdate = registration != null && registration.TryGetMethodId(EntryPointKind.Update, out _updateMethodId);
            _hasFixedUpdate = registration != null && registration.TryGetMethodId(EntryPointKind.FixedUpdate, out _fixedUpdateMethodId);
            _hasLateUpdate = registration != null && registration.TryGetMethodId(EntryPointKind.LateUpdate, out _lateUpdateMethodId);
        }

        private void Update()
        {
            TryInvoke(EntryPointKind.Update);
        }

        private void FixedUpdate()
        {
            TryInvoke(EntryPointKind.FixedUpdate);
        }

        private void LateUpdate()
        {
            TryInvoke(EntryPointKind.LateUpdate);
        }

        private void OnGUI()
        {
            TryInvoke(EntryPointKind.OnGUI);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            TryInvoke(EntryPointKind.OnApplicationFocus, hasFocus);
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            TryInvoke(EntryPointKind.OnApplicationPause, pauseStatus);
        }

        private void OnApplicationQuit()
        {
            TryInvoke(EntryPointKind.OnApplicationQuit);
        }

        private void OnBecameVisible()
        {
            TryInvoke(EntryPointKind.OnBecameVisible);
        }

        private void OnBecameInvisible()
        {
            TryInvoke(EntryPointKind.OnBecameInvisible);
        }

        private void OnPreCull()
        {
            TryInvoke(EntryPointKind.OnPreCull);
        }

        private void OnPreRender()
        {
            TryInvoke(EntryPointKind.OnPreRender);
        }

        private void OnPostRender()
        {
            TryInvoke(EntryPointKind.OnPostRender);
        }

        private void OnRenderObject()
        {
            TryInvoke(EntryPointKind.OnRenderObject);
        }

        private void OnWillRenderObject()
        {
            TryInvoke(EntryPointKind.OnWillRenderObject);
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            TryInvoke(EntryPointKind.OnRenderImage, source, destination);
        }

        private void OnDrawGizmos()
        {
            TryInvoke(EntryPointKind.OnDrawGizmos);
        }

        private void OnDrawGizmosSelected()
        {
            TryInvoke(EntryPointKind.OnDrawGizmosSelected);
        }

        private void Reset()
        {
            TryInvoke(EntryPointKind.Reset);
        }

        private void OnValidate()
        {
            TryInvoke(EntryPointKind.OnValidate);
        }

        private void OnAnimatorMove()
        {
            TryInvoke(EntryPointKind.OnAnimatorMove);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            TryInvoke(EntryPointKind.OnAnimatorIK, layerIndex);
        }

        private void OnTransformChildrenChanged()
        {
            TryInvoke(EntryPointKind.OnTransformChildrenChanged);
        }

        private void OnTransformParentChanged()
        {
            TryInvoke(EntryPointKind.OnTransformParentChanged);
        }

        private void OnRectTransformDimensionsChange()
        {
            TryInvoke(EntryPointKind.OnRectTransformDimensionsChange);
        }

        private void OnCanvasGroupChanged()
        {
            TryInvoke(EntryPointKind.OnCanvasGroupChanged);
        }

        private void OnCanvasHierarchyChanged()
        {
            TryInvoke(EntryPointKind.OnCanvasHierarchyChanged);
        }

        private void OnDidApplyAnimationProperties()
        {
            TryInvoke(EntryPointKind.OnDidApplyAnimationProperties);
        }

        private void OnCollisionEnter(Collision collision)
        {
            TryInvoke(EntryPointKind.OnCollisionEnter, collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            TryInvoke(EntryPointKind.OnCollisionExit, collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            TryInvoke(EntryPointKind.OnCollisionStay, collision);
        }

        private void OnCollisionEnter2D(Collision2D collision)
        {
            TryInvoke(EntryPointKind.OnCollisionEnter2D, collision);
        }

        private void OnCollisionExit2D(Collision2D collision)
        {
            TryInvoke(EntryPointKind.OnCollisionExit2D, collision);
        }

        private void OnCollisionStay2D(Collision2D collision)
        {
            TryInvoke(EntryPointKind.OnCollisionStay2D, collision);
        }

        private void OnTriggerEnter(Collider other)
        {
            TryInvoke(EntryPointKind.OnTriggerEnter, other);
        }

        private void OnTriggerExit(Collider other)
        {
            TryInvoke(EntryPointKind.OnTriggerExit, other);
        }

        private void OnTriggerStay(Collider other)
        {
            TryInvoke(EntryPointKind.OnTriggerStay, other);
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            TryInvoke(EntryPointKind.OnTriggerEnter2D, other);
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            TryInvoke(EntryPointKind.OnTriggerExit2D, other);
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            TryInvoke(EntryPointKind.OnTriggerStay2D, other);
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            TryInvoke(EntryPointKind.OnControllerColliderHit, hit);
        }

        private void OnJointBreak(float breakForce)
        {
            TryInvoke(EntryPointKind.OnJointBreak, breakForce);
        }

        private void OnJointBreak2D(Joint2D joint)
        {
            TryInvoke(EntryPointKind.OnJointBreak2D, joint);
        }

        private void OnParticleCollision(GameObject other)
        {
            TryInvoke(EntryPointKind.OnParticleCollision, other);
        }

        private void OnParticleTrigger()
        {
            TryInvoke(EntryPointKind.OnParticleTrigger);
        }

        private void OnParticleSystemStopped()
        {
            TryInvoke(EntryPointKind.OnParticleSystemStopped);
        }

        private void OnParticleSystemPaused()
        {
            TryInvoke(EntryPointKind.OnParticleSystemPaused);
        }

        private void OnParticleSystemResumed()
        {
            TryInvoke(EntryPointKind.OnParticleSystemResumed);
        }

        private void OnParticleSystemPlaybackStateChanged()
        {
            TryInvoke(EntryPointKind.OnParticleSystemPlaybackStateChanged);
        }

        private void OnMouseDown()
        {
            TryInvoke(EntryPointKind.OnMouseDown);
        }

        private void OnMouseUp()
        {
            TryInvoke(EntryPointKind.OnMouseUp);
        }

        private void OnMouseEnter()
        {
            TryInvoke(EntryPointKind.OnMouseEnter);
        }

        private void OnMouseExit()
        {
            TryInvoke(EntryPointKind.OnMouseExit);
        }

        private void OnMouseOver()
        {
            TryInvoke(EntryPointKind.OnMouseOver);
        }

        private void OnMouseDrag()
        {
            TryInvoke(EntryPointKind.OnMouseDrag);
        }

        private void OnMouseUpAsButton()
        {
            TryInvoke(EntryPointKind.OnMouseUpAsButton);
        }

        private void OnBeforeRender()
        {
            TryInvoke(EntryPointKind.OnBeforeRender);
        }

        private void OnDestroy()
        {
            Target = null;
            _registration = null;
        }

        private void TryInvoke(EntryPointKind kind)
        {
            if (!TryGetMethodId(kind, out var methodId))
            {
                return;
            }

            HotReloadDispatcher.Invoke(Target, methodId, null);
        }

        private void TryInvoke(EntryPointKind kind, object arg0)
        {
            if (!TryGetMethodId(kind, out var methodId))
            {
                return;
            }

            if (_singleArgBuffer == null)
            {
                _singleArgBuffer = new object[1];
            }

            _singleArgBuffer[0] = arg0;
            HotReloadDispatcher.Invoke(Target, methodId, _singleArgBuffer);
            _singleArgBuffer[0] = null;
        }

        private void TryInvoke(EntryPointKind kind, object arg0, object arg1)
        {
            if (!TryGetMethodId(kind, out var methodId))
            {
                return;
            }

            if (_doubleArgBuffer == null)
            {
                _doubleArgBuffer = new object[2];
            }

            _doubleArgBuffer[0] = arg0;
            _doubleArgBuffer[1] = arg1;
            HotReloadDispatcher.Invoke(Target, methodId, _doubleArgBuffer);
            _doubleArgBuffer[0] = null;
            _doubleArgBuffer[1] = null;
        }

        private bool TryGetMethodId(EntryPointKind kind, out int methodId)
        {
            if (Target == null || _registration == null)
            {
                methodId = 0;
                return false;
            }

            switch (kind)
            {
                case EntryPointKind.Update:
                    if (_hasUpdate)
                    {
                        methodId = _updateMethodId;
                        return true;
                    }
                    break;
                case EntryPointKind.FixedUpdate:
                    if (_hasFixedUpdate)
                    {
                        methodId = _fixedUpdateMethodId;
                        return true;
                    }
                    break;
                case EntryPointKind.LateUpdate:
                    if (_hasLateUpdate)
                    {
                        methodId = _lateUpdateMethodId;
                        return true;
                    }
                    break;
            }

            return _registration.TryGetMethodId(kind, out methodId);
        }
    }
}
