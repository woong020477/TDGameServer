# TDGameServer
Unity 타워 디펜스 게임 Terion TD .NET 서버입니다.  

## 소개
서버는 .NET 콘솔 애플리케이션으로 동작하며,  
TCP 서버(포트 5000)에서 로그인, 로비, 방 관리, 채팅 등 게임 로직 전반을 처리하고,  
UDP 서버(포트 9000)에서 인게임 중 저지연 브로드캐스트를 처리합니다.  
서버는 MySQL과 연결되어 유저 정보, 방 정보, 칭호(Title) 등 영구 데이터를 관리합니다. DB 연결 정보는 appsettings.json에서 읽어와 구성되고, 서버 시작 시 로드됩니다.  
또한 접속 중인 클라이언트(세션)는 ConcurrentDictionary<int, Session>으로 관리하며, UserId, Username, TCP 연결 소켓, 마지막 ping 시간 등을 추적합니다. 이 정보는 브로드캐스트, 방 단위 메시지 전송, 강제 퇴장 처리 등에 사용됩니다.  

---

## 아키텍처 개요

```text
[Unity Client] 
   │
   ├─ TCP : 5000
   │    ├─ 회원가입 / 로그인 / 로그아웃 / 비밀번호 관리
   │    ├─ 로비 입장 및 세션 등록
   │    ├─ 방 생성 / 참가 / 퇴장 / 강퇴 / 슬롯 이동 / 슬롯 열기·닫기 / 호스트 양도
   │    ├─ 채팅(전체, 방, 귓속말) 및 시스템 공지
   │    ├─ 방 목록 / 방 상세 정보 동기화
   │    └─ 게임 시작 브로드캐스트
   │
   └─ UDP : 9000
        ├─ 인게임 저지연 브로드캐스트
        ├─ 클라이언트 엔드포인트 자동 등록
        └─ "action" 기반 메시지 분배
```

### TCP 서버
- `TcpListener`로 포트 5000에서 대기하고, 새 클라이언트가 접속하면 `HandleClient`에서 처리 루프를 시작합니다. 클라이언트가 보낸 JSON 요청을 읽고(`Command` 기반), 서버가 처리한 후 다시 JSON 문자열로 응답을 보냅니다. 응답은 `
`으로 구분되어 클라이언트가 패킷을 파싱할 수 있도록 합니다.

- 예: 로그아웃 처리 시 서버는 `"logout-result"`와 메시지를 JSON으로 직렬화하여 돌려줍니다.

### UDP 서버
- `UdpServer`는 포트 9000에서 시작되며, 클라이언트의 엔드포인트를 자동으로 등록하고, 받은 메시지를 분석한 뒤 다른 클라이언트에게 브로드캐스트하는 구조입니다.

- UDP 메시지는 `"header|json"` 형태 또는 `"action"` 필드를 가진 JSON 형태를 지원하며, 서버는 수신한 메시지를 파싱하고 송신자를 제외한 나머지 참가자에게 전달하도록 설계돼 있습니다.

### 세션 모니터링
- 서버는 별도 스레드에서 주기적으로 모든 세션을 검사하고, 연결이 끊긴 TCP 클라이언트를 감지하면 세션과 그 유저의 방 정보를 정리합니다. 이 정리는 방장이 나가서 방이 사라지는 경우 등에도 사용됩니다.


---

## 주요 시스템

### 1. 인증 / 계정
서버는 로그인, 로그아웃, 회원가입, 비밀번호 초기화/변경, 이메일 찾기 등을 처리합니다. 각 요청은 JSON으로 전달되며 `Command` 필드를 통해 어떤 동작인지 구분합니다. 예를 들어 로그인 요청은 `{"Command":"login", "Email":"...", "Password":"..."}` 형태입니다.

성공적으로 로그인하면 서버는 해당 유저에 대한 세션을 생성하고, UserId/Username 등을 기록해 이후 채팅, 방 참가, 브로드캐스트 등에 활용합니다. 세션은 `UserId`, `Username`, `TcpClient Client`, `DateTime LastPing`을 포함합니다.

로그아웃하면 세션에서 제거되고, 필요 시 그 유저가 속한 방도 정리한 뒤 `"logout-result"` 응답이 전송됩니다.

