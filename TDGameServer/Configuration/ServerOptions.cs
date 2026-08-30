using System.Text.Json;
using System.Text.Json.Serialization;
using MySql.Data.MySqlClient;
using TDGameServer.Models;

namespace TDGameServer.Configuration;

/// <summary>
/// 실행 환경에 따라 서버 포트와 데이터베이스 연결 정보를 구성합니다.
/// 배포 환경은 환경 변수를 우선하고, 로컬 개발에서는 appsettings.json을 대체값으로 사용합니다.
/// </summary>
public sealed class ServerOptions
{
    private const int DefaultTcpPort = 5000;
    private const int DefaultUdpPort = 9000;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public int TcpPort { get; private init; }
    public int UdpPort { get; private init; }
    public string ConnectionString { get; private init; } = string.Empty;

    /// <summary>
    /// 비밀값을 로그에 남기지 않고 실행 설정을 조합합니다.
    /// Mac에서 5000번 포트가 사용 중이면 PROJECTMINE_TCP_PORT로 안전하게 변경할 수 있습니다.
    /// </summary>
    public static ServerOptions Load()
    {
        DbSettings database = LoadDatabaseSettings();
        var connectionBuilder = new MySqlConnectionStringBuilder
        {
            Server = database.Host,
            Port = database.Port,
            Database = database.Name,
            UserID = database.User,
            Password = database.Password,
            SslMode = database.SslMode,
            Pooling = true
        };

        return new ServerOptions
        {
            TcpPort = ReadPort("PROJECTMINE_TCP_PORT", DefaultTcpPort),
            UdpPort = ReadPort("PROJECTMINE_UDP_PORT", DefaultUdpPort),
            ConnectionString = connectionBuilder.ConnectionString
        };
    }

    private static DbSettings LoadDatabaseSettings()
    {
        string host = Environment.GetEnvironmentVariable("DB_HOST") ?? string.Empty;
        string name = Environment.GetEnvironmentVariable("DB_NAME") ?? string.Empty;
        string user = Environment.GetEnvironmentVariable("DB_USER") ?? string.Empty;
        string password = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(password))
        {
            return new DbSettings
            {
                Host = host,
                Name = name,
                User = user,
                Password = password,
                Port = (uint)ReadPort("DB_PORT", 3306),
                SslMode = ReadSslMode()
            };
        }

        string configPath = FindConfigPath();
        string json = File.ReadAllText(configPath);
        Dictionary<string, DbSettings>? settings =
            JsonSerializer.Deserialize<Dictionary<string, DbSettings>>(json, SerializerOptions);

        if (settings == null || !settings.TryGetValue("Database", out DbSettings? database))
            throw new InvalidOperationException("appsettings.json에 Database 설정이 없습니다.");

        return database;
    }

    /// <summary>
    /// 현재 작업 폴더와 게시 폴더를 모두 지원해 Debug 전용 상대경로에 의존하지 않게 합니다.
    /// 명시적인 PROJECTMINE_CONFIG_PATH가 있으면 가장 먼저 사용합니다.
    /// </summary>
    private static string FindConfigPath()
    {
        string explicitPath = Environment.GetEnvironmentVariable("PROJECTMINE_CONFIG_PATH") ?? string.Empty;
        string[] candidates =
        {
            explicitPath,
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../appsettings.json"))
        };

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "DB 환경 변수가 없고 appsettings.json도 찾지 못했습니다. README의 로컬 실행 설정을 확인하세요.");
    }

    private static int ReadPort(string key, int fallback)
    {
        string value = Environment.GetEnvironmentVariable(key) ?? string.Empty;
        return int.TryParse(value, out int port) && port is > 0 and <= 65535 ? port : fallback;
    }

    private static MySqlSslMode ReadSslMode()
    {
        string value = Environment.GetEnvironmentVariable("DB_SSL_MODE") ?? string.Empty;
        return Enum.TryParse(value, true, out MySqlSslMode sslMode) ? sslMode : MySqlSslMode.Preferred;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
