# WindowsFS

Windows 11을 위한 가볍고 확장 가능한 디스크 용량 분석 앱입니다. 어느 폴더와 파일이 공간을 많이 차지하는지, 탐색 중인 위치에서 바로 확인할 수 있습니다.

## 초기 버전

- 연결된 로컬 드라이브 또는 지정한 폴더 분석
- 큰 파일 상위 1,000개와 폴더별 누적 용량 탐색
- 확장자별 용량 집계
- 용량 지도, 목록 내 이름·경로 검색, 선택 경로 복사
- 백그라운드 병렬 분석, 진행 상황 표시, 취소 후 부분 결과 확인
- Windows 탐색기에서 선택 항목의 위치 열기
- 한국어 인터페이스와 네이티브 WPF 화면

앱은 파일 이름, 경로, 크기 같은 **메타데이터만 읽습니다**. 파일 내용을 열거나 파일을 삭제·변경하지 않으며, 분석 결과를 디스크에 자동 저장하거나 서버로 전송하지 않습니다. 분석 결과는 앱을 닫으면 사라집니다. 경로 복사와 탐색기 열기는 사용자가 선택한 항목을 Windows 클립보드 또는 탐색기로 전달합니다.

## 실행

개발 환경은 Windows 11과 **.NET 10 SDK**가 필요합니다. .NET Runtime만 설치되어 있다면 SDK를 추가로 설치해야 합니다.

```powershell
./scripts/run.ps1
```

스크립트는 `.tools/dotnet/dotnet.exe`가 있으면 이를 사용하고, 없으면 `PATH`의 `dotnet`을 사용합니다. SDK를 자동 다운로드하거나 시스템 설정을 변경하지 않습니다.

특정 폴더를 바로 분석하려면 `./scripts/run.ps1 -Scan 'C:\Data'`를 사용하세요. 배포된 앱도 `WindowsFS.exe --scan 'C:\Data'` 형식으로 실행할 수 있습니다.

배포용 앱 생성:

```powershell
./scripts/publish.ps1
```

결과는 `.artifacts/publish/win-x64/`에 생성됩니다. 폴더 전체를 복사한 뒤 `WindowsFS.exe`를 실행하세요. 런타임이 포함되어 있어 대상 PC에 .NET을 별도로 설치할 필요가 없습니다. ARM64용 빌드는 `./scripts/publish.ps1 -Runtime win-arm64`로 생성할 수 있습니다.

GitHub Actions도 Windows에서 빌드, 코어 검증, WPF 뷰 모델 검증을 수행하고, 실행 파일과 런타임이 담긴 `WindowsFS-win-x64` 아티팩트를 만듭니다.

## 용량을 읽는 방법

표시되는 용량은 파일의 **논리적 크기** 합계입니다. Windows의 실제 디스크 사용량과는 다를 수 있습니다.

- 압축 파일, 희소 파일의 실제 할당 크기는 별도로 계산하지 않습니다.
- 하드 링크는 경로마다 집계하므로 같은 데이터가 중복 계산될 수 있습니다.
- 접근 권한이 없는 폴더, 정션·심볼릭 링크 등 재분석 지점, 오프라인 파일은 건너뜁니다. 클라우드 파일을 다운로드하지 않습니다.
- 건너뛴 항목 수는 확인한 항목 또는 디렉터리 수입니다. 접근할 수 없는 폴더 내부의 파일 수는 알 수 없습니다.
- 분석 중 파일이 변경되면 그 시점에 읽은 메타데이터가 반영됩니다. 볼륨 스냅샷을 만들지는 않습니다.
- 취소된 분석 결과는 확인한 범위만 포함합니다.

큰 파일 목록은 전체 검색 인덱스가 아니라 분석 범위에서 가장 큰 1,000개 파일입니다. 속도와 메모리를 위해 모든 파일 객체를 계속 보관하지 않습니다. 관리자 권한이나 원시 디스크 접근은 요구하지 않습니다.

## 개발 및 검증

```powershell
dotnet build src/WindowsFS.App/WindowsFS.App.csproj -c Release
dotnet run --project tests/WindowsFS.Core.Tests/WindowsFS.Core.Tests.csproj -c Release
dotnet run --project tests/WindowsFS.App.Tests/WindowsFS.App.Tests.csproj -c Release
```

별도의 테스트 프레임워크 패키지 없이 실행되는 코어 및 WPF 뷰 모델 검증 프로그램을 제공합니다. 테스트 파일은 임시 테스트 디렉터리에 생성하며, 실제 개인 파일 목록을 테스트 데이터로 저장하지 않습니다.

선택적으로 합성 파일을 생성해 분석 성능을 측정할 수 있습니다.

```powershell
dotnet run --project tests/WindowsFS.Core.Tests/WindowsFS.Core.Tests.csproj -c Release -- --benchmark
```

벤치마크는 임시 폴더의 합성 데이터로 실행하며 종료 후 정리합니다. 생성 직후의 파일을 읽으므로 첫 측정에도 OS 캐시의 영향이 있고, 반복 측정은 캐시가 더 준비된 상태입니다. 실제 전체 드라이브 분석이나 차가운 캐시 환경의 속도를 대표하는 수치로 해석하지 마세요.

```text
src/WindowsFS.App/         WPF 화면과 사용자 상호작용
src/WindowsFS.Core/        UI와 분리된 분석 엔진 및 결과 모델
tests/WindowsFS.Core.Tests/ 코어 동작 검증
tests/WindowsFS.App.Tests/  WPF 뷰 모델 동작 검증
scripts/                  로컬 실행 및 배포 스크립트
```

확장 지점과 성능 설계는 [아키텍처 문서](docs/architecture.md)에 정리했습니다.

## 저장소에 올리지 않는 정보

`.gitignore`는 로컬 SDK, 빌드 산출물, IDE 설정, 환경 변수 파일, 인증 키·인증서, 로그·덤프, 데이터베이스 및 개인 분석 결과 폴더를 제외합니다. 수동 분석 결과나 개인 테스트 파일은 `.artifacts/`, `scan-results/`, `exports/`, `local-data/`, `private/` 안에만 보관하세요.

`.gitignore`는 이미 추적 중인 파일이나 소스 코드 안에 직접 적힌 개인정보를 제거하지 않습니다. 커밋 전 `git diff --cached`와 `git status --short`로 실제 업로드 내용을 확인해야 합니다. 실제 파일 경로가 포함된 스크린샷이나 분석 결과는 이슈·PR·공개 저장소에 첨부하지 마세요.

커밋 작성자 이메일도 `.gitignore`의 보호 대상이 아닙니다. 개인정보 공개를 피하려면 저장소의 Git 설정에 GitHub에서 제공하는 `noreply` 이메일을 사용하세요.
