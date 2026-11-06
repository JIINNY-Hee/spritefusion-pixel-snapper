# Windows 버전 빌드와 배포

## 배포 구조

- 앱: `desktop/PixelSnapper.App`, .NET 8 WPF, Windows x64 단일 EXE.
- 변환 엔진: 저장소의 Rust 소스를 `cargo build --release --locked`로 빌드해 EXE에 포함.
- 업데이트 원본: `JIINNY-Hee/spritefusion-pixel-snapper`의 최신 정식 GitHub Release.
- 필수 릴리스 파일: `PixelSnapper.exe`, `SHA256SUMS.txt`.
- 체크섬 형식: SHA-256 소문자 64자리, 공백 두 개, `PixelSnapper.exe`.
- 버전 형식: 정식 태그 `vMAJOR.MINOR.PATCH`. 예: `v1.1.0`. 미리보기 릴리스는 자동 업데이트 대상으로 사용하지 않습니다.

## 로컬 빌드

Windows에 .NET 8 SDK, Rust MSVC 도구 모음과 C++ 빌드 도구가 필요합니다.

```powershell
./desktop/build.ps1
```

Rust 테스트, 이미지 변환 테스트, 업데이트 테스트를 실행한 뒤 `desktop/dist`에 실행파일·체크섬·설명서·라이선스를 만듭니다. EXE, `bin`, `obj`, `dist`와 빌드 임시 파일은 Git에 추가하지 않습니다.

같은 소스에서 미리 빌드한 변환 엔진을 사용하는 경우에는 다음처럼 지정할 수 있습니다. 이 경로에서는 Rust 빌드와 Rust 테스트만 생략되며 .NET 테스트는 실행합니다.

```powershell
./desktop/build.ps1 -EnginePath 'C:\path\spritefusion-pixel-snapper.exe'
```

`-CargoPath`로 cargo 실행 경로를 지정할 수 있습니다. `-Version 1.1.1`은 로컬 빌드 버전 재정의이며, 공식 배포에서는 프로젝트 파일의 `<Version>`을 갱신해야 합니다. `-SkipTests`는 이미 같은 소스를 검증한 로컬 재패키징용이며 GitHub Actions에서는 사용하지 않습니다.

## 다음 버전 자동 배포

1. `desktop/PixelSnapper.App/PixelSnapper.App.csproj`의 `<Version>`을 이전보다 높은 값으로 바꿉니다. 예: `1.1.1`.
2. 변경 내용을 검토·테스트하고 `main`에 올립니다.
3. GitHub 저장소의 **Actions → Windows desktop → Run workflow**를 엽니다.
4. `main`을 선택하고 `release_tag`에 `v1.1.1`을 입력해 실행합니다.

워크플로는 선택한 커밋을 빌드·검사하고, 새 태그와 임시 릴리스를 만든 뒤 모든 파일 업로드가 끝나면 정식 최신 릴리스로 공개합니다. 릴리스에 실패하면 Actions 로그와 Draft 상태를 확인하세요. 기존 태그와 릴리스 파일은 덮어쓰지 않습니다. 실패한 임시 릴리스를 정리해야 하는 경우 소유자가 내용을 확인한 뒤 직접 처리하거나 새 버전으로 배포합니다.

CLI로 태그를 올리는 방법도 지원합니다. 태그 버전은 프로젝트 파일의 버전과 일치해야 합니다.

```powershell
git tag v1.1.1
git push origin v1.1.1
```

`main` 푸시와 PR에서는 빌드·테스트만 실행합니다. `release_tag`를 비운 수동 실행도 빌드·테스트만 합니다. 테스트용 EXE는 해당 Actions 실행의 `PixelSnapper-win-x64` 아티팩트에서 받을 수 있습니다.

## 처음 한 번 확인할 설정

- 저장소의 Actions가 활성화되어 있어야 합니다. 포크 저장소는 Actions 화면에서 활성화가 필요할 수 있습니다.
- 워크플로의 배포 작업에는 `contents: write` 권한이 필요합니다. 조직 정책이 이를 차단하면 저장소 소유자가 정책을 확인해야 합니다.
- 별도 개인 토큰을 저장할 필요가 없습니다. 릴리스 작업은 실행 중 제공되는 `GITHUB_TOKEN`을 사용합니다.
- GitHub에 릴리스를 공개하는 계정은 이 저장소에 쓰기 권한이 있어야 합니다.

## 처음 설치한 사용자

기존 1.0.0 EXE에는 자동 업데이트 코드가 없습니다. 1.1.0 이상을 한 번 직접 내려받아 교체해야 이후 업데이트 안내를 받을 수 있습니다. 새 버전은 앱 시작 시 자동 확인하거나 앱의 업데이트 확인 기능으로 찾을 수 있습니다. 설치 전에 사용자가 선택하며 진행 중인 이미지 변환을 중단해 강제로 재시작하지 않습니다.

참고: [GitHub 워크플로 문법](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax), [GitHub CLI 릴리스 생성](https://cli.github.com/manual/gh_release_create).
