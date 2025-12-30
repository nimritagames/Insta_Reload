# InstaReload Unity Message Support (Fallback + Caveats)

This document describes exactly how Unity message methods are supported in the
current WAY-1 pipeline, including the fallback proxy path for MonoBehaviours that
do not inherit from HotReloadBehaviour, plus the important caveats.

## Mental model

Unity builds its message-method call table once at domain load. If a message
method (like Update) does not exist before Play Mode, Unity never calls it.
Hot reload can change method bodies, but it cannot make Unity rescan types.

To handle mid-play additions, InstaReload uses two paths:

1) Trampoline path (preferred)
   - If the runtime method exists, the patcher detours it to a trampoline that
     calls the dispatcher.

2) Fallback proxy path (for MonoBehaviour only)
   - If the runtime method does not exist, the patcher registers the message
     with the fallback manager, which attaches a proxy component at runtime and
     forwards Unity callbacks to the dispatcher.

## How the fallback proxy path works

1) You add a Unity message method mid-play (for example, OnTriggerEnter).
2) The patcher sees a method with a Unity message signature that is missing at
   runtime and computes its method id.
3) The patcher registers it with HotReloadEntryPointManager using EntryPointKind.
4) The manager ensures a scanner exists and periodically scans for live instances
   of the declaring type.
5) For each instance, a HotReloadEntryPointProxy is attached (one per instance).
6) Unity invokes the proxy message method.
7) The proxy calls HotReloadDispatcher.Invoke(instance, methodId, args).

Important: This does not change Unity's internal call table. It only creates an
additional MonoBehaviour that Unity already knows how to call.

## Supported Unity message signatures (fallback map)

Message methods must be instance, non-static, non-generic, and return void.

Lifecycle and app:
```
void Update()
void FixedUpdate()
void LateUpdate()
void OnGUI()
void OnApplicationFocus(bool hasFocus)
void OnApplicationPause(bool pauseStatus)
void OnApplicationQuit()
```

Visibility and rendering:
```
void OnBecameVisible()
void OnBecameInvisible()
void OnPreCull()
void OnPreRender()
void OnPostRender()
void OnRenderObject()
void OnWillRenderObject()
void OnRenderImage(UnityEngine.RenderTexture source, UnityEngine.RenderTexture destination)
void OnDrawGizmos()
void OnDrawGizmosSelected()
void OnBeforeRender()
```

Editor and validation (fallback only runs in Play Mode):
```
void Reset()
void OnValidate()
```

Animation and transform:
```
void OnAnimatorMove()
void OnAnimatorIK(int layerIndex)
void OnTransformChildrenChanged()
void OnTransformParentChanged()
void OnDidApplyAnimationProperties()
```

UI and canvas:
```
void OnRectTransformDimensionsChange()
void OnCanvasGroupChanged()
void OnCanvasHierarchyChanged()
```

Physics and collisions (3D + 2D):
```
void OnCollisionEnter(UnityEngine.Collision collision)
void OnCollisionExit(UnityEngine.Collision collision)
void OnCollisionStay(UnityEngine.Collision collision)
void OnCollisionEnter2D(UnityEngine.Collision2D collision)
void OnCollisionExit2D(UnityEngine.Collision2D collision)
void OnCollisionStay2D(UnityEngine.Collision2D collision)
void OnTriggerEnter(UnityEngine.Collider other)
void OnTriggerExit(UnityEngine.Collider other)
void OnTriggerStay(UnityEngine.Collider other)
void OnTriggerEnter2D(UnityEngine.Collider2D other)
void OnTriggerExit2D(UnityEngine.Collider2D other)
void OnTriggerStay2D(UnityEngine.Collider2D other)
void OnControllerColliderHit(UnityEngine.ControllerColliderHit hit)
void OnJointBreak(float breakForce)
void OnJointBreak2D(UnityEngine.Joint2D joint)
```

Particles:
```
void OnParticleCollision(UnityEngine.GameObject other)
void OnParticleTrigger()
void OnParticleSystemStopped()
void OnParticleSystemPaused()
void OnParticleSystemResumed()
void OnParticleSystemPlaybackStateChanged()
```

Mouse input (legacy input + collider required):
```
void OnMouseDown()
void OnMouseUp()
void OnMouseEnter()
void OnMouseExit()
void OnMouseOver()
void OnMouseDrag()
void OnMouseUpAsButton()
```

## Caveats and limitations

Unity discovery rules:
- Unity only discovers message methods at domain load.
- If a message method did not exist before Play Mode, Unity will not call it
  directly. Only the fallback proxy can trigger it.

Initialization/destruction methods:
- The fallback proxy intentionally DOES NOT dispatch `Awake`, `Start`,
  `OnEnable`, `OnDisable`, or `OnDestroy`. These have one-time or stateful
  semantics and are too risky to replay automatically.
- To change these safely, predeclare them before Play Mode (manual stub or
  HotReloadBehaviour) or restart Play Mode.

Signature strictness:
- Only the exact signatures listed above are supported by the fallback map.
- Overloads with different parameters are ignored.
- Byref, pointer, and generic signatures are not proxied.

Audio thread callbacks:
- OnAudioFilterRead is intentionally NOT proxied. It runs on the audio thread,
  and invoking the dispatcher (which touches managed state and Unity APIs) is
  unsafe. Restart or predeclare if you need it.

Editor-only callbacks:
- Reset and OnValidate are editor events. The fallback manager only runs in Play
  Mode, so those methods are not proxied in edit mode.

Script execution order:
- The proxy is a separate MonoBehaviour. Unity may call it in a different order
  than the original component, and Script Execution Order settings do not
  automatically apply to the proxy. If order matters, use HotReloadBehaviour
  (predeclared entry points) or restart.

Scene and activation:
- The scanner uses FindObjectsOfType and only sees loaded, active instances.
  Inactive objects receive proxies after they become active and the next scan
  runs (scan interval is 0.5s).

Performance:
- The scan uses FindObjectsOfType and can be expensive in large scenes.
  Registration happens only when a message is added mid-play, but it can still
  be noticeable in huge scenes. Consider opt-out or throttling if needed.

Structural changes:
- Method removal or signature changes that remove existing methods are structural
  changes and still require leaving Play Mode. Adding new methods is supported.

Build behavior:
- The proxy lives in the Runtime assembly. If InstaReload is included in a build,
  the proxy can be attached at runtime. If you want to exclude it from builds,
  add your own build-time guards or compile defines.

## Practical guidance

- For guaranteed, order-correct behavior, use HotReloadBehaviour (predeclared
  entry points) or manually predeclare message methods before Play Mode.
- For user scripts that stay on MonoBehaviour, the fallback proxy makes most
  Unity message additions work mid-play, but it cannot change Unity’s internal
  discovery rules.
- If a message is not listed in the supported map, you must restart or use
  HotReloadBehaviour/IL weaving to predeclare it.
