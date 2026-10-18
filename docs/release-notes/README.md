# Release notes

An optional file per version, `docs/release-notes/vX.Y.Z.md`, written as part of the release commit. When it exists,
`release.yml` uses it for the GitHub release and the store channel's notes (`tools/release/release_notes.sh`); when it
doesn't, both fall back to the version's entries in the `docs/PROTOCOL.md` changelog (every line starting with
`- **vX.Y.Z`). A version with neither stops the release in its first job.

## Format

- **First line**, `# vX.Y.Z: a short title`: the GitHub release title, and the release notes on the Meta Horizon ALPHA
  channel (testers see them in the store). Keep it to one line of plain text. Without a `# ` heading the title is
  `vX.Y.Z` and the store notes are the first changelog line.
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

Check what a release will show before tagging:

```
tools/release/release_notes.sh X.Y.Z --title
tools/release/release_notes.sh X.Y.Z --body
tools/release/release_notes.sh X.Y.Z --short
```

The repo is public, and so are these notes: keep them technical, with no personal details, hostnames, LAN addresses or
usernames, the same rules as every other doc here.
