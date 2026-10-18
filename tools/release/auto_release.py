#!/usr/bin/env python3
"""Automatic releases from master (docs/RELEASING.md section 6, .github/workflows/auto-release.yml).

Decides whether master's new commits make a release and at which version, writes that version everywhere it is kept
(VERSION, the docs/PROTOCOL.md header and changelog, quest-app/ProjectSettings/ProjectSettings.asset), writes
docs/release-notes/vX.Y.Z.md from the commit subjects, then commits, tags and pushes the release.

  auto_release.py plan  [--event push|workflow_dispatch] [--before SHA] [--bump auto|patch|minor|major]
        the decision as key=value lines; changes nothing
  auto_release.py apply [--bump auto|patch|minor|major]
        edit the files for the next version as a manual run would (no commit): a preview, `git diff` shows it
  auto_release.py run   [plan options] [--trailer LINE]... [--remote origin] [--branch master] [--no-push]
        plan, apply, commit "release: vX.Y.Z [skip release]", tag vX.Y.Z and push both atomically. When the branch
        moved meanwhile, the release commit is made again on top of it (the version and notes then include the new
        commits); anything else that refuses the push stops the run. Never force-pushes.

Rules:
- a push releases when the files changed since the last vX.Y.Z tag include shipped code (is_shipped) and no commit of
  the push says [skip release]; a workflow_dispatch run always releases;
- the level: [major] or [minor] in any commit message since the last tag, else patch; --bump overrides it;
- a VERSION that has no tag yet (bumped by hand) is released as it is;
- docs/PROTOCOL.md changelog lines "- **vNEXT..." and docs/release-notes/vNEXT.md take the new version.
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

DEFAULT_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_REPO = "NickStassen/tabletop-craft-ar"
TAG_GLOB = "v[0-9]*.[0-9]*.[0-9]*"
SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
LEVELS = ("patch", "minor", "major")
SKIP = "[skip release]"

PROTOCOL = "docs/PROTOCOL.md"
SETTINGS = "quest-app/ProjectSettings/ProjectSettings.asset"
NOTES_DIR = "docs/release-notes"
PLACEHOLDER = "vNEXT"

CHANGELOG_HEADING = "## Changelog"
ENTRY_VERSION = re.compile(r"^- \*\*v(\d+)\.(\d+)\.(\d+)[:* ]")
HEADER = re.compile(r"^(# .*[ ,]v)\d+\.\d+\.\d+(\s*)$")
SETTINGS_VERSION_LINE = re.compile(r"^  (bundleVersion|AndroidBundleVersionCode):")
BOT_SUBJECT = re.compile(r"^release: v\d+\.\d+\.\d+\b")
CONVENTIONAL = re.compile(r"^(?P<type>[A-Za-z]+)(?:\((?P<scope>[^)]*)\))?!?:\s*(?P<text>\S.*)$")
MAINTENANCE_TYPES = {"docs", "doc", "ci", "test", "tests", "build", "chore", "style", "release"}
MAINTENANCE_SCOPES = {"docs", "doc", "ci", "test", "tests", "build", "release"}
GROUPS = (("new", "New"), ("fix", "Fixes"), ("other", "Other changes"))


class ReleaseError(Exception):
    """Stops the run without releasing; the message says why and what to do."""


# ---------------------------------------------------------------------------------------------------- versions

def parse_version(v: str) -> tuple[int, int, int]:
    m = SEMVER.match(v.strip())
    if not m:
        raise ReleaseError(f"not a version X.Y.Z: {v!r}")
    return int(m[1]), int(m[2]), int(m[3])


def bump_version(v: str, level: str) -> str:
    major, minor, patch = parse_version(v)
    if level == "major":
        return f"{major + 1}.0.0"
    if level == "minor":
        return f"{major}.{minor + 1}.0"
    if level == "patch":
        return f"{major}.{minor}.{patch + 1}"
    raise ReleaseError(f"unknown bump level {level!r} (patch, minor, major)")


def version_code(v: str) -> int:
    """Android versionCode as TabletopBatch.ApplyVersion sets it with TABLETOP_BUILD 0 (RELEASING.md section 1)."""
    major, minor, patch = parse_version(v)
    if minor > 99 or patch > 99:
        raise ReleaseError(f"v{v} doesn't fit the versionCode scheme M*1000000 + m*10000 + p*100 (RELEASING.md section 1)")
    return major * 1_000_000 + minor * 10_000 + patch * 100


def keyword_level(messages: list[str]) -> str:
    text = "\n".join(messages).lower()
    if "[major]" in text:
        return "major"
    if "[minor]" in text:
        return "minor"
    return "patch"


# ---------------------------------------------------------------------------------------------------- what ships

def is_shipped(path: str) -> bool:
    """Whether a changed file is part of what a release ships: app, plugin or server starter (RELEASING.md 6.1)."""
    if path.startswith("quest-app/Assets/"):
        # Unity leaves out files and folders whose names end in "~" (quest-app/Tests~ is outside Assets anyway)
        return not any(part.endswith("~") for part in path.split("/")[2:])
    return (
        path == "quest-app/Packages/manifest.json"
        or path.startswith("quest-app/ProjectSettings/")
        or path.startswith("server-plugin/src/main/")
        or path == "server-plugin/build.gradle.kts"
        or path.startswith("server-starter/")
    )


def only_version_lines(diff: str) -> bool:
    """Whether a `git diff -U0` of ProjectSettings.asset changes nothing but the version lines this script writes."""
    changed = [ln[1:] for ln in diff.splitlines() if ln[:1] in "+-" and not ln.startswith(("+++", "---"))]
    return all(SETTINGS_VERSION_LINE.match(ln) for ln in changed)


# ---------------------------------------------------------------------------------------------------- commits

@dataclass
class Commit:
    sha: str
    author: str
    parents: int
    message: str
    files: list[str] | None = None   # the files it changes (not filled for merge commits)

    @property
    def subject(self) -> str:
        return self.message.split("\n", 1)[0].strip()

    @property
    def is_merge(self) -> bool:
        return self.parents > 1

    @property
    def is_bot(self) -> bool:
        return self.author.endswith("[bot]") or bool(BOT_SUBJECT.match(self.subject))


def classify(subject: str) -> tuple[str, str, str]:
    """(group, scope, text) of a commit subject: group is new, fix, other or maintenance (docs, CI, tests, tooling)."""
    m = CONVENTIONAL.match(subject)
    if not m:
        return "other", "", subject
    kind = m["type"].lower()
    scope = (m["scope"] or "").strip()
    scopes = {s.strip().lower() for s in scope.split(",") if s.strip()}
    if kind in MAINTENANCE_TYPES or (scopes and scopes <= MAINTENANCE_SCOPES):
        return "maintenance", scope, m["text"]
    if kind == "feat":
        return "new", scope, m["text"]
    if kind == "fix":
        return "fix", scope, m["text"]
    return "other", scope, m["text"]


def group_commits(commits: list[Commit]) -> dict[str, list[tuple[str, str]]]:
    """Release-note groups, oldest first, without merge commits and release commits. A commit that changes no shipped
    file is maintenance whatever its prefix says."""
    groups: dict[str, list[tuple[str, str]]] = {"new": [], "fix": [], "other": [], "maintenance": []}
    for c in reversed(commits):   # git log lists the newest first
        if c.is_merge or c.is_bot:
            continue
        kind, scope, text = classify(c.subject)
        if c.files is not None and not any(is_shipped(f) for f in c.files):
            kind = "maintenance"
        groups[kind].append((scope, text))
    return groups


def summary_counts(groups: dict[str, list[tuple[str, str]]]) -> str:
    parts = []
    for key, one, many in (("new", "new feature", "new features"), ("fix", "fix", "fixes"),
                           ("other", "other change", "other changes"),
                           ("maintenance", "maintenance commit", "maintenance commits")):
        n = len(groups[key])
        if n:
            parts.append(f"{n} {one if n == 1 else many}")
    if not parts:
        return "no new commits"
    return parts[0] if len(parts) == 1 else ", ".join(parts[:-1]) + " and " + parts[-1]


def shorten(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    cut = text[:limit].rsplit(" ", 1)[0].rstrip(" ,;:")
    return cut + "..."


def notes_title(groups: dict[str, list[tuple[str, str]]]) -> str:
    user = groups["new"] + groups["fix"] + groups["other"]
    if not user:
        return "Maintenance release"
    text = shorten(user[0][1], 110)
    text = text[:1].upper() + text[1:]
    more = len(user) - 1
    if more:
        text += f" (and {more} more change{'s' if more > 1 else ''})"
    return text


# ---------------------------------------------------------------------------------------------------- git

class Git:
    def __init__(self, root: Path):
        self.root = root

    def run(self, *args: str, check: bool = True, stdin: str | None = None) -> subprocess.CompletedProcess[str]:
        r = subprocess.run(["git", "-C", str(self.root), *args], capture_output=True, text=True, input=stdin)
        if check and r.returncode != 0:
            raise ReleaseError(f"git {' '.join(args)} failed: {(r.stderr or r.stdout).strip()}")
        return r

    def out(self, *args: str) -> str:
        return self.run(*args).stdout.strip()

    def ok(self, *args: str) -> bool:
        return self.run(*args, check=False).returncode == 0

    def rev(self, ref: str) -> str:
        return self.out("rev-parse", "--verify", "--quiet", f"{ref}^{{commit}}")

    def last_tag(self, head: str) -> str:
        r = self.run("describe", "--tags", "--abbrev=0", "--match", TAG_GLOB, head, check=False)
        return r.stdout.strip() if r.returncode == 0 else ""

    def tags(self) -> list[str]:
        return [t for t in self.out("tag", "--list", TAG_GLOB).split() if SEMVER.match(t[1:])]

    def empty_tree(self) -> str:
        return self.run("hash-object", "-t", "tree", "--stdin", stdin="").stdout.strip()

    def commits(self, *revs: str) -> list[Commit]:
        raw = self.run("log", "--format=%H%x00%an%x00%P%x00%B%x1e", *revs, "--").stdout
        commits = []
        for record in raw.split("\x1e"):
            record = record.lstrip("\n")
            if not record:
                continue
            sha, author, parents, message = record.split("\x00", 3)
            commit = Commit(sha, author, len(parents.split()), message.strip())
            if not commit.is_merge:
                out = self.run("diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "--no-renames", "-z", sha).stdout
                commit.files = [p for p in out.split("\0") if p]
            commits.append(commit)
        return commits

    def changed_files(self, since: str, head: str) -> list[str]:
        out = self.run("diff", "--name-only", "--no-renames", "-z", since, head, "--").stdout
        return [p for p in out.split("\0") if p]


# ---------------------------------------------------------------------------------------------------- the decision

@dataclass
class Plan:
    release: bool
    reason: str
    version: str = ""
    level: str = ""
    last_tag: str = ""
    head: str = ""
    shipped: list[str] = field(default_factory=list)
    commits: list[Commit] = field(default_factory=list)

    @property
    def tag(self) -> str:
        return f"v{self.version}" if self.version else ""


def describe(plan: Plan) -> str:
    return f"release v{plan.version} ({plan.level})" if plan.release else "no release"


def make_plan(git: Git, head: str = "HEAD", event: str = "push", before: str = "", bump: str = "auto") -> Plan:
    if bump not in ("auto", *LEVELS):
        raise ReleaseError(f"--bump must be auto, patch, minor or major, not {bump!r}")
    if event not in ("push", "workflow_dispatch"):
        raise ReleaseError(f"--event must be push or workflow_dispatch, not {event!r}")
    head_sha = git.rev(head)
    current = git.out("show", f"{head_sha}:VERSION")
    parse_version(current)
    last_tag = git.last_tag(head_sha)
    since = last_tag or git.empty_tree()
    since_text = last_tag or "the first commit"
    commits = git.commits(f"{last_tag}..{head_sha}" if last_tag else head_sha)
    shipped = [p for p in git.changed_files(since, head_sha) if is_shipped(p)]
    if SETTINGS in shipped and only_version_lines(git.run("diff", "-U0", since, head_sha, "--", SETTINGS).stdout):
        shipped.remove(SETTINGS)
    plan = Plan(False, "", last_tag=last_tag, head=head_sha, shipped=shipped, commits=commits)

    if event == "workflow_dispatch":
        reason = "run by hand (workflow_dispatch)"
    else:
        if not shipped:
            plan.reason = f"no shipped code changed since {since_text} (app, plugin, server starter)"
            return plan
        pushed = commits
        if before and before.strip("0") and git.ok("merge-base", "--is-ancestor", before, head_sha):
            pushed = git.commits(head_sha, f"^{before}", *([f"^{last_tag}"] if last_tag else []))
        skipping = [c for c in pushed if SKIP in c.message.lower()]
        if skipping:
            c = skipping[0]
            plan.reason = (f"{SKIP} in {c.sha[:7]} ({shorten(c.subject, 80)}): this push doesn't release; its changes go "
                           "out with the next release")
            return plan
        reason = f"{len(shipped)} shipped file{'s' if len(shipped) != 1 else ''} changed since {since_text}"

    tags = git.tags()
    newest = max(tags, key=lambda t: parse_version(t[1:]), default="")
    if f"v{current}" not in tags:
        if newest and parse_version(current) <= parse_version(newest[1:]):
            raise ReleaseError(f"VERSION says {current}, which has no tag and isn't above the newest tag {newest}: "
                               "set VERSION to the newest released version")
        version, level = current, "as-is"
        reason += f"; VERSION {current} has no tag yet (bumped by hand), so it is released as it is"
        if bump != "auto":
            reason += f" (the {bump} bump asked for is ignored)"
    else:
        level = bump if bump != "auto" else keyword_level([c.message for c in commits])
        version = bump_version(current, level)
    if f"v{version}" in tags:
        raise ReleaseError(f"the tag v{version} already exists")
    if newest and parse_version(version) <= parse_version(newest[1:]):
        raise ReleaseError(f"v{version} is not above the newest tag {newest}: set VERSION to the newest released version")
    version_code(version)
    plan.release, plan.reason, plan.version, plan.level = True, reason, version, level
    return plan


# ---------------------------------------------------------------------------------------------------- the files

def set_protocol_header(text: str, version: str) -> str:
    first, nl, rest = text.partition("\n")
    m = HEADER.match(first)
    if not m:
        raise ReleaseError(f"{PROTOCOL}: the first line should end in the version (vX.Y.Z), it is {first!r}")
    return f"{m[1]}{version}{m[2]}{nl}{rest}"


def auto_entry(version: str, last_tag: str, groups: dict[str, list[tuple[str, str]]]) -> str:
    return (f"- **v{version}: automatic release.** {summary_counts(groups)} since {last_tag or 'the first commit'}, "
            f"listed in [the release notes](release-notes/v{version}.md): "
            "no protocol changes unless an earlier entry says so.")


def update_changelog(text: str, version: str, entry: str) -> tuple[str, list[str]]:
    """Gives "- **vNEXT" changelog lines the version and adds `entry` after the last entry of a version up to this one,
    so the list stays in version order. Returns the text and this version's other entries."""
    lines = text.split("\n")
    try:
        start = lines.index(CHANGELOG_HEADING)
    except ValueError:
        raise ReleaseError(f"{PROTOCOL} has no '{CHANGELOG_HEADING}' section") from None
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    target = parse_version(version)
    prefix = f"- **{PLACEHOLDER}"
    for i in range(start + 1, end):
        if lines[i].startswith(prefix):
            lines[i] = f"- **v{version}" + lines[i][len(prefix):]
    entries = [i for i in range(start + 1, end) if lines[i].startswith("- **")]
    if not entries:
        raise ReleaseError(f"{PROTOCOL}: the changelog has no '- **vX.Y.Z...' entries")

    def entry_version(line: str) -> tuple[int, int, int] | None:
        m = ENTRY_VERSION.match(line)
        return (int(m[1]), int(m[2]), int(m[3])) if m else None

    auto_prefix = f"- **v{version}: automatic release.**"
    own = [lines[i] for i in entries if entry_version(lines[i]) == target and not lines[i].startswith(auto_prefix)]
    if any(lines[i].startswith(auto_prefix) for i in entries):
        return "\n".join(lines), own
    after = None
    for i in entries:
        v = entry_version(lines[i])
        if v is None or v <= target:   # the early "v1 (...)" entries have no X.Y.Z and come first
            after = i
    if after is None:
        at = entries[0]
    else:
        at = after + 1
        while at < end and lines[at].strip() and lines[at][:1].isspace():   # the entry's continuation lines
            at += 1
    lines.insert(at, entry)
    return "\n".join(lines), own


