using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TDGameServer.Networking;

/// <summary>
/// UDP 공인 엔드포인트를 교환해 인게임 패킷이 호스트와 참가자 사이에서 직접 흐르게 합니다.
/// 직접 연결 준비 전 패킷만 같은 방 안에서 중계합니다.
/// </summary>
public sealed class UdpServer : IDisposable
{
    private readonly object peerLock = new();
    private readonly Dictionary<IPEndPoint, UdpPeer> peersByEndpoint = new();
    private readonly int port;
    private UdpClient? udpServer;
    private CancellationTokenSource? cancellation;

    public UdpServer(int port)
    {
        this.port = port;
    }

    /// <summary>UDP 소켓을 열고 비동기 수신 루프를 한 번만 시작합니다.</summary>
    public void Start()
    {
        if (udpServer != null)
            return;

        udpServer = new UdpClient(port);
        cancellation = new CancellationTokenSource();
        Console.WriteLine($"[UDP] 서버가 포트 {port}에서 실행 중입니다.");
        _ = ListenLoopAsync(cancellation.Token);
    }

    /// <summary>수신 대기를 취소하고 등록된 엔드포인트를 정리합니다.</summary>
    public void Stop()
    {
        cancellation?.Cancel();
        udpServer?.Close();
        udpServer = null;
        lock (peerLock)
            peersByEndpoint.Clear();
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (udpServer == null)
                    return;

                UdpReceiveResult result = await udpServer.ReceiveAsync(cancellationToken);
                string message = Encoding.UTF8.GetString(result.Buffer);
                HandleMessage(message, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP 오류] {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 'header|json' 메시지를 해석하고 REGISTER/UNREGISTER는 서버 내부 상태로만 처리합니다.
    /// 일반 게임 메시지는 송신자가 등록된 방 번호를 기준으로 중계합니다.
    /// </summary>
    private void HandleMessage(string message, IPEndPoint sender)
    {
        (string header, string json) = ParseMessage(message);
        if (string.IsNullOrWhiteSpace(header))
        {
            Console.WriteLine("[UDP] header 누락 메시지 스킵");
            return;
        }

        if (header == "REGISTER")
        {
            UdpRoomRegistrationMessage? registration =
                JsonSerializer.Deserialize<UdpRoomRegistrationMessage>(json);
            if (registration == null || registration.roomId <= 0)
            {
                Console.WriteLine($"[UDP] 잘못된 방 등록 요청: {sender}");
                return;
            }

            lock (peerLock)
            {
                foreach (IPEndPoint oldEndpoint in peersByEndpoint
                             .Where(pair => pair.Value.RoomId == registration.roomId && pair.Value.UserId == registration.userId)
                             .Select(pair => pair.Key)
                             .ToArray())
                    peersByEndpoint.Remove(oldEndpoint);

                peersByEndpoint[sender] = new UdpPeer(registration.roomId, registration.userId, registration.isHost);
            }
            Console.WriteLine($"[UDP] 클라이언트 등록: RoomId={registration.roomId}, UserId={registration.userId}");
            SendPeerList(registration.roomId);
            return;
        }

        if (header == "UNREGISTER")
        {
            int roomId = 0;
            lock (peerLock)
            {
                if (peersByEndpoint.TryGetValue(sender, out UdpPeer? peer)) roomId = peer.RoomId;
                peersByEndpoint.Remove(sender);
            }
            if (roomId > 0) SendPeerList(roomId);
            return;
        }

        if (header == "P2P_HEARTBEAT") return;

        int registeredRoomId;
        lock (peerLock)
        {
            if (!peersByEndpoint.TryGetValue(sender, out UdpPeer? peer))
            {
                Console.WriteLine($"[UDP] 미등록 클라이언트 메시지 스킵: {sender}");
                return;
            }
            registeredRoomId = peer.RoomId;
        }

        // 직접 P2P 경로가 준비되기 전의 시작 패킷만 기존 중계로 보완합니다.
        Broadcast($"{header}|{json}", sender, registeredRoomId);
    }

    public void PromoteHost(int roomId, int newHostUserId, int departedUserId)
    {
        lock (peerLock)
        {
            foreach (UdpPeer peer in peersByEndpoint.Values.Where(peer => peer.RoomId == roomId))
                peer.IsHost = peer.UserId == newHostUserId;
        }

        BroadcastToRoom("HOST_CHANGED|" + JsonSerializer.Serialize(new
        {
            roomId,
            hostUserId = newHostUserId,
            departedUserId
        }), roomId);
        SendPeerList(roomId);
    }

    public void CloseRoom(int roomId)
    {
        lock (peerLock)
        {
            foreach (IPEndPoint endpoint in peersByEndpoint
                         .Where(pair => pair.Value.RoomId == roomId)
                         .Select(pair => pair.Key)
                         .ToArray())
                peersByEndpoint.Remove(endpoint);
        }
    }

    private static (string Header, string Json) ParseMessage(string message)
    {
        if (message.Contains('|'))
        {
            string[] parts = message.Split('|', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "{}");
        }

        UdpBaseMessage? messageBase = JsonSerializer.Deserialize<UdpBaseMessage>(message);
        return (messageBase?.action ?? string.Empty, message);
    }

    /// <summary>송신자를 제외하고 같은 RoomId로 등록된 클라이언트에게만 데이터를 전달합니다.</summary>
    private void Broadcast(string message, IPEndPoint sender, int roomId)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        IPEndPoint[] targets;
        lock (peerLock)
            targets = peersByEndpoint.Where(pair => pair.Value.RoomId == roomId && !pair.Key.Equals(sender)).Select(pair => pair.Key).ToArray();

        foreach (IPEndPoint endpoint in targets)
        {
            try
            {
                udpServer?.Send(data, data.Length, endpoint);
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[UDP 전송 오류] {endpoint}: {ex.Message}");
            }
        }
    }

    private void BroadcastToRoom(string message, int roomId)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        IPEndPoint[] targets;
        lock (peerLock)
            targets = peersByEndpoint.Where(pair => pair.Value.RoomId == roomId).Select(pair => pair.Key).ToArray();

        foreach (IPEndPoint endpoint in targets)
        {
            try { udpServer?.Send(data, data.Length, endpoint); }
            catch (SocketException ex) { Console.WriteLine($"[UDP 전송 오류] {endpoint}: {ex.Message}"); }
        }
    }

