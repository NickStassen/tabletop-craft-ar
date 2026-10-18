"""Tests for tools/release/auto_release.py (docs/RELEASING.md section 6). Run from tools/: `pytest release`.

The git tests build throwaway repositories (a bare "origin" and clones) under pytest's tmp_path, with the git
configuration isolated from the machine's.
"""

from __future__ import annotations

import importlib.util
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
_spec = importlib.util.spec_from_file_location("auto_release", HERE / "auto_release.py")
ar = importlib.util.module_from_spec(_spec)
sys.modules["auto_release"] = ar
_spec.loader.exec_module(ar)

BOT = "NickStassen <120063190+NickStassen@users.noreply.github.com>"
TRAILERS = ["Co-Authored-By: Example Agent <noreply@example.com>", "Claude-Session: https://example.com/session"]
ASSET = "quest-app/Assets/Scripts/World/NavState.cs"
PLUGIN = "server-plugin/src/main/java/dev/tabletopcraft/ar/TabletopPlugin.java"
STARTER = "server-starter/start.ps1"

PROTOCOL_TEXT = """# PROTOCOL.md — Tabletop link protocol, v1.2.1

The project version versions the plugin, the app and this protocol together.

## Changelog

- **v1 (2026-10-13):** initial.
- **v1.2.0: chat safety ("Chat safety").** Additive: cap `report_v1`.
- **v1.2.1: no message changes.** The Info tab wraps its status text.

## 4.13–4.16 Runtime region control (v1, additive)

- `region_request` (client→server).
"""

SETTINGS_TEXT = """%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!129 &1
PlayerSettings:
  productName: Tabletop Craft AR
  bundleVersion: 1.2.1
  preloadedAssets: []
  macOSBundleVersionCode: 7
  AndroidBundleVersionCode: 1020100
  AndroidMinSdkVersion: 32
"""

AUTO = "- **v1.2.2: automatic release.** 1 fix since v1.2.1."


# ---------------------------------------------------------------------------------------------------- fixtures

@pytest.fixture(autouse=True)
def isolated_git(monkeypatch, tmp_path):
    for key in list(os.environ):
        if key.startswith(("GIT_", "GITHUB_")):
            monkeypatch.delenv(key)
    config = tmp_path / "gitconfig"
    config.write_text("")
    monkeypatch.setenv("GIT_CONFIG_GLOBAL", str(config))
    monkeypatch.setenv("GIT_CONFIG_NOSYSTEM", "1")
    for role in ("AUTHOR", "COMMITTER"):
        monkeypatch.setenv(f"GIT_{role}_NAME", "Dev")
        monkeypatch.setenv(f"GIT_{role}_EMAIL", "dev@example.com")


def bot_identity(monkeypatch):
    for role in ("AUTHOR", "COMMITTER"):
        monkeypatch.setenv(f"GIT_{role}_NAME", "NickStassen")
        monkeypatch.setenv(f"GIT_{role}_EMAIL", "120063190+NickStassen@users.noreply.github.com")


class Repo:
    def __init__(self, path: Path):
        self.path = path

    def git(self, *args: str) -> str:
        r = subprocess.run(["git", "-C", str(self.path), *args], capture_output=True, text=True)
        assert r.returncode == 0, f"git {args}: {r.stderr}"
        return r.stdout.strip()

    def read(self, rel: str) -> str:
        return (self.path / rel).read_text(encoding="utf-8")

    def write(self, rel: str, text: str) -> None:
        p = self.path / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def commit(self, message: str, files: dict[str, str] | None = None, author: str | None = None) -> str:
        for rel, text in (files or {}).items():
            self.write(rel, text)
        self.git("add", "-A")
        self.git("commit", "-q", "--allow-empty", "-m", message, *(["--author", author] if author else []))
        return self.head()

    def head(self) -> str:
        return self.git("rev-parse", "HEAD")

    def push(self, *extra: str) -> None:
        self.git("push", "-q", *extra, "origin", "master")


@pytest.fixture
def origin(tmp_path) -> Repo:
    bare = tmp_path / "origin.git"
    subprocess.run(["git", "init", "-q", "--bare", "-b", "master", str(bare)], check=True)
    return Repo(bare)


