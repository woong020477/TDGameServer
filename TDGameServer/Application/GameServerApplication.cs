using MySql.Data.MySqlClient;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using TDGameServer.Configuration;
using TDGameServer.Helpers;
using TDGameServer.Models;
using TDGameServer.Networking;

namespace TDGameServer.Application;

/// <summary>
/// 인증, 로비, 방 수명주기와 세션 상태를 조정하는 서버 애플리케이션 계층입니다.
/// 소켓 수명주기와 실행 설정은 각각 Networking, Configuration 계층에 위임합니다.
/// </summary>
public static class GameServerApplication
{
    // MySQL 연결 문자열
    private static string connectionString = string.Empty;

    // 접속 세션 관리
    private static readonly ConcurrentDictionary<int, Session> sessions = new();
    private static readonly ConcurrentDictionary<int, byte> startedRooms = new();

    /// <summary>
    /// 실행 설정을 로드하고 TCP, UDP, 세션 모니터를 구성합니다.
    /// Ctrl+C를 하나의 취소 토큰으로 전달해 각 네트워크 자원이 정상적으로 종료되게 합니다.
    /// </summary>
    public static async Task RunAsync()
    {
        ServerOptions options = ServerOptions.Load();
        connectionString = options.ConnectionString;
        Console.WriteLine("[Server] 설정 로드 완료");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        using var udpServer = new UdpServer(options.UdpPort);
        udpServer.Start();
        MonitorSessions(shutdown.Token);

        var tcpServer = new TcpServerHost(options.TcpPort, HandleRequest);
        Console.WriteLine("서버를 종료하려면 Ctrl+C를 누르세요.");
        await tcpServer.RunAsync(shutdown.Token);
    }

    /// <summary>브로드캐스트와 개별 응답이 같은 줄바꿈 전송 규칙을 사용하도록 위임합니다.</summary>
    private static void SendMessage(TcpClient client, string message)
    {
        TcpMessageWriter.Send(client, message);
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
            startedRooms.TryRemove(roomId, out _);
        }
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

