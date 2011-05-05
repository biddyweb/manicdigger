﻿using System;
using System.Collections.Generic;
using System.Text;
using ManicDigger;
using System.IO;
using System.Net;
using System.Net.Sockets;
using OpenTK;
using System.Text.RegularExpressions;
using ProtoBuf;
using System.Diagnostics;
using System.Windows.Forms;

namespace GameModeFortress
{
    public interface INetworkPacketReceived
    {
        bool NetworkPacketReceived(PacketServer packet);
    }
    public interface INetworkClientFortress : INetworkClient
    {
        void SendPacketClient(PacketClient packetClient);
    }
    public class NetworkClientFortress : INetworkClientFortress
    {
        [Inject]
        public IMap d_Map;
        public IMapStoragePortion d_MapStoragePortion;
        [Inject]
        public IClients d_Clients;
        [Inject]
        public IAddChatLine d_Chatlines;
        [Inject]
        public ILocalPlayerPosition d_Position;
        [Inject]
        public INetworkPacketReceived d_NetworkPacketReceived;
        [Inject]
        public ICompression d_Compression;
        [Inject]
        public InfiniteMapChunked2d d_Heightmap;
        [Inject]
        public IShadows d_Shadows;
        [Inject]
        public IResetMap d_ResetMap;
        [Inject]
        public GameDataCsv d_GameData;
        [Inject]
        public GetFileStream d_GetFile;
        public event EventHandler<MapLoadedEventArgs> MapLoaded;
        public bool ENABLE_FORTRESS = true;
        public void Connect(string serverAddress, int port, string username, string auth)
        {
            main = new Socket(AddressFamily.InterNetwork,
                   SocketType.Stream, ProtocolType.Tcp);

            iep = new IPEndPoint(IPAddress.Any, port);
            main.Connect(serverAddress, port);
            this.username = username;
            this.auth = auth;
            byte[] n = CreateLoginPacket(username, auth);
            main.Send(n);
        }
        string username;
        string auth;
        private byte[] CreateLoginPacket(string username, string verificationKey)
        {
            PacketClientIdentification p = new PacketClientIdentification()
            {
                Username = username,
                MdProtocolVersion = GameVersion.Version,
                VerificationKey = verificationKey
            };
            return Serialize(new PacketClient() { PacketId = ClientPacketId.PlayerIdentification, Identification = p });
        }
        IPEndPoint iep;
        Socket main;
        public void SendPacket(byte[] packet)
        {
            try
            {
                main.BeginSend(packet, 0, packet.Length, SocketFlags.None, EmptyCallback, new object());
            }
            catch
            {
                Console.WriteLine("SendPacket error");
            }
        }
        void EmptyCallback(IAsyncResult result)
        {
        }
        public void Disconnect()
        {
            ChatLog("---Disconnected---");
            main.Disconnect(false);
        }
        DateTime lastpositionsent;
        public void SendSetBlock(Vector3 position, BlockSetMode mode, int type, int materialslot)
        {
            PacketClientSetBlock p = new PacketClientSetBlock()
            {
                X = (int)position.X,
                Y = (int)position.Y,
                Z = (int)position.Z,
                Mode = (mode == BlockSetMode.Create ? (byte)1 : (byte)0),
                BlockType = type,
                MaterialSlot = materialslot,
            };
            SendPacket(Serialize(new PacketClient() { PacketId = ClientPacketId.SetBlock, SetBlock = p }));
        }
        public void SendPacketClient(PacketClient packet)
        {
            SendPacket(Serialize(packet));
        }
        public void SendChat(string s)
        {
            PacketClientMessage p = new PacketClientMessage() { Message = s };
            SendPacket(Serialize(new PacketClient() { PacketId = ClientPacketId.Message, Message = p }));
        }
        private byte[] Serialize(PacketClient p)
        {
            MemoryStream ms = new MemoryStream();
            Serializer.SerializeWithLengthPrefix(ms, p, PrefixStyle.Base128);
            return ms.ToArray();
        }
        /// <summary>
        /// This function should be called in program main loop.
        /// It exits immediately.
        /// </summary>
        public void Process()
        {
            currentTime = DateTime.UtcNow;
            stopwatch.Reset();
            stopwatch.Start();
            if (main == null)
            {
                return;
            }
            bool again = false;
            for (; ; )
            {
                if (!(main.Poll(0, SelectMode.SelectRead)))
                {
                    if (!again)
                    {
                        again = true;
                        goto process;
                    }
                    break;
                }
                byte[] data = new byte[1024];
                int recv;
                try
                {
                    recv = main.Receive(data);
                }
                catch
                {
                    recv = 0;
                }
                if (recv == 0)
                {
                    //disconnected
                    return;
                }
                for (int i = 0; i < recv; i++)
                {
                    received.Add(data[i]);
                }
            process:
                for (; ; )
                {
                    if (received.Count < 4)
                    {
                        break;
                    }
                    if (stopwatch.ElapsedMilliseconds >= maxMiliseconds)
                    {
                        goto end;
                    }
                    int bytesRead;
                    bytesRead = TryReadPacket();
                    if (bytesRead > 0)
                    {
                        received.RemoveRange(0, bytesRead);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        end:
            if (spawned && ((DateTime.UtcNow - lastpositionsent).TotalSeconds > 0.1))
            {
                lastpositionsent = DateTime.UtcNow;
                SendPosition(d_Position.LocalPlayerPosition, d_Position.LocalPlayerOrientation);
            }
        }
        public int mapreceivedsizex;
        public int mapreceivedsizey;
        public int mapreceivedsizez;
        Vector3 lastsentposition;
        public void SendPosition(Vector3 position, Vector3 orientation)
        {
            PacketClientPositionAndOrientation p = new PacketClientPositionAndOrientation()
            {
                PlayerId = 255,//self
                X = (int)((position.X) * 32),
                Y = (int)((position.Y + CharacterPhysics.characterheight) * 32),
                Z = (int)(position.Z * 32),
                Heading = HeadingByte(orientation),
                Pitch = PitchByte(orientation),
            };
            SendPacket(Serialize(new PacketClient() { PacketId = ClientPacketId.PositionandOrientation, PositionAndOrientation = p }));
            lastsentposition = position;
        }
        public static byte HeadingByte(Vector3 orientation)
        {
            return (byte)((((orientation.Y) % (2 * Math.PI)) / (2 * Math.PI)) * 256);
        }
        public static byte PitchByte(Vector3 orientation)
        {
            double xx = (orientation.X + Math.PI) % (2 * Math.PI);
            xx = xx / (2 * Math.PI);
            return (byte)(xx * 256);
        }
        bool spawned = false;
        string serverName = "";
        string serverMotd = "";
        public string ServerName { get { return serverName; } set { serverName = value; } }
        public string ServerMotd { get { return serverMotd; } set { serverMotd = value; } }
        public bool allowfreemove = true;
        public bool AllowFreemove { get { return allowfreemove; } set { allowfreemove = value; } }
        public int LocalPlayerId = 255;
        Stopwatch stopwatch = new Stopwatch();
        public int maxMiliseconds = 3;
        private int TryReadPacket()
        {
            MemoryStream ms = new MemoryStream(received.ToArray());
            if (received.Count == 0)
            {
                return 0;
            }
            int packetLength;
            int lengthPrefixLength;
            bool packetLengthOk = Serializer.TryReadLengthPrefix(ms, PrefixStyle.Base128, out packetLength);
            lengthPrefixLength = (int)ms.Position;
            if (!packetLengthOk || lengthPrefixLength + packetLength > ms.Length)
            {
                return 0;
            }
            ms.Position = 0;
            PacketServer packet = Serializer.DeserializeWithLengthPrefix<PacketServer>(ms, PrefixStyle.Base128);
            if (Debugger.IsAttached
                && packet.PacketId != ServerPacketId.PositionUpdate
                && packet.PacketId != ServerPacketId.OrientationUpdate
                && packet.PacketId != ServerPacketId.PlayerPositionAndOrientation
                && packet.PacketId != ServerPacketId.ExtendedPacketTick
                && packet.PacketId != ServerPacketId.Chunk
                && packet.PacketId != ServerPacketId.Ping)
            {
                Console.WriteLine(Enum.GetName(typeof(MinecraftServerPacketId), packet.PacketId));
            }
            switch (packet.PacketId)
            {
                case ServerPacketId.ServerIdentification:
                    {
                        string invalidversionstr = "Invalid game version. Local: {0}, Server: {1}. Do you want to connect anyway?";
                        {
                            string servergameversion = packet.Identification.MdProtocolVersion;
                            if (servergameversion != GameVersion.Version)
                            {
                                for (int i = 0; i < 5; i++)
                                {
                                    System.Windows.Forms.Cursor.Show();
                                    System.Threading.Thread.Sleep(100);
                                    Application.DoEvents();
                                }
                                string q = string.Format(invalidversionstr, GameVersion.Version, servergameversion);
                                var result = System.Windows.Forms.MessageBox.Show(q, "Invalid version", System.Windows.Forms.MessageBoxButtons.OKCancel);
                                if (result == System.Windows.Forms.DialogResult.Cancel)
                                {
                                    throw new Exception(q);
                                }
                                for (int i = 0; i < 10; i++)
                                {
                                    System.Windows.Forms.Cursor.Hide();
                                    System.Threading.Thread.Sleep(100);
                                    Application.DoEvents();
                                }
                            }
                        }
                        this.serverName = packet.Identification.ServerName;
                        this.ServerMotd = packet.Identification.ServerMotd;
                        this.AllowFreemove = !packet.Identification.DisallowFreemove;
                        ChatLog("---Connected---");
                        List<byte[]> needed = new List<byte[]>();
                        foreach (byte[] b in packet.Identification.UsedBlobsMd5)
                        {
                            if (!IsBlob(b)) { needed.Add(b); }
                        }
                        SendRequestBlob(needed);
                        if (packet.Identification.MapSizeX != d_Map.Map.MapSizeX
                            || packet.Identification.MapSizeY != d_Map.Map.MapSizeY
                            || packet.Identification.MapSizeZ != d_Map.Map.MapSizeZ)
                        {
                            d_ResetMap.Reset(packet.Identification.MapSizeX,
                                packet.Identification.MapSizeY,
                                packet.Identification.MapSizeZ);
                        }
                    }
                    break;
                case ServerPacketId.Ping:
                    {
                    }
                    break;
                case ServerPacketId.LevelInitialize:
                    {
                        ReceivedMapLength = 0;
                        InvokeMapLoadingProgress(0, 0, "Connecting...");
                    }
                    break;
                case ServerPacketId.LevelDataChunk:
                    {
                        MapLoadingPercentComplete = packet.LevelDataChunk.PercentComplete;
                        MapLoadingStatus = packet.LevelDataChunk.Status;
                        InvokeMapLoadingProgress(MapLoadingPercentComplete, (int)ReceivedMapLength, MapLoadingStatus);
                    }
                    break;
                case ServerPacketId.LevelFinalize:
                    {
                        d_GameData.Load(MyStream.ReadAllLines(d_GetFile.GetFile("blocks.csv")),
                            MyStream.ReadAllLines(d_GetFile.GetFile("defaultmaterialslots.csv")));
                        if (MapLoaded != null)
                        {
                            MapLoaded.Invoke(this, new MapLoadedEventArgs() { });
                        }
                        loadedtime = DateTime.Now;
                    }
                    break;
                case ServerPacketId.SetBlock:
                    {
                        int x = packet.SetBlock.X;
                        int y = packet.SetBlock.Y;
                        int z = packet.SetBlock.Z;
                        int type = packet.SetBlock.BlockType;
                        try { d_Map.SetTileAndUpdate(new Vector3(x, y, z), type); }
                        catch { Console.WriteLine("Cannot update tile!"); }
                    }
                    break;
                case ServerPacketId.SpawnPlayer:
                    {
                        int playerid = packet.SpawnPlayer.PlayerId;
                        string playername = packet.SpawnPlayer.PlayerName;
                        connectedplayers.Add(new ConnectedPlayer() { name = playername, id = playerid });
                        d_Clients.Players[playerid] = new Player();
                        d_Clients.Players[playerid].Name = playername;
                        ReadAndUpdatePlayerPosition(packet.SpawnPlayer.PositionAndOrientation, playerid);
                        if (playerid == 255)
                        {
                            spawned = true;
                        }
                    }
                    break;
                case ServerPacketId.PlayerPositionAndOrientation:
                    {
                        int playerid = packet.PositionAndOrientation.PlayerId;
                        ReadAndUpdatePlayerPosition(packet.PositionAndOrientation.PositionAndOrientation, playerid);
                    }
                    break;
                case ServerPacketId.DespawnPlayer:
                    {
                        int playerid = packet.DespawnPlayer.PlayerId;
                        for (int i = 0; i < connectedplayers.Count; i++)
                        {
                            if (connectedplayers[i].id == playerid)
                            {
                                connectedplayers.RemoveAt(i);
                            }
                        }
                        d_Clients.Players.Remove(playerid);
                    }
                    break;
                case ServerPacketId.Message:
                    {
                        d_Chatlines.AddChatline(packet.Message.Message);
                        ChatLog(packet.Message.Message);
                    }
                    break;
                case ServerPacketId.DisconnectPlayer:
                    {
                        throw new Exception(packet.DisconnectPlayer.DisconnectReason);
                    }
                case ServerPacketId.Chunk:
                    {
                        var p = packet.Chunk;
                        byte[] decompressedchunk = d_Compression.Decompress(p.CompressedChunk);
                        byte[, ,] receivedchunk = new byte[p.SizeX, p.SizeY, p.SizeZ];
                        {
                            BinaryReader br2 = new BinaryReader(new MemoryStream(decompressedchunk));
                            for (int zz = 0; zz < p.SizeZ; zz++)
                            {
                                for (int yy = 0; yy < p.SizeY; yy++)
                                {
                                    for (int xx = 0; xx < p.SizeX; xx++)
                                    {
                                        receivedchunk[xx, yy, zz] = br2.ReadByte();
                                    }
                                }
                            }
                        }
                        d_MapStoragePortion.SetMapPortion(p.X, p.Y, p.Z, receivedchunk);
                        for (int xx = 0; xx < 2; xx++)
                        {
                            for (int yy = 0; yy < 2; yy++)
                            {
                                for (int zz = 0; zz < 2; zz++)
                                {
                                    d_Shadows.OnSetChunk(p.X + 16 * xx, p.Y + 16 * yy, p.Z + 16 * zz);//todo
                                }
                            }
                        }
                        ReceivedMapLength += lengthPrefixLength + packetLength;
                    }
                    break;
                case ServerPacketId.HeightmapChunk:
                    {
                        var p = packet.HeightmapChunk;
                        byte[] decompressedchunk = d_Compression.Decompress(p.CompressedHeightmap);
                        for (int xx = 0; xx < p.SizeX; xx++)
                        {
                            for (int yy = 0; yy < p.SizeY; yy++)
                            {
                                int height = decompressedchunk[MapUtil.Index2d(xx, yy, p.SizeX)];
                                d_Heightmap.SetBlock(p.X + xx, p.Y + yy, height);
                            }
                        }
                    }
                    break;
                default:
                    break;
            }
            {
                bool handled = d_NetworkPacketReceived.NetworkPacketReceived(packet);
                if (!handled)
                {
                    //Console.WriteLine("Invalid packet id: " + packet.PacketId);
                }
            }
            LastReceived = currentTime;
            return lengthPrefixLength + packetLength;
        }
        DateTime currentTime;
        private void SendRequestBlob(List<byte[]> needed)
        {
            PacketClientRequestBlob p = new PacketClientRequestBlob() { RequestBlobMd5 = needed };
            SendPacket(Serialize(new PacketClient() { PacketId = ClientPacketId.RequestBlob, RequestBlob = p }));
        }
        bool IsBlob(byte[] hash)
        {
            return false;
            //return File.Exists(Path.Combine(gamepathblobs, BytesToHex(hash)));
        }
        byte[] GetBlob(byte[] hash)
        {
            return null;
        }
        string BytesToHex(byte[] ba)
        {
            StringBuilder sb = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
            {
                sb.AppendFormat("{0:x2}", b);
            }
            return sb.ToString();
        }
        public int ReceivedMapLength = 0;
        DateTime loadedtime;
        private void InvokeMapLoadingProgress(int progressPercent, int progressBytes, string status)
        {
            if (MapLoadingProgress != null)
            {
                MapLoadingProgress(this, new MapLoadingProgressEventArgs()
                {
                    ProgressPercent = progressPercent,
                    ProgressBytes = progressBytes,
                    ProgressStatus = status,
                });
            }
        }
        public bool ENABLE_CHATLOG = true;
        public string gamepathlogs = Path.Combine(GameStorePath.GetStorePath(), "Logs");
        public string gamepathblobs = Path.Combine(GameStorePath.GetStorePath(), "Blobs");
        private void ChatLog(string p)
        {
            if (!ENABLE_CHATLOG)
            {
                return;
            }
            if (!Directory.Exists(gamepathlogs))
            {
                Directory.CreateDirectory(gamepathlogs);
            }
            string filename = Path.Combine(gamepathlogs, MakeValidFileName(serverName) + ".txt");
            try
            {
                File.AppendAllText(filename, string.Format("{0} {1}\n", DateTime.Now, p));
            }
            catch
            {
                Console.WriteLine("Cannot write to chat log file {0}.", filename);
            }
        }
        private static string MakeValidFileName(string name)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidReStr = string.Format(@"[{0}]", invalidChars);
            return Regex.Replace(name, invalidReStr, "_");
        }
        private void UpdatePositionDiff(byte playerid, Vector3 v)
        {
            if (playerid == 255)
            {
                d_Position.LocalPlayerPosition += v;
                spawned = true;
            }
            else
            {
                if (!d_Clients.Players.ContainsKey(playerid))
                {
                    d_Clients.Players[playerid] = new Player();
                    d_Clients.Players[playerid].Name = "invalid";
                    //throw new Exception();
                    InvalidPlayerWarning(playerid);
                }
                d_Clients.Players[playerid].Position += v;
            }
        }
        private static void InvalidPlayerWarning(int playerid)
        {
            Console.WriteLine("Position update of nonexistent player {0}." + playerid);
        }
        private void ReadAndUpdatePlayerPosition(PositionAndOrientation positionAndOrientation, int playerid)
        {
            float x = (float)((double)positionAndOrientation.X / 32);
            float y = (float)((double)positionAndOrientation.Y / 32);
            float z = (float)((double)positionAndOrientation.Z / 32);
            byte heading = positionAndOrientation.Heading;
            byte pitch = positionAndOrientation.Pitch;
            Vector3 realpos = new Vector3(x, y, z);
            if (playerid == 255)
            {
                if (!enablePlayerUpdatePosition.ContainsKey(playerid) || enablePlayerUpdatePosition[playerid])
                {
                    d_Position.LocalPlayerPosition = realpos;
                }
                spawned = true;
            }
            else
            {
                if (!d_Clients.Players.ContainsKey(playerid))
                {
                    d_Clients.Players[playerid] = new Player();
                    d_Clients.Players[playerid].Name = "invalid";
                    InvalidPlayerWarning(playerid);
                }
                if (!enablePlayerUpdatePosition.ContainsKey(playerid) || enablePlayerUpdatePosition[playerid])
                {
                    d_Clients.Players[playerid].Position = realpos;
                }
                d_Clients.Players[playerid].Heading = heading;
                d_Clients.Players[playerid].Pitch = pitch;
            }
        }
        List<byte> received = new List<byte>();
        public void Dispose()
        {
            if (main != null)
            {
                main.Disconnect(false);
                main = null;
            }
        }
        int MapLoadingPercentComplete;
        string MapLoadingStatus;
        class ConnectedPlayer
        {
            public int id;
            public string name;
        }
        List<ConnectedPlayer> connectedplayers = new List<ConnectedPlayer>();
        public IEnumerable<string> ConnectedPlayers()
        {
            foreach (ConnectedPlayer p in connectedplayers)
            {
                yield return p.name;
            }
        }
        #region IClientNetwork Members
        public event EventHandler<MapLoadingProgressEventArgs> MapLoadingProgress;
        #endregion
        Dictionary<int, bool> enablePlayerUpdatePosition = new Dictionary<int, bool>();
        #region INetworkClient Members
        public Dictionary<int, bool> EnablePlayerUpdatePosition { get { return enablePlayerUpdatePosition; } set { enablePlayerUpdatePosition = value; } }
        #endregion
        public DateTime LastReceived { get; set; }
    }
}