def clone(origin: Repo, path: Path) -> Repo:
    subprocess.run(["git", "clone", "-q", str(origin.path), str(path)], check=True, capture_output=True)
    return Repo(path)


@pytest.fixture
def repo(tmp_path, origin) -> Repo:
    """A repository at the release v1.2.1 (tagged and pushed), with the real check_version.sh and release_notes.sh."""
    r = clone(origin, tmp_path / "work")
    for script in ("check_version.sh", "release_notes.sh"):
        dest = r.path / "tools/release" / script
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(HERE / script, dest)
    r.commit("feat(plugin): chat safety", {
        "VERSION": "1.2.0\n",
        "docs/PROTOCOL.md": PROTOCOL_TEXT.replace("v1.2.1\n", "v1.2.0\n", 1),
        ar.SETTINGS: SETTINGS_TEXT,
        "docs/release-notes/README.md": "# Release notes\n",
        ASSET: "class NavState {}\n",
        PLUGIN: "class TabletopPlugin {}\n",
        STARTER: "Write-Host start\n",
        "README.md": "readme\n",
    })
    r.git("tag", "v1.2.0")
    r.commit("release: v1.2.1", {"VERSION": "1.2.1\n", "docs/PROTOCOL.md": PROTOCOL_TEXT})
    r.git("tag", "v1.2.1")
    r.git("push", "-q", "origin", "master", "v1.2.0", "v1.2.1")
    return r


def plan(repo: Repo, event: str = "push", before: str = "", bump: str = "auto") -> ar.Plan:
    return ar.make_plan(ar.Git(repo.path), "HEAD", event, before, bump)


def run_args(repo: Repo, before: str = "", event: str = "push", bump: str = "auto") -> list[str]:
    return ["--root", str(repo.path), "run", "--event", event, "--before", before, "--bump", bump,
            *[f"--trailer={t}" for t in TRAILERS]]


# ---------------------------------------------------------------------------------------------------- versions

@pytest.mark.parametrize("level,expected", [("patch", "1.2.2"), ("minor", "1.3.0"), ("major", "2.0.0")])
def test_bump_version(level, expected):
    assert ar.bump_version("1.2.1", level) == expected


@pytest.mark.parametrize("version,code", [("1.2.1", 1020100), ("1.2.2", 1020200), ("1.3.0", 1030000), ("2.0.0", 2000000),
                                          ("0.8.1", 80100)])
def test_version_code(version, code):
    assert ar.version_code(version) == code


def test_version_code_scheme_limits():
    with pytest.raises(ar.ReleaseError):
        ar.version_code("1.100.0")
    with pytest.raises(ar.ReleaseError):
        ar.parse_version("1.2")


def test_keyword_level():
    assert ar.keyword_level(["fix: a", "docs: b"]) == "patch"
    assert ar.keyword_level(["fix: a", "feat: b\n\nA new cap. [minor]"]) == "minor"
    assert ar.keyword_level(["feat: b [Minor]", "Merge branch 'x' [MAJOR]"]) == "major"


# ---------------------------------------------------------------------------------------------------- what ships

@pytest.mark.parametrize("path,shipped", [
    ("quest-app/Assets/Scripts/World/NavState.cs", True),
    ("quest-app/Assets/Shaders/TabletopClip.cginc", True),
    ("quest-app/Assets/Scripts/Editor/TabletopBatch.cs", True),
    ("quest-app/Assets/Plugins/Android/AndroidManifest.xml", True),
    ("quest-app/Assets/Samples~/Demo.cs", False),
    ("quest-app/Tests~/NavTests.cs", False),
    ("quest-app/Tests~/Tests.csproj", False),
    ("quest-app/Packages/manifest.json", True),
    ("quest-app/Packages/packages-lock.json", False),
    ("quest-app/ProjectSettings/ProjectSettings.asset", True),
    ("quest-app/ProjectSettings/QualitySettings.asset", True),
    ("quest-app/.gitignore", False),
    ("server-plugin/src/main/java/dev/tabletopcraft/ar/TabletopPlugin.java", True),
    ("server-plugin/src/main/resources/config.yml", True),
    ("server-plugin/src/test/java/dev/tabletopcraft/ar/FixtureTest.java", False),
    ("server-plugin/build.gradle.kts", True),
    ("server-plugin/settings.gradle.kts", False),
    ("server-plugin/gradlew.bat", False),
    ("server-starter/start.ps1", True),
    ("server-starter/start.bat", True),
    ("docs/PROTOCOL.md", False),
    ("docs/release-notes/v1.2.2.md", False),
    ("tools/fake_quest.py", False),
    ("tools/release/auto_release.py", False),
    (".github/workflows/release.yml", False),
    ("README.md", False),
    ("CLAUDE.md", False),
    ("VERSION", False),
    ("media/area-setup.png", False),
])
def test_is_shipped(path, shipped):
    assert ar.is_shipped(path) is shipped