                case "change-title":
                    var changeTitleData = JsonSerializer.Deserialize<ChangeTitleRequest>(jsonRequest);
                    Session? titleRequester = FindSessionByClient(client);
                    if (changeTitleData == null)
                        return JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });

                    if (titleRequester == null || titleRequester.UserId != changeTitleData.UserId)
                        return JsonSerializer.Serialize(new { Command = "error", Message = "본인의 칭호만 변경할 수 있습니다." });

                    return ChangeUserTitle(changeTitleData);

                case "chat":
                    {
                        var chatData = JsonSerializer.Deserialize<ChatRequest>(jsonRequest);
                        if (chatData != null)
                        {
                            string chatMsg = chatData.Message;
                            string chatType = chatData.ChatType ?? "All";
                            string targetUser = chatData.TargetUsername ?? string.Empty;

                            // 유저 Title 가져오기
                            string titleName = "";
                            string gradient = "#FFFFFF"; // 기본 흰색
                            using (var conn = new MySqlConnection(connectionString))
                            {
                                conn.Open();
                                var cmd = new MySqlCommand(@"
                                SELECT t.TitleName, t.ColorGradient
                                FROM Users u
                                LEFT JOIN Titles t ON u.TitleId = t.TitleId
                                WHERE u.Id=@uid", conn);
                                cmd.Parameters.AddWithValue("@uid", chatData.UserId);
                                using var reader = cmd.ExecuteReader();
                                if (reader.Read())
                                {
                                    titleName = reader.IsDBNull(0) ? "" : reader.GetString(0);
                                    gradient = reader.IsDBNull(1) ? "#FFFFFF" : reader.GetString(1);
                                }
                            }

                            var jsonMsg = JsonSerializer.Serialize(new
                            {
                                Command = "chat",
                                Sender = chatData.Username,
                                Message = chatMsg,
                                ChatType = chatType,
                                Target = targetUser,
                                TitleName = titleName,
                                ColorGradient = gradient
                            });

                            // 채팅타입별 브로드캐스트
                            if (chatType.Equals("All", StringComparison.OrdinalIgnoreCase))
                            {
                                BroadcastMessage(jsonMsg); // 로비 전체
                            }
                            else if (chatType.Equals("Room", StringComparison.OrdinalIgnoreCase))
                            {
                                BroadcastMessageToRoom(chatData.UserId, jsonMsg); // 같은 방만
                            }
                            else if (chatType.Equals("Whisper", StringComparison.OrdinalIgnoreCase) && targetUser != null)
                            {
                                SendWhisper(chatData.UserId, targetUser, jsonMsg);
                            }
                            return JsonSerializer.Serialize(new { Command = "chat-ok" });
                        }
                        return JsonSerializer.Serialize(new { Command = "error", Message = "요청 파싱 실패" });
                    }


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
                        string difficulty = doc.RootElement.GetProperty("Difficulty").GetString() ?? "Easy";

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 0) Host의 ColorGradient 가져오기
                        string hostName = "";
                        string colorGradient = "#000000";
                        var cmdHost = new MySqlCommand(@"
                            SELECT u.Username, COALESCE(t.ColorGradient, '#000000')
                            FROM Users u
                            LEFT JOIN Titles t ON u.TitleId = t.TitleId
                            WHERE u.Id=@hid;", conn);
                        cmdHost.Parameters.AddWithValue("@hid", hostId);

                        using (var reader = cmdHost.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                hostName = reader.GetString(0);
                                colorGradient = reader.GetString(1);
                            }
                        }

                        // 1) 방 생성
                        var cmd = new MySqlCommand(@"
                                                    INSERT INTO Rooms (RoomName, HostId, Host, Difficulty) 
                                                    VALUES (@name, @hid, @hname, @diff);
                                                    SELECT LAST_INSERT_ID();", conn);
                        cmd.Parameters.AddWithValue("@name", roomName);
                        cmd.Parameters.AddWithValue("@hid", hostId);
                        cmd.Parameters.AddWithValue("@hname", hostName);
                        cmd.Parameters.AddWithValue("@diff", difficulty);
                        int roomId = Convert.ToInt32(cmd.ExecuteScalar());

                        // 2) 4개 슬롯 미리 생성
                        var cmd2 = new MySqlCommand(@"
                                                    INSERT INTO RoomPlayers (RoomId, UserId, Username, PlayerSlot, IsHost, IsClosed)
                                                    VALUES
                                                    (@rid, @hid, @hname, 1, 1, 0),
                                                    (@rid, 0, '', 2, 0, 0),
                                                    (@rid, 0, '', 3, 0, 0),
                                                    (@rid, 0, '', 4, 0, 0);", conn);
                        cmd2.Parameters.AddWithValue("@rid", roomId);
                        cmd2.Parameters.AddWithValue("@hid", hostId);
                        cmd2.Parameters.AddWithValue("@hname", hostName);
                        cmd2.ExecuteNonQuery();

                        // 3) 로비 목록 갱신
                        BroadcastRoomList();

                        return JsonSerializer.Serialize(new
                        {
                            Command = "create-room-result",
                            RoomId = roomId,
                            Host = hostName,
                            ColorGradient = colorGradient,
                            Message = "방이 생성되었습니다."
                        });
                    }


                case "join-room":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int userId = doc.RootElement.GetProperty("UserId").GetInt32();
                        string username = doc.RootElement.GetProperty("Username").GetString() ?? "";

                        if (startedRooms.ContainsKey(roomId))
                            return JsonSerializer.Serialize(new { Command = "join-room-result", Message = "이미 게임이 시작된 방입니다." });

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 이미 방에 있는지 확인
                        var checkCmd = new MySqlCommand("SELECT COUNT(*) FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                        checkCmd.Parameters.AddWithValue("@rid", roomId);
                        checkCmd.Parameters.AddWithValue("@uid", userId);
                        int exists = Convert.ToInt32(checkCmd.ExecuteScalar());

                        if (exists == 0)
                        {
                            // 빈 슬롯(열린 슬롯) 찾기
                            var slotCmd = new MySqlCommand(@"SELECT PlayerSlot FROM RoomPlayers 
                                                             WHERE RoomId=@rid AND UserId=0 AND IsClosed=0 
                                                             ORDER BY PlayerSlot LIMIT 1", conn);
                            slotCmd.Parameters.AddWithValue("@rid", roomId);
                            var slot = slotCmd.ExecuteScalar();

                            if (slot != null)
                            {
                                int playerSlot = Convert.ToInt32(slot);

                                // 슬롯 점유 처리
                                var cmd = new MySqlCommand(@"UPDATE RoomPlayers 
                                         SET UserId=@uid, Username=@uname 
                                         WHERE RoomId=@rid AND PlayerSlot=@slot", conn);
                                cmd.Parameters.AddWithValue("@rid", roomId);
                                cmd.Parameters.AddWithValue("@uid", userId);
                                cmd.Parameters.AddWithValue("@uname", username);
                                cmd.Parameters.AddWithValue("@slot", playerSlot);
                                cmd.ExecuteNonQuery();
                            }
                            else
                            {
                                return JsonSerializer.Serialize(new { Command = "join-room-result", Message = "빈 슬롯이 없습니다." });
                            }
                        }

                        // 모든 클라이언트에 방 정보 업데이트
                        BroadcastRoomUpdate(roomId);
                        BroadcastRoomList();

                        return JsonSerializer.Serialize(new
                        {
                            Command = "join-room-result",
                            RoomId = roomId,
                            Message = "방에 입장했습니다."
                        });
                    }

                case "kick-player":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int targetUserId = doc.RootElement.GetProperty("TargetUserId").GetInt32();
                        Session? requester = FindSessionByClient(client);
                        if (requester == null || !IsRoomHost(roomId, requester.UserId))
                            return JsonSerializer.Serialize(new { Command = "error", Message = "방장만 플레이어를 강퇴할 수 있습니다." });
                        if (targetUserId == requester.UserId)
                            return JsonSerializer.Serialize(new { Command = "error", Message = "방장은 자신을 강퇴할 수 없습니다." });

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 제거 전에 대상에게 먼저 알린다. 제거 후에는 방 브로드캐스트 대상에서 빠지기 때문이다.
                        if (sessions.TryGetValue(targetUserId, out Session? kickedSession) && kickedSession != null)
                        {
                            SendMessageToClient(kickedSession, JsonSerializer.Serialize(new
                            {
                                Command = "kicked-from-room",
                                RoomId = roomId,
                                Message = "방장에 의해 강퇴되었습니다."
                            }));
                        }

                        // 대상 슬롯을 빈 상태로 초기화
                        var cmd = new MySqlCommand(@"
                                                    UPDATE RoomPlayers
                                                    SET UserId = 0, Username = 'Open', IsHost = 0, IsClosed = 0
                                                    WHERE RoomId=@rid AND UserId=@uid", conn);
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
                        Session? requester = FindSessionByClient(client);

                        if (requester == null)
                            return JsonSerializer.Serialize(new { Command = "error", Message = "로그인 세션을 확인할 수 없습니다." });

                        if (fromSlot == toSlot)
                            return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = "같은 슬롯으로는 이동할 수 없습니다." });

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 이동할 플레이어 정보
                        var getPlayerCmd = new MySqlCommand(
                            "SELECT UserId, IsHost FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@from", conn);
                        getPlayerCmd.Parameters.AddWithValue("@rid", roomId);
                        getPlayerCmd.Parameters.AddWithValue("@from", fromSlot);
                        using var reader = getPlayerCmd.ExecuteReader();

                        if (!reader.Read())
                            return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = "이동할 플레이어가 없습니다." });

                        int targetUserId = reader.GetInt32("UserId");
                        bool isHost = reader.GetBoolean("IsHost");
                        reader.Close();

                        bool requesterIsHost = IsRoomHost(roomId, requester.UserId);
                        if (!requesterIsHost && targetUserId != requester.UserId)
                            return JsonSerializer.Serialize(new { Command = "error", Message = "자신의 자리만 이동할 수 있습니다." });

                        // ---------------- 비호스트 처리 ----------------
                        if (!isHost)
                        {
                            // 대상 슬롯 상태 확인
                            var checkSlot = new MySqlCommand(
                                "SELECT UserId, IsClosed FROM RoomPlayers WHERE RoomId=@rid AND PlayerSlot=@slot", conn);
                            checkSlot.Parameters.AddWithValue("@rid", roomId);
                            checkSlot.Parameters.AddWithValue("@slot", toSlot);
                            using var slotReader = checkSlot.ExecuteReader();

                            if (!slotReader.Read())
                                return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = "대상 슬롯을 찾을 수 없습니다." });

                            int destUserId = slotReader.GetInt32("UserId");
                            bool destClosed = slotReader.GetBoolean("IsClosed");
                            slotReader.Close();

                            if (destUserId != 0 || destClosed)
                                return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = "대상 슬롯이 사용 중이거나 닫혀 있습니다." });

                            // 빈 슬롯 행도 보존해야 하므로 두 행의 슬롯 번호를 트랜잭션으로 교환한다.
                            using var transaction = conn.BeginTransaction();
                            var moveEmptyAside = new MySqlCommand(
                                "UPDATE RoomPlayers SET PlayerSlot=99 WHERE RoomId=@rid AND PlayerSlot=@to", conn, transaction);
                            moveEmptyAside.Parameters.AddWithValue("@rid", roomId);
                            moveEmptyAside.Parameters.AddWithValue("@to", toSlot);
                            moveEmptyAside.ExecuteNonQuery();

                            var movePlayer = new MySqlCommand(
                                "UPDATE RoomPlayers SET PlayerSlot=@to WHERE RoomId=@rid AND PlayerSlot=@from AND UserId=@uid", conn, transaction);
                            movePlayer.Parameters.AddWithValue("@rid", roomId);
                            movePlayer.Parameters.AddWithValue("@from", fromSlot);
                            movePlayer.Parameters.AddWithValue("@to", toSlot);
                            movePlayer.Parameters.AddWithValue("@uid", targetUserId);
                            movePlayer.ExecuteNonQuery();

                            var restoreEmpty = new MySqlCommand(
                                "UPDATE RoomPlayers SET PlayerSlot=@from WHERE RoomId=@rid AND PlayerSlot=99", conn, transaction);
                            restoreEmpty.Parameters.AddWithValue("@rid", roomId);
                            restoreEmpty.Parameters.AddWithValue("@from", fromSlot);
                            restoreEmpty.ExecuteNonQuery();
                            transaction.Commit();

                            BroadcastRoomUpdate(roomId);
                            return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = $"{fromSlot}P → {toSlot}P로 이동 완료" });
                        }
                        else
                        {
                            // ---------------- 호스트 자리 강제 교환 처리 ----------------
                            using var transaction = conn.BeginTransaction();

                            try
                            {
                                // 1) 대상 슬롯을 임시 슬롯(99)로 이동
                                var cmd1 = new MySqlCommand(
                                    "UPDATE RoomPlayers SET PlayerSlot=99 WHERE RoomId=@rid AND PlayerSlot=@slot", conn, transaction);
                                cmd1.Parameters.AddWithValue("@rid", roomId);
                                cmd1.Parameters.AddWithValue("@slot", toSlot);
                                cmd1.ExecuteNonQuery();

                                // 2) fromSlot → toSlot
                                var cmd2 = new MySqlCommand(
                                    "UPDATE RoomPlayers SET PlayerSlot=@to WHERE RoomId=@rid AND PlayerSlot=@from", conn, transaction);
                                cmd2.Parameters.AddWithValue("@rid", roomId);
                                cmd2.Parameters.AddWithValue("@from", fromSlot);
                                cmd2.Parameters.AddWithValue("@to", toSlot);
                                cmd2.ExecuteNonQuery();

                                // 3) 99 → fromSlot
                                var cmd3 = new MySqlCommand(
                                    "UPDATE RoomPlayers SET PlayerSlot=@from WHERE RoomId=@rid AND PlayerSlot=99", conn, transaction);
                                cmd3.Parameters.AddWithValue("@rid", roomId);
                                cmd3.Parameters.AddWithValue("@from", fromSlot);
                                cmd3.ExecuteNonQuery();

                                transaction.Commit();
                                BroadcastRoomUpdate(roomId);
                                return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = $"(호스트) {fromSlot}P ↔ {toSlot}P 자리 교환 완료" });
                            }
                            catch (Exception ex)
                            {
                                transaction.Rollback();
                                return JsonSerializer.Serialize(new { Command = "move-slot-result", Message = $"자리 이동 중 오류: {ex.Message}" });
                            }
                        }
                    }

                case "change-host":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int newHostUserId = doc.RootElement.GetProperty("TargetUserId").GetInt32();
                        Session? requester = FindSessionByClient(client);
                        if (requester == null || !IsRoomHost(roomId, requester.UserId))
                            return JsonSerializer.Serialize(new { Command = "error", Message = "현재 방장만 방장을 위임할 수 있습니다." });

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        var targetCheckCmd = new MySqlCommand(
                            "SELECT COUNT(*) FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid AND UserId<>0", conn);
                        targetCheckCmd.Parameters.AddWithValue("@rid", roomId);
                        targetCheckCmd.Parameters.AddWithValue("@uid", newHostUserId);
                        if (Convert.ToInt32(targetCheckCmd.ExecuteScalar()) != 1)
                            return JsonSerializer.Serialize(new { Command = "error", Message = "방장으로 지정할 플레이어를 찾을 수 없습니다." });

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

                        // IsClosed 해제
                        var cmd = new MySqlCommand(@"
                                                    UPDATE RoomPlayers 
                                                    SET IsClosed=0, Username='Open', UserId=0
                                                    WHERE RoomId=@rid AND PlayerSlot=@slot;", conn);
                        cmd.Parameters.AddWithValue("@rid", roomId);
                        cmd.Parameters.AddWithValue("@slot", slot);
                        cmd.ExecuteNonQuery();

                        BroadcastRoomUpdate(roomId);
                        BroadcastRoomList();

                        return JsonSerializer.Serialize(new { Command = "open-slot-result", Message = "슬롯을 열었습니다." });
                    }

                case "close-slot":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        int slot = doc.RootElement.GetProperty("Slot").GetInt32();

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        var cmd = new MySqlCommand(@"
                                                    UPDATE RoomPlayers 
                                                    SET IsClosed = 1, UserId = 0, Username = 'Close' 
                                                    WHERE RoomId=@rid AND PlayerSlot=@slot;", conn);
                        cmd.Parameters.AddWithValue("@rid", roomId);
                        cmd.Parameters.AddWithValue("@slot", slot);
                        cmd.ExecuteNonQuery();

                        BroadcastRoomUpdate(roomId);
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
                        var hostCheckCmd = new MySqlCommand("SELECT IsHost, PlayerSlot FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid", conn);
                        hostCheckCmd.Parameters.AddWithValue("@rid", roomId);
                        hostCheckCmd.Parameters.AddWithValue("@uid", userId);
                        using var reader = hostCheckCmd.ExecuteReader();

                        if (!reader.Read())
                            return JsonSerializer.Serialize(new { Command = "exit-room-result", Message = "해당 플레이어가 방에 없습니다." });

                        bool isHost = reader.GetBoolean("IsHost");
                        int playerSlot = reader.GetInt32("PlayerSlot");
                        reader.Close();

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
                            // 2) 일반 플레이어 나가기 → 슬롯 초기화
                            var updateCmd = new MySqlCommand(@"
                                                            UPDATE RoomPlayers
                                                            SET UserId=0, Username='Open', IsClosed=0
                                                            WHERE RoomId=@rid AND PlayerSlot=@slot", conn);
                            updateCmd.Parameters.AddWithValue("@rid", roomId);
                            updateCmd.Parameters.AddWithValue("@slot", playerSlot);
                            updateCmd.ExecuteNonQuery();

                            // 남은 참가자들에게 방 정보 갱신
                            BroadcastRoomUpdate(roomId);

                            // 로비 목록 갱신
                            BroadcastRoomList();

                            return JsonSerializer.Serialize(new { Command = "exit-room-result", Message = "방에서 나갔습니다." });
                        }
                    }

                case "request-room-list":
                    {
                        SendRoomListToClient(client);
                        return JsonSerializer.Serialize(new
                        {
                            Command = "request-room-list-result",
                            Message = "방 목록을 갱신했습니다."
                        });
                    }

                case "request-room-info":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();

                        if (startedRooms.ContainsKey(roomId))
                            return JsonSerializer.Serialize(new { Command = "room-info", Message = "이미 게임이 시작된 방입니다." });

                        using var conn = new MySqlConnection(connectionString);
                        conn.Open();

                        // 1) 방 기본 정보 조회
                        var cmdRoom = new MySqlCommand("SELECT RoomName, Host, Difficulty FROM Rooms WHERE RoomId=@rid", conn);
                        cmdRoom.Parameters.AddWithValue("@rid", roomId);
                        using var reader = cmdRoom.ExecuteReader();
                        if (!reader.Read())
                        {
                            return JsonSerializer.Serialize(new { Command = "room-info", Message = "방을 찾을 수 없습니다." });
                        }

                        string roomName = reader.GetString("RoomName");
                        string hostName = reader.GetString("Host");
                        string difficulty = reader.GetString("Difficulty");
                        reader.Close();

                        // 2) 플레이어 목록 조회
                        var cmdPlayers = new MySqlCommand(@"
                            SELECT rp.PlayerSlot, rp.UserId, rp.Username, rp.IsHost, rp.IsClosed,
                                   IFNULL(t.TitleId, 0) AS TitleId,
                                   IFNULL(t.TitleName, '') AS TitleName,
                                   IFNULL(t.ColorGradient, '#000000') AS ColorGradient
                            FROM RoomPlayers rp
                            LEFT JOIN Users u ON rp.UserId = u.Id
                            LEFT JOIN Titles t ON u.TitleId = t.TitleId
                            WHERE rp.RoomId=@rid
                            ORDER BY rp.PlayerSlot", conn);
                        cmdPlayers.Parameters.AddWithValue("@rid", roomId);

                        var players = new List<object>();
                        using var reader2 = cmdPlayers.ExecuteReader();
                        while (reader2.Read())
                        {
                            players.Add(new
                            {
                                PlayerSlot = reader2.GetInt32("PlayerSlot"),
                                UserId = reader2.GetInt32("UserId"),
                                Username = reader2.GetString("Username"),
                                IsHost = reader2.GetBoolean("IsHost"),
                                IsClosed = reader2.GetBoolean("IsClosed"),
                                TitleId = reader2.GetInt32("TitleId"),
                                TitleName = reader2.GetString("TitleName"),
                                ColorGradient = reader2.GetString("ColorGradient")
                            });
                        }

                        // 3) 클라이언트로 JSON 반환
                        return JsonSerializer.Serialize(new
                        {
                            Command = "room-info",
                            RoomId = roomId,
                            RoomName = roomName,
                            Host = hostName,
                            Difficulty = difficulty,
                            Players = players
                        });
                    }

                // 게임 시작 시 방 정보 재사용
                case "game-start":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();

                        if (startedRooms.ContainsKey(roomId))
                            return JsonSerializer.Serialize(new { Command = "error", Message = "이미 시작된 게임입니다." });

                        Session? requester = FindSessionByClient(client);
                        if (requester == null || !IsRoomHost(roomId, requester.UserId))
                        {
                            return JsonSerializer.Serialize(new
                            {
                                Command = "error",
                                Message = "방장만 게임을 시작할 수 있습니다."
                            });
                        }

                        var roomData = GetRoomInfo(roomId);
                        startedRooms.TryAdd(roomId, 0);

                        var startMsg = JsonSerializer.Serialize(new
                        {
                            Command = "room-info-game-start",
                            RoomId = roomId,
                            RoomName = roomData.RoomName,
                            Host = roomData.Host,
                            Difficulty = roomData.Difficulty,
                            Players = roomData.Players
                        });

                        // 모든 유저에게 단일 패킷 전송 (로비→게임 전환 데이터)
                        BroadcastMessageToRoomByRoomId(roomId, startMsg);

                        // 멤버십은 룸 채팅을 위해 유지하고 로비 목록/신규 입장에서만 숨긴다.
                        BroadcastRoomList();

                        return JsonSerializer.Serialize(new
                        {
                            Command = "game-start",
                            RoomId = roomId,
                            Message = "게임을 시작했습니다."
                        });
                    }

                case "close-match-room":
                    {
                        int roomId = doc.RootElement.GetProperty("RoomId").GetInt32();
                        Session? requester = FindSessionByClient(client);
                        if (requester == null || !IsRoomHost(roomId, requester.UserId))
                            return JsonSerializer.Serialize(new { Command = "error", Message = "방장만 종료된 게임 방을 정리할 수 있습니다." });

                        BroadcastMessageToRoomByRoomId(roomId, JsonSerializer.Serialize(new
                        {
                            Command = "room-closed",
                            RoomId = roomId
                        }));
                        DeleteRoom(roomId);
                        startedRooms.TryRemove(roomId, out _);
                        BroadcastRoomList();
                        return JsonSerializer.Serialize(new { Command = "close-match-room-result", Message = "게임 방을 정리했습니다." });
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

        // 사용자 정보 조회
        var cmd = new MySqlCommand("SELECT Id, Username, IFNULL(TitleId, 0) AS TitleId FROM Users WHERE Email=@e AND PasswordHash=@p;", conn);
        cmd.Parameters.AddWithValue("@e", data.Email);
        cmd.Parameters.AddWithValue("@p", hashed);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            int userId = reader.GetInt32("Id");
            string username = reader.GetString("Username");
            int titleId = reader.IsDBNull(reader.GetOrdinal("TitleId")) ? 0 : reader.GetInt32("TitleId");
            reader.Close();

            // 타이틀 변수 미리 선언
            string titleName = "";
            string colorGradient = "#FFFFFF";

            // 타이틀Id가 0이 아닐 때 타이틀 조회
            if (titleId > 0)
            {
                var cmdTitle = new MySqlCommand("SELECT TitleName, ColorGradient FROM Titles WHERE TitleId=@tid", conn);
                cmdTitle.Parameters.AddWithValue("@tid", titleId);
                using var titleReader = cmdTitle.ExecuteReader();
                if (titleReader.Read())
                {
                    titleName = titleReader.GetString("TitleName");
                    colorGradient = titleReader.GetString("ColorGradient");
                }
            }

            // JSON으로 응답
            return JsonSerializer.Serialize(new
            {
                Command = "login-success",
                UserId = userId,
                Username = username,
                TitleId = titleId,
                TitleName = titleName ?? "",
                ColorGradient = colorGradient ?? "#FFFFFF"
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

    // ---------------- 칭호 변경 ----------------
    private static string ChangeUserTitle(ChangeTitleRequest data)
    {
        try
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            // 1. 칭호 정보 조회
            var titleCmd = new MySqlCommand("SELECT TitleName, ColorGradient FROM Titles WHERE TitleId=@titleId", conn);
            titleCmd.Parameters.AddWithValue("@titleId", data.TitleId);
            using var reader = titleCmd.ExecuteReader();

            if (!reader.Read())
            {
                return JsonSerializer.Serialize(new { Command = "error", Message = "존재하지 않는 칭호입니다." });
            }

            string titleName = reader.GetString("TitleName");
            string colorGradient = reader.GetString("ColorGradient");
            reader.Close();

            // 2. 유저 칭호 업데이트
            var updateCmd = new MySqlCommand("UPDATE Users SET TitleId=@titleId WHERE Id=@Id", conn);
            updateCmd.Parameters.AddWithValue("@titleId", data.TitleId);
            updateCmd.Parameters.AddWithValue("@Id", data.UserId);
            updateCmd.ExecuteNonQuery();

            // 3. 세션이 존재하면 실시간 전송
            if (sessions.TryGetValue(data.UserId, out var session))
            {
                string json = JsonSerializer.Serialize(new
                {
                    Command = "title-update",
                    data.TitleId,
                    TitleName = titleName,
                    ColorGradient = colorGradient
                });
                SendMessage(session.Client, json);
            }

            // 4. 요청자에게 결과 응답
            return JsonSerializer.Serialize(new { Command = "change-title-result", Message = "칭호가 변경되었습니다." });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"칭호 변경 오류: {ex.Message}");
            return JsonSerializer.Serialize(new { Command = "error", Message = "칭호 변경 중 오류가 발생했습니다." });
        }
    }


    /// <summary>
    /// 5초마다 끊어진 세션을 찾아 방 상태와 세션 사전을 함께 정리합니다.
    /// 종료 토큰을 사용해 서버 프로세스가 백그라운드 스레드 때문에 남지 않도록 합니다.
    /// </summary>
    private static void MonitorSessions(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (Session session in sessions.Values)
                {
                    if (IsClientConnected(session.Client))
                        continue;

                    RemoveUserAndRoom(session.UserId);
                    sessions.TryRemove(session.UserId, out _);
                    Console.WriteLine($"연결 끊김 감지 → 세션 제거: {session.UserId}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, cancellationToken);
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

    /// <summary>요청 소켓을 서버가 관리하는 인증 세션으로 역참조합니다.</summary>
    private static Session? FindSessionByClient(TcpClient client)
    {
        return sessions.Values.FirstOrDefault(session => ReferenceEquals(session.Client, client));
    }

    private static void SendMessageToClient(Session session, string message)
    {
        try
        {
            if (session.Client != null && session.Client.Connected)
                SendMessage(session.Client, message);
        }
        catch
        {
            Console.WriteLine($"[SendMessageToClient] UserId {session.UserId} 전송 실패");
        }
    }

    // ---------------- 메시지 브로드캐스트 ----------------
    private static void BroadcastMessage(string msg)
    {
        foreach (var s in sessions.Values)
        {
            if (s.Client != null && s.Client.Connected)
            {
                try
                {
                    SendMessage(s.Client, msg);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 특정 방에 속한 모든 플레이어에게 메시지를 브로드캐스트합니다.
    /// </summary>
    /// <param name="senderUserId">메시지 보낸 사람의 UserId</param>
    /// <param name="jsonMsg">전송할 JSON 메시지</param>
    private static void BroadcastMessageToRoom(int senderUserId, string jsonMsg)
    {
        try
        {
            int roomId = 0;

            // UserId가 속한 방 찾기
            using (var conn = new MySqlConnection(connectionString))
            {
                conn.Open();
                var cmd = new MySqlCommand("SELECT RoomId FROM RoomPlayers WHERE UserId=@uid LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@uid", senderUserId);
                var result = cmd.ExecuteScalar();
                if (result != null)
                    roomId = Convert.ToInt32(result);
            }

            if (roomId == 0)
            {
                Console.WriteLine($"[BroadcastMessageToRoom] UserId {senderUserId}가 속한 방을 찾을 수 없음");
                return;
            }

            // 같은 방 유저들에게 메시지 전송
            using (var conn = new MySqlConnection(connectionString))
            {
                conn.Open();
                var cmd = new MySqlCommand("SELECT UserId FROM RoomPlayers WHERE RoomId=@rid AND UserId <> 0", conn);
                cmd.Parameters.AddWithValue("@rid", roomId);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int targetUserId = reader.GetInt32("UserId");

                    if (sessions.TryGetValue(targetUserId, out Session? targetSession) && targetSession != null)
                    {
                        SendMessageToClient(targetSession, jsonMsg);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BroadcastMessageToRoom 오류] {ex.Message}");
        }
    }

    /// <summary>
    /// 귓속말을 대상 사용자와 발신자에게만 보냅니다.
    /// </summary>
    /// <param name="senderUserId">발신자 UserId</param>
    /// <param name="targetUsername">수신자 Username</param>
    /// <param name="jsonMsg">전송할 JSON 메시지</param>
    private static void SendWhisper(int senderUserId, string targetUsername, string jsonMsg)
    {
        try
        {
            int targetUserId = 0;

            // 대상 Username → UserId 조회
            using (var conn = new MySqlConnection(connectionString))
            {
                conn.Open();
                var cmd = new MySqlCommand("SELECT Id FROM Users WHERE Username=@uname LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@uname", targetUsername);
                var result = cmd.ExecuteScalar();
                if (result != null)
                    targetUserId = Convert.ToInt32(result);
            }

            if (targetUserId == 0)
            {
                Console.WriteLine($"[SendWhisper] 대상 사용자 '{targetUsername}'를 찾을 수 없음");
                return;
            }

            // 발신자와 수신자에게만 메시지 전송
            int[] targetIds = { senderUserId, targetUserId };

            foreach (int uid in targetIds)
            {
                if (sessions.TryGetValue(uid, out Session? targetSession) && targetSession != null)
                {
                    SendMessageToClient(targetSession, jsonMsg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SendWhisper 오류] {ex.Message}");
        }
    }

    // ---------------- 방 목록 브로드캐스트 ----------------
    private static void BroadcastRoomList()
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var cmd = new MySqlCommand(@"
        SELECT 
            r.RoomId, 
            r.RoomName, 
            r.Host, 
            r.Difficulty, 
            (
                SELECT COUNT(*) 
                FROM RoomPlayers rp
                WHERE rp.RoomId = r.RoomId
                  AND rp.IsClosed = 0      -- 닫힌 슬롯은 카운트 제외
            ) AS MaxPlayers,
            (
                SELECT COUNT(*) 
                FROM RoomPlayers rp 
                WHERE rp.RoomId = r.RoomId
                  AND rp.UserId <> 0       -- 실제 유저만
                  AND rp.IsClosed = 0      -- 닫힌 슬롯 제외
            ) AS CurrentPlayers,
            COALESCE(t.ColorGradient, '#FFFFFF') AS ColorGradient
        FROM Rooms r
        LEFT JOIN Users u ON u.Username = r.Host
        LEFT JOIN Titles t ON t.TitleId = u.TitleId;", conn);

        var reader = cmd.ExecuteReader();
        var roomList = new List<object>();
        while (reader.Read())
        {
            int roomId = reader.GetInt32("RoomId");
            if (startedRooms.ContainsKey(roomId)) continue;

            roomList.Add(new
            {
                RoomId = roomId,
                RoomName = reader.GetString("RoomName"),
                Host = reader.GetString("Host"),
                Difficulty = reader.GetString("Difficulty"),
                CurrentPlayers = reader.GetInt32("CurrentPlayers"),
                MaxPlayers = reader.GetInt32("MaxPlayers"),
                ColorGradient = reader.GetString("ColorGradient")
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

        var cmd = new MySqlCommand(@"
        SELECT 
            r.RoomId, 
            r.RoomName, 
            r.Host, 
            r.Difficulty, 
            (
                SELECT COUNT(*) 
                FROM RoomPlayers rp
                WHERE rp.RoomId = r.RoomId
                  AND rp.IsClosed = 0      -- 닫힌 슬롯은 카운트 제외
            ) AS MaxPlayers,
            (
                SELECT COUNT(*) 
                FROM RoomPlayers rp 
                WHERE rp.RoomId = r.RoomId
                  AND rp.UserId <> 0       -- 실제 유저만
                  AND rp.IsClosed = 0      -- 닫힌 슬롯 제외
            ) AS CurrentPlayers,
            COALESCE(t.ColorGradient, '#FFFFFF') AS ColorGradient
        FROM Rooms r
        LEFT JOIN Users u ON u.Username = r.Host
        LEFT JOIN Titles t ON t.TitleId = u.TitleId;", conn);
        var reader = cmd.ExecuteReader();

        var roomList = new List<object>();
        while (reader.Read())
        {
            int roomId = reader.GetInt32("RoomId");
            if (startedRooms.ContainsKey(roomId)) continue;

            roomList.Add(new
            {
                RoomId = roomId,
                RoomName = reader.GetString("RoomName"),
                Host = reader.GetString("Host"),
                Difficulty = reader.GetString("Difficulty"),
                CurrentPlayers = reader.GetInt32("CurrentPlayers"),
                MaxPlayers = reader.GetInt32("MaxPlayers"),
                ColorGradient = reader.GetString("ColorGradient")
            });
        }

        string jsonMsg = JsonSerializer.Serialize(new
        {
            Command = "room-list-update",
            Rooms = roomList
        });

        SendMessage(client, jsonMsg);
    }

    private static bool IsRoomHost(int roomId, int userId)
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        var command = new MySqlCommand(
            "SELECT COUNT(*) FROM RoomPlayers WHERE RoomId=@rid AND UserId=@uid AND IsHost=1", conn);
        command.Parameters.AddWithValue("@rid", roomId);
        command.Parameters.AddWithValue("@uid", userId);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static void DeleteRoom(int roomId)
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();
        using var transaction = conn.BeginTransaction();

        var deletePlayers = new MySqlCommand("DELETE FROM RoomPlayers WHERE RoomId=@rid", conn, transaction);
        deletePlayers.Parameters.AddWithValue("@rid", roomId);
        deletePlayers.ExecuteNonQuery();

        var deleteRoom = new MySqlCommand("DELETE FROM Rooms WHERE RoomId=@rid", conn, transaction);
        deleteRoom.Parameters.AddWithValue("@rid", roomId);
        deleteRoom.ExecuteNonQuery();

        transaction.Commit();
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

        var cmd2 = new MySqlCommand(@"
        SELECT rp.PlayerSlot, rp.UserId,
               CASE 
                   WHEN rp.IsClosed = 1 THEN 'Close'
                   WHEN rp.UserId = 0 THEN 'Open'
                   ELSE rp.Username 
               END AS Username,
               rp.IsHost,
               rp.IsClosed,
               IFNULL(t.TitleId, 0) AS TitleId,
               IFNULL(t.TitleName, '') AS TitleName,
               IFNULL(t.ColorGradient, '#FFFFFF') AS ColorGradient
        FROM RoomPlayers rp
        LEFT JOIN Users u ON rp.UserId = u.Id
        LEFT JOIN Titles t ON u.TitleId = t.TitleId
        WHERE rp.RoomId=@rid
        ORDER BY rp.PlayerSlot", conn);
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
                IsClosed = r2.GetBoolean("IsClosed"),
                TitleId = r2.GetInt32("TitleId"),
                TitleName = r2.GetString("TitleName"),
                ColorGradient = r2.GetString("ColorGradient")
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

        BroadcastMessageToRoomByRoomId(roomId, jsonMsg);
    }

    // ---------------- 방에 메시지 브로드캐스트 (roomId 기반) ----------------
    private static void BroadcastMessageToRoomByRoomId(int roomId, string jsonMsg)
    {
        try
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            var cmd = new MySqlCommand("SELECT UserId FROM RoomPlayers WHERE RoomId=@rid AND UserId <> 0", conn);
            cmd.Parameters.AddWithValue("@rid", roomId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int targetUserId = reader.GetInt32("UserId");
                if (sessions.TryGetValue(targetUserId, out var targetSession) && targetSession != null)
                {
                    SendMessageToClient(targetSession, jsonMsg);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BroadcastMessageToRoomByRoomId 오류] {ex.Message}");
        }
    }

    // ---------------- roomId 기반으로 방 정보 조회 ----------------
    private static (string RoomName, string Host, string Difficulty, List<object> Players) GetRoomInfo(int roomId)
    {
        using var conn = new MySqlConnection(connectionString);
        conn.Open();

        // 1) 방 기본 정보 조회
        var cmdRoom = new MySqlCommand("SELECT RoomName, Host, Difficulty FROM Rooms WHERE RoomId=@rid", conn);
        cmdRoom.Parameters.AddWithValue("@rid", roomId);
        using var reader = cmdRoom.ExecuteReader();
        if (!reader.Read())
            throw new Exception("방을 찾을 수 없습니다.");

        string roomName = reader.GetString("RoomName");
        string hostName = reader.GetString("Host");
        string difficulty = reader.GetString("Difficulty");
        reader.Close();

        // 2) 플레이어 목록 조회
        var cmdPlayers = new MySqlCommand(@"
        SELECT rp.PlayerSlot, rp.UserId, rp.Username, rp.IsHost, rp.IsClosed,
               IFNULL(t.TitleId, 0) AS TitleId,
               IFNULL(t.TitleName, '') AS TitleName,
               IFNULL(t.ColorGradient, '#000000') AS ColorGradient
        FROM RoomPlayers rp
        LEFT JOIN Users u ON rp.UserId = u.Id
        LEFT JOIN Titles t ON u.TitleId = t.TitleId
        WHERE rp.RoomId=@rid
        ORDER BY rp.PlayerSlot", conn);
        cmdPlayers.Parameters.AddWithValue("@rid", roomId);

        var players = new List<object>();
        using var reader2 = cmdPlayers.ExecuteReader();
        while (reader2.Read())
        {
            players.Add(new
            {
                PlayerSlot = reader2.GetInt32("PlayerSlot"),
                UserId = reader2.GetInt32("UserId"),
                Username = reader2.GetString("Username"),
                IsHost = reader2.GetBoolean("IsHost"),
                IsClosed = reader2.GetBoolean("IsClosed"),
                TitleId = reader2.GetInt32("TitleId"),
                TitleName = reader2.GetString("TitleName"),
                ColorGradient = reader2.GetString("ColorGradient")
            });
        }

        return (roomName, hostName, difficulty, players);
    }
}
