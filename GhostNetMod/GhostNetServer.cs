﻿using Celeste.Mod;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.Detour;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.Ghost.Net {
    public class GhostNetServer : GameComponent {

        // TODO: Timeout? Auth? Proper server functionality?

        public bool IsRunning { get; protected set; } = false;

        public TcpListener ManagementListener;
        public UdpClient UpdateClient;

        // Pseudo-connection because sending / receiving data on the same machine sucks on Windows.
        public GhostNetConnection LocalConnectionToServer;

        // Used to broadcast updates.
        public GhostNetConnection UpdateConnection;

        // All managed player connections.
        public List<GhostNetConnection> Connections = new List<GhostNetConnection>();
        public Dictionary<IPEndPoint, GhostNetConnection> ConnectionMap = new Dictionary<IPEndPoint, GhostNetConnection>();
        public Dictionary<IPAddress, GhostNetConnection> UpdateConnectionQueue = new Dictionary<IPAddress, GhostNetConnection>();
        public Dictionary<uint, GhostChunkNetMPlayer> GhostPlayers = new Dictionary<uint, GhostChunkNetMPlayer>();
        public Dictionary<uint, uint> GhostIndices = new Dictionary<uint, uint>();

        public Thread ListenerThread;

        // Allows testing a subset of GhostNetMod's functions in an easy manner.
        public bool AllowLoopbackGhost = false;

        public GhostNetServer(Game game)
            : base(game) {
        }

        public override void Update(GameTime gameTime) {
            base.Update(gameTime);
        }

        #region Management Connection Listener

        protected virtual void ListenerLoop() {
            while (IsRunning) {
                Thread.Sleep(0);

                while (ManagementListener.Pending()) {
                    // Updates are handled via WorldUpdateConnection.
                    // Receive management updates in a separate connection.
                    TcpClient client = ManagementListener.AcceptTcpClient();
                    Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Client #{Connections.Count} ({client.Client.RemoteEndPoint}) connected");
                    Accept(new GhostNetRemoteConnection(
                        client,
                        null
                    ) {
                        OnReceiveManagement = OnReceiveManagement,
                        OnDisconnect = OnDisconnect
                    });
                }
            }
        }

        public virtual void Accept(GhostNetConnection con) {
            uint id = (uint) Connections.Count;
            Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Client #{id} ({con.ManagementEndPoint}) accepted");
            Connections.Add(con);
            ConnectionMap[con.ManagementEndPoint] = con;
            UpdateConnectionQueue[con.ManagementEndPoint.Address] = con;
            con.SendManagement(new GhostNetFrame {
                HHead = new GhostChunkNetHHead {
                    IsValid = true,
                    PlayerID = id
                },

                MServerInfo = new GhostChunkNetMServerInfo {
                    IsValid = true
                }
            });
        }

        #endregion

        #region Frame Parsers

        protected virtual void SetNetHead(GhostNetConnection con, ref GhostNetFrame frame) {
            frame.HHead = new GhostChunkNetHHead {
                IsValid = true,
                PlayerID = (uint) Connections.IndexOf(con)
            };

            frame.MServerInfo.IsValid = false;
        }

        public virtual void Parse(GhostNetConnection con, ref GhostNetFrame frame) {
            SetNetHead(con, ref frame);

            if (!frame.HHead.IsValid)
                return;

            if (frame.MPlayer.IsValid)
                ParseMPlayer(con, ref frame);

            if (frame.MEmote.IsValid) {
                // Logger.Log(LogLevel.Info, "ghostnet-s", $"#{frame.HHead.PlayerID} emote: {frame.MEmote.Value}");
                if (frame.MEmote.Value.Length > 128)
                    frame.MEmote.Value = frame.MEmote.Value.Substring(0, 128);
                PropagateM(con, ref frame);
            }

            if (frame.UUpdate.IsValid)
                ParseUUpdate(con, ref frame);

            // TODO: Let mods parse extras.
        }

        public virtual void ParseMPlayer(GhostNetConnection con, ref GhostNetFrame frame) {
            frame.MPlayer.Name = frame.MPlayer.Name.Replace("*", "").Replace("\r", "").Replace("\n", "").Trim();
            if (string.IsNullOrEmpty(frame.MPlayer.Name))
                frame.MPlayer.Name = "#" + frame.HHead.PlayerID;

            // Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Received nM0 from #{frame.PlayerID} ({con.EndPoint})");
            Logger.Log(LogLevel.Info, "ghostnet-s", $"#{frame.HHead.PlayerID} {frame.MPlayer.Name} in {frame.MPlayer.SID} {frame.MPlayer.Level}");

            // Propagate status to all other players.
            PropagateM(con, ref frame);

            // Inform the player about all existing ghosts.
            foreach (KeyValuePair<uint, GhostChunkNetMPlayer> otherStatus in GhostPlayers) {
                if (!AllowLoopbackGhost && otherStatus.Key == frame.HHead.PlayerID)
                    continue;
                con.SendManagement(new GhostNetFrame {
                    HHead = {
                        IsValid = true,
                        PlayerID = otherStatus.Key
                    },

                    MPlayer = otherStatus.Value
                });
            }

            GhostIndices[frame.HHead.PlayerID] = 0;
            GhostPlayers[frame.HHead.PlayerID] = frame.MPlayer;
        }

        public virtual void ParseUUpdate(GhostNetConnection con, ref GhostNetFrame frame) {
            GhostChunkNetMPlayer player;
            if (!GhostPlayers.TryGetValue(frame.HHead.PlayerID, out player)) {
                // Ghost not managed - ignore the update.
                Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Unknown update from #{frame.HHead.PlayerID} ({con.UpdateEndPoint}) - statusless ghost, possibly premature");
                return;
            }

            // Prevent unordered outdated frames from being handled.
            uint lastIndex;
            if (GhostIndices.TryGetValue(frame.HHead.PlayerID, out lastIndex) && frame.UUpdate.UpdateIndex < lastIndex) {
                // Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Out of order update from #{frame.H0.PlayerID} ({con.UpdateEndPoint}) - got {frame.U0.UpdateIndex}, newest is {lastIndex]}");
                return;
            }
            GhostIndices[frame.HHead.PlayerID] = frame.UUpdate.UpdateIndex;

            // Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Received nU0 from #{frame.H0.PlayerID} ({con.UpdateEndPoint})");

            // Propagate update to all active players in the same room.
            PropagateU(con, ref frame, player);
        }

        #endregion

        #region Frame Senders

        public void PropagateM(GhostNetConnection con, ref GhostNetFrame frame) {
            if (frame.Propagated)
                return;
            frame.Propagated = true;

            foreach (GhostNetConnection otherCon in Connections)
                if (otherCon != null)
                    otherCon.SendManagement(frame);
        }

        public void PropagateU(GhostNetConnection con, ref GhostNetFrame frame, GhostChunkNetMPlayer player) {
            // U is always handled after M. Even if sending this fails, we shouldn't worry about loosing M chunks.
            if (frame.Propagated)
                return;
            frame.Propagated = true;

            for (int i = 0; i < Connections.Count; i++) {
                GhostNetConnection otherCon = Connections[i];
                if (otherCon == null || (!AllowLoopbackGhost && otherCon == con))
                    continue;

                GhostChunkNetMPlayer otherPlayer;
                if (!GhostPlayers.TryGetValue((uint) i, out otherPlayer) ||
                    player.SID != otherPlayer.SID ||
                    player.Level != otherPlayer.Level
                ) {
                    continue;
                }

                if (!(otherCon is GhostNetRemoteConnection)) {
                    otherCon.SendUpdate(frame);
                } else if (otherCon.UpdateEndPoint != null) {
                    UpdateConnection.SendUpdate(otherCon.UpdateEndPoint, frame);
                } else {
                    // Fallback for UDP-less clients.
                    otherCon.SendManagement(frame);
                }
            }
        }

        #endregion

        #region Connection Handlers

        protected virtual void OnReceiveManagement(GhostNetConnection con, IPEndPoint remote, GhostNetFrame frame) {
            // We can receive frames from LocalConnectionToServer, which isn't "valid" when we want to send back data.
            // Get the management connection to the remote client.
            if (con == null || !ConnectionMap.TryGetValue(remote, out con) || con == null)
                return;

            // If we received an update via the managed con, forget about the update con.
            if (frame.UUpdate.IsValid) {
                if (con.UpdateEndPoint != null) {
                    ConnectionMap[con.UpdateEndPoint] = null;
                    ConnectionMap[con.ManagementEndPoint] = con; // In case Managed == Update
                    con.UpdateEndPoint = null;
                } else {
                    UpdateConnectionQueue[con.ManagementEndPoint.Address] = null;
                }
            } else {
                UpdateConnectionQueue[con.ManagementEndPoint.Address] = con;
            }

            Parse(con, ref frame);
        }

        protected virtual void OnReceiveUpdate(GhostNetConnection conReceived, IPEndPoint remote, GhostNetFrame frame) {
            // Prevent UpdateConnection locking in on a single player.
            if (conReceived == UpdateConnection)
                UpdateConnection.UpdateEndPoint = null;

            GhostNetConnection con;
            // We receive updates either from LocalConnectionToServer or from UpdateConnection.
            // Get the management connection to the remote client.
            if (conReceived == null || !ConnectionMap.TryGetValue(remote, out con) || con == null) {
                // Unlike management connections, which we already know the target port of at the time of connection,
                // updates are sent via UDP (by default) and thus "connectionless."
                // If we've got a queued connection for that address, update it.
                GhostNetConnection queue;
                if (UpdateConnectionQueue.TryGetValue(remote.Address, out queue) && queue != null) {
                    con = queue;
                    con.UpdateEndPoint = remote;
                    ConnectionMap[con.UpdateEndPoint] = con;
                    Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Mapped update connection ({con.ManagementEndPoint}, {con.UpdateEndPoint})");
                } else {
                    // If the address is completely unknown, drop the frame.
                    Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Unknown update from {remote} - unknown connection, possibly premature");
                    return;
                }
            }

            Parse(con, ref frame);
        }

        protected virtual void OnDisconnect(GhostNetConnection con) {
            if (!ConnectionMap.TryGetValue(con.ManagementEndPoint, out con) || con == null)
                return; // Probably already disconnected.

            uint id = (uint) Connections.IndexOf(con);
            if (id == uint.MaxValue) {
                Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Client #? ({con.ManagementEndPoint}) disconnected?");
                return;
            }
            Logger.Log(LogLevel.Verbose, "ghostnet-s", $"Client #{id} ({con.ManagementEndPoint}) disconnected");

            Connections[(int) id] = null;

            ConnectionMap[con.ManagementEndPoint] = null;

            if (con.UpdateEndPoint != null) {
                ConnectionMap[con.UpdateEndPoint] = null;
            } else {
                UpdateConnectionQueue[con.ManagementEndPoint.Address] = null;
            }

            // Propagate disconnect to all other players.
            GhostNetFrame frame = new GhostNetFrame {
                HHead = new GhostChunkNetHHead {
                    IsValid = true,
                    PlayerID = id
                },

                MPlayer = new GhostChunkNetMPlayer {
                    IsValid = true,
                    Name = "",
                    SID = "",
                    Level = ""
                }
            };
            foreach (GhostNetConnection otherCon in Connections)
                if (otherCon != null && otherCon != con)
                    otherCon.SendManagement(frame);
        }

        #endregion

        public void Start() {
            if (IsRunning) {
                Logger.Log(LogLevel.Warn, "ghostnet-s", "Server already running, restarting");
                Stop();
            }

            Logger.Log(LogLevel.Info, "ghostnet-s", "Starting server");
            IsRunning = true;

            ManagementListener = new TcpListener(IPAddress.Any, GhostNetModule.Settings.Port);
            ManagementListener.Start();

            UpdateClient = new UdpClient(GhostNetModule.Settings.Port);
            UpdateConnection = new GhostNetRemoteConnection(
                null,
                UpdateClient
            ) {
                OnReceiveUpdate = OnReceiveUpdate
            };

            // Fake connection for any local clients running in the same instance.
            LocalConnectionToServer = new GhostNetLocalConnection {
                OnReceiveManagement = OnReceiveManagement,
                OnReceiveUpdate = OnReceiveUpdate,
                OnDisconnect = OnDisconnect
            };

            ListenerThread = new Thread(ListenerLoop);
            ListenerThread.IsBackground = true;
            ListenerThread.Start();
        }

        public void Stop() {
            if (!IsRunning)
                return;
            Logger.Log(LogLevel.Info, "ghostnet-s", "Stopping server");
            IsRunning = false;

            ListenerThread.Join();

            ManagementListener.Stop();

            // Close all management connections.
            foreach (GhostNetConnection connection in Connections) {
                if (connection == null)
                    continue;
                connection.Dispose();
            }

            UpdateConnection.Dispose();

            LocalConnectionToServer.Dispose();

            Celeste.Instance.Components.Remove(this);
        }

        protected override void Dispose(bool disposing) {
            base.Dispose(disposing);
            Stop();
        }

    }
}