def test_only_version_lines():
    version_only = ("diff --git a/P b/P\n--- a/P\n+++ b/P\n@@ -6 +6 @@\n-  bundleVersion: 1.2.1\n+  bundleVersion: 1.2.2\n"
                    "@@ -9 +9 @@\n-  AndroidBundleVersionCode: 1020100\n+  AndroidBundleVersionCode: 1020200\n")
    assert ar.only_version_lines(version_only)
    assert ar.only_version_lines("")
    assert not ar.only_version_lines(version_only + "@@ -10 +10 @@\n-  AndroidMinSdkVersion: 32\n+  AndroidMinSdkVersion: 33\n")
    assert not ar.only_version_lines("@@ -8 +8 @@\n-  macOSBundleVersionCode: 7\n+  macOSBundleVersionCode: 8\n")


@pytest.mark.parametrize("subject,expected", [
    ("feat(quest): width presets", ("new", "quest", "width presets")),
    ("feat(plugin,quest): pairing", ("new", "plugin,quest", "pairing")),
    ("feat!: a new wire format", ("new", "", "a new wire format")),
    ("fix(server-starter): say when the network is Public", ("fix", "server-starter", "say when the network is Public")),
    ("fix(ci): keep the release key", ("maintenance", "ci", "keep the release key")),
    ("ci(release): bound Unity's runs", ("maintenance", "release", "bound Unity's runs")),
    ("docs(protocol): changelog in version order", ("maintenance", "protocol", "changelog in version order")),
    ("test(plugin): fixtures", ("maintenance", "plugin", "fixtures")),
    ("perf(quest): faster meshing", ("other", "quest", "faster meshing")),
    ("Faster section meshing", ("other", "", "Faster section meshing")),
])
def test_classify(subject, expected):
    assert ar.classify(subject) == expected


# ---------------------------------------------------------------------------------------------------- the files

def test_protocol_header():
    out = ar.set_protocol_header(PROTOCOL_TEXT, "1.3.0")
    first, rest = out.split("\n", 1)
    assert first == "# PROTOCOL.md — Tabletop link protocol, v1.3.0"
    assert rest == PROTOCOL_TEXT.split("\n", 1)[1]
    with pytest.raises(ar.ReleaseError):
        ar.set_protocol_header("# PROTOCOL.md\n", "1.3.0")


def changelog_lines(text: str) -> list[str]:
    lines = text.split("\n")
    start = lines.index("## Changelog")
    end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith("## "))
    return [ln for ln in lines[start + 1:end] if ln]


def test_changelog_entry_goes_after_the_last_version():
    out, own = ar.update_changelog(PROTOCOL_TEXT, "1.2.2", AUTO)
    entries = changelog_lines(out)
    assert entries[-2].startswith("- **v1.2.1:") and entries[-1] == AUTO
    assert own == []
    assert out.count("\n## 4.13") == 1 and out.endswith("(client→server).\n")
    again, _ = ar.update_changelog(out, "1.2.2", AUTO)
    assert again == out   # a second run doesn't add it twice


def test_changelog_keeps_version_order():
    text = PROTOCOL_TEXT.replace("Info tab wraps its status text.\n",
                                 "Info tab wraps its status text.\n- **v1.3.0: pairing.** Additive.\n")
    out, _ = ar.update_changelog(text, "1.2.2", AUTO)
    entries = changelog_lines(out)
    assert [e[:9] for e in entries[-3:]] == ["- **v1.2.", AUTO[:9], "- **v1.3."]
    assert entries[-2] == AUTO
    out, _ = ar.update_changelog(text.replace("- **v1 (2026-10-13):** initial.\n", ""), "1.1.9",
                                 "- **v1.1.9: automatic release.** x")
    assert changelog_lines(out)[0] == "- **v1.1.9: automatic release.** x"   # before every higher version


