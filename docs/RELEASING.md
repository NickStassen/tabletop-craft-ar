# RELEASING.md: versions, signing keys and uploads

How a release of tabletop-craft-ar is built, signed and published: the plugin jar, the server starter and the sideload
APK on GitHub, and the store build on Meta Horizon release channels. Normally a pushed tag does all of it on GitHub
Actions (section 6), and a second workflow promotes the store build once it has passed on a headset. Sections 3 to 5 are
the same steps by hand, and the fallback when the automation can't run. `CLAUDE.md` "Releases" has the short checklist.

## 1. Versions

- The repo's `VERSION` file is the only source. Gradle reads it for the plugin (`tabletop-craft-ar-plugin-<version>.jar`),
  and `TabletopBatch` sets the app's `bundleVersion` from it.
- Semantic versioning from v1.0.0: a plugin and an app with the same major version work together, features come in
  minor versions (behind capabilities, PROTOCOL.md), fixes in patch versions.
- **Android versionCode** = `M*1000000 + m*10000 + p*100 + TABLETOP_BUILD` (v1.1.0 is 1010000). `TABLETOP_BUILD`
  (environment, 0-99, default 0) is for uploading the same version to a store channel again: the store only takes a
  versionCode higher than the last one it has.

## 2. The release key (do this once)

The store identifies the app by its signing certificate. **Every update must be signed with the same key, and Meta has
no key recovery**: a lost key means a new app ID, a new store page, and every user reinstalling. A leaked key lets
someone else sign updates. So the key lives outside the repo, in two offline backups, and its passwords in a password
manager.

1. Create it (JDK `keytool`, any JDK 17 or newer), outside the repo:

   ```
   mkdir -p ~/.config/tabletop-craft-ar && chmod 700 ~/.config/tabletop-craft-ar
   keytool -genkeypair -keystore ~/.config/tabletop-craft-ar/tabletop-release.jks -alias tabletop \
     -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=Tabletop Craft AR"
   chmod 600 ~/.config/tabletop-craft-ar/tabletop-release.jks
   ```

   keytool asks for the keystore password (and, for a PKCS12 keystore, uses the same one for the key). Keep `-dname`
   generic: it is readable by anyone who has the APK, so no personal name or place.
2. Store the passwords in your password manager, next to the alias (`tabletop`) and the certificate's SHA-256
   (`keytool -list -v -keystore ~/.config/tabletop-craft-ar/tabletop-release.jks`).
3. Make **two offline backups** of the `.jks` (an encrypted USB stick and a second place). Test a backup once by
   listing it with keytool.
4. Never commit it, attach it to a release, put it in CI other than as an encrypted environment secret (section 6.2),
   or send it anywhere. `.gitignore` ignores `*.jks`, `*.keystore` and `upload.env` as a safety net.

## 3. Building the APK

`TabletopBatch` reads the key from four environment variables, only for the build. Nothing is written to
`ProjectSettings.asset` (the keystore fields are cleared after the build) and nothing is logged except the file name
and the alias.

| variable | value |
|---|---|
| `TABLETOP_KEYSTORE` | path to the `.jks` (`~/` is expanded) |
| `TABLETOP_KEYSTORE_PASS` | the keystore password |
| `TABLETOP_KEY_ALIAS` | `tabletop` |
| `TABLETOP_KEY_PASS` | the key password (for PKCS12, the keystore password) |
| `TABLETOP_BUILD` | optional, 0-99 (section 1) |

- `TabletopBatch.BuildApk`: release-signed when all four are set; with none set, Unity's debug key and the log line
  `TABLETOP_KEYSTORE is not set: debug signing`. Some but not all set fails the build.
- `TabletopBatch.BuildReleaseApk`: the same, but fails when the key isn't set. Use it for anything that goes to the
  store.
- Both build without the Development flag (not debuggable) to `quest-app/Builds/TabletopCraftAR.apk`.
- After a release build `ProjectSettings.asset` may show `AndroidKeystoreName: '{inproject}: '` (Unity's way of
  writing "no keystore"; it holds no path). Revert it with `git checkout quest-app/ProjectSettings/ProjectSettings.asset`
  rather than committing it.
- Unity writes the two passwords in plain text into the generated Gradle project and Bee's own files under
  `quest-app/Library/Bee/` (for example `Library/Bee/Android/Prj/IL2CPP/Gradle/launcher/build.gradle`). After a
  release build on your own machine, delete `quest-app/Library/Bee` if anyone else can read that folder; the next build
  makes it again.

