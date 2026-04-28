using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CupheadOnline.Sync;
using CupheadOnline.UI;

namespace CupheadOnline.Net
{
    /// <summary>
    /// UDP networking manager — no external dependencies, works with Mono 2.0+.
    ///
    /// Wire format
    ///   Unreliable: [type: 1 byte]  [payload bytes]
    ///   Reliable:   [type|0x80: 1 byte]  [seq_lo: 1 byte]  [seq_hi: 1 byte]  [payload bytes]
    ///   ACK:        [0xE4]  [seq_lo]  [seq_hi]
    ///   Ctrl msgs:  0xE0–0xE3  (see CTRL_* constants)
    ///
    /// Threading model
    ///   The receive thread drops raw UDP payloads into _recvQueue (locked Queue).
    ///   Poll() on the main thread drains that queue and handles all game logic.
    ///   No locks needed except for the reliable-pending dictionary.
    /// </summary>
    public sealed class NetManager
    {
        // ── Public API ────────────────────────────────────────────────────────
        public const int  DEFAULT_PORT  = 7890;
        public bool       IsConnected   => _connected;
        public int        Latency       { get; private set; }

        // ── Protocol constants ────────────────────────────────────────────────
        private const byte CTRL_CONNECT_REQ = 0xE0;
        private const byte CTRL_CONNECT_ACK = 0xE1;
        private const byte CTRL_DISCONNECT  = 0xE2;
        private const byte CTRL_KEEPALIVE   = 0xE3;
        private const byte CTRL_ACK         = 0xE4;

        private const string CONNECTION_KEY      = "CHOnline1";
        private const int    TIMEOUT_MS          = 10_000;
        private const int    RETRANSMIT_MS       = 200;
        private const int    MAX_RETRIES         = 25;   // 200 ms × 25 = 5 s max
        private const int    KEEPALIVE_MS        = 2_000;
        private const float  PING_INTERVAL_SEC   = 2f;

        // ── Socket state ──────────────────────────────────────────────────────
        private UdpClient        _udp;
        private volatile bool    _running;
        private volatile bool    _connected;
        private bool             _isHost;
        private IPEndPoint       _remote;        // written once in handshake, then read-only
        private Thread           _recvThread;

        // ── Receive queue (recv thread → main thread) ─────────────────────────
        // Plain Queue + lock — ConcurrentQueue<T> requires .NET 4.0 which is not
        // available in Cuphead's mscorlib 2.0.0.0 Mono runtime.
        private readonly Queue<RawPacket> _recvQueue = new Queue<RawPacket>();
        private readonly object           _recvLock  = new object();

        private struct RawPacket
        {
            public byte[]      Data;
            public IPEndPoint  From;
        }

        // ── Reliable delivery ─────────────────────────────────────────────────
        private ushort _sendSeq;
        private readonly Dictionary<ushort, PendingReliable> _pending = new Dictionary<ushort, PendingReliable>();
        private readonly Dictionary<ushort, bool>            _seenSeqs = new Dictionary<ushort, bool>();

        private struct PendingReliable
        {
            public byte[]   Data;
            public DateTime SentAt;
            public int      Retries;
        }

        // ── Timing ────────────────────────────────────────────────────────────
        private DateTime _lastReceive = DateTime.UtcNow;
        private DateTime _lastSend    = DateTime.UtcNow;

        // Connect retry (client only)
        private bool     _connecting;
        private DateTime _lastConnectRetry;

        // Ping / latency
        private float    _nextPingTime;
        private DateTime _pingSentAt;
        private bool     _pingSentPending;

        // ── Reusable send buffer (main thread only) ───────────────────────────
        private readonly MemoryStream _sendBuf;
        private readonly BinaryWriter _sendWriter;

        // ── Constructor ───────────────────────────────────────────────────────
        public NetManager()
        {
            _sendBuf    = new MemoryStream(256);
            _sendWriter = new BinaryWriter(_sendBuf);
        }

        // ── Session management ────────────────────────────────────────────────

        public void StartHost(int port = DEFAULT_PORT)
        {
            Shutdown();
            _isHost  = true;
            _udp     = new UdpClient(port);
            _running = true;
            _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "CupOnlineRecv" };
            _recvThread.Start();
            MultiplayerSession.StartAsHost();
            Plugin.Log.LogInfo($"[Net] Hosting on :{port}");
        }