def test_changelog_names_next_version_entries():
    text = PROTOCOL_TEXT.replace("Info tab wraps its status text.\n",
                                 "Info tab wraps its status text.\n- **vNEXT: pairing (\"Pairing\").** Additive: cap `pair_v1`.\n"
                                 "  A second line of the same entry.\n")
    auto = "- **v1.3.0: automatic release.** 1 new feature since v1.2.1."
    out, own = ar.update_changelog(text, "1.3.0", auto)
    assert "vNEXT" not in out
    assert own == ["- **v1.3.0: pairing (\"Pairing\").** Additive: cap `pair_v1`."]
    entries = changelog_lines(out)
    assert entries[-3].startswith("- **v1.3.0: pairing") and entries[-2].startswith("  A second line")
    assert entries[-1] == auto


def test_changelog_needs_the_section():
    with pytest.raises(ar.ReleaseError):
        ar.update_changelog("# PROTOCOL.md — v1.2.1\n\nNo changelog.\n", "1.2.2", AUTO)


def test_project_settings_changes_only_the_version_lines():
    out = ar.set_project_settings(SETTINGS_TEXT, "1.3.0")
    changed = [(a, b) for a, b in zip(SETTINGS_TEXT.split("\n"), out.split("\n"), strict=True) if a != b]
    assert changed == [("  bundleVersion: 1.2.1", "  bundleVersion: 1.3.0"),
                       ("  AndroidBundleVersionCode: 1020100", "  AndroidBundleVersionCode: 1030000")]
    crlf = ar.set_project_settings(SETTINGS_TEXT.replace("\n", "\r\n"), "1.3.0")
    assert "  bundleVersion: 1.3.0\r\n" in crlf and "  AndroidBundleVersionCode: 1030000\r\n" in crlf
    with pytest.raises(ar.ReleaseError):
        ar.set_project_settings(SETTINGS_TEXT.replace("  bundleVersion: 1.2.1\n", ""), "1.3.0")
    with pytest.raises(ar.ReleaseError):
        ar.set_project_settings(SETTINGS_TEXT + "  AndroidBundleVersionCode: 1\n", "1.3.0")


def test_the_repository_files():
    """The real PROTOCOL.md and ProjectSettings.asset take the next version."""
    current = (REPO / "VERSION").read_text().strip()
    nxt = ar.bump_version(current, "patch")
    protocol = (REPO / "docs/PROTOCOL.md").read_text(encoding="utf-8")
    out = ar.set_protocol_header(protocol, nxt)
    assert out.split("\n", 1)[0].endswith(f", v{nxt}")
    auto = f"- **v{nxt}: automatic release.** x"
    out, _ = ar.update_changelog(out, nxt, auto)
    entries = changelog_lines(out)
    i = entries.index(auto)
    assert entries[i - 1].startswith((f"- **v{current}", f"- **v{nxt}")), entries[i - 1][:40]
    settings = (REPO / ar.SETTINGS).read_text(encoding="utf-8")
    new = ar.set_project_settings(settings, nxt)
    assert sum(a != b for a, b in zip(settings.split("\n"), new.split("\n"), strict=True)) == 2


# ---------------------------------------------------------------------------------------------------- the decision

def test_docs_tools_ci_and_tests_dont_release(repo):
    before = repo.head()
    repo.commit("docs: setup", {"docs/QUEST_SETUP.md": "x\n"})
    repo.commit("ci: lint", {".github/workflows/ci.yml": "x\n"})
    repo.commit("test(quest): nav", {"quest-app/Tests~/NavTests.cs": "x\n"})
    repo.commit("test(plugin): fixtures", {"server-plugin/src/test/java/FixtureTest.java": "x\n", "tools/fixtures/a.json": "{}\n"})
    p = plan(repo, before=before)
    assert not p.release and "no shipped code changed since v1.2.1" in p.reason


