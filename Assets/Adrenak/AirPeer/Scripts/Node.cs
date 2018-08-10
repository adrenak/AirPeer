﻿using System;
using Byn.Net;
using UnityEngine;
using System.Collections.Generic;

namespace Adrenak.AirPeer {
    public class Node : MonoBehaviour {
        public enum State {
            Inactive,
            Offline,
            Server,
            Client
        }

        string k_SignallingServer = "wss://because-why-not.com:12777/chatapp";
        string k_ICEServer1 = "stun:because-why-not.com:12779";
        string k_ICEServer2 = "stun:stun.l.google.com:19302";

        Action<bool> m_StartServerCallback;
        Action m_StopServerCallback;
        Action<ConnectionId> m_ConnectCallback;
        Action<ConnectionId> m_DisconnectCallback;

        public event Action OnServerDown;
        public event Action<ConnectionId> OnJoin;
        public event Action<ConnectionId> OnLeave;
        public event Action<ConnectionId, Packet, bool> OnGetMessage;

        IBasicNetwork m_Network;
        public List<ConnectionId> ConnectionIds { get; private set; }
        public ConnectionId CId {
            get {
                if (ConnectionIds == null || ConnectionIds.Count == 0)
                    return ConnectionId.INVALID;
                return ConnectionIds[0];
            }
        }
        public State Status { get; private set; }

        // ================================================
        // LIFECYCLE
        // ================================================
        public static Node New(string name = "Adrenak.AirPeer.Node") {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            return go.AddComponent<Node>();
        }

        public bool Init() {
            Deinit();

            ConnectionIds = new List<ConnectionId>();
            m_Network = WebRtcNetworkFactory.Instance.CreateDefault(
                k_SignallingServer,
                new[] { new IceServer(k_ICEServer1), new IceServer(k_ICEServer2) }
            );
            var result = m_Network != null;
            if (result)
                Status = State.Offline;
            else
                Status = State.Inactive;
            return m_Network != null;
        }

        public void Deinit() {
            if (m_Network != null) {
                m_Network.Dispose();
                m_Network = null;
            }
        }

        void Update() {
            if (m_Network != null) {
                m_Network.Update();
                ReadNetworkEvents();
            }
            if(m_Network != null)
                m_Network.Flush();
        }

        void OnDestroy() {
            Deinit();
        }

        // ================================================
        // NETWORK API
        // ================================================
        public void StartServer(string roomName, Action<bool> callback) {
            m_StartServerCallback = callback;
            m_Network.StartServer(roomName);
        }

        public void StopServer(Action callback) {
            // WebRTC doesn't tell the client when the server it is connected to
            // goes offline. So broadcast a reserved event message to everyone
            Send(Packet.From(CId).WithTag(ReservedTags.ServerDown), true);
            m_StopServerCallback = callback;
            m_Network.StopServer();
        }

        public void Connect(string roomName, Action<ConnectionId> callback) {
            m_ConnectCallback = callback;
            m_Network.Connect(roomName);
        }

        public void Disconnect(Action<ConnectionId> callback) {
            m_DisconnectCallback = callback;
            m_Network.Disconnect(CId);
        }

        public bool Send(Packet packet, bool reliable = false) {
            if (m_Network == null || ConnectionIds == null || ConnectionIds.Count == 0) return false;

            var recipients = packet.Recipients;
            foreach(var cid in ConnectionIds) 
                m_Network.SendData(cid, packet.Serialize(), 0, packet.Serialize().Length, reliable);
            
            return true;
        }
        
        // ================================================
        // NETWORK EVENT PROCESSING
        // ================================================
        void ReadNetworkEvents() {
            NetworkEvent netEvent;
            while (m_Network != null && m_Network.Dequeue(out netEvent))
                ProcessNetworkEvent(netEvent);
        }

        void ProcessNetworkEvent(NetworkEvent netEvent) {
            switch (netEvent.Type) {
                case NetEventType.ServerInitialized:
                    OnServerInit(netEvent);
                    break;

                case NetEventType.ServerInitFailed:
                    OnServerInitFailed(netEvent);
                    break;

                case NetEventType.ServerClosed:
                    OnServerClosed(netEvent);
                    break;

                case NetEventType.NewConnection:
                    OnNewConnection(netEvent);
                    break;

                case NetEventType.ConnectionFailed:
                    OnConnectionFailed(netEvent);
                    break;

                case NetEventType.Disconnected:
                    OnDisconnected(netEvent);
                    break;

                case NetEventType.ReliableMessageReceived:
                    OnMessageReceived(netEvent, true);
                    break;

                case NetEventType.UnreliableMessageReceived:
                    OnMessageReceived(netEvent, false);
                    break;
            }
        }