### 2. 로비
- 유저는 로비에 진입하면서 자신의 UserId/Username을 서버에 알리고(`LobbyEnterRequest`), 서버는 현재 접속자 목록 등 로비 정보를 JSON으로 돌려줍니다.
- 로비에는 현재 존재하는 방 목록이 주기적으로/이벤트마다 브로드캐스트됩니다. 이 목록은 클라이언트 UI에서 방 리스트 UI로 그대로 사용할 수 있게 하는 구조입니다.

### 3. 방(룸) 시스템
서버는 최대 4인 기준 방(Room)을 다룹니다. 각 방은 `RoomName`, `Host`, `Difficulty`, 현재 인원 수, 최대 인원 수 등의 정보를 가집니다.

또한 DB에서 방 정보와 슬롯 정보(RoomPlayers)를 조회해, 각 플레이어의 슬롯 번호, UserId, Username, Host 여부, 슬롯이 닫혀 있는지 여부, 칭호(TitleName), 그리고 닉네임에 적용할 색상 그라디언트(ColorGradient)까지 함께 내려줄 수 있습니다.

서버는 특정 방의 상세 정보를 요청했을 때 이 구조를 직렬화하여 클라이언트에게 돌려주고, 이 데이터는 인게임 씬 전환 전 동기화에도 재사용됩니다. 즉 게임 시작 시 "현재 파티 구성" 전체를 모든 참가자에게 한 번에 브로드캐스트할 수 있습니다.

슬롯 관리(열림/닫힘), 자리 이동, 강퇴, 호스트 양도 등도 이 방 구조 위에서 동작하며, 변경된 내용은 방 단위 혹은 로비 전체에 브로드캐스트되어 클라이언트 UI가 즉시 갱신될 수 있게 설계돼 있습니다.

### 4. 채팅 시스템
채팅은 크게 세 가지 타입을 가집니다:
- `"All"`: 전체/로비 단위 브로드캐스트
- `"Room"`: 같은 방에 있는 유저들에게만 전달
- `"Whisper"`: 특정 대상(`TargetUsername`)에게만 귓속말

채팅 요청은 `ChatRequest`를 통해 전달되며, 이 안에는 `UserId`, `Username`, `ChatType`, 선택적 `TargetUsername`, 그리고 실제 `Message`가 포함됩니다.

또한 시스템 공지(서버에서 보내는 공지 등)는 별도의 `SystemChatRequest` 구조를 통해 전달 가능하며, 여기에는 `Sender`와 `Message`가 포함됩니다. 이 메시지는 서버에서 모든 세션으로 브로드캐스트할 수 있습니다.

채팅 및 시스템 메시지는 서버에서 JSON 직렬화 후 TCP로 전송되며, 클라이언트는 `Command` 필드를 읽어 어떤 종류의 메시지인지 구분합니다.

### 5. 칭호(Title) 시스템
유저는 자신의 칭호(TitleId)를 변경할 수 있으며(`ChangeTitleRequest`), 서버는 DB에서 해당 칭호의 `TitleName`과 `ColorGradient`를 조회한 뒤:
1. `Users` 테이블의 TitleId를 갱신하고,
2. 세션이 살아있는 경우 즉시 `"title-update"` JSON 패킷을 그 유저에게 전송합니다.

마지막으로 `"change-title-result"` 응답으로 처리 결과를 돌려줍니다.

결과적으로 클라이언트 UI는 닉네임에 등급/희귀도 느낌의 그라디언트 컬러를 실시간 반영할 수 있습니다.


---

## 통신 규칙

### 전송 포맷
- 클라이언트 ↔ 서버 간 모든 통신은 JSON 문자열입니다.
- 각 메시지는 최소한 `"Command"` 필드를 포함하며, 이 값으로 동작을 구분합니다. 예: `"login"`, `"logout"`, `"create-room"`, `"chat"`, `"change-password"`, `"change-title"` 등.

- 서버는 응답/브로드캐스트 시에도 `"Command"` 값을 붙여서 보냅니다. 예: `"logout-result"`, `"title-update"`, `"change-title-result"`, `"lobby-enter"`.

- TCP 측은 메시지 경계를 명확히 하기 위해 각 JSON 문자열 끝에 `\n`을 덧붙여 전송합니다.

- UDP 측은 `"header|json"` 또는 `"action"` 필드가 있는 순수 JSON을 허용하며, 수신 후 발신자를 등록하고 브로드캐스트 재전송할 수 있도록 설계돼 있습니다.