def set_project_settings(text: str, version: str) -> str:
    for key, value in (("bundleVersion", version), ("AndroidBundleVersionCode", str(version_code(version)))):
        text, n = re.subn(rf"(?m)^(  {key}:)[^\r\n]*", rf"\g<1> {value}", text)
        if n != 1:
            raise ReleaseError(f"{SETTINGS}: expected one '{key}:' line, found {n}")
    return text


def release_notes(version: str, last_tag: str, commits: list[Commit], protocol_entries: list[str], repo: str) -> str:
    groups = group_commits(commits)
    since = last_tag or "the first commit"
    major = parse_version(version)[0]
    out = [f"# v{version}: {notes_title(groups)}", ""]
    intro = f"Released automatically from master: {summary_counts(groups)} since {since}"
    if last_tag:
        intro += f" ([all changes](https://github.com/{repo}/compare/{last_tag}...v{version}))"
    out += [intro + ".", ""]
    out += ["## What's in it", ""]
    if not any(groups.values()):
        out += [f"Nothing new since {since}: the same code, built again.", ""]
    for key, heading in GROUPS:
        if groups[key]:
            out += [f"### {heading}", ""]
            out += [f"- **{scope}:** {text}" if scope else f"- {text}" for scope, text in groups[key]]
            out += [""]
    n = len(groups["maintenance"])
    if n:
        out += ["### Maintenance", "", f"{n} commit{'s' if n != 1 else ''} to the documentation, tests, CI and release "
                "tooling.", ""]
    if protocol_entries:
        out += ["## Protocol", "",
                f"From the [PROTOCOL.md changelog](https://github.com/{repo}/blob/v{version}/docs/PROTOCOL.md#changelog):",
                "", *protocol_entries, ""]
    out += ["## Verified on a Quest 3", "",
            "Not yet. The APK goes to the Meta Horizon ALPHA channel first; once it has passed on a headset, "
            "`promote.yml` moves it to BETA and makes this the latest GitHub release.", ""]
    if major > 1 and parse_version(version)[1:] == (0, 0):
        compat = (f"v{version} is a new major version: it doesn't work with plugins or apps of v{major - 1}.x.y. "
                  "Update the plugin and the app together.")
    else:
        compat = (f"A plugin and an app with the same major version (v{major}.x.y) work together (semantic versioning, "
                  "docs/PROTOCOL.md).")
        compat += " The protocol changes are listed above." if protocol_entries else " No protocol changes."
    out += ["## Compatibility", "", compat, ""]
    out += ["## Files", "",
            f"- `tabletop-craft-ar-plugin-{version}.jar`: the server plugin",
            f"- `tabletop-craft-ar-{version}.apk`: the Quest app, release-signed (the same file as the store build)",
            f"- `tabletop-server-starter-{version}.zip`: the server starter scripts"]
    return "\n".join(out) + "\n"


