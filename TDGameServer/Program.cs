using MySql.Data.MySqlClient;
using Org.BouncyCastle.Asn1.Crmf;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TDGameServer.Helpers;
using TDGameServer.Models;

class Program
{
    // MySQL 연결 문자열
    private static string? connectionString;

    // 접속 세션 관리
    private static ConcurrentDictionary<int, Session> sessions = new ConcurrentDictionary<int, Session>();

    static void Main()
    {
        // DB 설정 로드
        LoadDbConfig();
        Console.WriteLine("DB 연결 문자열 로드 완료: " + connectionString);

        // TCP 서버 시작
        TcpListener server = new TcpListener(IPAddress.Any, 5000);
        server.Start();
        Console.WriteLine("TCP 서버가 5000 포트에서 실행 중입니다...");

        // 세션 모니터링 스레드 시작
        MonitorSessions();

        // 클라이언트 연결 대기
        while (true)
        {
            TcpClient client = server.AcceptTcpClient();
            _ = Task.Run(() => HandleClient(client));
        }
    }

    // DB 설정 로드
    private static void LoadDbConfig()
    {
        // bin/Debug/net8.0 → ../../.. → 프로젝트 루트
        string configPath = Path.Combine(AppContext.BaseDirectory, "../../../appsettings.json");

        if (!File.Exists(configPath))
            throw new FileNotFoundException("appsettings.json 파일을 찾을 수 없습니다.", configPath);

        string jsonContent = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<Dictionary<string, DbSettings>>(jsonContent);

        if (config == null || !config.ContainsKey("Database"))
            throw new InvalidOperationException("appsettings.json에서 'Database' 설정을 찾을 수 없습니다.");

        var db = config["Database"];
        connectionString = $"Server={db.Host};Database={db.Name};User={db.User};Password={db.Password};";
    }

    // 클라이언트 요청 처리
    private static void HandleClient(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4096];

            while (client.Connected)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine("요청 수신: " + request);