@pytest.mark.parametrize("path", [ASSET, "quest-app/Packages/manifest.json", PLUGIN, "server-plugin/build.gradle.kts", STARTER,
                                  "quest-app/ProjectSettings/QualitySettings.asset"])
def test_shipped_code_releases_a_patch(repo, path):
    before = repo.head()
    repo.commit("fix: something", {path: "changed\n"})
    p = plan(repo, before=before)
    assert p.release and (p.version, p.level, p.tag, p.last_tag) == ("1.2.2", "patch", "v1.2.2", "v1.2.1")
    assert p.shipped == [path]


def test_minor_and_major_keywords(repo):
    before = repo.head()
    repo.commit("feat(plugin): pairing\n\nA new cap. [minor]", {PLUGIN: "pair\n"})
    assert (plan(repo, before=before).version, plan(repo, before=before).level) == ("1.3.0", "minor")
    repo.commit("feat!: a new wire format [MAJOR]", {ASSET: "v2\n"})
    assert plan(repo, before=before).version == "2.0.0"


def test_keywords_count_from_the_whole_range_since_the_tag(repo):
    repo.commit("docs: plan the feature [minor]", {"docs/PLAN.md": "x\n"})   # an earlier push that didn't release
    before = repo.head()
    repo.commit("feat(quest): the feature", {ASSET: "x\n"})
    assert plan(repo, before=before).version == "1.3.0"


def test_skip_release_holds_only_its_own_push(repo):
    before = repo.head()
    repo.commit("feat(quest): first half\n\n[skip release]", {ASSET: "half\n"})
    p = plan(repo, before=before)
    assert not p.release and "[skip release]" in p.reason
    before = repo.head()
    repo.commit("feat(quest): second half", {ASSET: "whole\n"})
    p = plan(repo, before=before)
    assert p.release and p.version == "1.2.2"
    assert len(p.commits) == 2   # the held changes go out with this release
    for unknown in ("", "0" * 40, "1234567"):   # no usable "before": the push is everything since the tag
        assert not plan(repo, before=unknown).release


def test_settings_change_of_only_the_version_lines_doesnt_release(repo):
    before = repo.head()
    repo.commit("chore: versions", {ar.SETTINGS: ar.set_project_settings(SETTINGS_TEXT, "1.2.5")})
    p = plan(repo, before=before)
    assert not p.release and p.shipped == []
    repo.commit("fix(quest): min SDK", {ar.SETTINGS: repo.read(ar.SETTINGS).replace("MinSdkVersion: 32", "MinSdkVersion: 33")})
    p = plan(repo, before=before)
    assert p.release and p.shipped == [ar.SETTINGS]


def test_a_manual_run_always_releases(repo):
    p = plan(repo, event="workflow_dispatch")
    assert p.release and p.version == "1.2.2" and p.commits == []
    repo.commit("docs: notes [skip release]", {"docs/a.md": "x\n"})
    assert plan(repo, event="workflow_dispatch").version == "1.2.2"
    assert plan(repo, event="workflow_dispatch", bump="minor").version == "1.3.0"
    assert plan(repo, event="workflow_dispatch", bump="major").version == "2.0.0"
    repo.commit("feat: big [major]", {ASSET: "x\n"})
    assert plan(repo, event="workflow_dispatch", bump="patch").version == "1.2.2"   # the input beats the keywords


def test_a_version_bumped_by_hand_is_released_as_it_is(repo):
    before = repo.head()
    repo.commit("feat(plugin): pairing", {PLUGIN: "pair\n", "VERSION": "1.3.0\n"})
    p = plan(repo, before=before)
    assert (p.release, p.version, p.level) == (True, "1.3.0", "as-is")
    assert plan(repo, event="workflow_dispatch", bump="major").version == "1.3.0"