---

## 요청 메시지 구조 (TCP Request Payloads)

아래 구조체들은 Unity 클라이언트가 서버로 보내는 요청(JSON) 형식의 기준이 됩니다. 모든 필드 이름은 직렬화된 키와 동일하게 사용됩니다.

### 1. RegisterRequest
회원가입 요청.

```json
{
  "Command": "register",
  "Username": "닉네임",
  "Email": "user@example.com",
  "Password": "SHA256해시 or 원문(클라 정책에 따라)"
}
```

- `Command`: "register"  
- `Username`: 새로 만들 닉네임  
- `Email`: 가입 이메일  
- `Password`: 비밀번호  

---

### 2. LoginRequest
로그인 요청.

```json
{
  "Command": "login",
  "Email": "user@example.com",
  "Password": "비밀번호"
}
```

- `Command`: "login"  
- `Email`: 로그인용 이메일  
- `Password`: 비밀번호(해시된 값 사용 가능)  

서버는 인증에 성공하면 세션을 생성하고, UserId / Username / 현재 칭호 등의 정보를 기억합니다. 세션은 `UserId`, `Username`, `Client`(TcpClient), `LastPing`으로 구성됩니다.

---

### 3. LogoutRequest
로그아웃 요청.

```json
{
  "Command": "logout",
  "UserId": 12
}
```

- `Command`: "logout"  
- `UserId`: 로그아웃하려는 유저의 고유 ID  

서버는 세션을 제거하고 `"logout-result"` 응답을 보냅니다.

---

### 4. ResetPasswordRequest
비밀번호 초기화(임시 비밀번호 발급) 요청.

```json
{
  "Command": "reset-password",
  "Username": "플레이어닉",
  "Email": "user@example.com"
}
```

- `Command`: "reset-password"  
- `Username`: 계정에 등록된 사용자명  
- `Email`: 계정에 등록된 이메일  

서버는 Username+Email이 일치할 경우 임시 비밀번호를 생성해 DB에 반영하고, 그 임시 비밀번호를 응답으로 내려줄 수 있도록 설계됩니다.

---

### 5. ChangePasswordRequest
현재 비밀번호를 새 비밀번호로 변경.

```json
{
  "Command": "change-password",
  "Username": "플레이어닉",
  "Email": "user@example.com",
  "Password": "현재 비밀번호",
  "NewPassword": "새 비밀번호"
}
```

- `Command`: "change-password"  
- `Username`: 사용자명  
- `Email`: 이메일  
- `Password`: 현재 비밀번호  
- `NewPassword`: 변경할 비밀번호  

---

### 6. FindEmailRequest
가입된 이메일 찾기(아이디 찾기 느낌).

```json
{
  "Command": "find-email",
  "Username": "플레이어닉",
  "Password": "비밀번호"
}
```

- `Command`: "find-email"  
- `Username`: 사용자명  
- `Password`: 비밀번호  

---

### 7. LobbyEnterRequest
로비 입장(세션 등록) 요청.

```json
{
  "Command": "enter-lobby",
  "UserId": 12,
  "Username": "플레이어닉"
}
```

- `Command`: 로비 입장 명령(예: "enter-lobby")  
- `UserId`: 유저 고유 ID  
- `Username`: 유저 닉네임  

서버는 `"lobby-enter"` 응답과 함께 현재 접속자 정보(예: "현재 접속자: ...") 등을 돌려주어 로비 UI에 표시할 수 있도록 합니다.

---

### 8. CreateRoomRequest
방 생성 요청.

```json
{
  "Command": "create-room",
  "HostId": 12,
  "Host": "방장닉",
  "RoomName": "방 제목",
  "Difficulty": "난이도"
}
```

- `Command`: "create-room"  
- `HostId`: 방장 유저 ID  
- `Host`: 방장 닉네임  
- `RoomName`: 방 이름  
- `Difficulty`: 방 난이도(예: "Easy", "Normal", "Hard")  

서버는 DB에 Rooms / RoomPlayers 레코드를 생성하고, 방 목록을 로비 전체에 브로드캐스트할 수 있습니다. 각 슬롯은 최대 4인 기준으로 미리 자리(1P~4P)를 확보하고, 1번 슬롯은 Host로 설정됩니다.

---

### 9. ChangeTitleRequest
유저 칭호(Title) 변경 요청.