                string response = HandleRequest(request, client);
                byte[] respBytes = Encoding.UTF8.GetBytes(response);
                SendMessage(client, response);
            }
        }
        catch { }
        finally
        {
            client.Close();
        }
    }

    // 클라이언트에게 메시지 전송
    private static void SendMessage(TcpClient client, string message)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message + "\n");
            client.GetStream().Write(data, 0, data.Length);
        }
        catch { }
    }


    // 요청 처리
    private static string HandleRequest(string jsonRequest, TcpClient client)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonRequest);
            if (!doc.RootElement.TryGetProperty("Command", out var cmdProp))
                return JsonSerializer.Serialize(new { Command = "error", Message = "잘못된 요청: Command가 없습니다." });

            string command = cmdProp.GetString() ?? "";

            switch (command.ToLower())
            {
                case "register":
                    var regData = JsonSerializer.Deserialize<RegisterRequest>(jsonRequest);
                    return regData != null
                        ? JsonSerializer.Serialize(new { Command = "register-result", Message = Register(regData) })
                        : JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                case "login":
                    var loginData = JsonSerializer.Deserialize<LoginRequest>(jsonRequest);
                    return loginData != null
                        ? Login(loginData)
                        : JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                case "find-email":
                    {
                        var findData = JsonSerializer.Deserialize<FindEmailRequest>(jsonRequest);

                        if (findData == null)
                            return JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                        string emailResult = FindEmail(findData);

                        // 이메일을 문자열 그대로 Message에 담기
                        return JsonSerializer.Serialize(new
                        {
                            Command = "find-email-result",
                            Message = emailResult
                        });
                    }

                case "reset-password":
                    var resetData = JsonSerializer.Deserialize<ResetPasswordRequest>(jsonRequest);
                    return resetData != null
                        ? JsonSerializer.Serialize(new { Command = "reset-password-result", Message = ResetPassword(resetData) })
                        : JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                case "enter-lobby":
                    var lobbyData = JsonSerializer.Deserialize<LobbyEnterRequest>(jsonRequest);
                    return lobbyData != null
                        ? EnterLobby(lobbyData, client)
                        : JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                case "logout":
                    var logoutData = JsonSerializer.Deserialize<LogoutRequest>(jsonRequest);
                    return logoutData != null
                        ? JsonSerializer.Serialize(new { Command = "logout-result", Message = Logout(logoutData) })
                        : JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                case "ping":
                    return JsonSerializer.Serialize(new { Command = "pong" });

                case "chat":
                    var chatData = JsonSerializer.Deserialize<ChatRequest>(jsonRequest);
                    if (chatData != null)
                    {
                        string chatMsg = chatData.Message;
                        string jsonMsg = JsonSerializer.Serialize(new
                        {
                            Command = "chat",
                            Sender = chatData.Username,
                            Message = chatMsg
                        });
                        BroadcastMessage(jsonMsg);
                        return JsonSerializer.Serialize(new { Command = "chat-ok" });
                    }
                    return JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                case "get-session-info":
                    if (doc.RootElement.TryGetProperty("UserId", out var idProp))
                    {
                        int userId = idProp.GetInt32();
                        if (sessions.TryGetValue(userId, out var session))
                        {
                            return JsonSerializer.Serialize(new
                            {
                                Command = "session-info",
                                UserId = session.UserId,
                                Username = session.Username
                            });
                        }
                    }
                    return JsonSerializer.Serialize(new { Command = "error", Message = "세션 없음" });

                case "change-password":
                    {
                        var changeData = JsonSerializer.Deserialize<ChangePasswordRequest>(jsonRequest);
                        if (changeData == null)
                            return JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                        return JsonSerializer.Serialize(new
                        {
                            Command = "change-password-result",
                            Message = ChangePassword(changeData)
                        });
                    }

                default:
                    return JsonSerializer.Serialize(new { Command = "error", Message = "알 수 없는 명령어입니다." });
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { Command = "error", Message = $"서버 오류: {ex.Message}" });
        }
    }

    // ---------------- 회원가입 ----------------
    private static string Register(RegisterRequest data)
    {
        if (string.IsNullOrWhiteSpace(data.Username) ||
            string.IsNullOrWhiteSpace(data.Email) ||
            string.IsNullOrWhiteSpace(data.Password))
            return "모든 필드를 올바르게 입력하세요.";

        if (!Regex.IsMatch(data.Username, @"^[가-힣a-zA-Z0-9]+$"))
            return "사용자명은 한글, 영문, 숫자만 사용할 수 있습니다.";

        if (!Regex.IsMatch(data.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return "올바른 이메일 형식을 입력해주세요.";

        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand("SELECT COUNT(*) FROM Users WHERE Username=@u", conn);
        cmd.Parameters.AddWithValue("@u", data.Username);
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
            return "이미 사용 중인 사용자명입니다.";

        cmd = new MySqlCommand("SELECT COUNT(*) FROM Users WHERE Email=@e", conn);
        cmd.Parameters.AddWithValue("@e", data.Email);
        if (Convert.ToInt32(cmd.ExecuteScalar()) > 0)
            return "이미 사용 중인 이메일입니다.";

        string hashed = PasswordHelper.HashPassword(data.Password);

        cmd = new MySqlCommand("INSERT INTO Users (Username, Email, PasswordHash, CreatedAt) VALUES (@u,@e,@p,NOW())", conn);
        cmd.Parameters.AddWithValue("@u", data.Username);
        cmd.Parameters.AddWithValue("@e", data.Email);
        cmd.Parameters.AddWithValue("@p", hashed);
        cmd.ExecuteNonQuery();

        return "회원가입이 완료되었습니다.";
    }

    // ---------------- 로그인 ----------------
    private static string Login(LoginRequest data)
    {
        if (string.IsNullOrWhiteSpace(data.Email) ||
            string.IsNullOrWhiteSpace(data.Password))
            return JsonSerializer.Serialize(new { Command = "error", Message = "이메일과 비밀번호를 입력하세요." });

        string hashed = PasswordHelper.HashPassword(data.Password);

        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand("SELECT Id, Username FROM Users WHERE Email=@e AND PasswordHash=@p", conn);
        cmd.Parameters.AddWithValue("@e", data.Email);
        cmd.Parameters.AddWithValue("@p", hashed);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            int userId = reader.GetInt32("Id");
            string username = reader.GetString("Username");

            // JSON으로 응답
            return JsonSerializer.Serialize(new
            {
                Command = "login-success",
                UserId = userId,
                Username = username
            });
        }

        return JsonSerializer.Serialize(new { Command = "error", Message = "이메일 또는 비밀번호가 잘못되었습니다." });
    }

    // ---------------- 이메일 찾기 ----------------
    private static string FindEmail(FindEmailRequest data)
    {
        if (string.IsNullOrWhiteSpace(data.Username) ||
            string.IsNullOrWhiteSpace(data.Password))
            return "아이디와 비밀번호를 입력하세요.";

        string hashed = PasswordHelper.HashPassword(data.Password);

        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand("SELECT Email FROM Users WHERE Username=@u AND PasswordHash=@p", conn);
        cmd.Parameters.AddWithValue("@u", data.Username);
        cmd.Parameters.AddWithValue("@p", hashed);

        object? result = cmd.ExecuteScalar();
        return result != null ? result.ToString() ?? "" : "가입된 계정을 찾을 수 없습니다.";
    }

    // ---------------- 비밀번호 재발급 ----------------
    private static string ResetPassword(ResetPasswordRequest data)
    {
        if (string.IsNullOrWhiteSpace(data.Username) ||
            string.IsNullOrWhiteSpace(data.Email))
            return "아이디와 이메일을 입력하세요.";

        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand("SELECT COUNT(*) FROM Users WHERE Username=@u AND Email=@e", conn);
        cmd.Parameters.AddWithValue("@u", data.Username);
        cmd.Parameters.AddWithValue("@e", data.Email);

        int count = Convert.ToInt32(cmd.ExecuteScalar());
        if (count == 0) return "계정을 찾을 수 없습니다.";

        string tempPw = Guid.NewGuid().ToString("N").Substring(0, 8);
        string hashed = PasswordHelper.HashPassword(tempPw);

        cmd = new MySqlCommand("UPDATE Users SET PasswordHash=@p WHERE Username=@u AND Email=@e", conn);
        cmd.Parameters.AddWithValue("@p", hashed);
        cmd.Parameters.AddWithValue("@u", data.Username);
        cmd.Parameters.AddWithValue("@e", data.Email);
        cmd.ExecuteNonQuery();

        return $"임시 비밀번호: {tempPw}";
    }

    // ---------------- 비밀번호 변경 ----------------
    private static string ChangePassword(ChangePasswordRequest data)
    {
        if (string.IsNullOrWhiteSpace(data.Username) ||
            string.IsNullOrWhiteSpace(data.Email) ||
            string.IsNullOrWhiteSpace(data.Password) ||
            string.IsNullOrWhiteSpace(data.NewPassword))
        {
            return "모든 필드를 입력해주세요.";
        }

        string hashedOld = PasswordHelper.HashPassword(data.Password);
        string hashedNew = PasswordHelper.HashPassword(data.NewPassword);

        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        // 사용자 검증
        var checkCmd = new MySqlCommand("SELECT COUNT(*) FROM Users WHERE Username=@u AND Email=@e AND PasswordHash=@p", conn);
        checkCmd.Parameters.AddWithValue("@u", data.Username);
        checkCmd.Parameters.AddWithValue("@e", data.Email);
        checkCmd.Parameters.AddWithValue("@p", hashedOld);

        int count = Convert.ToInt32(checkCmd.ExecuteScalar());
        if (count == 0)
            return "사용자 정보가 일치하지 않습니다.";

        // 비밀번호 변경
        var updateCmd = new MySqlCommand("UPDATE Users SET PasswordHash=@np WHERE Username=@u AND Email=@e", conn);
        updateCmd.Parameters.AddWithValue("@np", hashedNew);
        updateCmd.Parameters.AddWithValue("@u", data.Username);
        updateCmd.Parameters.AddWithValue("@e", data.Email);
        updateCmd.ExecuteNonQuery();

        return "비밀번호가 성공적으로 변경되었습니다.";
    }

    // ---------------- 로비 입장 ----------------
    private static string EnterLobby(LobbyEnterRequest data, TcpClient client)
    {
        var session = new Session
        {
            UserId = data.UserId,
            Username = data.Username,
            Client = client,
            LastPing = DateTime.Now
        };

        sessions[data.UserId] = session;

        // 현재 로비에 접속한 사용자 목록 생성
        var lobbyUsers = string.Join(",", sessions.Values.Select(s => s.Username));

        return JsonSerializer.Serialize(new
        {
            Command = "lobby-enter",
            Message = $"현재 접속자: {lobbyUsers}"
        });
    }

    // ---------------- 로그아웃 ----------------
    private static string Logout(LogoutRequest data)
    {
        if (sessions.ContainsKey(data.UserId))
        {
            sessions.TryRemove(data.UserId, out _);
            return "로그아웃 성공";
        }
        return "세션이 존재하지 않습니다.";
    }

    // ---------------- 세션 모니터링 ----------------
    private static void MonitorSessions()
    {
        new Thread(() =>
        {
            while (true)
            {
                foreach (var s in sessions.Values)
                {
                    if (s.Client == null || !IsClientConnected(s.Client))
                    {
                        sessions.TryRemove(s.UserId, out _);
                        Console.WriteLine($"연결 끊김 감지 → 세션 제거: {s.UserId}");
                    }
                }
                Thread.Sleep(5000); // 5초마다 체크
            }
        }).Start();
    }

    // 클라이언트 연결 상태 확인
    private static bool IsClientConnected(TcpClient client)
    {
        try
        {
            return !(client.Client.Poll(1, SelectMode.SelectRead) && client.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    // ---------------- 메시지 브로드캐스트 ----------------
    private static void BroadcastMessage(string msg)
    {
        // 메시지 끝에 \n 추가하여 클라이언트에서 구분 가능하게 함
        byte[] data = Encoding.UTF8.GetBytes(msg + "\n");

        foreach (var s in sessions.Values)
        {
            if (s.Client != null && s.Client.Connected)
            {
                try
                {
                    NetworkStream ns = s.Client.GetStream();
                    ns.Write(data, 0, data.Length);
                }
                catch { }
            }
        }
    }

}

public class Session
{
    public int UserId { get; set; }
    public required string Username { get; set; }
    public required TcpClient Client { get; set; }
    public DateTime LastPing { get; set; } = DateTime.Now;
}

public class DbSettings
{
    public required string Host { get; set; }
    public required string Name { get; set; }
    public required string User { get; set; }
    public required string Password { get; set; }
}