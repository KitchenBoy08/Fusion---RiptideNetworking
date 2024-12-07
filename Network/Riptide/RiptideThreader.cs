﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabFusion.Senders;

using Riptide;
using Riptide.Transports;
using Riptide.Utils;

namespace FNPlus.Network.Riptide
{
    internal class RiptideThreader
    {
        private static Thread _riptideThread;

        /// <summary>
        /// Starts the Riptide Thread and begins polling messages/actions.
        /// </summary>
        internal static void StartThread()
        {
            _riptideThread = new Thread(RiptideThread);

            if (_riptideThread.IsAlive)
                return;

            _isThreadAlive = true;

            _riptideThread.IsBackground = true;
            _riptideThread.Start();
        }

        /// <summary>
        /// Stops the Riptide Thread from running and clears all events in the action queue.
        /// </summary>
        internal static void KillThread()
        {
            _isThreadAlive = false;
        }

        // ANYTIME these are accessed outside of the Riptide Thread, please, please, please lock it, future people. (You shouldn't do that anyway but still)
        private static Client _riptideClient;
        private static Server _riptideServer;

        internal static readonly ConcurrentQueue<Tuple<byte[], MessageSendMode>> ClientSendQueue = new();
        internal static readonly ConcurrentQueue<Tuple<byte[], MessageSendMode, ushort, bool>> ServerSendQueue = new();

        private static bool _isThreadAlive = false;
        private static void RiptideThread()
        {
            InitalizeRiptide();

            while (_isThreadAlive)
            {
                if (ClientSendQueue.Count > 0 && ClientSendQueue.TryDequeue(out Tuple<byte[], MessageSendMode> clientMessageTuple))
                {
                    byte[] messageData = clientMessageTuple.Item1;
                    MessageSendMode sendMode = clientMessageTuple.Item2;

                    Message message = Message.Create(sendMode, 0);
                    message.AddBytes(messageData);

                    _riptideClient.Send(message);
                }

                if (ServerSendQueue.Count > 0 && ServerSendQueue.TryDequeue(out Tuple<byte[], MessageSendMode, ushort, bool> serverMessageTuple))
                {
                    byte[] messageData = serverMessageTuple.Item1;
                    MessageSendMode sendMode = serverMessageTuple.Item2;
                    ushort id = serverMessageTuple.Item3;
                    bool isBroadcast = serverMessageTuple.Item4;

                    Message message = Message.Create(sendMode, 0);
                    message.AddBytes(messageData);

                    if (isBroadcast)
                        _riptideServer.SendToAll(message);
                    else
                        _riptideServer.Send(message, id);
                }

                _riptideClient.Update();
                _riptideServer.Update();
            }

            DeinitializeRiptide();

            MelonLogger.Msg("Thread dead!");
        }

        private static void InitalizeRiptide()
        {
            RiptideLogger.Initialize(MelonLogger.Msg, true);

            _riptideClient = new("RIPTIDE CLIENT");
            _riptideServer = new("RIPTIDE SERVER");

            _riptideClient.MessageReceived += OnClientReceives;
            _riptideServer.MessageReceived += OnServerReceives;

            _riptideClient.Disconnected += OnDisconnected;
            _riptideServer.ClientDisconnected += OnClientDisconnected;
        }

        private static void OnClientDisconnected(object sender, ServerDisconnectedEventArgs e)
        {
            ushort id = e.Client.Id;

            RiptideNetworkLayer.ActionQueue.Enqueue(new Action(() =>
            {
                if (id == PlayerIdManager.LocalId)
                    return;

                // Make sure the user hasn't previously disconnected
                if (PlayerIdManager.HasPlayerId(id))
                {
                    // Update the mod so it knows this user has left
                    InternalServerHelpers.OnUserLeave(id);

                    // Send disconnect notif to everyone
                    ConnectionSender.SendDisconnect(id);
                }
            }));
        }

        private static void OnDisconnected(object sender, global::Riptide.DisconnectedEventArgs e)
        {
            RiptideNetworkLayer.ActionQueue.Enqueue(new Action(() => InternalServerHelpers.OnDisconnect()));
        }

        private static void DeinitializeRiptide()
        {
            _riptideClient = null;
            _riptideServer = null;
        }

        internal static bool IsServerRunning { get; private set; }
        internal static bool IsClientConnected { get; private set; }

        public static void StartServer()
        {
            lock (_riptideClient)
            {
                lock (_riptideServer)
                {
                    MelonLogger.Msg(Thread.CurrentThread.IsBackground);

                    _riptideServer.Start(7777, 256, 0, false);

                    _riptideClient.Connected += OnConnect;
                    _riptideClient.Connect("127.0.0.1:7777", 5, 0, null, false);

                    void OnConnect(object sender, EventArgs args)
                    {
                        _riptideClient.Connected -= OnConnect;

                        IsServerRunning = true;
                        IsClientConnected = true;

                        RiptideNetworkLayer.ActionQueue.Enqueue(() =>
                        {
                            PlayerIdManager.SetLongId(_riptideClient.Id);
                            LocalPlayer.Username = Utilities.PlayerInfo.Username;

                            InternalServerHelpers.OnStartServer();
                        });
                    }
                }
            }
        }

        public static void StopServer()
        {
            lock (_riptideServer)
            {
                _riptideServer.Stop();

                IsServerRunning = false;
                IsClientConnected = false;
            }
        }

        public static void ConnectToServer(string serverCode)
        {
            lock (_riptideClient)
            {
                string ipString;

                if (IPAddress.TryParse(serverCode, out IPAddress ipAddress))
                    ipString = serverCode;
                else
                    ipString = IPUtils.DecodeIPAddress(serverCode);

                _riptideClient.Connected += OnConnect;
                _riptideClient.Connect($"{ipAddress}:7777", 5, 0, null, false);

                void OnConnect(object sender, EventArgs args)
                {
                    _riptideClient.Connected -= OnConnect;

                    IsClientConnected = true;

                    RiptideNetworkLayer.ActionQueue.Enqueue(() =>
                    {
                        PlayerIdManager.SetLongId(_riptideClient.Id);
                        LocalPlayer.Username = Utilities.PlayerInfo.Username;

                        ConnectionSender.SendConnectionRequest();
                    });
                }
            }
        }

        public static void Disconnect()
        {
            lock (_riptideClient)
            {
                lock (_riptideServer)
                {
                    if (IsServerRunning)
                        _riptideServer.Stop();
                    if (IsClientConnected)
                        _riptideClient.Disconnect();

                    IsClientConnected = false;
                    IsServerRunning = false;
                }
            }
        }

        private static void OnServerReceives(object sender, MessageReceivedEventArgs e)
        {
            RiptideNetworkLayer.MessageQueue.Enqueue(new Tuple<byte[], bool>(e.Message.GetBytes(), true));
        }

        private static void OnClientReceives(object sender, MessageReceivedEventArgs e)
        {
            RiptideNetworkLayer.MessageQueue.Enqueue(new Tuple<byte[], bool>(e.Message.GetBytes(), false));
        }
    }
}