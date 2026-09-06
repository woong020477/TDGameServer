# Linux 서버 배포

## 구성

- Ubuntu 24.04 x64에서 MySQL과 .NET 콘솔 서버를 함께 실행합니다.
- 최초 DB 스키마 및 적용 순서는 [mysql/README.md](mysql/README.md)를 따릅니다.
- 앱은 Linux x64 self-contained Release로 게시합니다. VPS에 .NET SDK/런타임을 별도 설치하지 않지만, 운영체제의 네이티브 의존성은 필요합니다.
- 런타임 보안 업데이트는 서버 패키지를 새 런타임으로 다시 게시하고 재배포해야 반영됩니다.
- DB 설정은 서버 환경 변수로 주입합니다. 로컬 `appsettings.json`, SQL 덤프, SSH 키는 앱 패키지에 포함하지 않습니다.

## Mac에서 게시

서버 저장소 루트에서 실행합니다. .NET 8 SDK를 사용하고, 기존 게시물과 섞이지 않도록 새 임시 디렉터리에 출력합니다.

```bash
projectmine_build_dir=$(mktemp -d /private/tmp/projectmine-publish.XXXXXX)
dotnet publish TDGameServer/TDGameServer.csproj -c Release -r linux-x64 --self-contained true --artifacts-path "$projectmine_build_dir/build" -o "$projectmine_build_dir/publish" -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
```

`publish` 디렉터리 전체가 한 배포 단위입니다. 실행 파일 하나만 복사하지 않습니다. SQL 덤프나 저장소 전체를 업로드하지 않습니다. `deploy/artifacts/`는 배포 압축 파일을 두는 Git 제외 디렉터리입니다.

## VPS에서 이어서 할 작업

1. 업로드한 압축 파일의 SHA-256을 Mac에서 계산한 값과 대조합니다.
2. 버전별 새 디렉터리에 압축을 해제하고 네이티브 의존성을 확인합니다.
3. sudo 권한이 없는 게임 서버 실행 계정과 보호된 DB 환경 설정 파일을 준비합니다.
4. systemd로 실행하고 서버 내부 TCP ping, DB 로그인, 메모리를 검증합니다.
5. 테스트 PC에 한해 TCP 5000 / UDP 9000 접근을 허용합니다. DB 3306 / 33060은 공개하지 않습니다.
6. 서로 다른 네트워크의 클라이언트로 회원가입, 로그인, 방 참가, 게임 시작, 호스트 교체, 두 방 동시 플레이를 검증합니다.

게시 성공은 Linux 실행이나 실제 플레이 검증이 아닙니다. 기존 서버의 TCP 전송 암호화와 인증/권한 검증 등 공개 서비스용 보안 검토가 끝나기 전에는 불특정 다수에게 공개하지 않습니다.

## systemd 서비스와 비밀 설정

`projectmine.service`는 최초 릴리스 `/opt/projectmine/releases/20260907-01`을 실행합니다. 릴리스 교체 시 `WorkingDirectory`와 `ExecStart`를 함께 변경합니다. 기존 서비스 파일이 있으면 덮어쓰기 전에 내용을 확인합니다.

- 앱 계정은 `projectmine`이며 sudo 권한이나 로그인 셸을 주지 않습니다.
- 실행 파일은 root 소유로 두고 읽기/실행만 허용합니다.
- `/etc/projectmine`은 root 소유 0700, `/etc/projectmine/server.env`는 root 소유 0600으로 관리합니다.
- systemd가 환경 파일을 읽어 앱에 전달합니다. 환경 변수는 앱 프로세스에서 접근 가능하므로 비밀 관리 시스템을 대체하지는 않습니다. 해당 계정으로 다른 프로그램을 운영하지 않습니다.
- `KillSignal=SIGINT`는 현재 앱의 `Console.CancelKeyPress` 종료 처리에 맞춘 설정입니다. 종료/재시작 동작은 VPS에서 별도로 검증합니다.
- 오류 종료 시 5초 뒤 재시도하며, 60초 동안 3회 기동 제한을 둡니다. 설정 수정 후 제한에 걸렸다면 `sudo systemctl reset-failed projectmine` 후 시작합니다.

환경 파일 예시입니다. 실제 비밀번호를 저장소에 저장하지 않습니다.

```ini
DB_HOST=127.0.0.1
DB_PORT=3306
DB_NAME=projectmine
DB_USER=projectmine_app
DB_PASSWORD="REPLACE_WITH_ROTATED_DB_PASSWORD"
DB_SSL_MODE=Preferred
PROJECTMINE_TCP_PORT=5000
PROJECTMINE_UDP_PORT=9000
```

systemd 환경 파일의 큰따옴표 안에서 비밀번호에 포함된 `\`는 `\\`, `"`는 `\"`로 입력합니다. `$`는 그대로 둡니다. 이 파일을 셸의 `source` 명령으로 읽지 않습니다.

서비스를 `/etc/systemd/system/projectmine.service`에 root 소유 0644로 설치한 뒤 검사합니다.

```bash
sudo systemd-analyze verify /etc/systemd/system/projectmine.service
sudo systemctl daemon-reload
sudo systemctl enable --now projectmine
sudo systemctl status projectmine --no-pager -l
sudo journalctl -u projectmine -n 40 --no-pager
```

검사 오류가 있으면 활성화하지 않습니다. 서비스의 `active` 상태는 프로세스 실행만 의미하며 DB 인증과 실제 요청 성공을 보장하지 않습니다. 로그를 공유할 때는 비밀값이 없는지 확인합니다. 현재 서비스 템플릿은 로컬 정적 검사만 수행했고 실제 systemd 기동 검증은 VPS에서 진행해야 합니다.