        void OnServerInit(NetworkEvent netEvent) {
            ConnectionIds.Add(new ConnectionId(0));
            Status = State.Server;
            m_StartServerCallback.TryInvoke(true);
            m_StartServerCallback = null;
        }

        void OnServerInitFailed(NetworkEvent netEvent) {
            Deinit();
            Status = State.Offline;
            m_StartServerCallback.TryInvoke(false);
            m_StartServerCallback = null;
        }

        void OnServerClosed(NetworkEvent netEvent) {
            Status = State.Offline;
            m_StopServerCallback.TryInvoke();
            m_StopServerCallback = null;
        }

        void OnNewConnection(NetworkEvent netEvent) {
            ConnectionId newid = netEvent.ConnectionId;
            ConnectionIds.Add(newid);

            if (Status == State.Offline) {
                // Add server as a connection
                ConnectionIds.Add(new ConnectionId(0));
                Status = State.Client;
            }
            else if (Status == State.Server) {
                foreach (var id in ConnectionIds) {
                    if (id.id == 0 || id.id == newid.id) continue;

                    byte[] payload;

                    // Announce the new connection to the old ones
                    payload = PayloadWriter.New().WriteShort(newid.id).Bytes;
                    Send(Packet.From(this).To(id).With(ReservedTags.ConnectionRegister, payload), true);

                    payload = PayloadWriter.New().WriteShort(id.id).Bytes;
                    Send(Packet.From(this).To(newid).With(ReservedTags.ConnectionRegister, payload), true);
                }
            }

            m_ConnectCallback.TryInvoke(newid);
            m_ConnectCallback = null;
        }

        void OnConnectionFailed(NetworkEvent netEvent) {
            Deinit();
            Status = State.Offline;
            m_ConnectCallback.TryInvoke(ConnectionId.INVALID);
            m_ConnectCallback = null;
        }

        private void OnDisconnected(NetworkEvent netEvent) {
            if (Status == State.Client) {
                Status = State.Offline;
                Deinit();
            }
            else if (Status == State.Server) {
                var dId = netEvent.ConnectionId;
                ConnectionIds.Remove(netEvent.ConnectionId);

                var payload = PayloadWriter.New().WriteShort(dId.id).Bytes;

                // Clients are not aware of each other as this is a star network
                // Send a reliable reserved message to annouce the disconnection
                Send(Packet.From(CId).With(ReservedTags.ConnectionDeregister, payload), true);
            }

            m_DisconnectCallback.TryInvoke(CId);
            m_DisconnectCallback = null;
        }

        void OnMessageReceived(NetworkEvent netEvent, bool reliable) {
            var packet = Packet.Deserialize(netEvent.GetDataAsByteArray());
            if (packet == null) {
                Debug.Log("nop");
                return;
            }

            string reservedTag = packet.Tag.StartsWith("reserved") ? packet.Tag : string.Empty;

            // If is not a reserved message
            if (reservedTag == string.Empty) {
                OnGetMessage.TryInvoke(netEvent.ConnectionId, packet, reliable);

                if (Status != State.Server) return;

                // The server tried o broadcast the packet to everyone else listed as recipients
                foreach(var r in packet.Recipients) {
                    if (r == CId.id || r == netEvent.ConnectionId.id) continue;
                    Send(Packet.From(CId).To(r).With(ReservedTags.PacketForwarding, packet.Serialize()), true);
                }
            }

            // handle reserved messages
            switch (reservedTag) {
                case ReservedTags.ServerDown:
                    OnServerDown.TryInvoke();
                    break;
                case ReservedTags.ConnectionRegister:
                    ConnectionIds.Add(netEvent.ConnectionId);
                    OnJoin.TryInvoke(netEvent.ConnectionId);
                    break;
                case ReservedTags.ConnectionDeregister:
                    ConnectionIds.Remove(netEvent.ConnectionId);
                    OnLeave.TryInvoke(netEvent.ConnectionId);
                    break;
                case ReservedTags.PacketForwarding:
                    OnGetMessage.TryInvoke(netEvent.ConnectionId, packet, reliable);
                    break;
            }
        }
    }
}
