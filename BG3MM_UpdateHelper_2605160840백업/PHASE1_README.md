# Phase 1 — BG3MM_UpdateHelper

앱 셸 + 설정 + 첫 실행 다이얼로그 + 로깅 인프라.

## Phase 1에서 동작하는 것

- 첫 실행 시 BG3MM 폴더 + API 키 입력 다이얼로그
- 설정을 `%LOCALAPPDATA%\BG3MM_UpdateHelper\settings.json`에 저장
- 메인 윈도우: BG3MM 경로, 모드 폴더, 설치된 모드 개수 표시
- BG3MM 실행 버튼 (Process.Start)
- 설정 다이얼로그 (BG3MM 경로 / API 키 / 옵션 변경)
- 도움말 다이얼로그
- 파일 로깅: `%LOCALAPPDATA%\BG3MM_UpdateHelper\logs\{날짜}.log`

## Phase 1에서 아직 동작 안 함

- [Check for Updates] 버튼 — 안내 메시지만 표시 (Phase 5에서 구현)
- 실제 모드 정보 파싱 — meta.lsx 읽기는 Phase 2에서
- Nexus / mod.io API 호출 — Phase 3, 4에서

## 프로젝트에 적용하는 방법

### 1. 기존 프로젝트 백업 (선택)

VS에서 만든 `BG3MM_UpdateHelper` 프로젝트 폴더를 통째로 복사해서 백업해두면 안전. (예: `BG3MM_UpdateHelper_backup`)

### 2. 압축 풀기

이 zip을 압축 풀면 `BG3MM_UpdateHelper/` 폴더가 나옵니다.

### 3. 프로젝트 폴더로 복사

압축 푼 `BG3MM_UpdateHelper/` 안의 **모든 파일과 폴더**를 본인이 만든 프로젝트의 같은 위치(.csproj 있는 폴더)로 복사. 기존 파일은 **덮어쓰기**.

복사할 항목:
- BG3MM_UpdateHelper.csproj (기존 덮어쓰기)
- App.xaml, App.xaml.cs (기존 덮어쓰기)
- MainWindow.xaml, MainWindow.xaml.cs (기존 덮어쓰기)
- Constants.cs
- Models/ 폴더 전체
- ViewModels/ 폴더 전체
- Views/ 폴더 전체
- Services/ 폴더 전체

### 4. VS에서 열기

`.sln` 파일 더블 클릭해서 VS 실행. 또는 이미 열려있으면 새로 추가된 파일들이 자동으로 보입니다.

### 5. NuGet 패키지 복원

VS에서 자동으로 됩니다. 안 되면:
```
솔루션 우클릭 → NuGet 패키지 복원
```

또는 명령:
```
dotnet restore
```

### 6. F5로 실행

처음 실행하면:
1. 첫 실행 다이얼로그 표시
2. BG3MM 폴더 지정 + (선택) API 키 입력
3. "시작" 클릭
4. 메인 윈도우 표시

두 번째 실행부터는 첫 실행 다이얼로그 건너뛰고 바로 메인 윈도우.

## 확인 사항 (Phase 1 검증)

다음이 모두 동작하면 Phase 1 통과:

- [ ] 첫 실행 시 다이얼로그 표시되고 BG3MM 폴더 선택 가능
- [ ] "시작" 클릭 후 메인 윈도우 표시
- [ ] 메인 윈도우에 BG3MM 경로, 모드 폴더 경로, 모드 개수 표시
- [ ] [BG3MM 실행] 버튼으로 BG3MM 실행됨
- [ ] [설정] 버튼으로 설정 다이얼로그 열림
- [ ] 설정에서 값 변경 후 저장 → 메인 윈도우 즉시 갱신
- [ ] 앱 재시작 후 설정 유지됨
- [ ] `%LOCALAPPDATA%\BG3MM_UpdateHelper\` 폴더에 settings.json과 logs/ 생성됨

## 알려진 제한

- LSLib 미사용이므로 모드 개수만 표시 (이름/버전은 Phase 2부터)
- [Check for Updates] 버튼은 placeholder 메시지만 출력

## 문제 해결

| 증상 | 원인 / 해결 |
|---|---|
| 빌드 에러 "Newtonsoft.Json을 찾을 수 없음" | NuGet 복원 안 됨. 솔루션 우클릭 → "NuGet 패키지 복원" |
| 빌드 에러 "Microsoft.Win32.OpenFolderDialog 없음" | .NET 8 SDK 버전 낮음. VS 설치 시 .NET 8.0 워크로드 최신 확인 |
| 첫 실행 다이얼로그 안 뜸 | settings.json이 이미 있음. `%LOCALAPPDATA%\BG3MM_UpdateHelper\` 폴더 삭제하고 재실행 |
| 다른 에러 | 로그 확인: `%LOCALAPPDATA%\BG3MM_UpdateHelper\logs\` |
