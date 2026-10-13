# CI workflow (pending install)

`github-actions-ci.yml` is the GitHub Actions workflow (Gradle tests, ruff + pytest, C# tests).
To enable it, move it to `.github/workflows/ci.yml` and push with a token that has the `workflow` scope
(e.g. `gh auth refresh -s workflow`).