def read(path: Path) -> str:
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def write(path: Path, text: str) -> None:
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def apply_release(root: Path, plan: Plan, repo: str) -> list[str]:
    """Writes the version into every file that holds it and the release notes; returns the paths to commit."""
    version = plan.version
    groups = group_commits(plan.commits)
    write(root / "VERSION", version + "\n")
    protocol = set_protocol_header(read(root / PROTOCOL), version)
    protocol, own = update_changelog(protocol, version, auto_entry(version, plan.last_tag, groups))
    write(root / PROTOCOL, protocol)
    write(root / SETTINGS, set_project_settings(read(root / SETTINGS), version))
    paths = ["VERSION", PROTOCOL, SETTINGS]
    notes = root / NOTES_DIR / f"v{version}.md"
    placeholder = root / NOTES_DIR / f"{PLACEHOLDER}.md"
    if placeholder.exists():
        if notes.exists():
            raise ReleaseError(f"both {NOTES_DIR}/{PLACEHOLDER}.md and {NOTES_DIR}/v{version}.md exist: keep one")
        write(notes, read(placeholder).replace(PLACEHOLDER, f"v{version}"))
        placeholder.unlink()
        paths.append(f"{NOTES_DIR}/{PLACEHOLDER}.md")
    elif not notes.exists():
        notes.parent.mkdir(parents=True, exist_ok=True)
        write(notes, release_notes(version, plan.last_tag, plan.commits, own, repo))
    paths.append(f"{NOTES_DIR}/v{version}.md")
    return paths


