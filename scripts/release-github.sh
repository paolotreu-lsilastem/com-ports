#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PROJECT_FILE="${REPO_ROOT}/ComPortsTool.csproj"
PROJECT_NAME="com-ports"
CONFIGURATION="Release"
ARTIFACTS_DIR="${REPO_ROOT}/artifacts"
RUNTIME_IDENTIFIER="win-x64"

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
  - publishes a Windows win-x64 self-contained single-file executable
  - creates or updates a GitHub release and uploads the .exe
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

release_exists() {
  local tag_name="$1"
  gh release view "$tag_name" >/dev/null 2>&1
}

delete_legacy_release_asset_if_present() {
  local tag_name="$1"
  local legacy_asset_name legacy_asset_id

  for legacy_asset_name in \
    "${PROJECT_NAME}-${tag_name}.zip" \
    "${PROJECT_NAME}-${tag_name}-${RUNTIME_IDENTIFIER}.zip"
  do
    legacy_asset_id="$(gh release view "$tag_name" --json assets --jq ".assets[] | select(.name == \"${legacy_asset_name}\") | .apiUrl | split(\"/\") | last" 2>/dev/null || true)"

    if [[ -n "${legacy_asset_id}" ]]; then
      gh api -X DELETE "repos/{owner}/{repo}/releases/assets/${legacy_asset_id}" >/dev/null
    fi
  done
}

count_files_in_dir() {
  local dir="$1"
  find "$dir" -maxdepth 1 -type f | wc -l | tr -d '[:space:]'
}

create_release_artifact() {
  local version="$1"
  local tag_name="v${version}"
  local publish_dir="${ARTIFACTS_DIR}/publish/${PROJECT_NAME}-${version}-${RUNTIME_IDENTIFIER}"
  local exe_path="${ARTIFACTS_DIR}/${PROJECT_NAME}-${tag_name}-${RUNTIME_IDENTIFIER}.exe"

  rm -rf "$publish_dir"
  mkdir -p "$publish_dir" "$ARTIFACTS_DIR"

  dotnet publish "$PROJECT_FILE" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME_IDENTIFIER" \
    --self-contained true \
    -p:Version="$version" \
    -p:AssemblyVersion="${version}.0" \
    -p:FileVersion="${version}.0" \
    -p:InformationalVersion="$version" \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugSymbols=false \
    -p:DebugType=None \
    -o "$publish_dir" >&2

  [[ -f "${publish_dir}/${PROJECT_NAME}.exe" ]] || fail "Expected Windows executable not found: ${publish_dir}/${PROJECT_NAME}.exe"
  [[ "$(count_files_in_dir "$publish_dir")" == "1" ]] || fail "Publish output is not a single-file artifact: ${publish_dir}"

  rm -f "$exe_path"
  cp "${publish_dir}/${PROJECT_NAME}.exe" "$exe_path"

  echo "$exe_path"
}

upsert_github_release() {
  local version="$1"
  local tag_name="v${version}"
  local artifact_path="$2"
  local notes_file="${ARTIFACTS_DIR}/release-notes-${tag_name}.md"
  local asset_name
  asset_name="$(basename "$artifact_path")"

  cat >"$notes_file" <<EOF
Release ${tag_name}

Artifact:
- ${asset_name}

Versione tool:
- ${version}

Target:
- Windows ${RUNTIME_IDENTIFIER}
- Self-contained single-file executable
EOF

  if release_exists "$tag_name"; then
    gh release upload "$tag_name" "$artifact_path" --clobber
    gh release edit "$tag_name" --title "$tag_name" --notes-file "$notes_file"
  else
    gh release create "$tag_name" \
      "$artifact_path" \
      --title "$tag_name" \
      --notes-file "$notes_file"
  fi

  delete_legacy_release_asset_if_present "$tag_name"
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
  upsert_github_release "$new_version" "$artifact_path"

  echo "Release completed: ${tag_name}"
  echo "Artifact: ${artifact_path}"
}

main "$@"
