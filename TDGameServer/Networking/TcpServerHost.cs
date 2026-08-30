using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TDGameServer.Networking;

/// <summary>
/// TCP 연결 수락과 줄바꿈 기반 메시지 프레이밍만 담당합니다.
/// 실제 Command 처리는 델리게이트로 전달해 네트워크 수명주기와 게임 규칙을 분리합니다.
/// </summary>
public sealed class TcpServerHost
{
    private readonly TcpListener listener;
    private readonly Func<string, TcpClient, string> requestHandler;

    public TcpServerHost(int port, Func<string, TcpClient, string> requestHandler)
    {
        listener = new TcpListener(IPAddress.Any, port);
        this.requestHandler = requestHandler;
        Port = port;
    }

    public int Port { get; }

    /// <summary>
    /// 취소 요청이 올 때까지 클라이언트를 수락하고 각 연결을 독립 작업으로 처리합니다.
    /// 서버 종료 시 listener를 중지해 포트가 다음 실행에 남지 않도록 합니다.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        listener.Start();
        Console.WriteLine($"[TCP] 서버가 포트 {Port}에서 실행 중입니다.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClient(client));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 정상 종료 경로입니다.
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// TCP 스트림에서 JSON 한 줄씩 읽어 요청 처리기로 전달합니다.
    /// 하나의 Read가 하나의 패킷이라는 잘못된 가정을 피하기 위해 StreamReader 경계를 사용합니다.
    /// </summary>
    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
            {
                while (client.Connected && reader.ReadLine() is { } request)
                {
                    LogRequestSummary(client, request);

                    string response = requestHandler(request, client);
                    if (!string.IsNullOrWhiteSpace(response))
                        TcpMessageWriter.Send(client, response);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP 처리 오류] {ex.Message}");
        }
    }

    /// <summary>
    /// 비밀번호와 개인정보가 포함된 JSON 본문은 남기지 않고 명령과 원격지만 기록합니다.
    /// ping은 주기적으로 발생하므로 일반 로그에서 제외합니다.
    /// </summary>
    private static void LogRequestSummary(TcpClient client, string request)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(request);
            string command = document.RootElement.TryGetProperty("Command", out JsonElement element)
                ? element.GetString() ?? "unknown"
                : "missing";

            if (!command.Equals("ping", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[TCP 수신] {client.Client.RemoteEndPoint} Command={command}");
        }
        catch (JsonException)
        {
            Console.WriteLine($"[TCP 수신] {client.Client.RemoteEndPoint} 잘못된 JSON");
        }
    }
}

/// <summary>모든 TCP 응답에 동일한 줄바꿈 프레이밍과 전송 잠금을 적용합니다.</summary>
public static class TcpMessageWriter
{
    public static void Send(TcpClient client, string message)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message.TrimEnd('\r', '\n') + "\n");
            lock (client)
            {
                client.GetStream().Write(data, 0, data.Length);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP 전송 오류] {ex.Message}");
        }
    }
}
