# TDGameServer

Unity 멀티플레이 타워 디펜스를 위한 .NET 8 콘솔 서버입니다. TCP는 인증·로비처럼 신뢰성이 필요한 요청을 처리하고, UDP는 같은 방 참가자 사이의 인게임 이벤트를 중계합니다.

## 프로젝트 구조

```text
TDGameServer/
├── Application/
│   └── GameServerApplication.cs  인증, 세션, 로비와 방 유스케이스
├── Configuration/
│   └── ServerOptions.cs          환경 변수, 포트와 DB 연결 설정
├── Networking/
│   ├── TcpServerHost.cs          TCP 연결과 줄바꿈 프레이밍
│   └── UdpServer.cs              방 단위 UDP 중계
├── Models/                       요청 DTO와 런타임 모델
├── Helpers/                      비밀번호 등 공통 기능
└── Program.cs                    실행 진입점
```

## 통신 설계

### TCP 5000

- 회원가입, 로그인, 로그아웃과 계정 관리
- 로비 입장, 방 생성·참가·퇴장
- 슬롯 이동, 강퇴, 방장 위임
- 전체·방·귓속말 채팅
- 방 목록과 방 상세 동기화
- 게임 시작 권한 검증과 대기 방 삭제

TCP 요청과 응답은 JSON 한 건 뒤에 `\n`을 붙입니다. `TcpServerHost`는 `StreamReader.ReadLine`으로 패킷 경계를 복원하고, 모든 응답은 `TcpMessageWriter`를 거쳐 같은 규칙으로 전송합니다.

### UDP 9000

클라이언트는 GameScene 진입 시 다음 메시지로 엔드포인트를 방에 등록합니다.

```text
REGISTER|{"roomId":10}
```

등록 뒤의 인게임 메시지는 송신자를 제외한 같은 RoomId 참가자에게만 전달됩니다. 종료 시 `UNREGISTER`로 엔드포인트를 제거합니다.

## 설정

배포 환경에서는 다음 환경 변수를 사용합니다.

```text
DB_HOST
DB_PORT
DB_NAME
DB_USER
DB_PASSWORD
DB_SSL_MODE
PROJECTMINE_TCP_PORT
PROJECTMINE_UDP_PORT
```

DB 환경 변수가 없으면 로컬 개발용 `appsettings.json`을 찾습니다. 설정 파일은 Git에 포함하지 않습니다.

```json
{
  "Database": {
    "Host": "127.0.0.1",
    "Port": 3306,
    "Name": "database-name",
    "User": "server-user",
    "Password": "local-secret",
    "SslMode": "Preferred"
  }
}
```

## 실행

빌드:

```bash
dotnet build TDGameServer.sln
```

기본 실행:

```bash
dotnet run --project TDGameServer/TDGameServer.csproj
```

macOS에서 5000번 포트가 사용 중이면:

```bash
PROJECTMINE_TCP_PORT=5050 \
dotnet run --project TDGameServer/TDGameServer.csproj
```

종료는 `Ctrl+C`를 사용합니다. TCP listener, UDP socket과 세션 모니터가 같은 취소 토큰을 사용해 종료됩니다.

## 주요 실행 흐름

### 로그인

1. TCP 요청의 Command를 읽습니다.
2. 비밀번호 해시를 DB 값과 비교합니다.
3. 성공하면 `ConcurrentDictionary<int, Session>`에 연결을 등록합니다.
4. `login-success`에 사용자와 칭호 정보를 담아 반환합니다.

### 게임 시작

1. 요청 TCP 연결과 로그인 세션을 연결합니다.
2. 요청자가 해당 방의 방장인지 DB에서 확인합니다.
3. 방과 참가자 스냅샷을 같은 방 사용자에게 보냅니다.
4. `Rooms`, `RoomPlayers`를 트랜잭션으로 삭제합니다.
5. 갱신된 방 목록을 로비 사용자에게 전송합니다.

### 연결 종료

세션 모니터가 5초마다 연결을 검사합니다. 끊어진 사용자의 세션을 제거하고, 해당 사용자가 대기 방 호스트였다면 방과 슬롯을 정리합니다.

## 보안 메모

- DB 포트는 외부에 공개하지 않습니다.
- 클라이언트에는 DB 접속 정보가 없습니다.
- 환경 변수와 비밀 파일의 값을 로그에 출력하지 않습니다.
- TCP 요청 본문 대신 원격지와 Command만 기록해 계정 정보가 로그에 남지 않게 했습니다.
- 칭호 변경은 인증된 자기 세션에만 허용하며, 나머지 방 조작 명령도 동일한 권한 검증을 적용하는 것이 다음 보강 과제입니다.
- 기존 `appsettings.json`이 Git 기록에 들어갔다면 비밀번호를 교체하고 기록에서도 제거해야 합니다.
- 현재 비밀번호 해시는 기존 데이터 호환을 위해 SHA-256을 사용합니다. 운영 전에는 PBKDF2 또는 Argon2 마이그레이션이 필요합니다.

## 검증

현재 확인된 항목:

- .NET 8 빌드 경고 0, 오류 0
- macOS arm64에서 TCP 5050, UDP 9000 바인딩
- 로컬 TCP `ping → pong` 응답
- 미인증 칭호 변경 요청 거부

아직 필요한 항목:

- 실제 MySQL 계정·방 데이터 테스트
- Unity 클라이언트 2개 동시 접속
- 연결 끊김, 방장 이탈과 재접속 정책 테스트
- 자동화된 요청·Repository 통합 테스트
