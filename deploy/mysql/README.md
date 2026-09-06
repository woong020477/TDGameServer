# 최초 배포용 MySQL 스키마

`001_initial_schema.sql`은 이미 생성된 빈 `projectmine` 데이터베이스에 한 번만 적용합니다. MySQL 8.0 기준이며, SQL 실행은 관리자 계정으로 하고 게임 서버는 기존 `projectmine_app` 계정을 사용합니다.

## 기존 덤프와의 차이

- `Users`, `Titles`, `Rooms`, `RoomPlayers`를 서버 쿼리의 대소문자와 맞춥니다. Linux의 `lower_case_table_names=0`을 그대로 사용합니다.
- 원본 테이블의 컬럼, 인덱스, 외래 키, 테이블 정렬 규칙은 유지합니다. 기존 AUTO_INCREMENT 진행 값은 가져오지 않습니다.
- 기본 칭호 9개만 입력합니다. 테스트 사용자, 이메일, 비밀번호 해시, 방 데이터는 포함하지 않습니다.
- 테이블 삭제 구문과 외래 키 검사 해제는 사용하지 않습니다. 기존 테이블이 있으면 오류로 중단하며, 재실행용 마이그레이션은 아닙니다.

## 서버에서 적용

먼저 빈 DB인지 확인합니다.

```bash
sudo mysql projectmine -e "SHOW TABLES;"
```

테이블이 하나라도 있으면 적용하지 말고 기존 상태를 조사합니다. 파일을 `/home/deploy/projectmine-initial-schema.sql`로 업로드한 경우:

```bash
sudo mysql --show-warnings projectmine < /home/deploy/projectmine-initial-schema.sql
```

`--force`는 사용하지 않습니다. DDL 전체가 하나의 트랜잭션으로 롤백되지는 않으므로 오류가 나면 테이블을 삭제하거나 파일을 바로 재실행하지 말고 부분 생성 상태부터 확인합니다.

## 검증

```bash
mysql -h 127.0.0.1 -u projectmine_app -p projectmine -e "SHOW TABLES; SELECT 'Users' AS table_name, COUNT(*) AS row_count FROM Users UNION ALL SELECT 'Rooms', COUNT(*) FROM Rooms UNION ALL SELECT 'RoomPlayers', COUNT(*) FROM RoomPlayers UNION ALL SELECT 'Titles', COUNT(*) FROM Titles;"
```

예상 결과는 테이블 4개, Users/Rooms/RoomPlayers 각 0행, Titles 9행입니다. 비밀번호는 프롬프트에서 입력하고 명령줄 인수나 저장소에 기록하지 않습니다.

이 확인은 스키마 생성과 읽기 권한만 검증합니다. 회원가입, 로그인, 방 생성과 참가, UDP 통신은 서버 배포 후 별도 검증이 필요합니다. 실제 사용자 공개 전에는 전송 암호화 등 서버 보안 보강도 필요합니다.