# ---------------------------------------------------------------------------------------------------- commit and push

def commit_message(plan: Plan, trailers: list[str]) -> str:
    counts = summary_counts(group_commits(plan.commits))
    msg = (f"release: v{plan.version} {SKIP}\n\n"
           f"Automatic release (auto-release.yml): {counts} since {plan.last_tag or 'the first commit'}.\n")
    if trailers:
        msg += "\n" + "\n".join(trailers) + "\n"
    return msg


def check_files(root: Path, tag: str) -> None:
    """tools/release/check_version.sh, the same check release.yml's first job runs on the tag."""
    script = root / "tools/release/check_version.sh"
    if not script.exists():
        raise ReleaseError("tools/release/check_version.sh is missing")
    r = subprocess.run(["bash", str(script), tag], capture_output=True, text=True)
    if r.returncode != 0:
        raise ReleaseError(f"check_version.sh {tag} failed after the bump: {(r.stderr or r.stdout).strip()}")


def run(git: Git, args: argparse.Namespace) -> Plan:
    # The release commit must hold only the release's own edits: nothing staged, and its files unchanged. Other files
    # may differ (a checkout can show a file as changed when only its line endings are normalized).
    if not git.ok("diff", "--cached", "--quiet"):
        raise ReleaseError("the index has staged changes")
    if git.out("status", "--porcelain", "--untracked-files=no", "--", "VERSION", PROTOCOL, SETTINGS, NOTES_DIR):
        raise ReleaseError(f"VERSION, {PROTOCOL}, {SETTINGS} or {NOTES_DIR} has uncommitted changes")
    ident = git.out("var", "GIT_COMMITTER_IDENT").rsplit(">", 1)[0] + ">"
    print(f"Committing as {ident}")
    repo = os.environ.get("GITHUB_REPOSITORY") or DEFAULT_REPO
    remote_ref = f"refs/remotes/{args.remote}/{args.branch}"
    for attempt in range(1, 4):
        plan = make_plan(git, "HEAD", args.event, args.before, args.bump)
        print(f"Plan at {plan.head[:7]}: {describe(plan)}: {plan.reason}")
        if not plan.release:
            return plan
        paths = apply_release(git.root, plan, repo)
        check_files(git.root, plan.tag)
        git.run("add", "-A", "--", *paths)
        git.run("commit", "--quiet", "-F", "-", stdin=commit_message(plan, args.trailer))
        git.run("tag", plan.tag)
        release_sha = git.rev("HEAD")
        print(f"Committed {release_sha[:7]} 'release: {plan.tag}' and tagged it {plan.tag}")
        if args.no_push:
            return plan
        pushed = git.run("push", "--atomic", args.remote, f"HEAD:refs/heads/{args.branch}", f"refs/tags/{plan.tag}",
                         check=False)
        if pushed.returncode == 0:
            print(f"Pushed {args.branch} and {plan.tag} to {args.remote}")
            return plan
        refusal = (pushed.stderr or pushed.stdout).strip()
        git.run("tag", "-d", plan.tag)
        git.run("fetch", "--quiet", "--tags", args.remote, f"+refs/heads/{args.branch}:{remote_ref}")
        moved_to = git.rev(remote_ref)
        if moved_to == plan.head:
            raise ReleaseError(f"{args.remote} refused the push of {args.branch} and {plan.tag} ({args.branch} did not "
                               f"move): {refusal}")
        local = git.out("rev-list", f"{remote_ref}..HEAD").split()
        if local != [release_sha]:
            raise ReleaseError(f"{args.branch} on {args.remote} moved to {moved_to[:7]} and no longer contains "
                               f"{plan.head[:7]} ({len(local)} local commits instead of just the release commit): "
                               "nothing pushed, nothing rebased; release by hand (Actions > Auto release > Run workflow)")
        print(f"{args.branch} moved to {moved_to[:7]} while v{plan.version} was prepared (attempt {attempt}): "
              "making the release commit again on top of it")
        git.run("reset", "--quiet", "--keep", moved_to)   # drops only the release commit, checked just above
    raise ReleaseError(f"{args.branch} kept moving: the push failed 3 times; run the workflow again")