```
read -rs TABLETOP_KEYSTORE_PASS && export TABLETOP_KEYSTORE_PASS TABLETOP_KEY_PASS="$TABLETOP_KEYSTORE_PASS"
export TABLETOP_KEYSTORE=~/.config/tabletop-craft-ar/tabletop-release.jks TABLETOP_KEY_ALIAS=tabletop
~/Unity/Hub/Editor/6000.0.84f1/Editor/Unity -batchmode -nographics -quit -projectPath quest-app -buildTarget Android \
  -executeMethod TabletopCraft.EditorTools.TabletopBatch.BuildReleaseApk -logFile build.log
grep "\[Tabletop\] Build" build.log
```

(`read -rs` keeps the password out of the shell history.) On Windows PowerShell, set them with
`$env:TABLETOP_KEYSTORE = "..."` and a `Read-Host -AsSecureString` for the passwords, or in the Unity Editor's own
environment.

**Which APK goes where.** The store build must be release-signed. The APK attached to the GitHub release (for
sideloading) is the same release-signed file, so a sideloaded copy and a store copy carry the same certificate and
either can update the other.
A build signed with a different key (the debug key) can't be updated in place by the other one: Android refuses, so the
user has to uninstall first, which also deletes the app's files folder (the app downloads its files from the server
again after pairing).

## 4. Checks

