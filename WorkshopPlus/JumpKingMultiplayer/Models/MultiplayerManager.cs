using EntityComponent;
using JumpKing.GameManager;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JumpKingMultiplayer.Extensions;
using JumpKing;
using JumpKingMultiplayer.Helpers;
using JumpKingMultiplayer.Models.Infos;
using System.Reflection.Emit;
using Steamworks;

namespace JumpKingMultiplayer.Models
{
    public enum PlayerSpriteEffect
    {
        None,
        FlipH,
        FlipV,
    }

    public enum PlayerSpriteState
    {
        JumpBounce,
        JumpUp,
        JumpFall,
        JumpSplat,
        Idle,
        WalkOne,
        WalkSmear,
        WalkTwo,
        JumpCharge,
        LookUp,
        StretchOne,
        StretchSmear,
        StretchTwo,
    }

    public static class Parser
    {
        public static T FromString<T>(string data)
        {
            return JsonConvert.DeserializeObject<T>(data);
        }

        public static string ToString<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj);
        }
    }

    public struct TrackData
    {
        public int screenIndex1 { get; set; }
        public float posX { get; set; }
        public float posY { get; set; }
        public ulong? levelId { get; set; }
        public int colorIdx { get; set; }
        public PlayerSpriteEffect flip { get; set; }
        public PlayerSpriteState sprite { get; set; }
        public int skippedFrames { get; set; }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public float Diff(TrackData other)
        {
            float d = 0;
            d += Math.Abs(other.posX - this.posX);
            d += Math.Abs(other.posY - this.posY);
            if (other.sprite != this.sprite)
            {
                d += 10;
            }
            if (other.flip != this.flip)
            {
                d += 10;
            }
            if (other.levelId != this.levelId)
            {
                d += 10;
            }
            return d;
        }

        public override string ToString()
        {
            return Parser.ToString(this);
        }
    }

    public class MultiplayerManager
    {
        public MultiplayerManager() : base()
        {
        }

        private bool _alreadyDrawedPlayers = false;

        public static void DrawPlayers()
        {
            if (MultiplayerManager.instance._alreadyDrawedPlayers) { return; }
            MultiplayerManager.instance.Players.ForEach(x => x.DrawFromOutside());
            MultiplayerManager.instance._alreadyDrawedPlayers = true;
        }

        public static MultiplayerManager instance { get; set; }

        internal List<GhostPlayer> Players = new List<GhostPlayer>();

        internal TrackData lastTrackData = new TrackData { posX = 500, posY = 500, };

        public void Init()
        {
            leaderBoard = new LeaderBoard();
            proximityPlayers = new ProximityPlayers();
            inviteInfo = new InviteInfo();
            emptyLobbyInviteInfo = new EmptyLobbyInviteInfo();

            string ConfigName = GetConfigName();
            string name = ConfigName.Length > 0 ? ConfigName : DefaultPlayerName;

            Debug.WriteLine($"Your player name is {name}");
            Debug.WriteLine($"Your ip:port is : ");

            if (ModEntry.Preferences.LobbySettings.CreateLobbyOnLaunch)
            {
                MultiplayerManager.instance.CreateLobby();
            }
        }

        public void SendGameplayUpdateToOthers()
        {
            if (!IsOnline()) { return; }
            // The in-game character data grabbed directly from Jump King (x,y positions, screenID etc.)
            var trackData = PlayerSpriteStateExtensions.FromPlayer(GameLoop.m_player);

            // If the current player data is different than the last
            if (lastTrackData.Diff(trackData) > 1)
            {
                SendGameplayStateToAll(trackData);
                lastTrackData = trackData;
            }
        }

        public async void SendGameplayStateToAll<T>(T message)
        {
            ArraySegment<byte> messageBytes;
            try
            {
                messageBytes = new ArraySegment<byte>(Encoding.ASCII.GetBytes(Parser.ToString(message)));
            }
            catch (Exception e)
            {
                throw;
            }
            
            foreach (IPEndPoint player in LobbyPlayers)
            {
                await gameUDPSocket.SendToAsync(messageBytes, SocketFlags.None, (EndPoint)player);
            }
        }

        public async void GetGameplayUpdates()
        {
            if (!IsOnline()) { return; }
            bool unreadPackets;
            try {
                unreadPackets = gameUDPSocket.Available > 0;
            } catch (SocketException e) { 
                Debug.WriteLine(e.Message);
                return;
            }
            if (unreadPackets)
            {
                ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[100]);
                IPEndPoint senderEndpoint = new IPEndPoint(GetConfigHostEndPoint().Address, 0);
                EndPoint ep = (EndPoint)senderEndpoint;
                string messageStr;
                try
                {
                    await gameUDPSocket.ReceiveFromAsync(buffer, SocketFlags.None, ep);
                    messageStr = System.Text.Encoding.ASCII.GetString(buffer.Array);

                    TrackData trackData = Parser.FromString<TrackData>(messageStr);
                    UpdatePlayerGameplayState(senderEndpoint, trackData);
                } catch (NullReferenceException e) {
                    Debug.WriteLine(e.Message);
                }
                catch (ArgumentNullException e) {
                    Debug.WriteLine(e.Message);
                } catch (SocketException e){
                    Debug.WriteLine(e.Message);
                }

                // idk how to get the ping
                //SteamNetworkingIdentity id = new SteamNetworkingIdentity();
                //id.SetSteamID(remoteId);
                //SteamNetworkingMessages.GetSessionConnectionInfo(ref id, out _, out SteamNetConnectionRealTimeStatus_t realTimeStatus);
                //Debug.WriteLine($"[UPDATE] {remoteId} - {realTimeStatus.m_eState} {realTimeStatus.m_nPing} {realTimeStatus.m_flConnectionQualityLocal}");
                
            }
        }

        public void CreateGhostPlayer(IPEndPoint endpoint, TrackData data)
        {
            var name = GetConfigName();
            if (name.Trim() == "")
            {
                name = Guid.NewGuid().ToString().Split('-')[0];
            }
            // custom color per player
            var color = data.colorIdx != 0 ? data.colorIdx : Players.Count() + 1;
            lock (_LobbyPlayersThreadLock)
            {
                Players.Add(new GhostPlayer(name, endpoint, color));
            }
        }

        public void UpdatePlayerGameplayState(IPEndPoint endpoint, TrackData data)
        {
            var idx = Players.FindIndex(x => x.endpoint == endpoint);
            if (idx == -1)
            {
                CreateGhostPlayer(endpoint, data);
                idx = Players.Count - 1;
            }
            lock (_LobbyPlayersThreadLock)
            {
                var player = Players[idx];
                player.ScreenIndex1 = data.screenIndex1;
                player.LevelId = data.levelId;
                player.tracker.Track(data);
            }
        }

        internal LeaderBoard leaderBoard { get; set; }
        internal ProximityPlayers proximityPlayers { get; set; }
        internal InviteInfo inviteInfo { get; set; }
        internal EmptyLobbyInviteInfo emptyLobbyInviteInfo { get; set; }

        private static string LeaveRequest = "Leave";
        private static string UpdateLobbyRequest = "UpdateLobby";

        private static async void AcceptConnections() {
            while (IsOnline()) {
                try
                {
                    Socket client = await lobbyTCPSocket.AcceptAsync();
                    lock (_LobbyPlayersThreadLock)
                    {
                        instance.PlayersTCPSockets.Add(client);
                        instance.LobbyPlayers.Add((IPEndPoint)client.LocalEndPoint);
                    }
                }
                catch (SocketException e) {
                    Debug.WriteLine(e.Message);
                }
                catch (ObjectDisposedException e) {
                    Debug.WriteLine(e.Message);
                }
            }
        }

        private static async void ListenForHost() {
            while (IsOnline()){
                ArraySegment<byte> buffer = new ArraySegment<byte>();
                await lobbyTCPSocket.ReceiveAsync(buffer, SocketFlags.None);

                string message = System.Text.Encoding.ASCII.GetString(buffer.Array).Trim(new char[] {(char)0});

                if (message.Equals(LeaveRequest)) {
                    MultiplayerManager.instance.LeaveLobby();
                    return;
                } else if (message.Contains(UpdateLobbyRequest)) { 
                    string endpointsData = message.Substring(UpdateLobbyRequest.Length);
                    MultiplayerManager.instance.UpdateLobbyMemberIds(endpointsData);
                }
            }
        }

        public async void OnOwnerLeave()
        {
            foreach (Socket player in PlayersTCPSockets)
            {
                byte[] buffer = Encoding.ASCII.GetBytes(LeaveRequest);
                await player.SendAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
            }
        }

        public void UpdateLobbyMemberIds(string lobbyPlayersData)
        {
            List<IPEndPoint> updatedLobbyPlayers = new List<IPEndPoint>();

            while (lobbyPlayersData.Contains("|")) {
                int separatorIndex = lobbyPlayersData.IndexOf("|");
                string data = lobbyPlayersData.Substring(0, separatorIndex - 1);

                int colonIndex = data.IndexOf(":");
                IPAddress ip = IPAddress.Parse(data.Substring(0, colonIndex - 1));
                int port = Int32.Parse(data.Substring(colonIndex + 1, separatorIndex - colonIndex));

                updatedLobbyPlayers.Add(new IPEndPoint(ip, port));

                lobbyPlayersData = lobbyPlayersData.Substring(separatorIndex + 1);
            }

            lock (_LobbyPlayersThreadLock)
            {
                LobbyPlayers.Clear();
                LobbyPlayers = updatedLobbyPlayers;
            }
            Debug.WriteLine($"[MP] Lobby members updated: {LobbyPlayers.Count} players");
        }

        private static void SetupUDPSocket() {
            gameUDPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            gameUDPSocket.Bind(new IPEndPoint(GetConfigHostEndPoint().Address, 0));
        }

        public void CreateLobby()
        {
            LeaveLobby();

            // TODO: - Use this to etermine if password is needed -> ModEntry.Preferences.LobbySettings.OpenToJoin;
            lock (_LobbyPlayersThreadLock) {
                SetupUDPSocket();
                instance.PlayersTCPSockets = new List<Socket>();

                LobbyPlayers = new List<IPEndPoint>();
                LobbyPlayers.Add(GetConfigHostEndPoint());
            }

            lobbyTCPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            int portTest = GetConfigHostEndPoint().Port;
            lobbyTCPSocket.Bind(LobbyPlayers[0]);
            lobbyTCPSocket.Listen(10);

            AcceptConnections();
        }

        public async void JoinLobby()
        {
            LeaveLobby();

            lock (_LobbyPlayersThreadLock)
            {
                SetupUDPSocket();

                lobbyTCPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            }
            IPEndPoint hostEndpoint = GetConfigHostEndPoint();
            await lobbyTCPSocket.ConnectAsync(hostEndpoint);

            // TODO: - use ModEntry.Preferences.LobbySettings.OpenToJoin to determine if the host requires a password so
            // that the connection does not get rejected.

            ListenForHost();
        }

        public void LeaveLobby()
        {
            if (!IsOnline()) { return; }
            // TODO: - If owner, instead of kicking all players. Give lobby ownership to another player.

            if (AmILobbyOwner)
            {
                OnOwnerLeave();
            }
            else {
                try {
                    byte[] buffer = Encoding.ASCII.GetBytes(LeaveRequest);
                    lobbyTCPSocket.Send(buffer);
                } catch (SocketException e) {
                    Debug.WriteLine(e);
                }
            }

            Players.ForEach(x => x.IsDisposed = true);
            Players.Clear();
            LobbyPlayers.Clear();

            if (lobbyTCPSocket.Connected) {
                lobbyTCPSocket.Disconnect(true);
            }

            lobbyTCPSocket.Dispose();
            lobbyTCPSocket = null;

            gameUDPSocket.Dispose();
            gameUDPSocket = null;
        }

        // TODO: - Repurpose these handlers to be called by dedicated TCP requests
        public void HandlePlayerLeft(IPEndPoint endpoint)
        {
            Debug.WriteLine($"[MP] Player left " + endpoint.ToString() + ":" + endpoint.Port);
            Players.Where(x => x.endpoint == endpoint).ToList().ForEach(x => x.IsDisposed = true);
            Players.RemoveAll(x => x.endpoint == endpoint);
            LobbyPlayers.Remove(endpoint);
        }

        public void HandlePlayerJoined(IPEndPoint endpoint)
        {
            Debug.WriteLine($"[MP] Player joined " + endpoint.Address.ToString() + ":" + endpoint.Port);
        }

        private static string ConfigFileDirectory = Directory.GetCurrentDirectory() + "/Content/JKMods/MultiplayerMod/MultiplayerConfig.txt";
        
        private static string NameConfigLabel = "Name:";
        private static string HostIPConfigLabel = "HostIP:";
        private static string HostPortConfigLabel = "HostPort:";
        private static string HostPasswordConfigLabel = "Password:";

        private static string DefaultPlayerName = "Determined Jumper";
        private static string DefaultHostIP = "127.0.0.1";
        private static int DefaultHostPort = 3000;
        private static string DefaultHostPassword = "Pass123";

        // Used in lock statements to prevent racing conditions between multiple threads trying to act on LobbyPlayers
        private static object _LobbyPlayersThreadLock = new object();

        // Handles all lobby events for the host or joining players. The host is the server who listens for the joining players (clients)
        private static Socket lobbyTCPSocket;

        // Handles gameplay networking mainly the sending/receiving of player's position and states
        private static Socket gameUDPSocket = null;

        public static IPEndPoint myEndpoint => (IPEndPoint)gameUDPSocket.LocalEndPoint;

        private List<Socket> PlayersTCPSockets = new List<Socket>();
        private List<IPEndPoint> LobbyPlayers = new List<IPEndPoint>();
        public bool AmILobbyOwner => IsOnline() && LobbyPlayers.Count > 0 && ((IPEndPoint)lobbyTCPSocket.LocalEndPoint).Equals(LobbyPlayers[0]);

        private static void RepairConfigFile() {
            //FIXME: - Throws an exception for the file already being in use
            if (!File.Exists(ConfigFileDirectory))
            {
                File.Create(ConfigFileDirectory);
                File.WriteAllText(ConfigFileDirectory, "=====CONFIG FILE=====\n" +
                    NameConfigLabel + DefaultPlayerName + "\n" +
                    HostIPConfigLabel + DefaultHostIP + "\n" +
                    HostPortConfigLabel + DefaultHostPort + "\n" +
                    HostPasswordConfigLabel + DefaultHostPassword
                );
            }
        }

        // TODO: - Unbloat these file reading methods by unifying them under single method
        public static string GetConfigName() {
            RepairConfigFile();

            string name = "";

            string[] lines = File.ReadAllLines(ConfigFileDirectory);

            foreach (string line in lines)
            {
                int indexOfSubstring = line.IndexOf(NameConfigLabel);
                if (indexOfSubstring >= 0)
                {
                   return line.Substring(indexOfSubstring + NameConfigLabel.Length);
                }
            }

            return "";
        }

        public static IPEndPoint GetConfigHostEndPoint() {
            RepairConfigFile();

            string[] lines = File.ReadAllLines(ConfigFileDirectory);

            string ip = "";
            int port = -1;

            foreach (string line in lines)
            {
                int indexOfSubstring = line.IndexOf(HostIPConfigLabel);
                if (indexOfSubstring >= 0)
                {
                    string readIP = line.Substring(indexOfSubstring + HostIPConfigLabel.Length);
                    if (readIP.Length > 0)
                    {
                        ip = readIP;
                    }

                    if (port != -1) {
                        break;
                    }
                    continue;
                }

                indexOfSubstring = line.IndexOf(HostPortConfigLabel);
                if (indexOfSubstring >= 0)
                {
                    int readPort = Int32.Parse(line.Substring(indexOfSubstring + HostPortConfigLabel.Length));
                    if (readPort != -1)
                    {
                        port = readPort;
                    }

                    if (ip.Length > 0) {
                        break;
                    }
                }
            }

            if (ip.Length > 0 && port != -1) {
                return new IPEndPoint(IPAddress.Parse(ip), port);
            }

            return null;
        }

        public static string GetConfigPassword() {
            RepairConfigFile();

            string password = "";

            string[] lines = File.ReadAllLines(ConfigFileDirectory);

            foreach (string line in lines)
            {
                int indexOfSubstring = line.IndexOf(HostPasswordConfigLabel);
                if (indexOfSubstring >= 0)
                {
                    return password = line.Substring(indexOfSubstring + HostPasswordConfigLabel.Length);
                }
            }

            return "";
        }

        public override string ToString()
        {
            return base.ToString();
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
        
        public void Tick(float p_delta)
        {
            emptyLobbyInviteInfo.Tick();
            inviteInfo.Tick();
            SendGameplayUpdateToOthers();
            GetGameplayUpdates();
        }

        public void Draw()
        {
            proximityPlayers?.Draw();
            emptyLobbyInviteInfo.Draw();
            inviteInfo.Draw();
            MultiplayerManager.DrawPlayers();
            MultiplayerManager.instance._alreadyDrawedPlayers = false;
        }

        public static bool IsOnline()
        {
            return lobbyTCPSocket != null;
        }

        public static void ToggleOnline()
        {
            if (MultiplayerManager.IsOnline())
            {
                MultiplayerManager.instance.LeaveLobby();
            }
            else
            {
                MultiplayerManager.instance.CreateLobby();
            }
        }
    }
}
