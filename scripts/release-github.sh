#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT_FILE="${REPO_ROOT}/ComPortsTool.csproj"
PROJECT_NAME="com-ports"
CONFIGURATION="Release"
ARTIFACTS_DIR="${REPO_ROOT}/artifacts"

usage() {
  cat <<'EOF'
Usage:
  scripts/release-github.sh [patch|minor|major|current|X.Y.Z]

Examples:
  scripts/release-github.sh
  scripts/release-github.sh patch
  scripts/release-github.sh minor
  scripts/release-github.sh current
  scripts/release-github.sh 1.4.0

Behavior:
  - requires a clean git working tree
  - bumps the version in ComPortsTool.csproj
  - creates a commit "Release vX.Y.Z"
  - creates annotated tag "vX.Y.Z"
  - pushes branch and tag
  - publishes the release artifact for the current version
  - zips the publish output
  - creates a GitHub release and uploads the ZIP
EOF
}

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "Missing required command: $1"
}

ensure_clean_worktree() {
  if [[ -n "$(git status --porcelain)" ]]; then
    fail "Working tree is not clean. Commit or stash changes before releasing."
  fi
}

get_current_version() {
  sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$PROJECT_FILE" | head -n 1
}

validate_version() {
  local version="$1"
  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Invalid version: ${version}"
}

next_version() {
  local current="$1"
  local mode="${2:-patch}"
  local major minor patch

  IFS='.' read -r major minor patch <<<"$current"

  case "$mode" in
    patch)
      patch=$((patch + 1))
      ;;
    minor)
      minor=$((minor + 1))
      patch=0
      ;;
    major)
      major=$((major + 1))
      minor=0
      patch=0
      ;;
    *)
      echo "$mode"
      return
      ;;
  esac

  echo "${major}.${minor}.${patch}"
}

update_version_in_project() {
  local version="$1"
  local assembly_version="${version}.0"

  python3 - "$PROJECT_FILE" "$version" "$assembly_version" <<'PY'
import pathlib
import re
import sys

project_path = pathlib.Path(sys.argv[1])
version = sys.argv[2]
assembly_version = sys.argv[3]
content = project_path.read_text(encoding="utf-8")

replacements = {
    "Version": version,
    "AssemblyVersion": assembly_version,
    "FileVersion": assembly_version,
    "InformationalVersion": version,
}

for tag, value in replacements.items():
    pattern = rf"<{tag}>.*?</{tag}>"
    replacement = f"<{tag}>{value}</{tag}>"
    content, count = re.subn(pattern, replacement, content, count=1)
    if count != 1:
        raise SystemExit(f"Missing <{tag}> in {project_path}")

project_path.write_text(content, encoding="utf-8")
PY
}

ensure_tag_does_not_exist() {
  local tag_name="$1"
  if git rev-parse -q --verify "refs/tags/${tag_name}" >/dev/null; then
    fail "Tag already exists: ${tag_name}"
  fi
}

ensure_tag_exists() {
  local tag_name="$1"
  git rev-parse -q --verify "refs/tags/${tag_name}" >/dev/null || fail "Tag does not exist: ${tag_name}"
}

ensure_gh_authenticated() {
  gh auth status >/dev/null 2>&1 || fail "GitHub CLI is not authenticated. Run: gh auth login"
}

create_release_artifact() {
  local version="$1"
  local tag_name="v${version}"
  local publish_dir="${ARTIFACTS_DIR}/publish/${PROJECT_NAME}-${version}"
  local zip_path="${ARTIFACTS_DIR}/${PROJECT_NAME}-${tag_name}.zip"

  rm -rf "$publish_dir"
  mkdir -p "$publish_dir" "$ARTIFACTS_DIR"

  dotnet publish "$PROJECT_FILE" \
    -c "$CONFIGURATION" \
    --self-contained false \
    -p:Version="$version" \
    -p:AssemblyVersion="${version}.0" \
    -p:FileVersion="${version}.0" \
    -p:InformationalVersion="$version" \
    -o "$publish_dir" >&2

  rm -f "$zip_path"
  (
    cd "$publish_dir"
    zip -r "$zip_path" . >&2
  )

  echo "$zip_path"
}

create_github_release() {
  local version="$1"
  local tag_name="v${version}"
  local artifact_path="$2"
  local notes_file="${ARTIFACTS_DIR}/release-notes-${tag_name}.md"

  cat >"$notes_file" <<EOF
Release ${tag_name}

Artifact:
- ${PROJECT_NAME}-${tag_name}.zip

Versione tool:
- ${version}
EOF

  gh release create "$tag_name" \
    "$artifact_path" \
    --title "$tag_name" \
    --notes-file "$notes_file"
}

main() {
  local mode="${1:-patch}"

  if [[ "${mode}" == "-h" || "${mode}" == "--help" ]]; then
    usage
    exit 0
  fi

  require_command git
  require_command dotnet
  require_command gh
  require_command zip
  require_command python3

  cd "$REPO_ROOT"

  ensure_clean_worktree
  ensure_gh_authenticated

  local current_version new_version tag_name current_branch artifact_path
  current_version="$(get_current_version)"
  [[ -n "$current_version" ]] || fail "Unable to read current version from ${PROJECT_FILE}"

  current_branch="$(git rev-parse --abbrev-ref HEAD)"
  [[ "$current_branch" != "HEAD" ]] || fail "Detached HEAD is not supported for release publishing."

  if [[ "$mode" == "current" ]]; then
    new_version="$current_version"
    validate_version "$new_version"
    tag_name="v${new_version}"
    ensure_tag_exists "$tag_name"
  else
    new_version="$(next_version "$current_version" "$mode")"
    validate_version "$new_version"

    if [[ "$new_version" == "$current_version" ]]; then
      fail "New version equals current version: ${new_version}"
    fi

    tag_name="v${new_version}"
    ensure_tag_does_not_exist "$tag_name"

    update_version_in_project "$new_version"

    git add -- "$PROJECT_FILE"
    git commit -m "Release ${tag_name}"
    git tag -a "$tag_name" -m "Release ${tag_name}"

    git push origin "$current_branch"
    git push origin "$tag_name"
  fi

  artifact_path="$(create_release_artifact "$new_version")"
  create_github_release "$new_version" "$artifact_path"

  echo "Release completed: ${tag_name}"
  echo "Artifact: ${artifact_path}"
}

main "$@"
