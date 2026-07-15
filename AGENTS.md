# AI Usage Counter for Windows - Agent Runbook

Use this file as the first-stop guide when working in this repository.

## Project Snapshot

- App: Windows tray app for Claude/Codex/Gemini usage tracking.
- Stack: C# WinForms, .NET 9, WebView2, Velopack.
- Main project: `AiUsageCounter.csproj`
- Entry point: `Program.cs`
- Tray/update owner: `TrayContext.cs`
- WebView2 lifecycle/cookie host: `WebViewHost.cs`
- Providers: `ClaudeProvider.cs`, `CodexProvider.cs`, `GeminiProvider.cs`

## Important Update Rule

For this app, `git tag` alone is not enough for auto-update.

Office machines only see a new version after a Velopack GitHub Release exists
with the required release assets:

- `AiUsageCounter-<version>-full.nupkg`
- `AiUsageCounter-<version>-delta.nupkg`
- `AiUsageCounter-win-Setup.exe`
- `AiUsageCounter-win-Portable.zip`
- `releases.win.json`
- `RELEASES`

If users say "office machine does not detect update", first check GitHub
Releases, not just git tags.

## Normal Validation

Run:

```powershell
dotnet build
```

Known warning: `MSB3277` WindowsBase version conflict from WebView2/WPF package.
It is currently non-blocking if the build succeeds.

## Release Checklist

When asked to `commit tag update` or publish a new version:

1. Check repo state:

```powershell
git status --short --branch
git tag --sort=-v:refname
Select-String -Path AiUsageCounter.csproj -Pattern '<Version>'
```

2. Bump `<Version>` in `AiUsageCounter.csproj`.

3. Build:

```powershell
dotnet build
```

4. Commit and tag:

```powershell
git add -- AiUsageCounter.csproj <changed-files>
git commit -m "<short release/fix message>"
git tag -a v<version> -m "v<version>"
git push origin main --follow-tags
```

5. Publish self-contained Windows output:

```powershell
$root = (Resolve-Path .).Path
$publish = Join-Path $root 'publish'
if ((Resolve-Path -LiteralPath $publish -ErrorAction SilentlyContinue).Path -notlike "$root*") { throw "publish path is outside workspace" }
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

6. Build Velopack release:

```powershell
vpk pack --packId AiUsageCounter --packVersion <version> --packDir publish --packAuthors Jaybo --packTitle "AI Usage Counter" --runtime win-x64 --mainExe AiUsageCounter.exe --icon Assets\app.ico --outputDir Releases --yes
```

7. Upload GitHub Release:

```powershell
$token = gh auth token
vpk upload github --outputDir Releases --repoUrl https://github.com/azsx69/AI-USAGE-COUNTER-FORWIN --token $token --publish --merge --tag v<version> --releaseName v<version>
```

Do not pass a short commit hash as `--targetCommitish`; GitHub can reject it.
If the tag already exists locally/remotely, omitting `--targetCommitish` is fine.

8. Verify GitHub Release:

```powershell
gh release list --repo azsx69/AI-USAGE-COUNTER-FORWIN --limit 5
gh release view v<version> --repo azsx69/AI-USAGE-COUNTER-FORWIN --json tagName,name,isDraft,isPrerelease,assets,url,publishedAt
```

9. Verify Velopack feed can see the release:

```powershell
$tmp = Join-Path $env:TEMP ('aiusage-vpk-download-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    vpk download github --repoUrl https://github.com/azsx69/AI-USAGE-COUNTER-FORWIN --outputDir $tmp --channel win
    Get-ChildItem $tmp | Select-Object Name,Length
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
```

Expected result: downloads `AiUsageCounter-<version>-full.nupkg`.

## WebView2 Memory Notes

`WebViewHost.cs` intentionally uses short-lived hidden WebView2 instances.
Cookies remain in `%LOCALAPPDATA%\AiUsageCounter\WebView2\<provider>`, but the
renderer/Form/WebView2 objects should be disposed after cookie checks, login
completion, and headless fetches.

Avoid changing this back to a long-lived WebView2 unless there is a clear reason:
the old pattern caused `WebView2: Usage` memory to grow while hidden provider
pages stayed loaded.

## Velopack Update Flow

`TrayContext.cs` checks updates with:

```csharp
new UpdateManager(new GithubSource(UpdateRepoUrl, null, false))
```

`UpdateRepoUrl` is:

```text
https://github.com/azsx69/AI-USAGE-COUNTER-FORWIN
```

If update detection fails:

1. Confirm the installed app was installed through the Velopack setup.
2. Confirm GitHub Release is published, not draft.
3. Confirm `releases.win.json` and `RELEASES` are uploaded to that release.
4. Confirm `vpk download github ... --channel win` finds the new version.

## Git Hygiene

- Keep release/package output ignored unless the user explicitly asks to commit it.
- Before committing, stage only intended source/config/doc files.
- After pushing, report the exact commit, tag, and final branch state.
