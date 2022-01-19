﻿/* If nested then the nested NB should be sent every tick.
 * This is because if that tick happens to drop then the
 * sent data is now wrong given the parent information is wrong.
 * Once EC is added in we won't have to send every time since
 * it will eventually correct itself. */


using FishNet.Connection;
using FishNet.Object;
using FishNet.Serializing;
using FishNet.Transporting;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FishNet.Component.Transforming
{
    /// <summary> 
    /// A somewhat basic but reliable NetworkTransform that will be improved upon greatly after release.
    /// </summary>   
    public class NetworkTransform : NetworkBehaviour
    {
        #region Types.
        [System.Serializable]
        public struct SnappedAxes
        {
            public bool X;
            public bool Y;
            public bool Z;
        }
        private enum ChangedDelta
        {
            Unset = 0,
            PositionX = 1,
            PositionY = 2,
            PositionZ = 4,
            Rotation = 8,
            Extended = 16,
            ScaleX = 32,
            ScaleY = 64,
            ScaleZ = 128,
            Nested = 256
        }
        private enum ChangedFull
        {
            Unset = 0,
            Position = 1,
            Rotation = 2,
            Scale = 4,
            Nested = 8
        }
        private enum SpecialFlag : byte
        {

        }

        private enum UpdateFlagA : byte
        {
            Unset = 0,
            X2 = 1,
            X4 = 2,
            Y2 = 4,
            Y4 = 8,
            Z2 = 16,
            Z4 = 32,
            Rotation = 64,
            Extended = 128
        }
        private enum UpdateFlagB : byte
        {
            Unset = 0,
            X2 = 1,
            X4 = 2,
            Y2 = 4,
            Y4 = 8,
            Z2 = 16,
            Z4 = 32,
            Nested = 64
        }
        private struct GoalData
        {
            public uint ReceivedTick;
            public bool IsDefault { get; private set; }
            public RateData Rates;
            public TransformData Transforms;

            public void SetIsDefault(bool value)
            {
                IsDefault = value;
            }
        }
        private GoalData _currentGoalData = new GoalData();
        private bool _queueReady = false;

        private struct RateData
        {
            public float Position;
            public float Rotation;
            public float Scale;
            public float LastUnalteredPositionRate;
            public bool AbnormalRateDetected;
            public float TimeRemaining;


            /// <summary>
            /// Updates rates.
            /// </summary>
            public void Update(float position, float rotation, float scale, float unalteredPositionRate, bool abnormalRateDetected, float timeRemaining)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
                LastUnalteredPositionRate = unalteredPositionRate;
                AbnormalRateDetected = abnormalRateDetected;
                TimeRemaining = timeRemaining;
            }
        }

        private struct TransformData
        {
            public enum ExtrapolateState
            {
                Disabled,
                Available,
                Active
            }
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Vector3 ExtrapolatedPosition;
            public ExtrapolateState ExtrapolationState;
            public NetworkBehaviour ParentBehaviour;

            public void Update(ref TransformData copy)
            {
                Update(copy.Tick, copy.Position, copy.Rotation, copy.Scale, copy.ExtrapolatedPosition, copy.ParentBehaviour);
            }
            public void Update(uint tick, Vector3 position, Quaternion rotation, Vector3 scale, Vector3 extrapolatedPosition, NetworkBehaviour parentBehaviour)
            {
                Tick = tick;
                Position = position;
                Rotation = rotation;
                Scale = scale;
                ExtrapolatedPosition = extrapolatedPosition;
                ParentBehaviour = parentBehaviour;
            }
        }

        #endregion

        #region Serialized.
        /// <summary>
        /// True to compress values. If you find accuracy of transform properties to be less than desirable try disabling this option.
        /// </summary>
        [Tooltip("True to compress values. If you find accuracy of transform properties to be less than desirable try disabling this option.")]
        [SerializeField]
        private bool _compress = true;
        /// <summary>
        /// True to synchronize when this transform changes parent.
        /// </summary>
        [Tooltip("True to synchronize when this transform changes parent.")]
        [SerializeField]
        private bool _synchronizeParent;
        /// <summary>
        /// How many ticks to interpolate.
        /// </summary>
        [Tooltip("How many ticks to interpolate.")]
        [Range(1, 256)]
        [SerializeField]
        private ushort _interpolation = 2;
        /// <summary>
        /// How many ticks to extrapolate.
        /// </summary>
        [Tooltip("How many ticks to extrapolate.")]
        [Range(0, 1024)]
        [SerializeField]
        private ushort _extrapolation;
        /// <summary>
        /// True to enable teleport threshhold.
        /// </summary>
        [Tooltip("True to enable teleport threshhold.")]
        [SerializeField]
        private bool _enableTeleport;
        /// <summary>
        /// How far the transform must travel in a single update to cause a teleport rather than smoothing. Using 0f will teleport every update.
        /// </summary>
        [Tooltip("How far the transform must travel in a single update to cause a teleport rather than smoothing. Using 0f will teleport every update.")]
        [Range(0f, float.MaxValue)]
        [SerializeField]
        private float _teleportThreshold = 1f;
        /// <summary>
        /// True if owner controls how the object is synchronized.
        /// </summary>
        [Tooltip("True if owner controls how the object is synchronized.")]
        [SerializeField]
        private bool _clientAuthoritative = true;
        /// <summary>
        /// True to synchronize movements on server to owner when not using client authoritative movement.
        /// </summary>
        [Tooltip("True to synchronize movements on server to owner when not using client authoritative movement.")]
        [SerializeField]
        private bool _sendToOwner = true;
        /// <summary>
        /// Axes to snap on position.
        /// </summary>
        [Tooltip("Axes to snap on position.")]
        [SerializeField]
        private SnappedAxes _positionSnapping = new SnappedAxes();
        /// <summary>
        /// Axes to snap on rotation.
        /// </summary>
        [Tooltip("Axes to snap on rotation.")]
        [SerializeField]
        private SnappedAxes _rotationSnapping = new SnappedAxes();
        /// <summary>
        /// Axes to snap on scale.
        /// </summary>
        [Tooltip("Axes to snap on scale.")]
        [SerializeField]
        private SnappedAxes _scaleSnapping = new SnappedAxes();
        #endregion

        #region Private.
        /// <summary>
        /// NetworkBehaviour this transform is a child of.
        /// </summary>
        private NetworkBehaviour _parentBehaviour;
        /// <summary>
        /// Last transform which this object was a child of.
        /// </summary>
        private Transform _parentTransform;
        /// <summary>
        /// Values changed over time that server has sent to clients since last reliable has been sent.
        /// </summary>
        private ChangedDelta _serverChangedSinceReliable = ChangedDelta.Unset;
        /// <summary>
        /// Values changed over time that client has sent to server since last reliable has been sent.
        /// </summary>
        private ChangedDelta _clientChangedSinceReliable = ChangedDelta.Unset;
        /// <summary>
        /// Last tick an ObserverRpc passed checks.
        /// </summary>
        private uint _lastObserversRpcTick;
        /// <summary>
        /// Last tick a ServerRpc passed checks.
        /// </summary>
        private uint _lastServerRpcTick;
        /// <summary>
        /// Last received data from an authoritative client.
        /// </summary>
        private PooledWriter _receivedClientBytes;
        /// <summary>
        /// True when receivedClientBytes contains new data.
        /// </summary>
        private bool _clientBytesChanged;
        /// <summary>
        /// True if subscribed to TimeManager for ticks.
        /// </summary>
        private bool _subscribedToTicks;
        /// <summary>
        /// Last TransformData to be sent.
        /// </summary>
        private TransformData _lastSentTransformData;
        /// <summary>
        /// Last TransformData to be received.
        /// </summary>
        private TransformData _lastReceivedTransformData;
        #endregion

        private Queue<GoalData> _goalDataQueue = new Queue<GoalData>();

        private void OnDisable()
        {
            if (_receivedClientBytes != null)
            {
                _receivedClientBytes.Dispose();
                _receivedClientBytes = null;
            }
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            SetDefaultGoalData();

            /* Server must always subscribe.
             * Server needs to relay client auth in
             * ticks or send non-auth/non-owner to
             * clients in tick. */
            ChangeTickSubscription(true);
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            SetDefaultGoalData();
        }

        public override void OnOwnershipServer(NetworkConnection prevOwner)
        {
            base.OnOwnershipServer(prevOwner);
            //Reset last tick since each client sends their own ticks.
            _lastServerRpcTick = 0;
        }

        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            /* If newOwner is self then client
             * must subscribe to ticks. Client can also
             * unsubscribe from ticks if not owner,
             * long as the server is also not active. */
            if (base.IsOwner)
            {
                ChangeTickSubscription(true);
            }
            //Not new owner.
            else
            {
                /* If client authoritative and ownership was lost
                 * then default goals must be set to force the
                 * object to it's last transform. */
                if (_clientAuthoritative)
                    SetDefaultGoalData();

                if (!base.IsServer)
                    ChangeTickSubscription(false);
            }
        }

        public override void OnStopServer()
        {
            base.OnStopServer();
            //Always unsubscribe; if the server stopped so did client.
            ChangeTickSubscription(false);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            //If not also server unsubscribe from ticks.
            if (!base.IsServer)
                ChangeTickSubscription(false);
        }

        private void Update()
        {
            MoveToTarget();
        }


        /// <summary>
        /// Called when a tick occurs.
        /// </summary>
        private void TimeManager_OnTick()
        {
            UpdateParentBehaviour();

            if (base.IsServer)
                SendToClients();
            if (base.IsClient)
                SendToServer();
        }

        /* 
         * //todo make a special method for handling network transforms that iterates all
         * of them at once and ALWAYS send the packetId TransformUpdate. This packet will
         * have the total length of all updates. theres a chance a nob might not exist since
         * these packets are unreliable and can arrive after a nob destruction. if thats
         * the case then the packet can still be parsed out and recovered because the updateflags
         * indicates exactly what data needs to be read.
         */


        /// <summary>
        /// Tries to subscribe to TimeManager ticks.
        /// </summary>
        private void ChangeTickSubscription(bool subscribe)
        {
            if (subscribe == _subscribedToTicks)
                return;

            _subscribedToTicks = subscribe;
            if (subscribe)
                base.NetworkManager.TimeManager.OnTick += TimeManager_OnTick;
            else
                base.NetworkManager.TimeManager.OnTick -= TimeManager_OnTick;
        }


        /// <summary>
        /// Creates goal data using current position.
        /// </summary>
        private void SetDefaultGoalData()
        {
            Transform t = transform;
            NetworkBehaviour parentBehaviour = null;
            //If there is a parent try to output the behaviour on it.
            if (transform.parent != null)
            {
                transform.parent.TryGetComponent<NetworkBehaviour>(out parentBehaviour);
                if (parentBehaviour == null)
                    LogInvalidParent();
            }
            _lastReceivedTransformData.Update(0, t.localPosition, t.localRotation, t.localScale, t.localPosition, parentBehaviour);
            SetInstantRates(ref _currentGoalData.Rates);
            _currentGoalData.SetIsDefault(true);
        }

        /// <summary>
        /// Prints an invalid parent debug.
        /// </summary>
        private void LogInvalidParent()
        {
            Debug.LogWarning($"{gameObject.name} [Id {base.ObjectId}] is nested but the parent {transform.parent.name} does not contain a NetworkBehaviour component. To synchronize parents the parent object must have a NetworkBehaviour component, even if empty.");
        }

        /// <summary>
        /// Serializes only changed data into writer.
        /// </summary>
        /// <param name="changed"></param>
        /// <param name="writer"></param>
        private void SerializeChanged(ChangedDelta changed, PooledWriter writer)
        {
            UpdateFlagA flagsA = UpdateFlagA.Unset;
            UpdateFlagB flagsB = UpdateFlagB.Unset;
            /* Do not use compression when nested. Depending
             * on the scale of the parent compression may
             * not be accurate enough. */
            bool compress = (ChangedContains(changed, ChangedDelta.Nested)) ? false : _compress;

            int startIndexA = writer.Position;
            writer.Reserve(1);
            //Original axis value.
            float original;
            //Compressed axis value.
            float compressed;
            //Multiplier for compression.
            float multiplier = 100f;
            /* Maximum value compressed may be 
             * to send as compressed. */
            float maxValue = (short.MaxValue - 1);

            Transform t = transform;
            //PositionX
            if (ChangedContains(changed, ChangedDelta.PositionX))
            {
                original = t.localPosition.x;
                compressed = original * multiplier;
                if (compress && Math.Abs(compressed) <= maxValue)
                {
                    flagsA |= UpdateFlagA.X2;
                    writer.WriteInt16((short)compressed);
                }
                else
                {
                    flagsA |= UpdateFlagA.X4;
                    writer.WriteSingle(original);
                }
            }
            //PositionY
            if (ChangedContains(changed, ChangedDelta.PositionY))
            {
                original = t.localPosition.y;
                compressed = original * multiplier;
                if (compress && Math.Abs(compressed) <= maxValue)
                {
                    flagsA |= UpdateFlagA.Y2;
                    writer.WriteInt16((short)compressed);
                }
                else
                {
                    flagsA |= UpdateFlagA.Y4;
                    writer.WriteSingle(original);
                }
            }
            //PositionZ
            if (ChangedContains(changed, ChangedDelta.PositionZ))
            {
                original = t.localPosition.z;
                compressed = original * multiplier;
                if (compress && Math.Abs(compressed) <= maxValue)
                {
                    flagsA |= UpdateFlagA.Z2;
                    writer.WriteInt16((short)compressed);
                }
                else
                {
                    flagsA |= UpdateFlagA.Z4;
                    writer.WriteSingle(original);
                }
            }

            //Rotation.
            if (ChangedContains(changed, ChangedDelta.Rotation))
            {
                flagsA |= UpdateFlagA.Rotation;
                writer.WriteQuaternion(t.localRotation);
            }

            if (ChangedContains(changed, ChangedDelta.Extended))
            {
                flagsA |= UpdateFlagA.Extended;
                int startIndexB = writer.Position;
                writer.Reserve(1);

                //ScaleX
                if (ChangedContains(changed, ChangedDelta.ScaleX))
                {
                    original = t.localScale.x;
                    compressed = original * multiplier;
                    if (compress && Math.Abs(compressed) <= maxValue)
                    {
                        flagsB |= UpdateFlagB.X2;
                        writer.WriteInt16((short)compressed);
                    }
                    else
                    {
                        flagsB |= UpdateFlagB.X4;
                        writer.WriteSingle(original);
                    }
                }
                //ScaleY
                if (ChangedContains(changed, ChangedDelta.ScaleY))
                {
                    original = t.localScale.y;
                    compressed = original * multiplier;
                    if (compress && Math.Abs(compressed) <= maxValue)
                    {
                        flagsB |= UpdateFlagB.Y2;
                        writer.WriteInt16((short)compressed);
                    }
                    else
                    {
                        flagsB |= UpdateFlagB.Y4;
                        writer.WriteSingle(original);
                    }
                }
                //ScaleZ
                if (ChangedContains(changed, ChangedDelta.ScaleZ))
                {
                    original = t.localScale.z;
                    compressed = original * multiplier;
                    if (compress && Math.Abs(compressed) <= maxValue)
                    {
                        flagsB |= UpdateFlagB.Z2;
                        writer.WriteInt16((short)compressed);
                    }
                    else
                    {
                        flagsB |= UpdateFlagB.Z4;
                        writer.WriteSingle(original);
                    }
                }

                //Nested.
                if (ChangedContains(changed, ChangedDelta.Nested) && _parentBehaviour != null)
                {
                    flagsB |= UpdateFlagB.Nested;
                    writer.WriteNetworkBehaviour(_parentBehaviour);
                }

                writer.FastInsertByte((byte)flagsB, startIndexB);
            }

            //Insert flags.
            writer.FastInsertByte((byte)flagsA, startIndexA);
            bool ChangedContains(ChangedDelta whole, ChangedDelta part)
            {
                return (whole & part) == part;
            }
        }

        /// <summary>
        /// Deerializes a received packet.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeserializePacket(ArraySegment<byte> data, ref TransformData prevTransformData, ref TransformData nextTransformData, ref ChangedFull changedFull)
        {
            using (PooledReader r = ReaderPool.GetReader(data, base.NetworkManager))
            {
                UpdateFlagA flagsA = (UpdateFlagA)r.ReadByte();

                int readerRemaining;
                readerRemaining = r.Remaining;
                //X
                if (UpdateFlagAContains(flagsA, UpdateFlagA.X2))
                    nextTransformData.Position.x = r.ReadInt16() / 100f;
                else if (UpdateFlagAContains(flagsA, UpdateFlagA.X4))
                    nextTransformData.Position.x = r.ReadSingle();
                else
                    nextTransformData.Position.x = prevTransformData.Position.x;
                //Y
                if (UpdateFlagAContains(flagsA, UpdateFlagA.Y2))
                    nextTransformData.Position.y = r.ReadInt16() / 100f;
                else if (UpdateFlagAContains(flagsA, UpdateFlagA.Y4))
                    nextTransformData.Position.y = r.ReadSingle();
                else
                    nextTransformData.Position.y = prevTransformData.Position.y;
                //Z
                if (UpdateFlagAContains(flagsA, UpdateFlagA.Z2))
                    nextTransformData.Position.z = r.ReadInt16() / 100f;
                else if (UpdateFlagAContains(flagsA, UpdateFlagA.Z4))
                    nextTransformData.Position.z = r.ReadSingle();
                else
                    nextTransformData.Position.z = prevTransformData.Position.z;
                //If remaining has changed then a position was read.
                if (readerRemaining != r.Remaining)
                    changedFull |= ChangedFull.Position;

                //Rotation.
                if (UpdateFlagAContains(flagsA, UpdateFlagA.Rotation))
                {
                    nextTransformData.Rotation = r.ReadQuaternion();
                    changedFull |= ChangedFull.Rotation;
                }
                else
                {
                    nextTransformData.Rotation = prevTransformData.Rotation;
                }

                //Extended settings.
                if (UpdateFlagAContains(flagsA, UpdateFlagA.Extended))
                {
                    UpdateFlagB flagsB = (UpdateFlagB)r.ReadByte();
                    readerRemaining = r.Remaining;

                    //X
                    if (UpdateFlagBContains(flagsB, UpdateFlagB.X2))
                        nextTransformData.Scale.x = r.ReadInt16() / 100f;
                    else if (UpdateFlagBContains(flagsB, UpdateFlagB.X4))
                        nextTransformData.Scale.x = r.ReadSingle();
                    else
                        nextTransformData.Scale.x = prevTransformData.Scale.x;
                    //Y
                    if (UpdateFlagBContains(flagsB, UpdateFlagB.Y2))
                        nextTransformData.Scale.y = r.ReadInt16() / 100f;
                    else if (UpdateFlagBContains(flagsB, UpdateFlagB.Y4))
                        nextTransformData.Scale.y = r.ReadSingle();
                    else
                        nextTransformData.Scale.y = prevTransformData.Scale.y;
                    //X
                    if (UpdateFlagBContains(flagsB, UpdateFlagB.Z2))
                        nextTransformData.Scale.z = r.ReadInt16() / 100f;
                    else if (UpdateFlagBContains(flagsB, UpdateFlagB.Z4))
                        nextTransformData.Scale.z = r.ReadSingle();
                    else
                        nextTransformData.Scale.z = prevTransformData.Scale.z;

                    if (r.Remaining != readerRemaining)
                        changedFull |= ChangedFull.Scale;
                    else
                        nextTransformData.Scale = prevTransformData.Scale;

                    if (UpdateFlagBContains(flagsB, UpdateFlagB.Nested))
                    {

                        nextTransformData.ParentBehaviour = r.ReadNetworkBehaviour();
                        changedFull |= ChangedFull.Nested;
                    }
                    else
                    {
                        nextTransformData.ParentBehaviour = prevTransformData.ParentBehaviour;
                    }
                }
                //No extended settings.
                else
                {
                    nextTransformData.Scale = prevTransformData.Scale;
                    nextTransformData.ParentBehaviour = prevTransformData.ParentBehaviour;
                }
            }


            //Returns if whole contains part.
            bool UpdateFlagAContains(UpdateFlagA whole, UpdateFlagA part)
            {
                return (whole & part) == part;
            }
            //Returns if whole contains part.
            bool UpdateFlagBContains(UpdateFlagB whole, UpdateFlagB part)
            {
                return (whole & part) == part;
            }
        }

        /// <summary>
        /// Updates the ParentBehaviour field when able to.
        /// </summary>
        private void UpdateParentBehaviour()
        {
            if (!_synchronizeParent)
                return;
            /* The field ParentBehaviour is only used by
             * the sending connection. When moving the ParentBehaviour
             * is deserialized into the TransformData, meaning the field
             * varient is never used on connections which do not control
             * the object. It's probably very similar in performance,
             * possibly even more efficient to just run the parent change
             * checks versus checking if those checks can be checked in the
             * first place. check. */

            Transform parent = transform.parent;
            //No parent.
            if (parent == null)
            {
                _parentBehaviour = null;
                _parentTransform = null;
            }
            //Has a parent, see if eligible.
            else
            {
                //No change.
                if (_parentTransform == parent)
                    return;

                _parentTransform = parent;
                parent.TryGetComponent<NetworkBehaviour>(out _parentBehaviour);
                if (_parentBehaviour == null)
                    LogInvalidParent();
            }
        }

        /// <summary>
        /// Moves to a GoalData. Automatically determins if to use data from server or client.
        /// </summary>
        private void MoveToTarget(float deltaOverride = -1f)
        {
            if (!_queueReady && _currentGoalData.Transforms.ExtrapolationState != TransformData.ExtrapolateState.Active)
                return;
            //Cannot move if neither is active.
            if (!base.IsServer && !base.IsClient)
                return;
            //If client auth and the owner don't move towards target.
            if (_clientAuthoritative && base.IsOwner)
                return;
            //If not client authoritative, is owner, and don't sync to owner.
            if (!_clientAuthoritative && base.IsOwner && !_sendToOwner)
                return;
            //True if not client controlled.
            bool controlledByClient = (_clientAuthoritative && base.Owner.IsActive);
            //If not controlled by client and is server then no reason to move.
            if (!controlledByClient && base.IsServer)
                return;

            float delta = (deltaOverride != -1f) ? deltaOverride : Time.deltaTime;
            /* Once here it's safe to assume the object will be moving.
             * Any checks which would stop it from moving be it client
             * auth and owner, or server controlled and server, ect,
             * would have already been run. */
            ref TransformData td = ref _currentGoalData.Transforms;
            ref RateData rd = ref _currentGoalData.Rates;

            //Set parent.
            if (td.ParentBehaviour != null)
                transform.SetParent(td.ParentBehaviour.transform);
            else
                transform.SetParent(null);

            float multiplier = 1f;
            int queueCount = _goalDataQueue.Count;
            //For every entry past interpolation increase move rate.
            if (queueCount > (_interpolation + 1))
                multiplier += (0.05f * queueCount);

            //Rate to update. Changes per property.
            float rate;
            Transform t = transform;
            //Position.
            rate = rd.Position;

            Vector3 posGoal = (td.ExtrapolationState == TransformData.ExtrapolateState.Active) ? td.ExtrapolatedPosition : td.Position;
            if (rate == -1f)
                t.localPosition = td.Position;
            else
                t.localPosition = Vector3.MoveTowards(t.localPosition, posGoal, rate * delta * multiplier);
            //Rotation.
            rate = rd.Rotation;
            if (rate == -1f)
                t.localRotation = td.Rotation;
            else
                t.localRotation = Quaternion.RotateTowards(t.localRotation, td.Rotation, rate * delta);
            //Scale.
            rate = rd.Scale;
            if (rate == -1f)
                t.localScale = td.Scale;
            else
                t.localScale = Vector3.MoveTowards(t.localScale, td.Scale, rate * delta);


            float timeRemaining = rd.TimeRemaining - (delta * multiplier);
            if (timeRemaining < -delta)
                timeRemaining = -delta;
            rd.TimeRemaining = timeRemaining;

            if (rd.TimeRemaining <= 0f)
            {
                float leftOver = Mathf.Abs(rd.TimeRemaining);
                //If more in buffer then run next buffer.
                if (queueCount > 0)
                {
                    _currentGoalData = _goalDataQueue.Dequeue();
                    if (leftOver > 0f)
                        MoveToTarget(leftOver);
                }
                //No more in buffer, see if can extrapolate.
                else
                {
                    _queueReady = false;
                    //Can extrapolate.
                    if (td.ExtrapolationState == TransformData.ExtrapolateState.Available)
                    {
                        rd.TimeRemaining = (_extrapolation * base.TimeManager.TickRate);
                        td.ExtrapolationState = TransformData.ExtrapolateState.Active;
                        if (leftOver > 0f)
                            MoveToTarget(leftOver);
                    }
                }
            }

        }

        /// <summary>
        /// Sends transform data to clients if needed.
        /// </summary>
        private void SendToClients()
        {
            //True if to send transform state rather than received state from client.
            bool sendServerState = (_receivedClientBytes == null || _receivedClientBytes.Length == 0 || !base.Owner.IsValid);
            //Channel to send rpc on.
            Channel channel = Channel.Unreliable;
            //If relaying from client.
            if (!sendServerState)
            {
                //No new data from clients.
                if (!_clientBytesChanged)
                    return;

                //Resend data from clients.
                ObserversUpdateTransform(_receivedClientBytes.GetArraySegment(), channel);
            }
            //Sending server transform state.
            else
            {
                ChangedDelta changed = GetChanged(ref _lastSentTransformData);

                //If no change.
                if (changed == ChangedDelta.Unset)
                {
                    //No changes since last reliable; transform is up to date.
                    if (_serverChangedSinceReliable == ChangedDelta.Unset)
                        return;

                    //Set changed to all changes over time and unset changes over time.
                    changed = _serverChangedSinceReliable;
                    _serverChangedSinceReliable = ChangedDelta.Unset;
                    channel = Channel.Reliable;
                }
                //There is change.
                else
                {
                    _serverChangedSinceReliable |= changed;
                }

                Transform t = transform;
                /* If here a send for transform values will occur. Update last values.
                 * Tick doesn't need to be set for whoever controls transform. */
                _lastSentTransformData.Update(0, t.localPosition, t.localRotation, t.localScale, t.localPosition, _parentBehaviour);

                //Send latest.
                using (PooledWriter writer = WriterPool.GetWriter())
                {
                    SerializeChanged(changed, writer);
                    ObserversUpdateTransform(writer.GetArraySegment(), channel);
                }
            }

        }

        /// <summary>
        /// Sends transform data to server if needed.
        /// </summary>
        private void SendToServer()
        {
            //Not client auth or not owner.
            if (!_clientAuthoritative || !base.IsOwner)
                return;

            //Channel to send on.
            Channel channel = Channel.Unreliable;
            //Values changed since last check.
            ChangedDelta changed = GetChanged(ref _lastSentTransformData);

            //If no change.
            if (changed == ChangedDelta.Unset)
            {
                //No changes since last reliable; transform is up to date.
                if (_clientChangedSinceReliable == ChangedDelta.Unset)
                    return;

                //Set changed to all changes over time and unset changes over time.
                changed = _clientChangedSinceReliable;
                _clientChangedSinceReliable = ChangedDelta.Unset;
                channel = Channel.Reliable;
            }
            //There is change.
            else
            {
                _clientChangedSinceReliable |= changed;
            }

            /* If here a send for transform values will occur. Update last values.
            * Tick doesn't need to be set for whoever controls transform. */
            Transform t = transform;
            _lastSentTransformData.Update(0, t.localPosition, t.localRotation, t.localScale, t.localPosition, _parentBehaviour);

            //Send latest.
            using (PooledWriter writer = WriterPool.GetWriter())
            {
                SerializeChanged(changed, writer);
                ServerUpdateTransform(writer.GetArraySegment(), channel);
            }
        }

        #region GetChanged.
        /// <summary>
        /// Returns if there is any change between two datas.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool HasChanged(ref TransformData a, ref TransformData b, ref ChangedFull changedFull)
        {
            bool hasChanged = false;

            if (a.Position != b.Position)
            {
                hasChanged = true;
                changedFull |= ChangedFull.Position;
            }
            if (a.Rotation != b.Rotation)
            {
                hasChanged = true;
                changedFull |= ChangedFull.Rotation;
            }
            if (a.Scale != b.Scale)
            {
                hasChanged = true;
                changedFull |= ChangedFull.Scale;
            }
            if (a.ParentBehaviour != b.ParentBehaviour)
            {
                hasChanged = true;
                changedFull |= ChangedFull.Nested;
            }

            return hasChanged;
        }
        /// <summary>
        /// Gets transform values that have changed against goalData.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ChangedDelta GetChanged(ref TransformData transformData)
        {
            return GetChanged(ref transformData.Position, ref transformData.Rotation, ref transformData.Scale, transformData.ParentBehaviour);
        }
        /// <summary>
        /// Gets transform values that have changed against specified proprties.
        /// </summary>
        private ChangedDelta GetChanged(ref Vector3 lastPosition, ref Quaternion lastRotation, ref Vector3 lastScale, NetworkBehaviour parentBehaviour)
        {
            ChangedDelta changed = ChangedDelta.Unset;
            Transform t = transform;

            Vector3 position = t.localPosition;
            if (position.x != lastPosition.x)
                changed |= ChangedDelta.PositionX;
            if (position.y != lastPosition.y)
                changed |= ChangedDelta.PositionY;
            if (position.z != lastPosition.z)
                changed |= ChangedDelta.PositionZ;

            Quaternion rotation = t.localRotation;
            if (rotation != lastRotation)
                changed |= ChangedDelta.Rotation;

            ChangedDelta startChanged;
            startChanged = changed;

            Vector3 scale = t.localScale;
            if (scale.x != lastScale.x)
                changed |= ChangedDelta.ScaleX;
            if (scale.y != lastScale.y)
                changed |= ChangedDelta.ScaleY;
            if (scale.z != lastScale.z)
                changed |= ChangedDelta.ScaleZ;

            if (_parentBehaviour != parentBehaviour)
                changed |= ChangedDelta.Nested;
            else if (_parentBehaviour != null && changed != ChangedDelta.Unset)
                changed |= ChangedDelta.Nested;

            //If added scale or nested then also add extended.
            if (startChanged != changed)
                changed |= ChangedDelta.Extended;

            return changed;
        }
        #endregion

        #region Rates.
        /// <summary>
        /// Snaps transform properties using snapping settings.
        /// </summary>
        private void SnapProperties(ref TransformData transformData)
        {
            Transform t = transform;
            //Position.
            Vector3 position;
            position.x = (_positionSnapping.X) ? transformData.Position.x : t.localPosition.x;
            position.y = (_positionSnapping.Y) ? transformData.Position.y : t.localPosition.y;
            position.z = (_positionSnapping.Z) ? transformData.Position.z : t.localPosition.z;
            t.localPosition = position;
            //Rotation.
            Vector3 eulers;
            Vector3 goalEulers = transformData.Rotation.eulerAngles;
            eulers.x = (_rotationSnapping.X) ? goalEulers.x : t.localEulerAngles.x;
            eulers.y = (_rotationSnapping.Y) ? goalEulers.y : t.localEulerAngles.y;
            eulers.z = (_rotationSnapping.Z) ? goalEulers.z : t.localEulerAngles.z;
            t.localEulerAngles = eulers;
            //Scale.
            Vector3 scale;
            scale.x = (_scaleSnapping.X) ? transformData.Scale.x : t.localScale.x;
            scale.y = (_scaleSnapping.Y) ? transformData.Scale.y : t.localScale.y;
            scale.z = (_scaleSnapping.Z) ? transformData.Scale.z : t.localScale.z;
            t.localScale = scale;
        }

        /// <summary>
        /// Sets move rates which will occur instantly.
        /// </summary>
        private void SetInstantRates(ref RateData rd)
        {
            rd.Update(-1f, -1f, -1f, -1f, false, -1f);
        }

        /// <summary>
        /// Sets move rates which will occur over time.
        /// </summary>
        // 
        private void SetCalculatedRates(uint lastTick, ref TransformData prevTransformData, ref GoalData nextGoalData, ChangedFull changedFull, Channel channel)
        {
            /* Only update rates if data has changed.
             * When data comes in reliably for eventual consistency
             * it's possible that it will be the same as the last
             * unreliable packet. When this happens no change has occurred
             * and the distance of change woudl also be 0; this prevents
             * the NT from moving. Only need to compare data if channel is reliable. */
            ref TransformData td = ref nextGoalData.Transforms;
            if (channel == Channel.Reliable && !HasChanged(ref prevTransformData,ref td, ref changedFull))
                return;

            //How much time has passed between last update and current.
            if (lastTick == 0)
                lastTick = (nextGoalData.Transforms.Tick - 1);

            uint tickDifference = (td.Tick - lastTick);
            float timePassed = base.NetworkManager.TimeManager.TicksToTime(tickDifference);

            //Distance between properties.
            float distance;
            float positionRate;
            float rotationRate;
            float scaleRate;

            ref RateData rd = ref nextGoalData.Rates;
            //Correction to apply towards rates when a rate change is detected as abnormal.
            float abnormalCorrection = 1f;
            bool abnormalRateDetected = false;
            float unalteredPositionRate = rd.LastUnalteredPositionRate;

            //Position.
            if (ChangedFullContains(changedFull, ChangedFull.Position))
            {
                Vector3 lastPosition = prevTransformData.Position;
                distance = Vector3.Distance(lastPosition, td.Position);
                //If distance teleports assume rest do.
                if (_enableTeleport && distance >= _teleportThreshold)
                {
                    SetInstantRates(ref rd);
                    return;
                }

                //Position distance already calculated.
                unalteredPositionRate = distance / timePassed;
                /* Try to detect abnormal rate changes.
                 * 
                 * This won't occur if the user
                 * is moving using the tick system but will likely happen when the transform
                 * is being moved in update.
                 * 
                 * Update will iterate a varying amount of times per tick,
                 * which will result in distances being slightly different. This is
                 * rarely an issue when the frame rate is high and the distance 
                 * variance is very little, but for games which are running at about
                 * the same frame rate as the tick it's possible the object will
                 * move twice the distance every few ticks. EG: if running 60 fps/50 tick.
                 * Execution may look like this..
                 * frame, tick, frame, tick, frame, frame, tick. The frame, frame would
                 * result in double movement distance. */

                //If last position rate is known then compare against it.
                if (unalteredPositionRate > 0f && rd.LastUnalteredPositionRate > 0f)
                {
                    float percentage = Mathf.Abs(1f - (unalteredPositionRate / rd.LastUnalteredPositionRate));
                    /* If percentage change is more than 25% then speed is considered
                     * to have changed drastically. */
                    if (percentage > 0.25f)
                    {
                        float c = (rd.LastUnalteredPositionRate / unalteredPositionRate);
                        /* Sometimes stop and goes can incorrectly trigger 
                         * an abnormal detection. Fortunately abnornalties tend
                         * to either skip a tick or send twice in one tick.
                         * Because of this it's fairly safe to assume that if the calculated
                         * correction is not ~0.5f or ~2f then it's a false detection. */
                        float allowedDifference = 0.1f;
                        if (
                            (c < 1f && Mathf.Abs(0.5f - c) < allowedDifference) ||
                            (c > 1f && Mathf.Abs(2f - c) < allowedDifference))
                        {
                            abnormalCorrection = c;
                            abnormalRateDetected = true;
                        }
                        /* If an abnormality has been marked then assume new rate
                         * is proper. When an abnormal rate occurs unintentionally
                         * the values will fix themselves next tick, therefor when
                         * rate changes drastically twice assume its intentional or
                         * that the rate had simply fixed itself, both which would unset
                         * abnormal rate detected. */
                    }
                }

                positionRate = (unalteredPositionRate * abnormalCorrection);
            }
            else
            {
                positionRate = rd.Position;
            }

            //Rotation.
            if (ChangedFullContains(changedFull, ChangedFull.Rotation))
            {
                Quaternion lastRotation = prevTransformData.Rotation;
                distance = Quaternion.Angle(lastRotation, td.Rotation);
                rotationRate = (distance / timePassed) * abnormalCorrection;
            }
            else
            {
                rotationRate = rd.Rotation;
            }

            //Scale.
            if (ChangedFullContains(changedFull, ChangedFull.Scale))
            {
                Vector3 lastScale = prevTransformData.Scale;
                distance = Vector3.Distance(lastScale, td.Scale);
                scaleRate = (distance / timePassed) * abnormalCorrection;
            }
            else
            {
                scaleRate = rd.Scale;
            }

            rd.Update(positionRate, rotationRate, scaleRate, unalteredPositionRate, abnormalRateDetected, timePassed);

            //Returns if whole contains part.
            bool ChangedFullContains(ChangedFull whole, ChangedFull part)
            {
                return (whole & part) == part;
            }
        }
        #endregion       

        private void SetExtrapolation(ref TransformData prev, ref TransformData next, Channel channel)
        {
            if (channel == Channel.Reliable)
                next.ExtrapolationState = TransformData.ExtrapolateState.Disabled;
            if (next.Position == prev.Position)
                return;

            Vector3 offet = (next.Position - prev.Position) * _extrapolation;
            next.ExtrapolatedPosition = (next.Position + offet);
            next.ExtrapolationState = TransformData.ExtrapolateState.Available;
        }
        /// <summary>
        /// Updates clients with transform data.
        /// </summary>
        /// <param name="tb"></param>
        /// <param name="channel"></param>
        [ObserversRpc]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ObserversUpdateTransform(ArraySegment<byte> data, Channel channel)
        {
            if (!_clientAuthoritative && base.IsOwner && !_sendToOwner)
                return;
            if (_clientAuthoritative && base.IsOwner)
                return;
            if (base.IsServer)
                return;

            //Not new data.
            uint lastPacketTick = base.TimeManager.LastPacketTick;
            if (lastPacketTick <= _lastObserversRpcTick)
                return;
            _lastObserversRpcTick = lastPacketTick;

            DataReceived(data, channel, false);
        }

        /// <summary>
        /// Updates the transform on the server.
        /// </summary>
        /// <param name="tb"></param>
        /// <param name="channel"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [ServerRpc]
        private void ServerUpdateTransform(ArraySegment<byte> data, Channel channel)
        {
            //Not new data.
            uint lastPacketTick = base.TimeManager.LastPacketTick;
            if (lastPacketTick <= _lastServerRpcTick)
                return;
            _lastServerRpcTick = lastPacketTick;

            //Set to received bytes.
            if (_receivedClientBytes == null)
                _receivedClientBytes = WriterPool.GetWriter();
            _receivedClientBytes.Reset();
            _receivedClientBytes.WriteArraySegment(data);

            //Indicates new data has been received from client.
            _clientBytesChanged = true;

            DataReceived(data, channel, true);
        }

        /// <summary>
        /// Processes received data for lcients and server.
        /// </summary>
        private void DataReceived(ArraySegment<byte> data, Channel channel, bool asServer)
        {
            TransformData lastTd = _lastReceivedTransformData;
            //Tick from last goal data.
            uint lastTick = lastTd.Tick;
            ChangedFull changedFull = ChangedFull.Unset;

            GoalData gd = new GoalData();
            ref TransformData td = ref gd.Transforms;
            UpdateTransformData(data, ref lastTd, ref td, ref changedFull);
            SetExtrapolation(ref lastTd, ref td, channel);
            //If server only teleport.
            if (asServer && !base.IsClient)
                SetInstantRates(ref gd.Rates);
            //Otherwise use timed.
            else
                SetCalculatedRates(lastTick, ref lastTd, ref gd, changedFull, channel);
            SnapProperties(ref td);

            _lastReceivedTransformData.Update(ref td);

            /* If channel is reliable then this is a settled packet.
             * Reset last received tick so next starting move eases
             * in. */
            if (channel == Channel.Reliable)
                td.Tick = 0;

            lastTd.Update(ref td);

            gd.SetIsDefault(false);
            gd.ReceivedTick = base.TimeManager.LocalTick;
            if (!_currentGoalData.IsDefault)
                _goalDataQueue.Enqueue(gd);
            else
                _currentGoalData = gd;

            /* If extrapolating then immediately break the extrapolation
             * in favor of newest results. This will bypass
             * rebuilding the buffer but for now it will do. */
            if (_currentGoalData.Transforms.ExtrapolationState == TransformData.ExtrapolateState.Active)
            {
                _currentGoalData = gd;
                _queueReady = true;
            }
            else if (!_queueReady && _goalDataQueue.Count >= _interpolation)
            {
                _queueReady = true;
            }
        }

        /// <summary>
        /// Updates a TransformData from packetData.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateTransformData(ArraySegment<byte> packetData, ref TransformData prevTransformData, ref TransformData nextTransformData, ref ChangedFull changedFull)
        {
            DeserializePacket(packetData, ref prevTransformData, ref nextTransformData, ref changedFull);
            nextTransformData.Tick = base.TimeManager.LastPacketTick;
        }
    }


}