`tools/release/upload_quest.sh --check-only` checks an APK against Meta's packaging rules (VRC.Quest.Packaging):
signed with APK Signature Scheme v2 and **not** with the Android debug key, package `com.tabletopcraft.ar`, not
debuggable, minSdk 32 and targetSdk 34, native code arm64-v8a only, under 1 GB. It prints the certificate's SHA-256:
compare it with the one in your password manager. It uses `apksigner` and `aapt2` from Android build-tools (found in
Unity's own SDK, `ANDROID_HOME`, or `TABLETOP_BUILD_TOOLS`).

## 5. Uploading to a release channel

Meta's command-line tool `ovr-platform-util` uploads builds (Meta Horizon developer docs, "Platform command line
utility"; a Linux build exists and is marked experimental). Install it somewhere on `PATH` or set `OVR_PLATFORM_UTIL`;
on Linux, `tools/release/fetch_ovr_platform_util.sh DIR` downloads the newest one from Meta's official link and prints
its path.

1. **App ID**: Developer Dashboard > your app > Development > API. **Token**: account settings > "Get Oculus platform
   utility token" (a user token: it can upload for every app of your organization, so treat it like a password).
2. Put both in `~/.config/tabletop-craft-ar/upload.env`, `chmod 600`:

   ```
   TABLETOP_APP_ID=1234567890
   TABLETOP_UPLOAD_TOKEN=...
   ```

   or export them in the shell. The script never takes them as options. It passes the token to `ovr-platform-util`
   as an argument (the tool has no other way), so while the upload runs it is visible in this machine's process list.
3. Dry run, then upload:

   ```
   tools/release/upload_quest.sh --dry-run
   tools/release/upload_quest.sh --channel ALPHA --notes "v1.1.0: pairing, automatic game files from your PC"
   ```

   The defaults are `--channel BETA` and `--age-group TEENS_AND_ADULTS` (without an age group a build stays a draft).
   Without `--notes`, the script uses the PROTOCOL.md changelog line for the version. `--symbols-dir` adds il2cpp debug
   symbols (`*.so`) when the build made them.
4. To move a build that passed on ALPHA to BETA without uploading again: `tools/release/promote_quest.sh X.Y.Z`
   (`--dry-run` first shows what each channel serves). It checks that ALPHA serves exactly that version, runs
   `ovr-platform-util set-release-channel-build --source-channel ALPHA --destination-channel BETA`, and checks BETA
   afterwards. Same credentials as the upload.
5. In the Developer Dashboard > Release channels: check the build arrived, and on the BETA channel turn on "Grant
   access to users by URL" (auto-approve) and copy the invite link for the README.

Release-channel rules worth remembering: builds there skip store review but must meet the packaging rules and the
content and app policies; a channel holds 200 users by default (2,500 at most, a permanent change); **the invite link
stops working 90 days after the channel's last upload**; invitees to a private channel must be 18 or older.

## 6. Automated releases (GitHub Actions)

### 6.1 The flow

```
git tag vX.Y.Z on master  ->  release.yml: verify, plugin jar, starter zip, release-signed APK
                          ->  GitHub pre-release vX.Y.Z (three files)  +  APK on the Meta ALPHA channel
check the ALPHA build on a Quest
Actions > Promote (version X.Y.Z)  ->  promote.yml: ALPHA's build to BETA, GitHub release marked latest
```

The GitHub release stays a pre-release until it's promoted. That matters beyond the label: the server starter downloads
the plugin from the **latest** release, so new servers only get the new plugin once the app that goes with it is on
BETA.

### 6.2 The `release` environment

Repo Settings > Environments > `release`. Its deployment rules allow only tags `v*` and the branch `master`, so a
workflow run from any other branch (or a pull request) can't reach these secrets. Only the jobs that need them use the
environment: `apk` and `store` in release.yml, and promote.yml.

| name | kind | value |
|---|---|---|
| `TABLETOP_KEYSTORE_BASE64` | secret | the release keystore, base64: `base64 -w0 ~/.config/tabletop-craft-ar/tabletop-release.jks \| gh secret set TABLETOP_KEYSTORE_BASE64 --env release` |
| `TABLETOP_KEYSTORE_PASS`, `TABLETOP_KEY_ALIAS`, `TABLETOP_KEY_PASS` | secrets | as in section 3 |
| `TABLETOP_UPLOAD_TOKEN` | secret | the `ovr-platform-util` user token (section 5) |
| `TABLETOP_APP_ID` | variable | the app ID (not secret: it's in the store URL) |
| `UNITY_LICENSE` | secret | the contents of `Unity_lic.ulf` from a machine where Unity Hub activated the Personal license (Linux `~/.local/share/unity3d/Unity/Unity_lic.ulf`, Windows `C:\ProgramData\Unity\Unity_lic.ulf`, macOS `/Library/Application Support/Unity/Unity_lic.ulf`): `gh secret set UNITY_LICENSE --env release < Unity_lic.ulf` |
| `UNITY_EMAIL`, `UNITY_PASSWORD` | secrets | the Unity ID that owns that license |

`gh secret set NAME --env release` with no value asks for it without echoing, which keeps it out of the shell history.
The Unity license is activated the way GameCI does it: the Personal license's serial is read from `UNITY_LICENSE` and
activated with `UNITY_EMAIL` and `UNITY_PASSWORD`, and the seat is returned after the build. A license file alone isn't
enough: it is bound to the machine that activated it. A Unity ID that signs in with two-factor authentication may be
refused by the command-line activation.

### 6.3 release.yml

Runs on a pushed tag `v*.*.*`, or from Actions > Release > Run workflow (`dry_run`, on by default).

| job | what it does |
|---|---|
| `verify` | `tools/release/check_version.sh`: the tag (or `VERSION` on a dry run), `VERSION` and the PROTOCOL.md version line agree, and the version has release notes (docs/release-notes/README.md). A release that will be published must be on a commit that is on master. |
| `plugin` | JDK 25, `./gradlew build` (with the tests), `tabletop-craft-ar-plugin-X.Y.Z.jar`. |
| `starter` | `tabletop-server-starter-X.Y.Z.zip` from `server-starter/start.*` (checks that `start.bat` kept CRLF and `start.sh` its executable bit). |
| `apk` | Release environment. Makes disk space if the runner has under 45 GB free (the image is 14 GB unpacked), pulls GameCI's `unityci/editor:ubuntu-6000.0.84f1-android-3.2.2`, runs `tools/patch-meta-sdk-linux.sh`, restores the cached `quest-app/Library`, decodes the keystore into the runner's temp folder, runs `tools/ci/unity-build.sh build` (activation, `TabletopBatch.BuildReleaseApk` with its il2cpp segfault retries, the license returned), deletes the keystore, runs `upload_quest.sh --check-only` with the image's build-tools (`unity-build.sh check`), and keeps `tabletop-craft-ar-X.Y.Z.apk`. The certificate's SHA-256 is in the job summary: compare it with your password manager. Skipped on branches other than master (the environment wouldn't allow it). |
| `publish` | Tag runs only. `gh release create vX.Y.Z --prerelease` with exactly those three files; the title and text come from `tools/release/release_notes.sh`. If the release already exists, the three files are replaced and the text is left alone. |
| `store` | Tag runs only, release environment. Downloads Meta's Linux `ovr-platform-util` (`tools/release/fetch_ovr_platform_util.sh`) and runs `upload_quest.sh --channel ALPHA --age-group TEENS_AND_ADULTS`, with the release title as the channel's notes. |

On a dry run `publish` and `store` are skipped and everything else runs, so a dry run on master builds and checks the
real signed APK (download it from the run's artifacts to sideload it).

### 6.4 Dry run

```
gh workflow run release.yml --ref master -f dry_run=true
gh run watch
```

From any other branch only `verify`, `plugin` and `starter` run. The first run on master fills the Library cache (it is
only saved from master), and a tag run reads master's cache, so a dry run on master before tagging also makes the tag's
build faster.

### 6.5 Cutting a release

1. On master: bump `VERSION`, update the PROTOCOL.md header and changelog, and write `docs/release-notes/vX.Y.Z.md`
   (optional but recommended: it becomes the GitHub release text and its heading the store notes).
2. `tools/release/check_version.sh` locally, commit `release: vX.Y.Z`, push, and optionally dry-run (6.4).
3. `git tag vX.Y.Z && git push origin vX.Y.Z`. Watch the run under Actions > Release.
4. Install the ALPHA build on a Quest (the headset's Meta account must be a user of the app's ALPHA channel: Developer
   Dashboard > Release channels > ALPHA) and check it. Record the result in TESTING.md and, if you like, the release
   text (`gh release edit vX.Y.Z --notes-file ...`).
5. Promote (6.6).

### 6.6 Promoting a release

Actions > Promote > Run workflow, with the version (`1.2.2`), or:

```
gh workflow run promote.yml --ref master -f version=1.2.2           # add -f dry_run=true to only look
```

It checks that GitHub has the release `v1.2.2`, that ALPHA serves `1.2.2`, moves ALPHA's build to BETA
(`tools/release/promote_quest.sh`, `set-release-channel-build ... --age-group TEENS_AND_ADULTS`), checks that BETA
serves it, then runs `gh release edit v1.2.2 --prerelease=false --latest`. The BETA invite link stays the same. Check
in the Developer Dashboard that the channel's 90-day invite-link timer was renewed; if it wasn't, an upload to BETA
(section 5) renews it.

### 6.7 When something fails

- **verify** names the mismatch (tag, VERSION, PROTOCOL.md, notes). Fix it on master, delete the tag
  (`git push origin :refs/tags/vX.Y.Z && git tag -d vX.Y.Z`) and tag the fixed commit.
- **A build or check failed**, or a runner hiccup: re-run the failed jobs from the run's page. To start over on an
  existing tag: `gh workflow run release.yml --ref vX.Y.Z -f dry_run=false` (the same checks; an existing GitHub release
  keeps its text and gets the files replaced).
- **store failed after publish**: re-run the `store` job. Meta refuses a versionCode it already has, so if Meta took
  the first upload, there's nothing to redo.
- **Unity activation**: the `apk` log says which secret is missing; "activation failed" usually means a wrong password,
  two-factor sign-in, or no free seat (Unity ID > Subscriptions shows the activations).

### 6.8 What protects the secrets

- Secrets exist only in the `release` environment, which only tags `v*` and master can use, and only in the jobs that
  need them. `release.yml` has no `pull_request` trigger. Inputs and outputs reach scripts as environment variables,
  never pasted into the script text.
- The secrets reach Unity's container as `docker run -e NAME` (the value comes from the environment, not from a command
  line), and nothing prints them; the serial read from the license is masked in the log.
- The keystore is decoded into its own folder in the runner's temp folder (outside the workspace), mounted read-only
  into Unity's container, and deleted after the build. The runner itself is thrown away after the job.
- The Library cache is saved from master only, and pull requests can read master's caches, so `Library/Bee` (which
  holds the passwords) is deleted first and every other file holding a secret's value is deleted too
  (`unity-build.sh scrub`).
- Third-party code: GameCI's Docker image (pinned to an exact tag), Meta's `ovr-platform-util` (official download, its
  version and SHA-256 printed in the log), and `actions/*` at their major versions.

## 7. Release checklist

With the automation (section 6):

1. Bump `VERSION` (minor for features, patch for fixes), update the PROTOCOL.md header and changelog, write
   `docs/release-notes/vX.Y.Z.md`.
2. Commit `release: vX.Y.Z` on master, push, dry-run (`gh workflow run release.yml --ref master -f dry_run=true`).
3. Tag `vX.Y.Z` and push the tag: the GitHub pre-release and the ALPHA upload follow.
4. Check the ALPHA build on a Quest; record it in TESTING.md.
5. Promote (`promote.yml`): BETA, and the GitHub release becomes the latest.
6. At least every 90 days something must reach BETA, so the invite link stays alive.
7. Install from the store on a second, non-developer account and record the result in TESTING.md.

By hand (the fallback): `cd server-plugin && ./gradlew build` (JDK 25); `BuildReleaseApk` (section 3) and
`upload_quest.sh --check-only`; commit and tag; `gh release create vX.Y.Z` with the plugin jar as
`tabletop-craft-ar-plugin-X.Y.Z.jar` (the server starter downloads exactly this name from the latest release; GitHub
lists its SHA-256, which the starter checks), the release-signed APK as `tabletop-craft-ar-X.Y.Z.apk`, and the three
starter scripts as `tabletop-server-starter-X.Y.Z.zip` (`cd server-starter && zip ../tabletop-server-starter-X.Y.Z.zip
start.*`); then `upload_quest.sh --channel ALPHA` and `promote_quest.sh` (section 5). **Never** attach a client jar,
`pack.zip`, `entity_models.json`, `sounds.zip` or the keystore.
