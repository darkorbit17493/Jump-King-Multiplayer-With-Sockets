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
using System.Runtime.InteropServices;

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
            MultiplayerManager.instance.GhostPlayers.ForEach(x => x.DrawFromOutside());
            MultiplayerManager.instance._alreadyDrawedPlayers = true;
        }

        public static MultiplayerManager instance { get; set; }

        internal List<GhostPlayer> GhostPlayers = new List<GhostPlayer>();

        internal TrackData lastTrackData = new TrackData { posX = 500, posY = 500, };

        public void Init()
        {
            inviteInfo = new InviteInfo();
            emptyLobbyInviteInfo = new EmptyLobbyInviteInfo();

            string ConfigName = GetConfigName();
            string name = ConfigName.Length > 0 ? ConfigName : DefaultPlayerName;

            Debug.WriteLine($"Your player name is {name}");
            Debug.WriteLine($"Your ip:port is : {GetConfigHostEndPoint().Port}");

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
            if (OtherPlayersEndpoints.Count < 1) {
                return;
            }
            ArraySegment<byte> messageBytes;
            try
            {
                messageBytes = new ArraySegment<byte>(Encoding.ASCII.GetBytes(Parser.ToString(message)));
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                return;
            }
            
            foreach (IPEndPoint player in OtherPlayersEndpoints)
            {
                try
                {
                    await gameUDPSocket.SendToAsync(messageBytes, SocketFlags.None, (EndPoint)player);
                } catch (SocketException e) {
                    if (!IsDisconnectingPlayer) {
                        Debug.WriteLine(e.Message);
                    }

                    if (AmILobbyOwner) {
                        HandleUndetectedClientDisconnection();
                    }
                } catch (Exception e) {
                    Debug.WriteLine(e.Message);
                }
            }
        }

        public async void ReceiveGameplayUpdates()
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
                ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[200]);
                IPEndPoint senderEndpoint = new IPEndPoint(IPAddress.Any, 0);
                string messageStr;
                try
                {
                    SocketReceiveFromResult result = await gameUDPSocket.ReceiveFromAsync(buffer, SocketFlags.None, senderEndpoint);
                    senderEndpoint = (IPEndPoint)result.RemoteEndPoint;
                    
                    messageStr = System.Text.Encoding.ASCII.GetString(buffer.Array).Trim(new char[] {(char)0});

                    TrackData trackData = Parser.FromString<TrackData>(messageStr);
                    UpdatePlayerGameplayState(senderEndpoint, trackData);
                } catch (NullReferenceException e) {
                    Debug.WriteLine(e.Message);
                }
                catch (ArgumentNullException e) {
                    Debug.WriteLine(e.Message);
                } catch (SocketException e){
                    if (!IsDisconnectingPlayer)
                    {
                        Debug.WriteLine(e.Message);
                    }

                    if (AmILobbyOwner)
                    {
                        HandleUndetectedClientDisconnection();
                    }
                }

                // idk how to get the ping
                //SteamNetworkingIdentity id = new SteamNetworkingIdentity();
                //id.SetSteamID(remoteId);
                //SteamNetworkingMessages.GetSessionConnectionInfo(ref id, out _, out SteamNetConnectionRealTimeStatus_t realTimeStatus);
                //Debug.WriteLine($"[UPDATE] {remoteId} - {realTimeStatus.m_eState} {realTimeStatus.m_nPing} {realTimeStatus.m_flConnectionQualityLocal}");
                
            }
        }

        public void UpdatePlayerGameplayState(IPEndPoint endpoint, TrackData data)
        {
            var idx = GhostPlayers.FindIndex(x => x.endpoint.Equals(endpoint));
            if (idx == -1)
            {
                CreateGhostPlayer(endpoint, data);
                idx = GhostPlayers.Count - 1;
            }
            lock (_LobbyPlayersThreadLock)
            {
                GhostPlayer player = GhostPlayers[idx];
                player.ScreenIndex1 = data.screenIndex1;
                player.LevelId = data.levelId;
                player.tracker.Track(data);
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
            var color = data.colorIdx != 0 ? data.colorIdx : 1;
            lock (_LobbyPlayersThreadLock)
            {
                GhostPlayers.Add(new GhostPlayer(name, endpoint, color));
            }
        }

        private static void RemoveGhostPlayers(List<IPEndPoint> playersEndpoints)
        {
            List<GhostPlayer> updatedGhostPlayers = new List<GhostPlayer>();

            foreach (IPEndPoint playerEndpoint in playersEndpoints) {
                foreach (GhostPlayer player in instance.GhostPlayers)
                {
                    if (!player.endpoint.Equals(playerEndpoint))
                    {
                        updatedGhostPlayers.Add(player);
                    }
                }
            }

            instance.GhostPlayers = updatedGhostPlayers;
        }

        private static void RemoveGhostPlayer(IPEndPoint playerEndpoint)
        {
            RemoveGhostPlayers(new List<IPEndPoint> {playerEndpoint});
        }

        internal LeaderBoard leaderBoard { get; set; }
        internal ProximityPlayers proximityPlayers { get; set; }
        internal InviteInfo inviteInfo { get; set; }
        internal EmptyLobbyInviteInfo emptyLobbyInviteInfo { get; set; }

        private static string LeaveRequest = "Leave";
        private static string UpdateLobbyRequest = "UpdateLobby";

        private static async void SendTCPData(Socket targetSocket, byte[] data) {
            ArraySegment<byte> buffer = new ArraySegment<byte>(data);
            
            try
            {
                await targetSocket.SendAsync(buffer, SocketFlags.None);
            } catch (SocketException e) {
                Debug.WriteLine(e.Message);
            }
        }

        private static void SendLobbyUpdateRequestToEachClient()
        {
            List<string> endpointsData = new List<string>();

            foreach (IPEndPoint endpoint in instance.OtherPlayersEndpoints)
            {
                endpointsData.Add(endpoint.ToString());
            }

            string JSONEndpointsData = Parser.ToString<List<string>>(endpointsData);

            string message = UpdateLobbyRequest + "|" + JSONEndpointsData;

            foreach (Socket player in instance.PlayersTCPSockets)
            {
                SendTCPData(player, Encoding.ASCII.GetBytes(message));
            }
        }

        private static void HandlePlayerConenction(Socket client) {
            lock (_LobbyPlayersThreadLock)
            {
                instance.PlayersTCPSockets.Add(client);
                instance.OtherPlayersEndpoints.Add((IPEndPoint)client.RemoteEndPoint);

                SendLobbyUpdateRequestToEachClient();
                ListenForClientRequests(client);
            }
        }

        // When a client has requested to leave the lobby/left the game. The OS terminates the TCP connection on program close
        // if the user forcefully closes the program.
        private static void HandleStandardClientDisconenction(Socket client)
        {
            lock (_LobbyPlayersThreadLock)
            {
                instance.PlayersTCPSockets.Remove(client);
                instance.OtherPlayersEndpoints.Remove((IPEndPoint)client.RemoteEndPoint);

                RemoveGhostPlayer((IPEndPoint)client.RemoteEndPoint);
            }

            SendLobbyUpdateRequestToEachClient();
        }

        private static bool IsDisconnectingPlayer = false;
        private static Object _DisconnectingPlayerThreadLock = new object();

        // Handles a client disconnection when the OS has failed to terminate the TCP connection such as during a computer crash.
        private static async void HandleUndetectedClientDisconnection()
        {
            // Allows only a single thread to be accessing this method at a time
            lock (_DisconnectingPlayerThreadLock) {
                if (IsDisconnectingPlayer) {
                    return;
                }

                IsDisconnectingPlayer = true;
            }

            Debug.WriteLine("Searching for disconnected players");

            List<Socket> updatedPlayersTCPSockets = new List<Socket>();
            List<IPEndPoint> updatedOtherLobbyPlayers = new List<IPEndPoint>();
            
            foreach (Socket player in instance.PlayersTCPSockets)
            {
                try {
                    // Sending a test packet to see if the player socket is connected or not
                    ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[] { 0 });
                    await player.SendAsync(buffer, SocketFlags.None);
                    if (player.Connected)
                    {
                        updatedPlayersTCPSockets.Add(player);
                        updatedOtherLobbyPlayers.Add((IPEndPoint)player.RemoteEndPoint);
                    }
                } catch (SocketException e) {
                    Debug.WriteLine(e.Message);
                }
            }

            lock (_LobbyPlayersThreadLock) {
                lock (_DisconnectingPlayerThreadLock) {
                    instance.PlayersTCPSockets = updatedPlayersTCPSockets;
                    instance.OtherPlayersEndpoints = updatedOtherLobbyPlayers;

                    SendLobbyUpdateRequestToEachClient();

                    IsDisconnectingPlayer = false;
                }
            }
        }

        private static async void AcceptClientConnections()
        {
            while (IsOnline())
            {
                try
                {
                    Socket client = await lobbyTCPSocket.AcceptAsync();
                    HandlePlayerConenction(client);
                }
                catch (SocketException e)
                {
                    Debug.WriteLine(e.Message);
                }
                catch (ObjectDisposedException e)
                {
                    Debug.WriteLine(e.Message);
                }
            }
        }

        private static async void ListenForHostRequests() {
            while (IsOnline()){
                ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[200]);
                try
                {
                    await lobbyTCPSocket.ReceiveAsync(buffer, SocketFlags.None);
                }
                catch (SocketException e)
                {
                    Debug.WriteLine(e.Message);
                    instance.LeaveLobby();
                    return;
                }
                string message = System.Text.Encoding.ASCII.GetString(buffer.Array).Trim(new char[] {(char)0});

                if (message.Equals(LeaveRequest)) {
                    instance.LeaveLobby();
                    return;
                } else if (message.Contains(UpdateLobbyRequest)) {
                    // Request length + 1 becuse of the separation token '|'
                    string endpointsData = message.Substring(UpdateLobbyRequest.Length + 1);
                    instance.UpdateOtherLobbyMembers(endpointsData);
                }
            }
        }

        private static async void ListenForClientRequests(Socket client) {
            while (IsOnline() && client.Connected) {
                ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[200]);
                try
                {
                    await client.ReceiveAsync(buffer, SocketFlags.None);
                } catch (SocketException e) {
                    HandleStandardClientDisconenction(client);
                    return;
                }

                string message = System.Text.Encoding.ASCII.GetString(buffer.Array).Trim(new char[] { (char)0 });

                if (message.Contains(LeaveRequest)) {
                    HandleStandardClientDisconenction(client);
                    return;
                }
            }
        }

        public async void OnOwnerLeave()
        {
            foreach (Socket player in PlayersTCPSockets)
            {
                try
                {
                    byte[] buffer = Encoding.ASCII.GetBytes(LeaveRequest);
                    await player.SendAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                }
                catch(SocketException e)
                {
                    Debug.WriteLine(e.Message);
                }
            }
        }

        public void UpdateOtherLobbyMembers(string lobbyPlayersEndpoints)
        {
            lock (_LobbyPlayersThreadLock)
            {
                List<IPEndPoint> updatedLobbyPlayers = new List<IPEndPoint>();
                // The first lobby player in the list is always the host
                updatedLobbyPlayers.Add((IPEndPoint)lobbyTCPSocket.RemoteEndPoint);

                List<GhostPlayer> updatedGhostPlayers = new List<GhostPlayer>();

                List<string> stringEndpoints = Parser.FromString<List<string>>(lobbyPlayersEndpoints);

                foreach (string strEndpoint in stringEndpoints) {
                    int separationTokenIndex = strEndpoint.IndexOf(':');
                    string ip = strEndpoint.Substring(0, separationTokenIndex);
                    int port = Int32.Parse(strEndpoint.Substring(separationTokenIndex + 1));

                    IPEndPoint endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

                    if (endpoint.Equals(myEndpoint)) {
                        continue;
                    }

                    updatedLobbyPlayers.Add(endpoint);

                    foreach (GhostPlayer ghostPlayer in GhostPlayers) {
                        if (ghostPlayer.endpoint.Equals(endpoint)) { 
                            updatedGhostPlayers.Add(ghostPlayer);
                        }
                    }
                }

                OtherPlayersEndpoints.Clear();
                OtherPlayersEndpoints = updatedLobbyPlayers;

                GhostPlayers.Clear();
                GhostPlayers = updatedGhostPlayers;

                Debug.WriteLine($"[MP] Lobby members updated: {OtherPlayersEndpoints.Count} players");
            }
        }

        private void InitUDPSocket() {
            gameUDPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            gameUDPSocket.ReceiveBufferSize = UDPReceiveBufferSize;
        }

        public void CreateLobby()
        {
            LeaveLobby();
            AmILobbyOwner = true;

            IPEndPoint hostEndpoint = GetConfigHostEndPoint();
            int hostPort = hostEndpoint.Port;

            // TODO: - Use this to determine if password is needed -> ModEntry.Preferences.LobbySettings.OpenToJoin;
            lock (_LobbyPlayersThreadLock) {
                InitUDPSocket();
                
                // Must have identical ip:port to the TCP socket since the clients use the tcp endpoint when they join to find this UDP one
                gameUDPSocket.Bind(GetConfigHostEndPoint());
                
                instance.PlayersTCPSockets = new List<Socket>();

                OtherPlayersEndpoints = new List<IPEndPoint>();
            }

            leaderBoard = new LeaderBoard();
            proximityPlayers = new ProximityPlayers();

            lobbyTCPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            lobbyTCPSocket.Bind(hostEndpoint);
            lobbyTCPSocket.Listen(10);

            AcceptClientConnections();
        }

        public async void JoinLobby()
        {
            LeaveLobby();

            lock (_LobbyPlayersThreadLock)
            {
                InitUDPSocket();
                // Putting 0 at prot number binds the socket to a free port it finds.
                // The host then remembers the endpoint of this client and shares it with the others
                gameUDPSocket.Bind(new IPEndPoint(GetConfigMyIP(), 0));

                lobbyTCPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                lobbyTCPSocket.Bind(myEndpoint);
            }
            IPEndPoint hostEndpoint = GetConfigHostEndPoint();
            try
            {
                await lobbyTCPSocket.ConnectAsync(hostEndpoint);
            }
            catch(SocketException e) {
                Debug.WriteLine(e.Message);
                LeaveLobby();
            }

            // TODO: - Send password to the host so that it doesn't reject the connection

            ListenForHostRequests();
        }

        public void LeaveLobby()
        {
            if (!IsOnline()) { return; }
            // TODO: - If owner, instead of kicking all players. Give lobby ownership to another player.

            if (AmILobbyOwner)
            {
                OnOwnerLeave();
            } else {
                try {
                    lobbyTCPSocket.SendTimeout = 2000;
                    lobbyTCPSocket.Send(Encoding.ASCII.GetBytes(LeaveRequest));
                    lobbyTCPSocket.Shutdown(SocketShutdown.Both);
                    lobbyTCPSocket.Disconnect(false);
                } catch (SocketException e) {
                    Debug.WriteLine(e);
                }
            }

            GhostPlayers.ForEach(x => x.IsDisposed = true);
            GhostPlayers.Clear();
            OtherPlayersEndpoints.Clear();

            lobbyTCPSocket.Dispose();
            lobbyTCPSocket = null;

            gameUDPSocket.Dispose();
            gameUDPSocket = null;

            AmILobbyOwner = false;
        }

        private static string ConfigFileDirectory = Directory.GetCurrentDirectory() + "/Content/JKMods/MultiplayerMod/MultiplayerConfig.txt";
        
        private static string NameConfigLabel = "Name:";
        private static string HostIPConfigLabel = "HostIP:";
        private static string HostPortConfigLabel = "HostPort:";
        private static string MyIPConfigLabel = "MyIP:";
        private static string HostPasswordConfigLabel = "Password:";

        private static string DefaultPlayerName = "Determined Jumper";
        private static string DefaultHostIP = "127.0.0.1";
        private static int DefaultHostPort = 3000;
        private static string DefaultMyIP = "127.0.0.1";
        private static string DefaultHostPassword = "Pass123";

        // Used in lock statements to prevent racing conditions between multiple threads trying to act on LobbyPlayers
        private static object _LobbyPlayersThreadLock = new object();

        // Handles all lobby events for the host or joining players. The host is the server who listens for the joining players (clients)
        private static Socket lobbyTCPSocket;

        // Handles gameplay networking mainly the sending/receiving of player's position and states
        private static Socket gameUDPSocket = null;
        private static int UDPReceiveBufferSize = 2048;

        public static IPEndPoint myEndpoint => (IPEndPoint)gameUDPSocket.LocalEndPoint;

        private List<Socket> PlayersTCPSockets = new List<Socket>();
        private List<IPEndPoint> OtherPlayersEndpoints = new List<IPEndPoint>();
        public bool AmILobbyOwner = false;

        private static void RepairConfigFile() {
            if (!File.Exists(ConfigFileDirectory))
            {
                //FIXME: - Throws an exception for the file already being in use
                File.Create(ConfigFileDirectory);
                File.WriteAllText(ConfigFileDirectory, "=====CONFIG FILE=====\n" +
                    NameConfigLabel + DefaultPlayerName + "\n" +
                    HostIPConfigLabel + DefaultHostIP + "\n" +
                    HostPortConfigLabel + DefaultHostPort + "\n" +
                    MyIPConfigLabel + DefaultMyIP + "\n" +
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

        public static IPAddress GetConfigMyIP() {
            RepairConfigFile();

            string ip = "";

            string[] lines = File.ReadAllLines(ConfigFileDirectory);

            foreach (string line in lines)
            {
                int indexOfSubstring = line.IndexOf(MyIPConfigLabel);
                if (indexOfSubstring >= 0)
                {
                    return IPAddress.Parse(line.Substring(indexOfSubstring + MyIPConfigLabel.Length));
                }
            }

            return IPAddress.Parse(DefaultMyIP);
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
            ReceiveGameplayUpdates();
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
