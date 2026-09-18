#nullable enable
#if UNITY_2018_1_OR_NEWER && !PLAYLOOP_DOTNET_STANDALONE
using UnityEngine;

namespace Playloop.Trace
{
    /// <summary>
    /// Drop-in component that feeds a Transform's position (and optionally
    /// its heading) to <c>client.Trace</c> every frame. Add it to the player,
    /// set the room id, pick the plane, and call <see cref="Attach"/> with the
    /// client you built. Action bits and axes stay the game's call: only the
    /// game knows its verbs, so wire <see cref="TraceApi.SetInput"/> yourself.
    ///
    /// <para>
    /// This is the one Inspector-facing component in the package; the rest of
    /// the SDK's MonoBehaviours are hidden helpers. It never reaches for
    /// <c>PlayloopClient.Current</c>: hand it the client explicitly.
    /// </para>
    /// </summary>
    [AddComponentMenu("Playloop/Trace")]
    [DisallowMultipleComponent]
    public sealed class PlayloopTrace : MonoBehaviour
    {
        [Tooltip("The Transform to sample. Defaults to this GameObject's Transform.")]
        public Transform? target;

        [Tooltip("Room or level id for this position: lowercase slug, letters, digits, _ : - (for example z3:pylon_corridors). Change it from code with SetRoom when the player moves rooms.")]
        public string roomId = "";

        [Tooltip("Which two world axes the Trace carries. XY for side-on and 2D, XZ for top-down and 3D. The client's TraceOptions.Plane is the one that counts: Attach adopts it and warns if this differed.")]
        public TracePlane plane = TracePlane.XY;

        [Tooltip("Derive facing from the direction of travel. Off: facing comes from the Transform's rotation (right on XY, forward on XZ).")]
        public bool facingFromVelocity = true;

        private PlayloopClient? _client;
        private bool _hasLast;
        private float _lastX;
        private float _lastY;
        private float _facing = -1f;
        private string _lastRoomId = "";
        private bool _warnedPlane;
        private bool _warnedRoom;

        /// <summary>
        /// Hand the component the client whose Trace it feeds. The client's
        /// <c>TraceOptions.Plane</c> is the plane the chunks declare, so it is
        /// the one this component projects on; the Inspector value is adopted
        /// from it here, with a warning if it disagreed.
        /// </summary>
        public void Attach(PlayloopClient client)
        {
            _client = client;
            _lastRoomId = "";
            if (client != null && client.Trace.Plane != plane)
            {
                if (!_warnedPlane)
                {
                    _warnedPlane = true;
                    Debug.LogWarning(
                        $"[Playloop] PlayloopTrace on \"{name}\" was set to plane {plane} but TraceOptions.Plane is {client.Trace.Plane}. " +
                        "The Trace declares the options plane, so this component now samples on it.");
                }
                plane = client.Trace.Plane;
                // _lastX/_lastY were projected on the OLD plane. Carrying them across the
                // swap makes the next Update subtract two different coordinate systems and
                // emit one bogus facing. Drop the history and let the next sample re-seed.
                // CodeRabbit on PR #5, minor.
                _hasLast = false;
            }
        }

        /// <summary>Change the room from code. Same validation as <see cref="TraceApi.SetRoom"/>.</summary>
        public void SetRoom(string id, TraceBounds? bounds = null)
        {
            roomId = id;
            var client = _client;
            if (client == null) return;
            client.Trace.SetRoom(id, bounds);
            _lastRoomId = id;
        }

        private void Update()
        {
            var client = _client;
            if (client == null) return;
            var t = target != null ? target : transform;
            var p = t.position;

            // Always the plane the chunks declare, never a field that can
            // drift from it after Attach.
            var tracePlane = client.Trace.Plane;
            float x = p.x;
            float y = tracePlane == TracePlane.XZ ? p.z : p.y;

            if (facingFromVelocity)
            {
                if (_hasLast)
                {
                    float dx = x - _lastX;
                    float dy = y - _lastY;
                    if (dx * dx + dy * dy > 1e-8f)
                    {
                        _facing = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    }
                }
                _lastX = x;
                _lastY = y;
                _hasLast = true;
            }
            else
            {
                var dir = tracePlane == TracePlane.XZ ? t.forward : t.right;
                float fy = tracePlane == TracePlane.XZ ? dir.z : dir.y;
                _facing = Mathf.Atan2(fy, dir.x) * Mathf.Rad2Deg;
            }

            if (!string.Equals(roomId, _lastRoomId, System.StringComparison.Ordinal))
            {
                if (TraceLabels.IsRoomId(roomId))
                {
                    client.Trace.SetRoom(roomId);
                    _lastRoomId = roomId;
                }
                else if (!_warnedRoom)
                {
                    _warnedRoom = true;
                    Debug.LogWarning(
                        $"[Playloop] PlayloopTrace on \"{name}\": room id \"{roomId}\" is not a slug (lowercase letter first, then letters, digits, _ : -). The room stays unset until it is.");
                }
            }

            client.Trace.SetPosition(x, y, _facing);
        }
    }
}
#endif