    private void SendPeerList(int roomId)
    {
        object[] peers;
        int hostUserId;
        lock (peerLock)
        {
            var roomPeers = peersByEndpoint.Where(pair => pair.Value.RoomId == roomId).ToArray();
            hostUserId = roomPeers.FirstOrDefault(pair => pair.Value.IsHost).Value?.UserId
                         ?? roomPeers.Select(pair => pair.Value.UserId).Where(id => id > 0).DefaultIfEmpty(0).Min();
            peers = roomPeers.Select(pair => (object)new
            {
                userId = pair.Value.UserId,
                address = pair.Key.Address.ToString(),
                port = pair.Key.Port,
                isHost = pair.Value.UserId == hostUserId
            }).ToArray();
        }

        BroadcastToRoom("PEER_LIST|" + JsonSerializer.Serialize(new { roomId, hostUserId, peers }), roomId);
    }

    public void Dispose()
    {
        Stop();
        cancellation?.Dispose();
    }
}

public sealed class UdpRoomRegistrationMessage
{
    public int roomId { get; set; }
    public int userId { get; set; }
    public bool isHost { get; set; }
}

internal sealed class UdpPeer
{
    public UdpPeer(int roomId, int userId, bool isHost)
    {
        RoomId = roomId;
        UserId = userId;
        IsHost = isHost;
    }

    public int RoomId { get; }
    public int UserId { get; }
    public bool IsHost { get; set; }
}

public class UdpBaseMessage
{
    public string? action { get; set; }
}

public sealed class TowerBuildMessage : UdpBaseMessage
{
    public int userId { get; set; }
    public string? towerType { get; set; }
    public PositionData? position { get; set; }

    public sealed class PositionData
    {
        public float x { get; set; }
        public float y { get; set; }
        public float z { get; set; }
    }
}
