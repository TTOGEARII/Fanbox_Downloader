# Fanbox Updater

FANBOX에서 팔로우/후원 중인 작가의 게시물을 원하는 폴더(로컬 디스크·NAS 등 아무 경로)에 최신화하는 Windows 프로그램입니다.

## 설치

[Releases](https://github.com/TTOGEARII/Fanbox_Downloader/releases/latest)에서 **FanboxUpdater-win-Setup.exe**를 받아 실행하면 설치됩니다.

- 새 버전이 릴리스되면 **앱이 자동으로 감지**해서 상단에 "⬆ 업데이트" 버튼이 나타납니다. 클릭 한 번으로 업데이트 후 재시작됩니다 (적용되는 동안 스플래시 창이 표시됩니다).
- 설정/기록은 `%AppData%\FanboxUpdater`에 저장되어 업데이트해도 유지됩니다.

## 배포 (개발자용)

버전 태그를 푸시하면 GitHub Actions가 설치 파일을 빌드해 Release로 올립니다:

```
git tag v1.0.1 && git push origin v1.0.1
```

## 실행 (개발 모드)

`실행.bat` 더블클릭 (또는 `src\Updater.App\bin\Release\net8.0-windows\FanboxUpdater.exe`)

- **▶ 지금 최신화** — 새 게시물만 받아옵니다.
- 창을 닫으면 트레이로 들어가며, 스케줄이 켜져 있으면 백그라운드에서 자동 실행됩니다.
- 완전 종료: 트레이 아이콘 우클릭 → 종료

## 주요 기능

### 작가 탭
- 저장 폴더 ↔ 작가 자동 매칭. 매핑 여부가 ✔/! 아이콘과 행 색으로 표시되고, 매핑된 작가가 위로 정렬됩니다.
- 매칭 안 된 폴더는 **[선택] 버튼**으로 팔로우/후원 작가 목록에서 고르거나, creatorId 칸에 직접 입력 (작가 주소 `https://xxxx.fanbox.cc`의 `xxxx`)
- **분류** 드롭다운 — 분류에 따라 저장 루트가 달라집니다. 바꾸면 기존 폴더 이동도 물어봅니다.
- 검색창(폴더명·작가명·ID)과 분류 필터, 행 우클릭 → 폴더/FANBOX 페이지 열기
- 동기화 체크 해제 → 해당 작가 제외. 기본값은 **매핑된 작가만 켜짐** (미매핑 폴더는 꺼짐)

### 설정 탭
- **저장 분류**: 분류(이름+저장 루트 경로)를 원하는 만큼 추가/삭제할 수 있습니다. 각 루트 바로 아래 하위 폴더가 작가 폴더로 매칭됩니다. 경로는 폴더 선택 창으로 지정.
- **일러스트 분리**: '일러 분리'를 체크한 분류의 낱장 일러스트(이미지 N장 이하)는 별도 일러스트 경로로 저장됩니다. 작가별 기준(장)도 그리드에서 지정 가능.
- **FANBOXSESSID 쿠키**: 브라우저 로그인 → F12 → Application → Cookies → fanbox.cc → `FANBOXSESSID` 값 복사. 만료되면("인증 실패") 다시 넣어주세요.
- **플랜 구독 게시물만 받기**: 무료 공개 글은 받지 않고, 후원 플랜으로 볼 수 있는 유료 게시물만 받습니다. (기본 켜짐)
- **압축파일 자동 해제**: 받은 zip을 게시물 폴더 안에 자동으로 풉니다 (일본어 파일명 지원). 해제 후 원본 삭제 옵션도 있습니다.
- 받을 파일 종류(이미지/영상/압축/기타), 폴더 이름 규칙(`{date} {title}` 등), 동시 다운로드 수
- **자동 실행 스케줄**: 매일 지정 시각 또는 N시간 간격 (트레이 상주 중 동작)

### 명령줄 (작업 스케줄러 연동)
```
FanboxUpdater.exe --sync    ← 창 없이 1회 동기화 후 종료 (로그: app.log)
```

## 파일

| 파일 | 설명 |
|---|---|
| `settings.json` | 모든 설정 (쿠키, 경로, 매핑, 분류, 스케줄) |
| `state.json` | 받은 게시물 기록. **지우면 전부 다시 받으므로 주의** |
| `app.log` | 동기화 로그 |

## 구조 (개발자용)

```
src\Updater.Core             사이트 독립 코어 (ISiteProvider, SyncEngine)
src\Updater.Providers.Fanbox Fanbox 구현 (WebView2로 Cloudflare 우회)
src\Updater.App              WPF GUI + 트레이 + 스케줄러
```

다른 사이트(Patreon 등)를 추가하려면 `ISiteProvider`를 구현하고 `ProviderRegistry`에 등록하면 됩니다.

빌드: `dotnet build -c Release` (.NET 8 SDK)

## 참고

- FANBOX API 일부(post.info)는 Cloudflare가 일반 프로그램을 차단하므로, 숨겨진 WebView2(엣지 엔진)로 실제 브라우저처럼 호출합니다.
- 이미 받은 게시물은 `state.json` 기준으로 건너뛰므로, 저장 폴더에서 파일을 지워도 다시 받지 않습니다. 다시 받으려면 상단 "선택 작가 다시 받기"를 쓰거나 state.json에서 해당 작가 항목을 지우세요.
