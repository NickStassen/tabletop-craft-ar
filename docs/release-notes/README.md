# Release notes

One file per version, `docs/release-notes/vX.Y.Z.md`, in the release commit. `release.yml` uses it for the GitHub
release and the store channel's notes (`tools/release/release_notes.sh`); without one, both fall back to the version's
entries in the `docs/PROTOCOL.md` changelog (every line starting with `- **vX.Y.Z`). A version with neither stops the
release in its first job.

## Written for you, or by you

`auto-release.yml` (docs/RELEASING.md section 6.2) writes the file for every automatic release from the commit
subjects since the last tag: a title from the first feature or fix, then **New**, **Fixes** and **Other changes** from
the conventional prefixes (`feat`, `fix`, the rest), a count of the docs, CI, test and tooling commits, "Not yet" under
**Verified on a Quest 3**, **Compatibility** and **Files**. Merge commits and release commits are left out. Good commit
subjects make good notes: they are the text players and server owners read.

To write a release's notes yourself, add `docs/release-notes/vNEXT.md` in the change that goes out with it (the version
isn't known until the release, so write `vNEXT` wherever it belongs, as in the heading `# vNEXT: a short title`). The
release renames it to `vX.Y.Z.md` and replaces every `vNEXT` in it; a file that already exists for the version is kept
as it is. After the headset check, edit the published text with `gh release edit vX.Y.Z --notes-file ...`.

## Format (for notes written by hand)

- **First line**, `# vX.Y.Z: a short title` (`# vNEXT: ...` in `vNEXT.md`): the GitHub release title, and the release
  notes on the Meta Horizon ALPHA channel (testers see them in the store). Keep it to one line of plain text. Without
  a `# ` heading the title is `vX.Y.Z` and the store notes are the first changelog line.
- **The rest**: the GitHub release text, in Markdown. What the earlier releases cover:
  - a short paragraph on what the release is for;
  - **Get started** when the steps changed (starter zip, APK or store link, pairing);
  - **What's in it**: the changes a player or server owner notices;
  - **Verified on a Quest 3** (date): what was checked on the headset. For a tag that hasn't been on a headset yet,
    say so, and edit the release text after the ALPHA check (`gh release edit vX.Y.Z --notes-file ...`);
  - **Compatibility**: which plugin and app versions work together (PROTOCOL.md versioning);
  - **Known issues**.
- Name the release files as they are attached: `tabletop-craft-ar-plugin-X.Y.Z.jar`, `tabletop-craft-ar-X.Y.Z.apk`,
  `tabletop-server-starter-X.Y.Z.zip`.

Check what a release will show before merging: `python3 tools/release/auto_release.py apply` writes the files the next
release would get (undo it afterwards, RELEASING.md 6.2), then:

```
tools/release/release_notes.sh X.Y.Z --title
tools/release/release_notes.sh X.Y.Z --body
tools/release/release_notes.sh X.Y.Z --short
```

The repo is public, and so are these notes: keep them technical, with no personal details, hostnames, LAN addresses or
usernames, the same rules as every other doc here.
