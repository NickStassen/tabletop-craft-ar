# RELEASING.md: versions, signing keys and uploads

How a release of tabletop-craft-ar is built, signed and published: the plugin jar, the server starter and the sideload
APK on GitHub, and the store build on Meta Horizon release channels. Normally a merge to master does all of it on
GitHub Actions (section 6): a change to the app, the plugin or the server starter becomes the next version by itself,
and a second workflow promotes the store build once it has passed on a headset. Sections 3 to 5 are the same steps by
hand, and the fallback when the automation can't run. `CLAUDE.md` "Releases" has the short checklist.

## 1. Versions

- The repo's `VERSION` file is the only source. Gradle reads it for the plugin (`tabletop-craft-ar-plugin-<version>.jar`),
  and `TabletopBatch` sets the app's `bundleVersion` from it. `auto-release.yml` bumps it (section 6.2); nobody bumps
  it by hand any more.
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
4. Never commit it, attach it to a release, put it in CI other than as an encrypted environment secret (section 6.3),
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
merge to master that changes the app, the plugin or the server starter
  ->  auto-release.yml: next version, commit "release: vX.Y.Z [skip release]", tag vX.Y.Z, push both
  ->  release.yml (called with the tag): verify, plugin jar, starter zip, release-signed APK
  ->  GitHub pre-release vX.Y.Z (three files)  +  APK on the Meta ALPHA channel