        public void Connect(string ip, int port = DEFAULT_PORT)
        {
            Shutdown();
            _isHost     = false;
            _connecting = true;
            _udp        = new UdpClient();
            _remote     = new IPEndPoint(IPAddress.Parse(ip), port);
            _running    = true;
            _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "CupOnlineRecv" };
            _recvThread.Start();
            SendConnectRequest();   // first attempt; Poll() retries every 500 ms
            Plugin.Log.LogInfo($"[Net] Connecting to {ip}:{port}…");
        }

        public void Shutdown()
        {
            if (_udp == null) return;
            _running   = false;
            _connected = false;
            if (_remote != null)
            {
                try { _udp.Send(new[] { CTRL_DISCONNECT }, 1, _remote); } catch { }
            }
            try { _udp.Close(); } catch { }
            _udp        = null;
            _remote     = null;
            _connecting = false;
            _pending.Clear();
            _seenSeqs.Clear();
            _sendSeq = 0;
            lock (_recvLock) _recvQueue.Clear();        // drain
            Plugin.Log.LogInfo("[Net] Shut down.");
        }

        // ── Main-thread poll ──────────────────────────────────────────────────

        /// <summary>Must be called every frame from Plugin.Update (main thread).</summary>
        public void Poll()
        {
            if (_udp == null) return;

            // ── Client: retry handshake until acknowledged ────────────────────
            if (_connecting && !_connected)
            {
                if ((DateTime.UtcNow - _lastConnectRetry).TotalMilliseconds > 500)
                {
                    _lastConnectRetry = DateTime.UtcNow;
                    SendConnectRequest();
                }
                // Still drain the queue so we can receive CTRL_CONNECT_ACK
            }

            // ── Drain receive queue ───────────────────────────────────────────
            while (true)
            {
                RawPacket raw;
                lock (_recvLock)
                {
                    if (_recvQueue.Count == 0) break;
                    raw = _recvQueue.Dequeue();
                }
                ProcessRaw(raw);
            }

            if (!_connected) return;

            var now = DateTime.UtcNow;

            // ── Retransmit timed-out reliable packets ─────────────────────────
            if (_pending.Count > 0)
            {
                var toRetry  = new List<ushort>();
                var toRemove = new List<ushort>();

                foreach (var kv in _pending)
                {
                    if ((now - kv.Value.SentAt).TotalMilliseconds >= RETRANSMIT_MS)
                    {
                        if (kv.Value.Retries >= MAX_RETRIES)
                            toRemove.Add(kv.Key);
                        else
                            toRetry.Add(kv.Key);
                    }
                }

                foreach (var seq in toRemove)
                {
                    Plugin.Log.LogWarning($"[Net] Reliable seq={seq} dropped after max retries.");
                    _pending.Remove(seq);
                }

                foreach (var seq in toRetry)
                {
                    var p = _pending[seq];
                    _pending[seq] = new PendingReliable { Data = p.Data, SentAt = now, Retries = p.Retries + 1 };
                    RawSend(p.Data);
                }
            }

            // ── Keepalive ─────────────────────────────────────────────────────
            if ((now - _lastSend).TotalMilliseconds > KEEPALIVE_MS)
                RawSend(new[] { CTRL_KEEPALIVE });

            // ── Timeout check ─────────────────────────────────────────────────
            if ((now - _lastReceive).TotalMilliseconds > TIMEOUT_MS)
            {
                Plugin.Log.LogWarning("[Net] Peer timed out.");
                _connected = false;
                ConnectionHUD.ShowDisconnected("Timed out");
                if (PauseManager.state != PauseManager.State.Paused)
                    PauseManager.Pause();
                return;
            }

            // ── Periodic ping for latency display ─────────────────────────────
            float t = UnityEngine.Time.realtimeSinceStartup;
            if (t > _nextPingTime)
            {
                _nextPingTime    = t + PING_INTERVAL_SEC;
                _pingSentAt      = now;
                _pingSentPending = true;
                RawSend(new[] { (byte)PacketType.Ping });
            }
        }

        // ── Send helpers (main thread) ────────────────────────────────────────

        public void SendPlayerState (ref PlayerStatePacket  p) => SendUnreliable(PacketType.PlayerState,  ref p);
        public void SendInputFrame  (ref InputFramePacket   p) => SendUnreliable(PacketType.InputFrame,   ref p);
        public void SendEnemyState  (ref EnemyStatePacket   p) => SendUnreliable(PacketType.EnemyState,   ref p);
        public void SendWeaponEvent (ref WeaponEventPacket  p) => SendReliable  (PacketType.WeaponEvent,  ref p);
        public void SendDamageEvent (ref DamageEventPacket  p) => SendReliable  (PacketType.DamageEvent,  ref p);
        public void SendSceneChange (ref SceneChangePacket  p) => SendReliable  (PacketType.SceneChange,  ref p);
        public void SendLobbySync   (ref LobbySyncPacket    p) => SendReliable  (PacketType.LobbySync,    ref p);
        public void SendReviveVisual(ref ReviveVisualPacket p) => SendReliable  (PacketType.ReviveVisual, ref p);
        public void SendBattleAssistStats(ref BattleAssistStatsPacket p) => SendReliable(PacketType.BattleAssistStats, ref p);
        public void SendMapDialogue (ref MapDialoguePacket  p) => SendReliable  (PacketType.MapDialogue,  ref p);

        private void SendUnreliable<T>(PacketType type, ref T pkt) where T : struct, IPacket
        {
            if (!_connected) return;
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)type);
            pkt.Write(_sendWriter);
            _sendWriter.Flush();
            RawSend(_sendBuf.GetBuffer(), (int)_sendBuf.Length);
        }

        private void SendReliable<T>(PacketType type, ref T pkt) where T : struct, IPacket
        {
            if (!_connected) return;
            ushort seq = _sendSeq++;
            _sendBuf.SetLength(0);
            _sendBuf.Position = 0;
            _sendWriter.Write((byte)((int)type | 0x80));   // bit 7 = reliable flag
            _sendWriter.Write((byte)(seq & 0xFF));
            _sendWriter.Write((byte)(seq >> 8));
            pkt.Write(_sendWriter);
            _sendWriter.Flush();
            // Copy to own array — needed for retransmit
            var data = new byte[(int)_sendBuf.Length];
            Buffer.BlockCopy(_sendBuf.GetBuffer(), 0, data, 0, data.Length);
            _pending[seq] = new PendingReliable { Data = data, SentAt = DateTime.UtcNow, Retries = 0 };
            RawSend(data);
        }

        // ── Packet processing (main thread, called from Poll) ─────────────────

        private void ProcessRaw(RawPacket raw)
        {
            var data = raw.Data;
            var from = raw.From;
            if (data == null || data.Length == 0) return;

            byte first = data[0];

            // ── Host handshake ────────────────────────────────────────────────
            if (_isHost && !_connected)
            {
                if (first != CTRL_CONNECT_REQ) return;
                if (data.Length < 1 + CONNECTION_KEY.Length) return;
                string key = System.Text.Encoding.ASCII.GetString(data, 1, data.Length - 1);
                if (key != CONNECTION_KEY) return;

                _remote      = from;
                _connected   = true;
                _lastReceive = DateTime.UtcNow;
                // Send ACK directly (main thread)
                try { _udp.Send(new[] { CTRL_CONNECT_ACK }, 1, from); } catch { }
                Plugin.Log.LogInfo($"[Net] Client connected from {from}");
                OnConnected();
                return;
            }

            // ── Only accept packets from our peer ─────────────────────────────
            if (_remote == null) return;
            if (!from.Address.Equals(_remote.Address) || from.Port != _remote.Port) return;

            _lastReceive = DateTime.UtcNow;

            // ── Client: waiting for CONNECT_ACK ───────────────────────────────
            if (_connecting && !_connected)
            {
                if (first != CTRL_CONNECT_ACK) return;
                _connecting = false;
                _connected  = true;
                Plugin.Log.LogInfo($"[Net] Connected to {_remote}");
                OnConnected();
                return;
            }

            if (!_connected) return;

            // ── Control messages ──────────────────────────────────────────────
            switch (first)
            {
                case CTRL_DISCONNECT:
                    _connected = false;
                    Plugin.Log.LogWarning("[Net] Remote disconnected.");
                    ConnectionHUD.ShowDisconnected("Remote disconnected");
                    if (PauseManager.state != PauseManager.State.Paused)
                        PauseManager.Pause();
                    return;

                case CTRL_KEEPALIVE:
                    return;

                case CTRL_ACK:
                    if (data.Length >= 3)
                    {
                        ushort ackSeq = (ushort)(data[1] | (data[2] << 8));
                        _pending.Remove(ackSeq);
                    }
                    return;
            }

            // ── Game packet ───────────────────────────────────────────────────
            bool   reliable = (first & 0x80) != 0;
            byte   typeRaw  = (byte)(first & 0x7F);
            int    offset;

            if (reliable)
            {
                if (data.Length < 3) return;
                ushort seq = (ushort)(data[1] | (data[2] << 8));
                // ACK it
                try { _udp.Send(new byte[] { CTRL_ACK, (byte)(seq & 0xFF), (byte)(seq >> 8) }, 3, _remote); }
                catch { }
                // Dedup
                if (_seenSeqs.ContainsKey(seq)) return;
                _seenSeqs[seq] = true;
                if (_seenSeqs.Count > 512) _seenSeqs.Clear();
                offset = 3;
            }
            else
            {
                offset = 1;
            }

            // Handle Ping/Pong inline
            var ptype = (PacketType)typeRaw;

            if (ptype == PacketType.Ping)
            {
                RawSend(new[] { (byte)PacketType.Pong });
                return;
            }

            if (ptype == PacketType.Pong && _pingSentPending)
            {
                _pingSentPending = false;
                Latency = (int)(DateTime.UtcNow - _pingSentAt).TotalMilliseconds;
                ConnectionHUD.UpdatePing(Latency);
                return;
            }

            // Dispatch to PacketDispatcher
            using (var ms = new MemoryStream(data, offset, data.Length - offset, false))
            using (var r  = new BinaryReader(ms))
                PacketDispatcher.Dispatch(ptype, r, byte.MaxValue);
        }

        // ── Connection established callback ───────────────────────────────────

        private void OnConnected()
        {
            _lastReceive = DateTime.UtcNow;
            ConnectionHUD.Show();

            if (_isHost)
            {
                var pkt = new SessionStartPacket
                {
                    Flags        = 1,
                    CurrentLevel = (int)SceneLoader.CurrentLevel,
                    SaveRevision = 0,
                    CurrentTick  = MultiplayerSession.Tick,
                    RngSeed      = RngSync.CurrentSeed,
                };
                SendReliable(PacketType.SessionStart, ref pkt);
            }
        }

        // ── Low-level send ────────────────────────────────────────────────────

        private void RawSend(byte[] data)
        {
            if (_udp == null || _remote == null) return;
            try
            {
                _udp.Send(data, data.Length, _remote);
                _lastSend = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                if (_running) Plugin.Log.LogError($"[Net] Send error: {ex.Message}");
            }
        }

        private void RawSend(byte[] data, int length)
        {
            if (_udp == null || _remote == null) return;
            try
            {
                _udp.Send(data, length, _remote);
                _lastSend = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                if (_running) Plugin.Log.LogError($"[Net] Send error: {ex.Message}");
            }
        }

        private void SendConnectRequest()
        {
            if (_udp == null || _remote == null) return;
            var keyBytes = System.Text.Encoding.ASCII.GetBytes(CONNECTION_KEY);
            var data = new byte[1 + keyBytes.Length];
            data[0] = CTRL_CONNECT_REQ;
            Buffer.BlockCopy(keyBytes, 0, data, 1, keyBytes.Length);
            try { _udp.Send(data, data.Length, _remote); } catch { }
        }

        // ── Receive thread ────────────────────────────────────────────────────

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var ep   = new IPEndPoint(IPAddress.Any, 0);
                    var data = _udp.Receive(ref ep);
                    lock (_recvLock)
                        _recvQueue.Enqueue(new RawPacket { Data = data, From = ep });
                }
                catch (SocketException ex) when (
                    ex.SocketErrorCode == SocketError.Interrupted      ||
                    ex.SocketErrorCode == SocketError.ConnectionReset  ||
                    ex.SocketErrorCode == SocketError.ConnectionAborted)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running) Plugin.Log.LogError($"[Net] Recv error: {ex.Message}");
                }
            }
        }
    }
}