# ---------------------------------------------------------------------------------------------------- output

def github_output(**values: str) -> None:
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            for k, v in values.items():
                f.write(f"{k}={v}\n")


def step_summary(plan: Plan, pushed: bool) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not path:
        return
    lines = [f"### Auto release: {describe(plan)}", "",
             f"- {plan.reason}", f"- last tag: {plan.last_tag or 'none'}, commits since: {len(plan.commits)}"]
    if plan.shipped:
        shown = ", ".join(f"`{p}`" for p in plan.shipped[:12])
        more = len(plan.shipped) - 12
        lines.append(f"- shipped files changed: {shown}{f' and {more} more' if more > 0 else ''}")
    if plan.release and pushed:
        lines.append(f"- pushed the release commit and the tag {plan.tag}; release.yml builds and publishes it next")
    with open(path, "a", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def plan_outputs(plan: Plan) -> dict[str, str]:
    return {"release": "true" if plan.release else "false", "version": plan.version, "tag": plan.tag,
            "level": plan.level, "last_tag": plan.last_tag, "reason": plan.reason}


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", type=Path, default=DEFAULT_ROOT, help="the repository (default: the one holding this script)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    def bump_option(p: argparse.ArgumentParser) -> None:
        p.add_argument("--bump", choices=("auto", *LEVELS), default="auto", help="the level; auto reads the keywords")

    def plan_options(p: argparse.ArgumentParser) -> None:
        p.add_argument("--event", choices=("push", "workflow_dispatch"), default="push")
        p.add_argument("--before", default="", help="the branch head before the push (github.event.before)")
        bump_option(p)

    plan_options(sub.add_parser("plan", help="the decision as key=value lines"))
    bump_option(sub.add_parser("apply", help="edit the files for the next version (no commit)"))
    p_run = sub.add_parser("run", help="plan, apply, commit, tag and push")
    plan_options(p_run)
    p_run.add_argument("--trailer", action="append", default=[], help="a trailer line for the release commit")
    p_run.add_argument("--remote", default="origin")
    p_run.add_argument("--branch", default="master")
    p_run.add_argument("--no-push", action="store_true", help="stop after the local commit and tag")
    args = ap.parse_args(argv)

    git = Git(args.root.resolve())
    try:
        if args.cmd == "plan":
            plan = make_plan(git, "HEAD", args.event, args.before, args.bump)
            for k, v in plan_outputs(plan).items():
                print(f"{k}={v}")
            for p in plan.shipped:
                print(f"  shipped: {p}", file=sys.stderr)
            github_output(**plan_outputs(plan))
        elif args.cmd == "apply":
            plan = make_plan(git, "HEAD", "workflow_dispatch", "", args.bump)
            for p in apply_release(git.root, plan, os.environ.get("GITHUB_REPOSITORY") or DEFAULT_REPO):
                print(p)
        else:
            plan = run(git, args)
            pushed = plan.release and not args.no_push
            github_output(**plan_outputs(plan), pushed="true" if pushed else "false")
            step_summary(plan, pushed)
    except ReleaseError as e:
        if os.environ.get("GITHUB_ACTIONS"):
            print(f"::error::{e}", file=sys.stderr)
        else:
            print(f"FAIL {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