```json
{
  "UserId": 12,
  "TitleId": 3
}
```

- `UserId`: 칭호를 변경할 유저의 ID  
- `TitleId`: 적용할 칭호의 ID  

서버는 `Titles` 테이블에서 `TitleName`, `ColorGradient`를 조회해 해당 유저의 TitleId를 업데이트하고, 그 유저에게 `"title-update"` 패킷과 `"change-title-result"` 응답을 즉시 전송합니다. 이를 통해 클라이언트는 닉네임 옆 칭호 및 그라디언트 컬러를 실시간으로 갱신할 수 있습니다.

---

### 10. ChatRequest
채팅 전송 요청.

```json
{
  "Command": "chat",
  "UserId": 12,
  "Username": "플레이어닉",
  "ChatType": "All",          // 또는 "Room", "Whisper"
  "TargetUsername": "상대닉", // Whisper일 때만 사용
  "Message": "채팅 내용"
}
```

- `Command`: "chat"  
- `UserId`: 보낸 사람의 ID  
- `Username`: 보낸 사람 닉네임  
- `ChatType`: `"All"`, `"Room"`, `"Whisper"`  
- `TargetUsername`: 귓속말 대상(Whisper일 때만 필수)  
- `Message`: 실제 텍스트 메시지  

서버는 `ChatType`에 따라  
- 전체 브로드캐스트,  
- 같은 방 참여자에게만 전송,  
- 특정 대상에게만 전송,  
을 수행하며, 메시지는 TCP로 전송되고 각 클라이언트는 수신된 `"Command"` 값(예: `"chat-broadcast"`, `"whisper"`, `"room-chat"`, `"system-chat"`)을 기반으로 UI에 출력할 수 있습니다.

---

### 11. SystemChatRequest
서버 공지 / 시스템 메시지용 요청.

```json
{
  "Command": "system-chat",
  "Sender": "SYSTEM",
  "Message": "공지 내용"
}
```

- `Command`: 시스템 메시지 명령(예: "system-chat")  
- `Sender`: 보낸 주체(보통 "SYSTEM" 또는 서버명)  
- `Message`: 공지 문자열  

서버는 이 메시지를 받아 세션 전원에게 브로드캐스트할 수 있습니다.


---

## 흐름 예시

1. **회원가입 / 로그인**
   - 클라이언트는 `RegisterRequest`로 계정을 생성한 후 `LoginRequest`로 로그인합니다.  
   - 서버는 로그인 성공 시 세션을 생성하고 유저 정보를 기록합니다.

2. **로비 입장**
   - 클라이언트는 `LobbyEnterRequest`를 보내 로비에 등록됩니다.
   - 서버는 `"lobby-enter"` 응답과 현재 접속자 / 방 목록을 돌려주고, 이후 방 목록 갱신을 계속 브로드캐스트합니다.

3. **방 생성 / 참가 / 준비**
   - 호스트는 `CreateRoomRequest`로 방을 만들고, 다른 유저는 이 방에 참가 요청을 보냅니다.
   - 서버는 RoomPlayers 슬롯 정보를 채워 넣고, 방 상태와 슬롯 구성(누가 Host인지, 빈 슬롯은 누구 차지인지, 닉네임 색상 등)을 모든 관련 클라이언트에 브로드캐스트합니다.

4. **채팅 / 공지**
   - 유저는 `ChatRequest`로 로비/방/귓속말 대화를 보냅니다.
   - 서버 또는 GM 툴은 `SystemChatRequest`로 전체 공지를 날릴 수 있습니다.

5. **게임 시작**
   - 호스트가 시작을 선언하면, 서버는 현재 방/슬롯 정보를 한 번 더 취합해 모든 참가자에게 브로드캐스트합니다.
   - 클라이언트는 이 정보를 근거로 인게임 씬으로 전환하고, 이후에는 UDP (포트 9000)를 통해 저지연 액션 브로드캐스팅을 주고받게 됩니다.


---

## 프로젝트 클론 시 안내사항  
TDGameServer.sln 파일을 통해 VS솔루션을 확인하실 수 있습니다.  
DB는 MySQL을 사용하였고 셋업 파일명은 TDGameServerDB입니다.
따로 DB 셋업을 수정하실 경우 appsettings.json 파일도 수정해야 DB 접근 가능합니다.

---