def test_versions_must_climb(repo):
    repo.git("checkout", "-q", "-b", "side")
    repo.commit("fix: elsewhere", {"docs/x.md": "x\n"})
    repo.git("tag", "v1.4.0")
    repo.git("checkout", "-q", "master")
    before = repo.head()
    repo.commit("fix(quest): a", {ASSET: "a\n"})
    with pytest.raises(ar.ReleaseError, match="not above the newest tag v1.4.0"):
        plan(repo, before=before)
    repo.git("tag", "-d", "v1.4.0")
    repo.git("tag", "v1.2.2", "side")
    with pytest.raises(ar.ReleaseError, match="v1.2.2 already exists"):
        plan(repo, before=before)
    repo.git("tag", "-d", "v1.2.2")
    repo.commit("chore: oops", {"VERSION": "1.1.0\n"})
    with pytest.raises(ar.ReleaseError, match="VERSION says 1.1.0"):
        plan(repo, before=before)


# ---------------------------------------------------------------------------------------------------- the release

def test_run_commits_tags_and_pushes(repo, origin, monkeypatch, capsys):
    bot_identity(monkeypatch)
    before = repo.head()
    repo.commit("fix(server-starter): say when Windows calls the network Public", {STARTER: "public\n"})
    repo.commit("docs: the Windows results", {"docs/TESTING.md": "x\n"})
    repo.push()
    assert ar.main(run_args(repo, before)) == 0

    tagged = origin.git("rev-parse", "v1.2.2^{commit}")
    assert origin.git("rev-parse", "master") == tagged == repo.head()
    assert origin.git("rev-parse", "master^") == repo.git("rev-parse", "HEAD^")
    message = origin.git("log", "-1", "--format=%B", "master")
    assert message.split("\n")[0] == "release: v1.2.2 [skip release]"
    assert message.endswith("\n".join(TRAILERS))
    assert "1 fix and 1 maintenance commit since v1.2.1" in message
    assert origin.git("log", "-1", "--format=%an <%ae>|%cn <%ce>", "master") == f"{BOT}|{BOT}"
    assert origin.git("show", "--name-only", "--format=", "master").split() == [
        "VERSION", "docs/PROTOCOL.md", "docs/release-notes/v1.2.2.md", ar.SETTINGS]

    assert repo.read("VERSION") == "1.2.2\n"
    protocol = repo.read("docs/PROTOCOL.md")
    assert protocol.startswith("# PROTOCOL.md — Tabletop link protocol, v1.2.2\n")
    assert changelog_lines(protocol)[-1] == (
        "- **v1.2.2: automatic release.** 1 fix and 1 maintenance commit since v1.2.1, listed in "
        "[the release notes](release-notes/v1.2.2.md): no protocol changes unless an earlier entry says so.")
    assert "  bundleVersion: 1.2.2\n" in repo.read(ar.SETTINGS)
    assert "  AndroidBundleVersionCode: 1020200\n" in repo.read(ar.SETTINGS)
    notes = repo.read("docs/release-notes/v1.2.2.md")
    assert notes.startswith("# v1.2.2: Say when Windows calls the network Public\n")
    assert "### Fixes\n\n- **server-starter:** say when Windows calls the network Public\n" in notes
    assert "### Maintenance\n\n1 commit to the documentation" in notes
    assert "Merge branch" not in notes and "### New" not in notes
    assert "compare/v1.2.1...v1.2.2" in notes and "No protocol changes." in notes

    # release.yml's own checks pass on the tagged commit
    check = subprocess.run(["bash", "tools/release/check_version.sh", "v1.2.2"], cwd=repo.path, capture_output=True, text=True)
    assert check.returncode == 0 and check.stdout.strip() == "1.2.2", check.stderr
    for mode, expected in (("--title", "v1.2.2: Say when Windows calls the network Public"),
                           ("--short", "v1.2.2: Say when Windows calls the network Public")):
        out = subprocess.run(["bash", "tools/release/release_notes.sh", "1.2.2", mode], cwd=repo.path, capture_output=True,
                             text=True)
        assert out.stdout.strip() == expected

    # loop safety, twice over: nothing ships since the new tag, and the release commit itself says [skip release]
    release_sha = repo.head()
    p = plan(repo, before=f"{release_sha}^")
    assert not p.release and "since v1.2.2" in p.reason
    repo.git("tag", "-d", "v1.2.2")
    p = plan(repo, before=f"{release_sha}^")
    assert not p.release and "[skip release]" in p.reason


