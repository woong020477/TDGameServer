using System.Net.Sockets;
using MySql.Data.MySqlClient;

namespace TDGameServer.Models;

/// <summary>접속 중인 사용자의 TCP 연결과 마지막 활동 시각을 보관합니다.</summary>
public sealed class Session
{
    public int UserId { get; init; }
    public required string Username { get; init; }
    public required TcpClient Client { get; init; }
    public DateTime LastPing { get; set; } = DateTime.Now;
}

/// <summary>파일 또는 환경 변수에서 읽은 DB 연결 값을 표현합니다.</summary>
public sealed class DbSettings
{
    public required string Host { get; init; }
    public required string Name { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
    public uint Port { get; init; } = 3306;
    public MySqlSslMode SslMode { get; init; } = MySqlSslMode.Preferred;
}

public sealed class RoomInfo
{
    public required string RoomName { get; init; }
    public required string Host { get; init; }
    public required string Difficulty { get; init; }
    public int CurrentPlayers { get; init; }
    public int MaxPlayers { get; init; } = 4;
}
