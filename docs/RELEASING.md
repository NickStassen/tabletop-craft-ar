# RELEASING.md: versions, signing keys and uploads

How a release of tabletop-craft-ar is built, signed and published: the plugin jar and the sideload APK on GitHub, and the
store build on a Meta Horizon release channel. `CLAUDE.md` "Releases" has the short checklist; this file has the detail.

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
4. Never commit it, attach it to a release, put it in CI without encrypted secrets, or send it anywhere. `.gitignore`
   ignores `*.jks`, `*.keystore` and `upload.env` as a safety net.

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
sideloading) can be the same release-signed file: then a sideloaded copy and a store copy carry the same certificate.
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
utility"; a Linux build exists and is marked experimental). Install it somewhere on `PATH` or set `OVR_PLATFORM_UTIL`.

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
   tools/release/upload_quest.sh --channel BETA --notes "1.1.0: pairing, automatic game files from your PC"
   ```

   The defaults are `--channel BETA` and `--age-group TEENS_AND_ADULTS` (without an age group a build stays a draft).
   Without `--notes`, the script uses the PROTOCOL.md changelog line for the version. `--symbols-dir` adds il2cpp debug
   symbols (`*.so`) when the build made them.
4. In the Developer Dashboard > Release channels: check the build arrived, and on the BETA channel turn on "Grant
   access to users by URL" (auto-approve) and copy the invite link for the README.

Release-channel rules worth remembering: builds there skip store review but must meet the packaging rules and the
content and app policies; a channel holds 200 users by default (2,500 at most, a permanent change); **the invite link
stops working 90 days after the channel's last upload**; invitees to a private channel must be 18 or older.

## 6. Release checklist

1. Bump `VERSION` (minor for features, patch for fixes), update the PROTOCOL.md header and changelog.
2. `cd server-plugin && ./gradlew build` (JDK 25); the jar is `build/libs/tabletop-craft-ar-plugin-<version>.jar`.
3. Build with `BuildReleaseApk` (section 3) and run `tools/release/upload_quest.sh --check-only`.
4. Commit `release: vX.Y.Z`, tag `vX.Y.Z`.
5. `gh release create vX.Y.Z` with notes (what's new, what was verified on a headset, known gaps), attaching the plugin
   jar as `tabletop-craft-ar-plugin-X.Y.Z.jar` (the server starter downloads exactly this name from the latest
   release; GitHub lists its SHA-256, which the starter checks), the APK as `tabletop-craft-ar-X.Y.Z.apk`, and the
   three starter scripts (`server-starter/start.bat`, `start.ps1`, `start.sh`) as `tabletop-server-starter-X.Y.Z.zip`
   (`cd server-starter && zip ../tabletop-server-starter-X.Y.Z.zip start.*`). **Never** attach a client jar, `pack.zip`, `entity_models.json`, `sounds.zip` or the keystore.
6. `tools/release/upload_quest.sh --channel BETA` (at least every 90 days, so the invite link stays alive).
7. Install from the store on a second, non-developer account and record the result in TESTING.md.