def test_run_without_a_release_changes_nothing(repo, origin):
    before = repo.head()
    repo.commit("docs: x", {"docs/x.md": "x\n"})
    repo.push()
    head = repo.head()
    assert ar.main(run_args(repo, before)) == 0
    assert origin.git("rev-parse", "master") == head == repo.head()
    assert origin.git("tag", "--list", "v1.2.2") == ""


def test_run_on_top_of_a_branch_that_moved(repo, origin, tmp_path, monkeypatch, capsys):
    bot_identity(monkeypatch)
    before = repo.head()
    repo.commit("fix(quest): clip caps", {ASSET: "a\n"})
    repo.push()
    other = clone(origin, tmp_path / "other")   # a second push lands before the job pushes
    other.commit("feat(plugin): pairing [minor]", {PLUGIN: "b\n"})
    other.push()
    assert ar.main(run_args(repo, before)) == 0
    out = capsys.readouterr().out
    assert "moved to" in out and "making the release commit again" in out
    assert origin.git("rev-parse", "master^") == other.head()
    assert origin.git("rev-parse", "v1.3.0^{commit}") == origin.git("rev-parse", "master")
    notes = repo.read("docs/release-notes/v1.3.0.md")
    assert "- **quest:** clip caps" in notes and "- **plugin:** pairing [minor]" in notes
    assert repo.git("tag", "--list", "v1.2.2") == ""


def test_run_stops_when_the_branch_was_rewritten(repo, origin, tmp_path, capsys):
    before = repo.head()
    repo.commit("fix(quest): a", {ASSET: "a\n"})
    repo.push()
    other = clone(origin, tmp_path / "other")
    other.git("reset", "-q", "--hard", "HEAD~1")
    other.commit("fix(quest): c", {ASSET: "c\n"})
    other.push("--force")
    rewritten = origin.git("rev-parse", "master")
    assert ar.main(run_args(repo, before)) == 1
    assert "no longer contains" in capsys.readouterr().err
    assert origin.git("rev-parse", "master") == rewritten
    assert origin.git("tag", "--list", "v1.2.2") == ""


def test_run_stops_when_the_push_is_refused(repo, origin, capsys):
    before = repo.head()
    repo.commit("fix(quest): a", {ASSET: "a\n"})
    repo.push()
    hook = origin.path / "hooks" / "pre-receive"
    hook.write_text("#!/bin/sh\necho 'protected branch' >&2\nexit 1\n")
    hook.chmod(0o755)
    assert ar.main(run_args(repo, before)) == 1
    err = capsys.readouterr().err
    assert "refused the push" in err and "protected branch" in err
    assert origin.git("tag", "--list", "v1.2.2") == "" and repo.git("tag", "--list", "v1.2.2") == ""


def test_run_commits_only_the_release_files(repo, origin, capsys):
    repo.write("VERSION", "1.2.9\n")
    assert ar.main(run_args(repo, event="workflow_dispatch")) == 1
    assert "VERSION, docs/PROTOCOL.md" in capsys.readouterr().err
    repo.git("checkout", "--", "VERSION")
    repo.write("README.md", "staged\n")
    repo.git("add", "README.md")
    assert ar.main(run_args(repo, event="workflow_dispatch")) == 1
    assert "staged changes" in capsys.readouterr().err
    repo.git("reset", "-q", "--", "README.md")
    # A file that only shows as changed (stored with CRLF in the index under "eol=crlf", like gradlew.bat once was)
    # or any other unrelated local edit stays out of the release commit.
    repo.write(".gitattributes", "*.bat text eol=crlf\n")
    repo.git("add", ".gitattributes")
    repo.git("-c", "core.autocrlf=false", "commit", "-q", "-m", "chore: attributes")
    (repo.path / "gradlew.bat").write_bytes(b"@echo off\r\n")
    repo.git("update-index", "--add", "--cacheinfo",
             f"100644,{repo.git('hash-object', '-w', '--no-filters', 'gradlew.bat')},gradlew.bat")
    repo.git("commit", "-q", "-m", "chore: a CRLF batch file")
    repo.push()
    assert "gradlew.bat" in repo.git("status", "--porcelain")
    assert ar.main(run_args(repo, event="workflow_dispatch")) == 0
    assert repo.read("README.md") == "staged\n"
    assert origin.git("show", "--name-only", "--format=", "master").split() == [
        "VERSION", "docs/PROTOCOL.md", "docs/release-notes/v1.2.2.md", ar.SETTINGS]


