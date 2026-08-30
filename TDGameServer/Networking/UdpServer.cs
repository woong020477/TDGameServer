using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TDGameServer.Networking;

/// <summary>
/// 인게임 UDP 메시지를 같은 방의 다른 참가자에게만 중계합니다.
/// 방 등록 이전 메시지는 거부해 서로 다른 매치의 상태가 섞이지 않게 합니다.
/// </summary>
public sealed class UdpServer : IDisposable
{
    private readonly Dictionary<IPEndPoint, int> clientRooms = new();
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
        clientRooms.Clear();
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
                Console.WriteLine($"[UDP 수신] {result.RemoteEndPoint} → {message}");
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

            clientRooms[sender] = registration.roomId;
            Console.WriteLine($"[UDP] 클라이언트 등록: {sender}, RoomId={registration.roomId}");
            return;
        }

        if (header == "UNREGISTER")
        {
            clientRooms.Remove(sender);
            Console.WriteLine($"[UDP] 클라이언트 해제: {sender}");
            return;
        }

        if (!clientRooms.TryGetValue(sender, out int roomId))
        {
            Console.WriteLine($"[UDP] 미등록 클라이언트 메시지 스킵: {sender}");
            return;
        }

        Broadcast($"{header}|{json}", sender, roomId);
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
        foreach ((IPEndPoint endpoint, int registeredRoomId) in clientRooms)
        {
            if (endpoint.Equals(sender) || registeredRoomId != roomId)
                continue;

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

    public void Dispose()
    {
        Stop();
        cancellation?.Dispose();
    }
}

public sealed class UdpRoomRegistrationMessage
{
    public int roomId { get; set; }
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
