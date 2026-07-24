# SubtitleCleanUp

SubtitleCleanUp is a self-hosted .NET 10 Blazor application that finds inconsistent
SRT language tags and duplicate subtitles. Scans only create review proposals.
Files are changed only after explicit approval in the web interface.

## Naming behavior

- ISO 639-1 language tags become lowercase ISO 639-2/T tags: `en` becomes `eng`,
  `sv` becomes `swe`, and `hi` as a language becomes `hin`.
- A hearing-impaired `hi` variant becomes `sdh`: `Movie.en.hi.srt` becomes
  `Movie.eng.sdh.srt`.
- `Movie.en.hi.srt`, `Movie.eng.hi2.srt`, and `Movie.eng.sdh.srt` are duplicates
  because they share the same normalized language and variant.
- `forced`, `sdh`, `cc`, `commentary`, `signs`, and `foreign` remain distinct
  variants.
- The largest duplicate is recommended by default, but another keeper can be
  selected before applying.

A subtitle must have a matching video stem in the same directory. Supported video
extensions are MKV, MP4, AVI, MOV, M4V, WEBM, WMV, TS, and M2TS. Ambiguous or
unrecognized SRT files are shown for manual review and cannot be applied automatically.

## Run with Docker Compose

1. Copy `.env.example` to `.env`.
2. Set `MEDIA_PATH` to the host folder containing your media. The folder must be
   writable by the container if you want to apply changes.
3. Start the application:

   ```sh
   docker compose up --build -d
   ```

4. Open <http://127.0.0.1:8080>.

The Compose example binds only to localhost. Change the port binding deliberately
if the application must be available elsewhere, preferably behind an authenticated
reverse proxy.

SQLite state and quarantined files are stored in the `subtitlecleanup-data` volume.
Removing and recreating the application container does not remove that volume.

## Prebuilt container

After the test suite passes on `main`, GitHub Actions publishes the same Dockerfile
to GitHub Container Registry:

```sh
docker pull ghcr.io/trembon/subtitlecleanup:latest
```

Every main-branch build receives both `latest` and `sha-<commit>` tags. Git tags
such as `v1.0.0` are also published with their matching image tag. Pull requests
build the image for validation but never publish it.

The first published package may be private depending on the GitHub account's
package settings. Change the package visibility to public in GitHub if anonymous
Docker pulls should be allowed.

## Configuration

ASP.NET Core's environment-variable format is used:

| Variable | Purpose |
| --- | --- |
| `SubtitleCleanup__Roots__0__Name` | Stable display name for the first media root |
| `SubtitleCleanup__Roots__0__Path` | Container path of the first media root |
| `SubtitleCleanup__ScanCron` | Five-field cron expression |
| `SubtitleCleanup__TimeZone` | IANA time-zone identifier |
| `SubtitleCleanup__ScanOnStartup` | Run a discovery scan when the container starts |
| `SubtitleCleanup__QuarantineRoot` | Persistent quarantine directory |
| `SubtitleCleanup__PreviewMaxBytes` | Maximum preview size before truncation |
| `ConnectionStrings__SubtitleCleanup` | SQLite connection string |

Add additional roots with `Roots__1`, `Roots__2`, and so on, and add corresponding
volume mounts. Scheduled scans never apply proposals.

## Safety model

Before applying a proposal, every participating file is checked against the size,
timestamp, and SHA-256 recorded by the scan. A mismatch makes the proposal stale.
Duplicates are copied to quarantine and hash-verified before their originals are
removed. Restore refuses to overwrite an existing file. Permanent purge requires a
second confirmation in the UI.

## Local development

The .NET 10 SDK is required.

```sh
dotnet restore SubtitleCleanUp.slnx
dotnet build SubtitleCleanUp.slnx
dotnet test SubtitleCleanUp.slnx
dotnet run --project src/SubtitleCleanUp.Web
```

The test projects use xUnit, Shouldly, NSubstitute, bUnit, real temporary
directories, and SQLite in-memory databases.