def test_next_version_placeholders(repo, origin, monkeypatch):
    bot_identity(monkeypatch)
    before = repo.head()
    protocol = repo.read("docs/PROTOCOL.md").replace(
        "Info tab wraps its status text.\n", "Info tab wraps its status text.\n- **vNEXT: pairing (\"Pairing\").** Additive.\n")
    repo.commit("feat(plugin): pairing\n\n[minor]", {
        PLUGIN: "pair\n", "docs/PROTOCOL.md": protocol, "docs/release-notes/vNEXT.md": "# vNEXT: pairing\n\nWritten by hand.\n"})
    repo.push()
    assert ar.main(run_args(repo, before)) == 0
    assert repo.read("docs/release-notes/v1.3.0.md") == "# v1.3.0: pairing\n\nWritten by hand.\n"
    assert not (repo.path / "docs/release-notes/vNEXT.md").exists()
    entries = changelog_lines(repo.read("docs/PROTOCOL.md"))
    assert entries[-2] == "- **v1.3.0: pairing (\"Pairing\").** Additive."
    assert entries[-1].startswith("- **v1.3.0: automatic release.** 1 new feature since v1.2.1")
    assert "D\tdocs/release-notes/vNEXT.md" in origin.git("show", "--no-renames", "--name-status", "--format=", "master")
    assert origin.git("ls-tree", "--name-only", "master", "docs/release-notes/").split() == [
        "docs/release-notes/README.md", "docs/release-notes/v1.3.0.md"]


def test_notes_leave_out_merges_and_bot_commits(repo):
    repo.git("checkout", "-q", "-b", "feature")
    repo.commit("feat(plugin): pairing", {PLUGIN: "pair\n"})
    repo.commit("fix(quest): the pairing card", {ASSET: "card\n"})
    repo.git("checkout", "-q", "master")
    repo.commit("chore: bump a dependency", {"tools/requirements.txt": "x\n"}, author="dependabot[bot] <bot@example.com>")
    repo.git("merge", "-q", "--no-ff", "-m", "Merge branch 'feature': pairing", "feature")
    commits = ar.Git(repo.path).commits("v1.2.1..HEAD")
    notes = ar.release_notes("1.3.0", "v1.2.1", commits, ["- **v1.3.0: pairing.** Additive."], "owner/repo")
    assert notes.startswith("# v1.3.0: Pairing (and 1 more change)\n")
    assert "1 new feature and 1 fix since v1.2.1" in notes
    assert "Merge branch" not in notes and "dependency" not in notes and "### Maintenance" not in notes
    assert "## Protocol" in notes and "- **v1.3.0: pairing.** Additive." in notes and "The protocol changes are listed above." in notes
    major = ar.release_notes("2.0.0", "v1.3.0", [], [], "owner/repo")
    assert major.startswith("# v2.0.0: Maintenance release\n")
    assert "Nothing new since v1.3.0" in major and "doesn't work with plugins or apps of v1.x.y" in major


def test_apply_previews_the_next_version(repo):
    assert ar.main(["--root", str(repo.path), "apply", "--bump", "minor"]) == 0
    assert repo.read("VERSION") == "1.3.0\n"
    assert (repo.path / "docs/release-notes/v1.3.0.md").exists()
    assert len(repo.git("status", "--porcelain").splitlines()) == 4   # VERSION, PROTOCOL.md, ProjectSettings, the notes


def test_plan_prints_outputs(repo, tmp_path, monkeypatch, capsys):
    out_file = tmp_path / "github_output"
    monkeypatch.setenv("GITHUB_OUTPUT", str(out_file))
    before = repo.head()
    repo.commit("fix(quest): a", {ASSET: "a\n"})
    assert ar.main(["--root", str(repo.path), "plan", "--before", before]) == 0
    printed = capsys.readouterr().out
    assert "release=true\nversion=1.2.2\ntag=v1.2.2\nlevel=patch\n" in printed
    assert out_file.read_text().startswith("release=true\nversion=1.2.2\n")