check the ALPHA build on a Quest
Actions > Promote (version X.Y.Z)  ->  promote.yml: ALPHA's build to BETA, GitHub release marked latest
```

Merging is releasing: nobody bumps `VERSION`, tags or writes the release commit by hand any more. The GitHub release
stays a pre-release until it's promoted. That matters beyond the label: the server starter downloads the plugin from
the **latest** release, so new servers only get the new plugin once the app that goes with it is on BETA.

### 6.2 auto-release.yml: what releases, and at which version

**What ships.** A push to master starts a run only when it touches one of these (the workflow's `paths`):

- `quest-app/Assets/**`, `quest-app/Packages/manifest.json`, `quest-app/ProjectSettings/**`;
- `server-plugin/src/main/**`, `server-plugin/build.gradle.kts`;
- `server-starter/**`.

`tools/release/auto_release.py` then checks the files changed since the last `vX.Y.Z` tag by the same list. A
`ProjectSettings.asset` change of only its `bundleVersion` and `AndroidBundleVersionCode` lines doesn't count, nor does a
file or folder under `Assets` whose name ends in `~` (Unity leaves those out). Docs, `tools/`, tests (`quest-app/Tests~`,
`server-plugin/src/test`), CI, media and `VERSION` itself never release. **Actions > Auto release > Run workflow**
always releases master as it is.

**Keywords** in commit messages (subject or body, any case):

| keyword | where | effect |
|---|---|---|
| `[minor]` | any commit since the last tag | a minor version: features, new caps or optional fields |
| `[major]` | any commit since the last tag | a major version: breaks plugins or apps of the same major version (beats `[minor]`) |
| neither | | a patch version |
| `[skip release]` | any commit of the push | this push doesn't release; its changes go out with the next release (the next push that changes shipped code, or a manual run), and its `[minor]`/`[major]` count then |

They count wherever they appear in a message, so don't quote them by accident (write "the minor keyword"). A manual
run's `bump` input (`patch`, `minor`, `major`) overrides them; `auto` reads them. A manual run ignores
`[skip release]`.

**The release commit.** The `bump` job runs `auto_release.py run`, which:

1. writes the version into `VERSION`, the PROTOCOL.md header line and `quest-app/ProjectSettings/ProjectSettings.asset`
   (`bundleVersion`, and `AndroidBundleVersionCode` as section 1 computes it with `TABLETOP_BUILD` 0);
2. gives PROTOCOL.md changelog lines that start with `- **vNEXT` the version, and adds
   `- **vX.Y.Z: automatic release.** <counts> since vA.B.C, listed in the release notes: no protocol changes unless an
   earlier entry says so.` after the last entry of a version up to this one, so the list stays in version order;
3. writes `docs/release-notes/vX.Y.Z.md` from the commit subjects since the last tag (New, Fixes and Other changes from
   the conventional prefixes; docs, CI, test and tooling commits, and any commit that changes no shipped file, only
   counted; merge and release commits left out), or
   renames a hand-written `docs/release-notes/vNEXT.md` (docs/release-notes/README.md);
4. runs `check_version.sh vX.Y.Z`, the check release.yml starts with;
5. commits `release: vX.Y.Z [skip release]` as `NickStassen <120063190+NickStassen@users.noreply.github.com>` (with
   the Co-Authored-By and Claude-Session trailers), tags `vX.Y.Z` (lightweight, like the earlier tags) and pushes the
   commit and the tag together (`git push --atomic`) with the job's `GITHUB_TOKEN`;
6. if master moved in the meantime, makes the release commit again on top of the new master, so the version, the
   changelog line and the notes cover the new commits too (at most 3 tries). If master no longer contains the commit
   the run started from (rewritten history), or the push is refused for another reason, it stops and pushes nothing.
   It never force-pushes.

Then the `release` job calls release.yml with the tag (6.4), in the same run. A push made with `GITHUB_TOKEN` starts no
workflow run, so the tag doesn't start release.yml a second time, CI doesn't run on the release commit, and the release
commit can't start another auto release (its `[skip release]`, and nothing shipped since the new tag, would stop one
anyway).

**A protocol change** still needs its changelog line in the same commit (CLAUDE.md "Protocol rules"). Since the version
isn't known yet, write it as `- **vNEXT: what changed ("Section").** ...` at the end of the changelog list (or with the
exact version, if you're sure of it), and mark the commit `[minor]` or `[major]` as the change needs.

**A `VERSION` with no tag yet** (bumped by hand) is released as it is; the keywords and the input are ignored then. A
`VERSION` behind the newest tag, or a version whose tag already exists, stops the run with the reason.

**Before merging**, from the branch:

```
python3 tools/release/auto_release.py plan --event workflow_dispatch      # the version a release would get now
python3 tools/release/auto_release.py apply --bump minor                  # write the files as a release would
git diff; git checkout -- VERSION docs/PROTOCOL.md quest-app/ProjectSettings; rm docs/release-notes/vX.Y.Z.md
```

`plan --before <old master>` decides as a push would. The unit tests: `cd tools && pytest release` (CI runs them).

**One at a time.** auto-release.yml, release.yml and promote.yml share the concurrency group `tabletop-release`, and a
running one is never cancelled. A push that arrives while a release builds waits; of several waiting runs in the group
GitHub keeps only the newest, which is enough for pushes, because a release covers everything since the last tag. A
waiting manual run or promotion replaced that way shows as cancelled and has to be started again.

### 6.3 The `release` environment

Repo Settings > Environments > `release`. Its deployment rules allow only tags `v*` and the branch `master`, so a
workflow run from any other branch (or a pull request) can't reach these secrets. Only the jobs that need them use the
environment: `apk` and `store` in release.yml, and promote.yml. When auto-release.yml calls release.yml, the run's ref
is still `refs/heads/master`, which the rules allow, and the called jobs read the environment's secrets themselves
(`secrets: inherit` passes the repository's, if any). auto-release.yml's own `bump` job uses no environment and no
secret.

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

### 6.4 release.yml

Called by auto-release.yml with the new tag (`workflow_call`, input `tag`: the usual way), or runs on a tag `v*.*.*`
pushed by hand, or from Actions > Release > Run workflow (`dry_run`, on by default). Every job checks out the tag (the
input when called, otherwise the run's own ref), so the build is exactly the tagged commit.

| job | what it does |
|---|---|
| `verify` | `tools/release/check_version.sh`: the tag (or `VERSION` on a dry run), `VERSION` and the PROTOCOL.md version line agree, and the version has release notes (docs/release-notes/README.md). A release that will be published must be on a commit that is on master. |
| `plugin` | JDK 25, `./gradlew build` (with the tests), `tabletop-craft-ar-plugin-X.Y.Z.jar`. |
| `starter` | `tabletop-server-starter-X.Y.Z.zip` from `server-starter/start.*` (checks that `start.bat` kept CRLF and `start.sh` its executable bit). |
| `apk` | Release environment. Makes disk space if the runner has under 45 GB free (the image is 14 GB unpacked), pulls GameCI's `unityci/editor:ubuntu-6000.0.84f1-android-3.2.2`, runs `tools/patch-meta-sdk-linux.sh`, restores the cached `quest-app/Library`, decodes the keystore into the runner's temp folder, runs `tools/ci/unity-build.sh build` (activation, `TabletopBatch.BuildReleaseApk` with its il2cpp segfault retries, the license returned), deletes the keystore, runs `upload_quest.sh --check-only` with the image's build-tools (`unity-build.sh check`), and keeps `tabletop-craft-ar-X.Y.Z.apk`. The certificate's SHA-256 is in the job summary: compare it with your password manager. Skipped on branches other than master (the environment wouldn't allow it). |
| `publish` | Called and tag runs only. `gh release create vX.Y.Z --prerelease` with exactly those three files; the title and text come from `tools/release/release_notes.sh`. If the release already exists, the three files are replaced and the text is left alone. |
| `store` | Called and tag runs only, release environment. Downloads Meta's Linux `ovr-platform-util` (`tools/release/fetch_ovr_platform_util.sh`) and runs `upload_quest.sh --channel ALPHA --age-group TEENS_AND_ADULTS`, with the release title as the channel's notes. |

On a dry run `publish` and `store` are skipped and everything else runs, so a dry run on master builds and checks the
real signed APK (download it from the run's artifacts to sideload it).

The `quest-app/Library` cache is saved only by runs whose ref is master: dry runs on master and automatic releases
(called from master, so the cache is master's, which every branch can read). Its key hashes `ProjectSettings/**`, so
each automatic release, which changes `bundleVersion`, saves a fresh Library (about 400 MB after the scrub) and the
next build restores the newest one. A run on a tag pushed by hand reads master's cache and saves nothing.

### 6.5 Dry run

```
gh workflow run release.yml --ref master -f dry_run=true
gh run watch
```

From any other branch only `verify`, `plugin` and `starter` run. The first run on master fills the Library cache, so a
dry run on master also makes the next release's build faster.

### 6.6 Releasing by hand, and after a release

Usually there's nothing to do: merge, then watch Actions > Auto release (the `bump` job's summary says what it decided
and why; a push that changes no shipped code starts no run at all). To release master as it is (after a held-back
push, for a docs-only change, or with a level of your choice):

```
gh workflow run auto-release.yml --ref master -f bump=auto        # or patch, minor, major
gh run watch
```

The old way still works when the workflow can't push (section 6.8): a commit on master with `VERSION`, the PROTOCOL.md
header and changelog and `docs/release-notes/vX.Y.Z.md`, pushed together with its tag
(`git push --atomic origin master vX.Y.Z`); release.yml runs on the tag, and auto-release.yml finds nothing new since
it. Push the tag with the commit: a `VERSION` without its tag on master is released by auto-release.yml as it is.

After the release:

1. Install the ALPHA build on a Quest (the headset's Meta account must be a user of the app's ALPHA channel: Developer
   Dashboard > Release channels > ALPHA) and check it. Record the result in TESTING.md and in the release text
   (`gh release edit vX.Y.Z --notes-file ...`: the generated notes say "Not yet" under "Verified on a Quest 3").
2. Promote (6.7).

### 6.7 Promoting a release

Actions > Promote > Run workflow, with the version (`1.2.2`), or:

```
gh workflow run promote.yml --ref master -f version=1.2.2           # add -f dry_run=true to only look
```

It checks that GitHub has the release `v1.2.2`, that ALPHA serves `1.2.2`, moves ALPHA's build to BETA
(`tools/release/promote_quest.sh`, `set-release-channel-build ... --age-group TEENS_AND_ADULTS`), checks that BETA
serves it, then runs `gh release edit v1.2.2 --prerelease=false --latest`. The BETA invite link stays the same. Check
in the Developer Dashboard that the channel's 90-day invite-link timer was renewed; if it wasn't, an upload to BETA
(section 5) renews it.

### 6.8 When something fails

- **auto-release's `bump` job** names the reason and has pushed nothing: a tag that already exists, a `VERSION` behind
  the newest tag, master rewritten under it, or a refused push. The job pushes with the default `GITHUB_TOKEN`
  (`contents: write`), so branch protection or a ruleset on master or on `v*` tags must let GitHub Actions push there;
  otherwise release the old way (6.6). Fix the cause, then run Auto release by hand.
- **The `release` job failed after the bump** (the release commit and the tag are on master): re-run the failed jobs
  from the run's page, or start over on the tag with `gh workflow run release.yml --ref vX.Y.Z -f dry_run=false`. Leave
  the tag in place; a later push simply releases the next version.
- **verify**, on a tag pushed by hand, names the mismatch (tag, VERSION, PROTOCOL.md, notes). Fix it on master, delete
  the tag (`git push origin :refs/tags/vX.Y.Z && git tag -d vX.Y.Z`) and tag the fixed commit.
- **A build or check failed**, or a runner hiccup: re-run the failed jobs from the run's page. To start over on an
  existing tag: `gh workflow run release.yml --ref vX.Y.Z -f dry_run=false` (the same checks; an existing GitHub release
  keeps its text and gets the files replaced).
- **store failed after publish**: re-run the `store` job. Meta refuses a versionCode it already has, so if Meta took
  the first upload, there's nothing to redo.
- **Unity activation**: the `apk` log says which secret is missing; "activation failed" usually means a wrong password,
  two-factor sign-in, or no free seat (Unity ID > Subscriptions shows the activations).

### 6.9 What protects the secrets

- Secrets exist only in the `release` environment, which only tags `v*` and master can use, and only in the jobs that
  need them. `release.yml` has no `pull_request` trigger. Inputs and outputs reach scripts as environment variables,
  never pasted into the script text.
- auto-release.yml's `bump` job is the only job that writes to the repository: it runs only on master (pushes and
  manual runs), holds `contents: write` with the job's own token and no secret, and runs only the repository's own
  `auto_release.py`.
- The secrets reach Unity's container as `docker run -e NAME` (the value comes from the environment, not from a command
  line), and nothing prints them; the serial read from the license is masked in the log.
- The keystore is decoded into its own folder in the runner's temp folder (outside the workspace), mounted read-only
  into Unity's container, and deleted after the build. The runner itself is thrown away after the job.
- The Library cache is saved from master-ref runs only, and pull requests can read master's caches, so `Library/Bee` (which
  holds the passwords) is deleted first and every other file holding a secret's value is deleted too
  (`unity-build.sh scrub`).
- Third-party code: GameCI's Docker image (pinned to an exact tag), Meta's `ovr-platform-util` (official download, its
  version and SHA-256 printed in the log), and `actions/*` at their major versions.

## 7. Release checklist

With the automation (section 6):

1. Merge to master. A change to the app, the plugin or the server starter releases by itself: a patch, or `[minor]` /
   `[major]` in a commit message; `[skip release]` holds a push back. A protocol change carries its `- **vNEXT: ...**`
   changelog line; a release that deserves more than generated notes carries `docs/release-notes/vNEXT.md`.
2. Watch Actions > Auto release: the release commit and tag, then the GitHub pre-release and the ALPHA upload.
3. Check the ALPHA build on a Quest; record it in TESTING.md and the release text.
4. Promote (`promote.yml`): BETA, and the GitHub release becomes the latest.
5. At least every 90 days something must reach BETA, so the invite link stays alive.
6. Install from the store on a second, non-developer account and record the result in TESTING.md.

By hand (the fallback): `cd server-plugin && ./gradlew build` (JDK 25); `BuildReleaseApk` (section 3) and
`upload_quest.sh --check-only`; commit and tag; `gh release create vX.Y.Z` with the plugin jar as
`tabletop-craft-ar-plugin-X.Y.Z.jar` (the server starter downloads exactly this name from the latest release; GitHub
lists its SHA-256, which the starter checks), the release-signed APK as `tabletop-craft-ar-X.Y.Z.apk`, and the three
starter scripts as `tabletop-server-starter-X.Y.Z.zip` (`cd server-starter && zip ../tabletop-server-starter-X.Y.Z.zip
start.*`); then `upload_quest.sh --channel ALPHA` and `promote_quest.sh` (section 5). **Never** attach a client jar,
`pack.zip`, `entity_models.json`, `sounds.zip` or the keystore.
