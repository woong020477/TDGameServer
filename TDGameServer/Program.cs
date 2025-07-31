using MySql.Data.MySqlClient;
using Org.BouncyCastle.Asn1.Crmf;
using System.Collections.Concurrent;
using System.Diagnostics;
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

    private static void RemoveUserAndRoom(int userId)
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        // 호스트가 방을 가지고 있는지 확인
        var findRoomCmd = new MySqlCommand("SELECT RoomId FROM Rooms WHERE HostId=@uid", conn);
        findRoomCmd.Parameters.AddWithValue("@uid", userId);
        var roomObj = findRoomCmd.ExecuteScalar();

        if (roomObj != null)
        {
            int roomId = Convert.ToInt32(roomObj);

            // 참가자 모두 제거
            var delPlayers = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid", conn);
            delPlayers.Parameters.AddWithValue("@rid", roomId);
            delPlayers.ExecuteNonQuery();

            // 방 제거
            var delRoom = new MySqlCommand("DELETE FROM Rooms WHERE RoomId=@rid", conn);
            delRoom.Parameters.AddWithValue("@rid", roomId);
            delRoom.ExecuteNonQuery();

            // 모든 클라이언트에게 방이 닫혔음을 알림
            BroadcastMessage(JsonSerializer.Serialize(new {Command = "room-closed", RoomId = roomId}));
        }
    }


    // 방 이름 유효성 검사
    private static bool ValidateRoomName(string roomName)
    {
        int count = 0;

        foreach (char c in roomName)
        {
            if (c >= 0xAC00 && c <= 0xD7A3) // 한글 범위
                count += 2; // 한글은 2글자로 계산
            else
                count += 1; // 영어/숫자 등은 1글자로 계산
        }

        // 한글 최대 20(=10글자*2), 영어 최대 20
        return count <= 20;
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
                    return JsonSerializer.Serialize(new { Command = "pong" }) + "\n";

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

                case "system-chat":
                    var systemChatData = JsonSerializer.Deserialize<SystemChatRequest>(jsonRequest);
                    if (systemChatData != null)
                    {
                        string chatMsg = systemChatData.Message;
                        string jsonMsg = JsonSerializer.Serialize(new
                        {
                            Command = "system-chat",
                            systemChatData.Sender,
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
                                session.UserId,
                                session.Username
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

                case "create-room":
                    {
                        string roomName = doc.RootElement.GetProperty("RoomName").GetString() ?? "";
                        int hostId = doc.RootElement.GetProperty("HostId").GetInt32();
                        string hostName = doc.RootElement.GetProperty("Host").GetString() ?? "";
                        string difficulty = doc.RootElement.GetProperty("Difficulty").GetString() ?? "Normal";

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 방 생성
                        var cmd = new MySqlCommand(@"INSERT INTO Rooms (RoomName, HostId, Host, Difficulty) 
                             VALUES (@name,@hid,@hname,@diff); SELECT LAST_INSERT_ID();", conn);
                        cmd.Parameters.AddWithValue("@name", roomName);
                        cmd.Parameters.AddWithValue("@hid", hostId);
                        cmd.Parameters.AddWithValue("@hname", hostName);
                        cmd.Parameters.AddWithValue("@diff", difficulty);
                        int roomId = Convert.ToInt32(cmd.ExecuteScalar());

                        // 호스트를 1P에 배치
                        var cmd2 = new MySqlCommand(@"INSERT INTO RoomPlayers (RoomId, UserId, Username, PlayerSlot, IsHost) 
                                  VALUES (@rid,@uid,@uname,1,1)", conn);
                        cmd2.Parameters.AddWithValue("@rid", roomId);
                        cmd2.Parameters.AddWithValue("@uid", hostId);
                        cmd2.Parameters.AddWithValue("@uname", hostName);
                        cmd2.ExecuteNonQuery();

                        // 로비 목록 갱신
                        BroadcastRoomList();
                        return JsonSerializer.Serialize(new { Command = "create-room-result", RoomId = roomId, Message = "방이 생성되었습니다." });
                    }

                case "join-room":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int userId = doc.RootElement.GetProperty("UserId").GetInt32();
                        string username = doc.RootElement.GetProperty("Username").GetString() ?? "";

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 이미 방에 있는지 확인
                        var checkCmd = new MySqlCommand("SELECT COUNT(*) FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                        checkCmd.Parameters.AddWithValue("@rid", roomId);
                        checkCmd.Parameters.AddWithValue("@uid", userId);
                        int exists = Convert.ToInt32(checkCmd.ExecuteScalar());

                        if (exists == 0)
                        {
                            // 빈 슬롯 찾기 (1~4 중 비어있는 슬롯)
                            var slotCmd = new MySqlCommand(@"SELECT s FROM (SELECT 1 as s UNION SELECT 2 UNION SELECT 3 UNION SELECT 4) a
                                                            WHERE s NOT IN (SELECT PlayerSlot FROM RoomPlayers WHERE RoomId=@rid)", conn);
                            slotCmd.Parameters.AddWithValue("@rid", roomId);
                            var slot = slotCmd.ExecuteScalar();
                            int playerSlot = slot != null ? Convert.ToInt32(slot) : 0;

                            if (playerSlot > 0)
                            {
                                var cmd = new MySqlCommand(@"INSERT INTO RoomPlayers (RoomId, UserId, Username, PlayerSlot, IsHost)
                                         VALUES (@rid,@uid,@uname,@slot,0)", conn);
                                cmd.Parameters.AddWithValue("@rid", roomId);
                                cmd.Parameters.AddWithValue("@uid", userId);
                                cmd.Parameters.AddWithValue("@uname", username);
                                cmd.Parameters.AddWithValue("@slot", playerSlot);
                                cmd.ExecuteNonQuery();
                            }
                        }

                        // 모든 클라이언트에 방 정보 업데이트
                        BroadcastRoomUpdate(roomId);

                        return JsonSerializer.Serialize(new { Command = "join-room-result", Message = "방에 입장했습니다." });
                    }


                case "kick-player":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int targetUserId = doc.RootElement.GetProperty("TargetUserId").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        var cmd = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                        cmd.Parameters.AddWithValue("@rid", roomId);
                        cmd.Parameters.AddWithValue("@uid", targetUserId);
                        cmd.ExecuteNonQuery();

                        BroadcastRoomUpdate(roomId);
                        return JsonSerializer.Serialize(new { Command = "kick-result", Message = "플레이어를 강퇴했습니다." });
                    }

                case "move-slot":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int fromSlot = doc.RootElement.GetProperty("FromSlot").GetInt32();
                        int toSlot = doc.RootElement.GetProperty("ToSlot").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 이동할 플레이어 정보
                        var getPlayerCmd = new MySqlCommand("SELECT UserId, IsHost FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@from", conn);
                        getPlayerCmd.Parameters.AddWithValue("@rid", roomId);
                        getPlayerCmd.Parameters.AddWithValue("@from", fromSlot);
                        using var reader = getPlayerCmd.ExecuteReader();

                        if (!reader.Read())
                            return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = "이동할 플레이어가 없습니다." });

                        int targetUserId = reader.GetInt32("UserId");
                        bool isHost = reader.GetBoolean("IsHost");
                        reader.Close();

                        if (!isHost)
                        {
                            // Host가 아닐 때는 빈 자리만 찾아 이동
                            int[] slotOrder = new int[] { toSlot, (toSlot % 4) + 1, ((toSlot + 1) % 4) + 1 };
                            int availableSlot = -1;

                            foreach (var slot in slotOrder)
                            {
                                var checkCmd = new MySqlCommand("SELECT COUNT(*) FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@slot AND UserId<>0 AND IsClosed=0", conn);
                                checkCmd.Parameters.AddWithValue("@rid", roomId);
                                checkCmd.Parameters.AddWithValue("@slot", slot);
                                int occupied = Convert.ToInt32(checkCmd.ExecuteScalar());

                                var closedCmd = new MySqlCommand("SELECT COUNT(*) FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@slot AND IsClosed=1", conn);
                                closedCmd.Parameters.AddWithValue("@rid", roomId);
                                closedCmd.Parameters.AddWithValue("@slot", slot);
                                int closed = Convert.ToInt32(closedCmd.ExecuteScalar());

                                if (occupied == 0 && closed == 0)
                                {
                                    availableSlot = slot;
                                    break;
                                }
                            }

                            if (availableSlot == -1)
                            {
                                return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = "이동할 빈 슬롯이 없습니다." });
                            }

                            // 슬롯 이동
                            var delCmd = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@slot", conn);
                            delCmd.Parameters.AddWithValue("@rid", roomId);
                            delCmd.Parameters.AddWithValue("@slot", availableSlot);
                            delCmd.ExecuteNonQuery();

                            var updateCmd = new MySqlCommand("UPDATE RoomPlayers SET PlayerSlot=@to WHERE RoomId=@rid AND PlayerSlot=@from", conn);
                            updateCmd.Parameters.AddWithValue("@rid", roomId);
                            updateCmd.Parameters.AddWithValue("@from", fromSlot);
                            updateCmd.Parameters.AddWithValue("@to", availableSlot);
                            updateCmd.ExecuteNonQuery();

                            BroadcastRoomUpdate(roomId);
                            return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = $"{fromSlot}P → {availableSlot}P로 이동 완료" });
                        }
                        else
                        {
                            // Host는 강제 자리 바꿈 가능
                            var swapCmd = new MySqlCommand("UPDATE RoomPlayers SET PlayerSlot=0 WHERE RoomId=@rid AND PlayerSlot=@to", conn);
                            swapCmd.Parameters.AddWithValue("@rid", roomId);
                            swapCmd.Parameters.AddWithValue("@to", toSlot);
                            swapCmd.ExecuteNonQuery();

                            var updateCmd = new MySqlCommand("UPDATE RoomPlayers SET PlayerSlot=@to WHERE RoomId=@rid AND PlayerSlot=@from", conn);
                            updateCmd.Parameters.AddWithValue("@rid", roomId);
                            updateCmd.Parameters.AddWithValue("@from", fromSlot);
                            updateCmd.Parameters.AddWithValue("@to", toSlot);
                            updateCmd.ExecuteNonQuery();

                            BroadcastRoomUpdate(roomId);
                            return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = $"(호스트) {fromSlot}P → {toSlot}P 강제 이동 완료" });
                        }
                    }


                case "change-host":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int newHostUserId = doc.RootElement.GetProperty("TargetUserId").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 기존 호스트 해제
                        var resetCmd = new MySqlCommand("UPDATE RoomPlayers SET IsHost=0 WHERE RoomId=@rid", conn);
                        resetCmd.Parameters.AddWithValue("@rid", roomId);
                        resetCmd.ExecuteNonQuery();

                        // 새 호스트 지정
                        var setCmd = new MySqlCommand("UPDATE RoomPlayers SET IsHost=1 WHERE RoomId=@rid AND UserId=@uid", conn);
                        setCmd.Parameters.AddWithValue("@rid", roomId);
                        setCmd.Parameters.AddWithValue("@uid", newHostUserId);
                        setCmd.ExecuteNonQuery();

                        // Rooms 테이블 호스트 정보도 갱신
                        var hostNameCmd = new MySqlCommand("SELECT Username FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                        hostNameCmd.Parameters.AddWithValue("@rid", roomId);
                        hostNameCmd.Parameters.AddWithValue("@uid", newHostUserId);
                        string newHostName = hostNameCmd.ExecuteScalar()?.ToString() ?? "";

                        var updateRoomCmd = new MySqlCommand("UPDATE Rooms SET HostId=@uid, Host=@uname WHERE RoomId=@rid", conn);
                        updateRoomCmd.Parameters.AddWithValue("@uid", newHostUserId);
                        updateRoomCmd.Parameters.AddWithValue("@uname", newHostName);
                        updateRoomCmd.Parameters.AddWithValue("@rid", roomId);
                        updateRoomCmd.ExecuteNonQuery();

                        BroadcastRoomUpdate(roomId);
                        // 로비 목록 갱신
                        BroadcastRoomList();
                        return JsonSerializer.Serialize(new { Command = "change-host-result", Message = "호스트가 변경되었습니다." });
                    }

                case "open-slot":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int slot = doc.RootElement.GetProperty("Slot").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // IsClosed 해제 (열린 슬롯 표시)
                        var cmd = new MySqlCommand(@"INSERT INTO RoomPlayers (RoomId, UserId, Username, PlayerSlot, IsClosed)
                                 VALUES (@rid, 0, 'Open', @slot, 0)
                                 ON DUPLICATE KEY UPDATE IsClosed=0, Username='Open', UserId=0", conn);
                        cmd.Parameters.AddWithValue("@rid", roomId);
                        cmd.Parameters.AddWithValue("@slot", slot);
                        cmd.ExecuteNonQuery();

                        BroadcastRoomUpdate(roomId);
                        // 로비 목록 갱신
                        BroadcastRoomList();

                        return JsonSerializer.Serialize(new { Command = "open-slot-result", Message = "슬롯을 오픈했습니다." });
                    }

                case "close-slot":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int slot = doc.RootElement.GetProperty("Slot").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 해당 슬롯에 플레이어가 있으면 강퇴
                        var delCmd = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@slot AND UserId<>0", conn);
                        delCmd.Parameters.AddWithValue("@rid", roomId);
                        delCmd.Parameters.AddWithValue("@slot", slot);
                        delCmd.ExecuteNonQuery();

                        // Close 슬롯 표시
                        var cmd = new MySqlCommand(@"INSERT INTO RoomPlayers (RoomId, UserId, Username, PlayerSlot, IsClosed)
                                 VALUES (@rid, 0, 'Close', @slot, 1)
                                 ON DUPLICATE KEY UPDATE IsClosed=1, Username='Close', UserId=0", conn);
                        cmd.Parameters.AddWithValue("@rid", roomId);
                        cmd.Parameters.AddWithValue("@slot", slot);
                        cmd.ExecuteNonQuery();

                        BroadcastRoomUpdate(roomId);
                        // 로비 목록 갱신
                        BroadcastRoomList();

                        return JsonSerializer.Serialize(new { Command = "close-slot-result", Message = "슬롯을 닫았습니다." });
                    }

                case "exit-room":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int userId = doc.RootElement.GetProperty("UserId").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 현재 유저가 호스트인지 확인
                        var hostCheckCmd = new MySqlCommand("SELECT IsHost FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                        hostCheckCmd.Parameters.AddWithValue("@rid", roomId);
                        hostCheckCmd.Parameters.AddWithValue("@uid", userId);
                        var isHostObj = hostCheckCmd.ExecuteScalar();

                        if (isHostObj == null)
                            return JsonSerializer.Serialize(new { Command = "exit-room-result", Message = "해당 플레이어가 방에 없습니다." });

                        bool isHost = Convert.ToBoolean(isHostObj);

                        if (isHost)
                        {
                            // 1) Host가 나가면 전체 방 삭제
                            var deletePlayersCmd = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid", conn);
                            deletePlayersCmd.Parameters.AddWithValue("@rid", roomId);
                            deletePlayersCmd.ExecuteNonQuery();

                            var deleteRoomCmd = new MySqlCommand("DELETE FROM Rooms WHERE RoomId=@rid", conn);
                            deleteRoomCmd.Parameters.AddWithValue("@rid", roomId);
                            deleteRoomCmd.ExecuteNonQuery();

                            // 방이 사라졌음을 참가자들에게 알림
                            BroadcastMessage(JsonSerializer.Serialize(new { Command = "room-closed", RoomId = roomId }));

                            // 로비 목록 갱신
                            BroadcastRoomList();

                            return JsonSerializer.Serialize(new { Command = "exit-room-result", Message = "방이 삭제되었습니다." });
                        }
                        else
                        {
                            // 2) 일반 플레이어 나가기
                            var delPlayerCmd = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                            delPlayerCmd.Parameters.AddWithValue("@rid", roomId);
                            delPlayerCmd.Parameters.AddWithValue("@uid", userId);
                            delPlayerCmd.ExecuteNonQuery();

                            // 자리를 "Open"으로 채움
                            var maxSlotCmd = new MySqlCommand("SELECT MAX(PlayerSlot)+1 FROM RoomPlayers WHERE RoomId=@rid", conn);
                            maxSlotCmd.Parameters.AddWithValue("@rid", roomId);
                            var nextSlot = Convert.ToInt32(maxSlotCmd.ExecuteScalar() ?? 1);

                            // 빈자리 기록
                            var insertCmd = new MySqlCommand(@"INSERT INTO RoomPlayers(RoomId,UserId,Username,PlayerSlot,IsClosed)
                                           VALUES(@rid,0,'Open',@slot,0)
                                           ON DUPLICATE KEY UPDATE UserId=0,Username='Open',IsClosed=0", conn);
                            insertCmd.Parameters.AddWithValue("@rid", roomId);
                            insertCmd.Parameters.AddWithValue("@slot", nextSlot);
                            insertCmd.ExecuteNonQuery();

                            // 남은 참가자들에게 방 정보 갱신
                            BroadcastRoomUpdate(roomId);

                            // 로비 목록 갱신
                            BroadcastRoomList();

                            return JsonSerializer.Serialize(new { Command = "exit-room-result", Message = "방에서 나갔습니다." });
                        }
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

        return JsonSerializer.Serialize(new
        {
            Command = "reset-password-result",
            Message = $"{tempPw}"
        });
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

        SendRoomListToClient(client);

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
            RemoveUserAndRoom(data.UserId);
            sessions.TryRemove(data.UserId, out _);
            Console.WriteLine($"로그아웃 결과: User ID {data.UserId}번 로그아웃됨");
            return JsonSerializer.Serialize(new
            {
                Command = "logout-result",
                Message = "로그아웃 성공"
            });
        }
        return JsonSerializer.Serialize(new
        {
            Command = "logout-result",
            Message = "세션이 존재하지 않습니다."
        });
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

    // ---------------- 방 목록 브로드캐스트 ----------------
    private static void BroadcastRoomList()
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand("SELECT RoomId, RoomName, Host, Difficulty, MaxPlayers, (SELECT COUNT(*) FROM RoomPlayers rp WHERE rp.RoomId = r.RoomId) AS CurrentPlayers FROM Rooms r", conn);

        var reader = cmd.ExecuteReader();
        var roomList = new List<object>();
        while (reader.Read())
        {
            roomList.Add(new
            {
                RoomId = reader.GetInt32("RoomId"),
                RoomName = reader.GetString("RoomName"),
                Host = reader.GetString("Host"),
                Difficulty = reader.GetString("Difficulty"),
                CurrentPlayers = reader.GetInt32("CurrentPlayers"),
                MaxPlayers = reader.GetInt32("MaxPlayers")
            });
        }

        string jsonMsg = JsonSerializer.Serialize(new
        {
            Command = "room-list-update",
            Rooms = roomList
        });

        BroadcastMessage(jsonMsg);
    }

    // ---------------- 방 목록을 클라이언트에 전송 ----------------
    private static void SendRoomListToClient(TcpClient client)
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand("SELECT RoomId, RoomName, Host, Difficulty, MaxPlayers, (SELECT COUNT(*) FROM RoomPlayers rp WHERE rp.RoomId = r.RoomId) AS CurrentPlayers FROM Rooms r", conn);
        var reader = cmd.ExecuteReader();

        var roomList = new List<object>();
        while (reader.Read())
        {
            roomList.Add(new
            {
                RoomId = reader.GetInt32("RoomId"),
                RoomName = reader.GetString("RoomName"),
                Host = reader.GetString("Host"),
                Difficulty = reader.GetString("Difficulty"),
                CurrentPlayers = reader.GetInt32("CurrentPlayers"),
                MaxPlayers = reader.GetInt32("MaxPlayers")
            });
        }

        string jsonMsg = JsonSerializer.Serialize(new
        {
            Command = "room-list-update",
            Rooms = roomList
        });

        byte[] data = Encoding.UTF8.GetBytes(jsonMsg + "\n");
        client.GetStream().Write(data, 0, data.Length);
    }

    // ---------------- 방 업데이트 브로드캐스트 ----------------
    private static void BroadcastRoomUpdate(int roomId)
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand(@"SELECT RoomName, Host, Difficulty FROM Rooms WHERE RoomId=@rid", conn);
        cmd.Parameters.AddWithValue("@rid", roomId);
        var reader = cmd.ExecuteReader();
        if (!reader.Read()) return;

        string roomName = reader.GetString("RoomName");
        string host = reader.GetString("Host");
        string difficulty = reader.GetString("Difficulty");
        reader.Close();

        var cmd2 = new MySqlCommand(@"SELECT PlayerSlot, UserId, Username, IsHost, IsClosed FROM RoomPlayers WHERE RoomId=@rid ORDER BY PlayerSlot", conn);
        cmd2.Parameters.AddWithValue("@rid", roomId);
        var players = new List<object>();
        var r2 = cmd2.ExecuteReader();
        while (r2.Read())
        {
            players.Add(new
            {
                PlayerSlot = r2.GetInt32("PlayerSlot"),
                UserId = r2.GetInt32("UserId"),
                Username = r2.GetString("Username"),
                IsHost = r2.GetBoolean("IsHost"),
                IsClosed = r2.GetBoolean("IsClosed")
            });
        }

        string jsonMsg = JsonSerializer.Serialize(new
        {
            Command = "room-update",
            RoomId = roomId,
            RoomName = roomName,
            Host = host,
            Difficulty = difficulty,
            Players = players
        });

        BroadcastMessage(jsonMsg);
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

public class RoomInfo
{
    public required string RoomName { get; set; }
    public required string Host { get; set; }
    public required string Difficulty { get; set; }
    public int CurrentPlayers { get; set; }
    public int MaxPlayers { get; set; } = 4;
}